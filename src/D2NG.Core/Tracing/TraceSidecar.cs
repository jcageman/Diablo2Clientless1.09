using Serilog.Events;
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using D2NG.Core.D2GS;
using System.Text.Json;

namespace D2NG.Core.Tracing;

/// <summary>A typed event as the bot reported it; the fields are rendered to JSON on the writer thread.</summary>
public sealed class TraceEvent
{
    public TraceEvent(string kind, string client, string game, (string Key, object Value)[] fields)
        : this(DateTime.UtcNow, kind, client, game, fields)
    {
    }

    public TraceEvent(DateTime utc, string kind, string client, string game, (string Key, object Value)[] fields)
    {
        Utc = utc;
        Kind = kind;
        Client = client;
        Game = game;
        Fields = fields;
    }

    public DateTime Utc { get; }

    public string Kind { get; }

    public string Client { get; }

    public string Game { get; }

    public (string Key, object Value)[] Fields { get; }
}

/// <summary>
/// The log lines and typed events of one process, as JSON lines in
/// &lt;root&gt;/&lt;yyyyMMdd&gt;/events-&lt;HHmmss&gt;.jsonl. Producers only queue the event; rendering
/// and writing happen in <see cref="Drain"/>, which the recorder's writer runs.
///
/// Every line starts with <c>{"t":"&lt;UTC, ISO 8601&gt;","k":"&lt;kind&gt;"</c>, so a reader can skip lines
/// by time without parsing them. Kinds: <c>build</c> (first in each file: build, buildUtc, rules - who wrote it),
/// <c>log</c> (lvl, msg, tpl when it differs from msg, p, ex),
/// <c>gap</c> (n events dropped on a full queue), <c>trace-failed</c> (n events that threw before
/// they were queued), and the typed kinds of <see cref="TraceEventWriter"/>,
/// which carry <c>c</c> (client) and <c>g</c> (game) followed by their own fields.
/// </summary>
public sealed class TraceSidecar
{
    public const string FilePrefix = "events-";

    public const string Extension = ".jsonl";

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _fileName;
    private readonly int _maxPending;
    private List<object> _active = [];
    private List<object> _spare = [];
    private long _dropped;
    private long _failedEvents;
    private FileStream _file;
    private DateTime _retryAfter = DateTime.MinValue;
    private long _lost;
    private bool _closed;
    private bool _flushed;
    private readonly ArrayBufferWriter<byte> _line = new(4096);
    private static readonly JsonWriterOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    internal TraceSidecar(string directory, string fileName, int maxPending)
    {
        _directory = directory;
        _fileName = fileName;
        _maxPending = maxPending;
    }

    /// <summary>Null until the first drain that had something to write.</summary>
    public string Path { get; private set; }

    public long Dropped => _dropped;

    /// <summary>A trace event or log line that failed before it was queued; reported as a <c>trace-failed</c> line.</summary>
    internal void CountFailure() => Interlocked.Increment(ref _failedEvents);

    internal void Enqueue(object entry)
    {
        lock (_gate)
        {
            if (_active.Count >= _maxPending)
            {
                _dropped++;
                return;
            }

            _active.Add(entry);
        }
    }

    /// <summary>One drain at a time: the recorder's drain lock.</summary>
    internal void Drain()
    {
        List<object> entries;
        long dropped;
        lock (_gate)
        {
            (_active, _spare) = (_spare, _active);
            entries = _spare;
            dropped = _dropped;
            _dropped = 0;
        }

        var failedEvents = Interlocked.Exchange(ref _failedEvents, 0);
        try
        {
            if ((entries.Count == 0 && dropped == 0 && failedEvents == 0 && _lost == 0) || _closed)
            {
                return;
            }

            if (_file == null && Clock() < _retryAfter)
            {
                _lost += entries.Count + dropped;
                return;
            }

            var opened = _file == null;
            _file ??= Open();
            if (_lost > 0)
            {
                dropped += _lost;
                _lost = 0;
            }

            using var json = new Utf8JsonWriter(_line, JsonOptions);
            if (opened)
            {
                WriteLine(json, BuildEvent(entries));
            }

            foreach (var entry in entries)
            {
                try
                {
                    WriteLine(json, entry);
                }
                catch (Exception e) when (e is not IOException)
                {
                    WriteLine(json, new TraceEvent("log", null, null, [("lvl", "Warning"), ("msg", $"Trace sidecar could not render an event: {e.Message}")]));
                }
            }

            if (dropped > 0)
            {
                WriteLine(json, new TraceEvent("gap", null, null, [("n", dropped)]));
            }

            if (failedEvents > 0)
            {
                WriteLine(json, new TraceEvent("trace-failed", null, null, [("n", failedEvents)]));
            }

            _file.Flush();
            _flushed = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _lost += entries.Count + dropped;
            _retryAfter = Clock() + RetryAfterFailure;
            Abandon();
            Serilog.Log.Warning("Tracing events to {Directory} paused for {Seconds}s: {Error}", _directory, RetryAfterFailure.TotalSeconds, e.Message);
        }
        finally
        {
            entries.Clear();
        }
    }

    internal void Close()
    {
        _closed = true;
        try
        {
            _file?.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        _file = null;
    }

    /// <summary>Disposing flushes what the failed write left buffered, which fails again on a full disk.</summary>
    private void Abandon()
    {
        try
        {
            _file?.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        _file = null;
        var flushed = _flushed;
        _flushed = false;
        if (!flushed && Path != null)
        {
            try
            {
                File.Delete(Path);
                Path = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>A file's first line: which build wrote it and the client rules it ran, dated no later than the entries after it.</summary>
    private TraceEvent BuildEvent(List<object> entries)
    {
        var utc = Clock();
        foreach (var entry in entries)
        {
            var at = entry switch
            {
                TraceEvent traceEvent => traceEvent.Utc,
                LogEvent logEvent => logEvent.Timestamp.UtcDateTime,
                _ => utc,
            };
            utc = at < utc ? at : utc;
        }

        return new TraceEvent(utc, BuildKind, null, null,
            [("build", BuildIdentity.Version), ("buildUtc", BuildIdentity.Utc is { } built ? Timestamp(built) : null), ("rules", ClientRules.LatestVersion)]);
    }

    /// <summary>The kind of the line that opens every sidecar file.</summary>
    public const string BuildKind = "build";

    private FileStream Open()
    {
        Directory.CreateDirectory(_directory);
        for (var attempt = 1; ; attempt++)
        {
            var name = attempt == 1 ? _fileName : $"{_fileName}-{attempt}";
            var path = System.IO.Path.Combine(_directory, name + Extension);
            try
            {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite, 1 << 16);
                Path = path;
                return stream;
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }
    }

    private void WriteLine(Utf8JsonWriter json, object entry)
    {
        _line.ResetWrittenCount();
        json.Reset(_line);
        switch (entry)
        {
            case LogEvent logEvent:
                Render(json, logEvent);
                break;
            case TraceEvent traceEvent:
                Render(json, traceEvent);
                break;
        }

        json.Flush();
        _file.Write(_line.WrittenSpan);
        _file.WriteByte((byte)'\n');
    }

    public static void Render(Utf8JsonWriter json, LogEvent logEvent)
    {
        json.WriteStartObject();
        json.WriteString("t", Timestamp(logEvent.Timestamp.UtcDateTime));
        json.WriteString("k", "log");
        json.WriteString("lvl", logEvent.Level.ToString());
        var message = logEvent.RenderMessage(CultureInfo.InvariantCulture);
        json.WriteString("msg", message);
        if (logEvent.MessageTemplate.Text != message)
        {
            json.WriteString("tpl", logEvent.MessageTemplate.Text);
        }

        if (logEvent.Properties.Count > 0)
        {
            json.WriteStartObject("p");
            foreach (var (name, value) in logEvent.Properties)
            {
                json.WritePropertyName(name);
                WriteValue(json, value);
            }

            json.WriteEndObject();
        }

        if (logEvent.Exception != null)
        {
            json.WriteString("ex", logEvent.Exception.ToString());
        }

        json.WriteEndObject();
    }

    public static void Render(Utf8JsonWriter json, TraceEvent traceEvent)
    {
        json.WriteStartObject();
        json.WriteString("t", Timestamp(traceEvent.Utc));
        json.WriteString("k", traceEvent.Kind);
        if (traceEvent.Client != null)
        {
            json.WriteString("c", traceEvent.Client);
        }

        if (traceEvent.Game != null)
        {
            json.WriteString("g", traceEvent.Game);
        }

        foreach (var (key, value) in traceEvent.Fields)
        {
            if (value == null)
            {
                continue;
            }

            json.WritePropertyName(key);
            WriteValue(json, value);
        }

        json.WriteEndObject();
    }

    public static string Timestamp(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static void WriteValue(Utf8JsonWriter json, LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue scalar:
                WriteValue(json, scalar.Value);
                break;
            case SequenceValue sequence:
                json.WriteStartArray();
                foreach (var element in sequence.Elements)
                {
                    WriteValue(json, element);
                }

                json.WriteEndArray();
                break;
            case StructureValue structure:
                json.WriteStartObject();
                foreach (var property in structure.Properties)
                {
                    json.WritePropertyName(property.Name);
                    WriteValue(json, property.Value);
                }

                json.WriteEndObject();
                break;
            case DictionaryValue dictionary:
                json.WriteStartObject();
                foreach (var (key, element) in dictionary.Elements)
                {
                    json.WritePropertyName(Convert.ToString(key.Value, CultureInfo.InvariantCulture) ?? "null");
                    WriteValue(json, element);
                }

                json.WriteEndObject();
                break;
            default:
                json.WriteStringValue(value?.ToString());
                break;
        }
    }

    private static void WriteValue(Utf8JsonWriter json, object value)
    {
        switch (value)
        {
            case null:
                json.WriteNullValue();
                break;
            case string text:
                json.WriteStringValue(text);
                break;
            case bool flag:
                json.WriteBooleanValue(flag);
                break;
            case Enum named:
                json.WriteStringValue(named.ToString());
                break;
            case byte or sbyte or short or ushort or int:
                json.WriteNumberValue(Convert.ToInt32(value, CultureInfo.InvariantCulture));
                break;
            case uint or long:
                json.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case ulong large:
                json.WriteNumberValue(large);
                break;
            case double or float or decimal:
                var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsFinite(number))
                {
                    json.WriteNumberValue(Math.Round(number, 3));
                }
                else
                {
                    json.WriteStringValue(number.ToString(CultureInfo.InvariantCulture));
                }

                break;
            case TimeSpan duration:
                json.WriteNumberValue((long)duration.TotalMilliseconds);
                break;
            case DateTime moment:
                json.WriteStringValue(Timestamp(moment.ToUniversalTime()));
                break;
            case Point point:
                json.WriteStartArray();
                json.WriteNumberValue(point.X);
                json.WriteNumberValue(point.Y);
                json.WriteEndArray();
                break;
            case RejectedUnit rejected:
                json.WriteStartObject();
                json.WriteNumber("id", rejected.Id);
                if (rejected.Code != null)
                {
                    json.WriteString("code", rejected.Code);
                }

                json.WriteString("filter", rejected.Filter);
                json.WriteEndObject();
                break;
            case IEnumerable<(string Key, object Value)> pairs:
                json.WriteStartObject();
                foreach (var (key, element) in pairs)
                {
                    json.WritePropertyName(key);
                    WriteValue(json, element);
                }

                json.WriteEndObject();
                break;
            case IEnumerable sequence:
                json.WriteStartArray();
                foreach (var element in sequence)
                {
                    WriteValue(json, element);
                }

                json.WriteEndArray();
                break;
            default:
                json.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}
