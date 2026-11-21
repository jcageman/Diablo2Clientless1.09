using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace D2NG.Trace.Query;

/// <summary>One line of a sidecar: a log event or a typed event.</summary>
public sealed class SidecarEntry
{
    public DateTime Utc { get; init; }

    public string Kind { get; init; }

    public string Client { get; init; }

    public string Game { get; init; }

    public string Level { get; init; }

    private string _message;
    private string _exception;
    private bool _textsPending;

    public string Message
    {
        get
        {
            ReadTexts();
            return _message;
        }

        init => _message = value;
    }

    public string Exception
    {
        get
        {
            ReadTexts();
            return _exception;
        }

        init => _exception = value;
    }

    /// <summary>For an entry read from a file: the message, exception and template are read off the line when one is first asked for.</summary>
    internal bool TextsPending
    {
        init => _textsPending = value;
    }

    private void ReadTexts()
    {
        if (!Volatile.Read(ref _textsPending))
        {
            return;
        }

        string message = null, exception = null, template = null;
        var reader = new Utf8JsonReader(_line.Span);
        reader.Read();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var field = reader.ValueTextEquals("msg"u8) ? 1 : reader.ValueTextEquals("ex"u8) ? 2 : reader.ValueTextEquals("tpl"u8) ? 3 : 0;
            reader.Read();
            var value = field != 0 && reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            reader.Skip();
            switch (field)
            {
                case 1: message = value; break;
                case 2: exception = value; break;
                case 3: template = value; break;
            }
        }

        _message = message;
        _exception = exception;
        _template = template;
        _templateRead = true;
        Volatile.Write(ref _textsPending, false);
    }

    /// <summary>The name of the sidecar file the entry came from and its index among the file's lines: what orders entries stamped alike.</summary>
    /// <summary>The sidecar file the line came from, without its folder: one process's events (events-HHmmss.jsonl, -2 for a second bot started in the same second).</summary>
    public string SourceName { get; init; }

    internal int LineIndex { get; init; }

    /// <summary>The process start (HHmmss) of the sidecar it came from, which pairs it with that process's realm traces; a continuation file (events-HHmmss-2) is the same process.</summary>
    public string Process { get; init; }

    private sealed class Parsed(JsonElement value)
    {
        public readonly JsonElement Value = value;
    }

    private Parsed _json;
    private ReadOnlyMemory<byte> _line;

    /// <summary>The whole line; read from a file, it is parsed the first time it is asked for.</summary>
    public JsonElement Json
    {
        get
        {
            if (Volatile.Read(ref _json) is { } parsed)
            {
                return parsed.Value;
            }

            var document = System.Text.Unicode.Utf8.IsValid(_line.Span)
                ? JsonDocument.Parse(_line)
                : JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(_line.Span));
            Interlocked.CompareExchange(ref _json, new Parsed(document.RootElement), null);
            return _json.Value;
        }

        init => _json = new Parsed(value);
    }

    /// <summary>The line's UTF-8 bytes, valid JSON, for <see cref="Json"/> to parse when it is first asked for.</summary>
    internal ReadOnlyMemory<byte> Line
    {
        init => _line = value;
    }

    private string _template;
    private bool _templateRead;

    /// <summary>The log message template ("tpl") when the line has one as a string, else null.</summary>
    public string Template
    {
        get
        {
            ReadTexts();
            if (Volatile.Read(ref _templateRead))
            {
                return _template;
            }

            _template = Json.TryGetProperty("tpl", out var tpl) && tpl.ValueKind == JsonValueKind.String ? tpl.GetString() : null;
            Volatile.Write(ref _templateRead, true);
            return _template;
        }

        init
        {
            _template = value;
            _templateRead = true;
        }
    }

    public bool IsLog => Kind == "log";

    public string Str(string key)
        => Json.TryGetProperty(key, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        } : null;

    public long? Num(string key)
        => Json.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    /// <summary>A decision's fields, rendered key=value.</summary>
    public string Fields()
        => Json.TryGetProperty("f", out var fields) && fields.ValueKind == JsonValueKind.Object
            ? string.Join(" ", fields.EnumerateObject().Select(p => $"{p.Name}={Render(p.Value)}"))
            : "";

    /// <summary>A typed event's fields under "f", in order, each rendered.</summary>
    public IEnumerable<(string Key, string Value)> FieldPairs()
        => Json.TryGetProperty("f", out var fields) && fields.ValueKind == JsonValueKind.Object
            ? fields.EnumerateObject().Select(p => (p.Name, Render(p.Value))).ToList()
            : [];

    public string Field(string key)
        => Json.TryGetProperty("f", out var fields) && fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(key, out var value)
            ? Render(value)
            : null;

    /// <summary>All text a client's label could appear in: the message and every property value.</summary>
    public IEnumerable<string> Texts()
    {
        if (Message != null)
        {
            yield return Message;
        }

        if (Json.TryGetProperty("p", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                yield return Render(property.Value);
            }
        }
    }

    public static string Render(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Array when value.GetArrayLength() == 2 && value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Number)
            => $"({value[0].GetRawText()}, {value[1].GetRawText()})",
        _ => value.GetRawText(),
    };
}

/// <summary>Reads the sidecar lines of a time window. Lines outside it are skipped by their leading timestamp, unparsed.</summary>
public static class SidecarReader
{
    private const string Prefix = "{\"t\":\"";
    private const int StampLength = 28;

    public static List<SidecarEntry> Read(IEnumerable<string> files, DateTime fromUtc, DateTime toUtc)
    {
        var entries = new List<SidecarEntry>();
        foreach (var file in files)
        {
            if (MayHold(file, fromUtc, toUtc))
            {
                File(file).AddWindow(entries, fromUtc, toUtc);
            }
        }

        entries.Sort(ByTime);
        return entries;
    }

    /// <summary>By time, then sidecar file name and line: a total order, so every path that sorts entries prints them alike.</summary>
    internal static readonly Comparison<SidecarEntry> ByTime = (a, b) =>
    {
        var byTime = a.Utc.CompareTo(b.Utc);
        if (byTime != 0)
        {
            return byTime;
        }

        var byFile = string.CompareOrdinal(a.SourceName, b.SourceName);
        return byFile != 0 ? byFile : a.LineIndex.CompareTo(b.LineIndex);
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<SidecarFile>> Files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A sidecar as this process first read it, so every game and every window reads the same snapshot of a file that
    /// is still being written. Its lines are parsed as windows ask for them.
    /// </summary>
    internal static SidecarFile File(string file)
        => Files.GetOrAdd(Path.GetFullPath(file), path => new Lazy<SidecarFile>(() => new SidecarFile(path))).Value;

    /// <summary>Forgets the snapshot of a file, so the next read sees it as it is now; for a test that reads a file again after it grew.</summary>
    public static void Forget(string file) => Files.TryRemove(Path.GetFullPath(file), out _);

    /// <summary>Reads the files not read yet, side by side.</summary>
    internal static void Prefetch(IEnumerable<string> files)
        => System.Threading.Tasks.Parallel.ForEach(files.ToList(), file => File(file));

    /// <summary>Reads the files not read yet that may hold a line stamped in the window, side by side.</summary>
    internal static void Prefetch(IEnumerable<string> files, DateTime fromUtc, DateTime toUtc)
        => Prefetch(files.Where(f => MayHold(f, fromUtc, toUtc)));

    /// <summary>
    /// False only when the file is known to hold no line stamped from <paramref name="fromUtc"/> to <paramref name="toUtc"/>:
    /// read already, or its first and last stamps are in the cache for its present length and write time
    /// (<see cref="TraceCache"/>). A window then skips it without reading it, and finds what reading it would have found.
    /// </summary>
    public static bool MayHold(string file, DateTime fromUtc, DateTime toUtc)
    {
        var path = Path.GetFullPath(file);
        if (Files.TryGetValue(path, out var known) && known.IsValueCreated)
        {
            return known.Value.Overlaps(fromUtc, toUtc);
        }

        if (!TraceCache.Enabled)
        {
            return true;
        }

        var info = new FileInfo(path);
        var length = info.Exists ? TraceCache.LengthNow(path) : -1;
        return length < 0 || TraceCache.SidecarRange(path, length, info.LastWriteTimeUtc) is not { } range || (range.Last >= fromUtc.Ticks && range.First <= toUtc.Ticks);
    }

    /// <summary>
    /// The lines of one sidecar, indexed by their time stamps on reading, each line parsed when a window first asks for
    /// it. A line is in the file's entries when its stamp reads and the line parses as one JSON object.
    /// </summary>
    internal sealed class SidecarFile
    {
        private static readonly SidecarEntry Unparsable = new();

        private readonly string _process;
        private readonly string _source;
        private readonly byte[] _bytes;
        private readonly long[] _ticks;
        private readonly int[] _starts;
        private readonly int[] _lengths;
        private readonly SidecarEntry[] _entries;
        private readonly int _count;
        private List<SidecarEntry> _sorted;

        /// <summary>How many bytes of the file were read.</summary>
        public long Length { get; }

        public SidecarFile(string path)
        {
            using var _ = Profile.Time("sidecar: read and index a file");
            _process = ProcessOf(path);
            _source = Path.GetFileName(path);
            int length;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1))
            {
                (_bytes, length) = ReadToEnd(stream);
                Length = length;
            }

            var bytes = _bytes.AsSpan(0, length);
            if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            {
                var text = ReadAsText(bytes.ToArray(), _process, _source);
                _count = text.Count;
                _entries = [.. text];
                _ticks = [.. text.Select(e => e.Utc.Ticks)];
                return;
            }

            var ticks = new List<long>();
            var starts = new List<int>();
            var lengths = new List<int>();
            var position = bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
            while (position < length)
            {
                var end = bytes[position..].IndexOfAny((byte)'\r', (byte)'\n');
                var size = end < 0 ? length - position : end;
                if (TryStamp(bytes.Slice(position, size), out var utc))
                {
                    ticks.Add(utc.Ticks);
                    starts.Add(position);
                    lengths.Add(size);
                }

                position += size;
                if (end >= 0)
                {
                    position += bytes[position] == '\r' && position + 1 < length && bytes[position + 1] == '\n' ? 2 : 1;
                }
            }

            _count = ticks.Count;
            _ticks = [.. ticks];
            _starts = [.. starts];
            _lengths = [.. lengths];
            _entries = new SidecarEntry[_count];
            Remember(path);
        }

        /// <summary>The earliest and latest stamp of a line; first above last for a file with none.</summary>
        public (long First, long Last) Range => _range ??= _count == 0 ? (long.MaxValue, long.MinValue) : (_ticks.Min(), _ticks.Max());

        private (long First, long Last)? _range;

        public bool Overlaps(DateTime fromUtc, DateTime toUtc) => Range.Last >= fromUtc.Ticks && Range.First <= toUtc.Ticks;

        /// <summary>Keeps the file's range for later processes, when the file is still as long as what was read.</summary>
        private void Remember(string path)
        {
            var info = new FileInfo(path);
            if (TraceCache.Enabled && info.Exists && TraceCache.LengthNow(path) == Length)
            {
                TraceCache.RememberSidecarRange(path, Length, info.LastWriteTimeUtc, Range);
            }
        }

        private SidecarEntry Entry(int i)
        {
            if (Volatile.Read(ref _entries[i]) is { } known)
            {
                return known;
            }

            var line = new ReadOnlyMemory<byte>(_bytes, _starts[i], _lengths[i]);
            var utc = new DateTime(_ticks[i], DateTimeKind.Utc);
            var parsed = (System.Text.Unicode.Utf8.IsValid(line.Span)
                ? ParseUtf8(line, utc, _process, _source, i)
                : Parse(System.Text.Encoding.UTF8.GetString(line.Span), utc, _process, _source, i)) ?? Unparsable;
            if (parsed.Kind == TraceSidecar.BuildKind)
            {
                parsed = Unparsable;
            }

            return Interlocked.CompareExchange(ref _entries[i], parsed, null) ?? parsed;
        }

        /// <summary>Adds the entries stamped from <paramref name="fromUtc"/> to <paramref name="toUtc"/>, in file order.</summary>
        public void AddWindow(List<SidecarEntry> entries, DateTime fromUtc, DateTime toUtc)
        {
            for (var i = 0; i < _count; i++)
            {
                if (_ticks[i] >= fromUtc.Ticks && _ticks[i] <= toUtc.Ticks && Entry(i) is var entry && !ReferenceEquals(entry, Unparsable))
                {
                    entries.Add(entry);
                }
            }
        }

        /// <summary>Every entry, sorted by time as a window over the whole file sorts it.</summary>
        public List<SidecarEntry> Sorted()
        {
            if (Volatile.Read(ref _sorted) is { } sorted)
            {
                return sorted;
            }

            var all = new List<SidecarEntry>();
            AddWindow(all, DateTime.MinValue, DateTime.MaxValue);
            all.Sort(ByTime);
            return Interlocked.CompareExchange(ref _sorted, all, null) ?? all;
        }
    }

    private static (byte[] Bytes, int Length) ReadToEnd(FileStream stream)
    {
        var buffer = new byte[Math.Max(4096, stream.Length + 1)];
        var filled = 0;
        while (true)
        {
            if (filled == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            var read = stream.Read(buffer, filled, buffer.Length - filled);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return (buffer, filled);
    }

    /// <summary>A file in an encoding other than UTF-8, read as text the way it always was.</summary>
    private static List<SidecarEntry> ReadAsText(byte[] bytes, string process, string source)
    {
        var entries = new List<SidecarEntry>();
        using var reader = new StreamReader(new MemoryStream(bytes));
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            if (TryStamp(line, out var utc) && Parse(line, utc, process, source, entries.Count) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    public static string ProcessOf(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var process = name.StartsWith(TraceSidecar.FilePrefix, StringComparison.Ordinal) ? name[TraceSidecar.FilePrefix.Length..] : name;
        var dash = process.IndexOf('-');
        return dash < 0 ? process : process[..dash];
    }

    private static bool TryStamp(string line, out DateTime utc)
    {
        utc = default;
        return line.Length > Prefix.Length + StampLength
            && line.StartsWith(Prefix, StringComparison.Ordinal)
            && DateTime.TryParseExact(line.AsSpan(Prefix.Length, StampLength), "yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
    }

    private static bool TryStamp(ReadOnlySpan<byte> line, out DateTime utc)
    {
        utc = default;
        if (line.Length <= Prefix.Length + StampLength || !line.StartsWith("{\"t\":\""u8))
        {
            return false;
        }

        var bytes = line.Slice(Prefix.Length, StampLength);
        if (TryStampDigits(bytes, out utc))
        {
            return true;
        }

        Span<char> stamp = stackalloc char[StampLength];
        for (var i = 0; i < StampLength; i++)
        {
            if (bytes[i] >= 0x80)
            {
                return false;
            }

            stamp[i] = (char)bytes[i];
        }

        return DateTime.TryParseExact(stamp, "yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
    }

    /// <summary>The common case of the stamp, read digit by digit; false sends anything unusual to the full parser.</summary>
    private static bool TryStampDigits(ReadOnlySpan<byte> s, out DateTime utc)
    {
        utc = default;
        if (s[4] != '-' || s[7] != '-' || s[10] != 'T' || s[13] != ':' || s[16] != ':' || s[19] != '.' || s[27] != 'Z')
        {
            return false;
        }

        if (!Digits(s[..4], out var year) || !Digits(s.Slice(5, 2), out var month) || !Digits(s.Slice(8, 2), out var day)
            || !Digits(s.Slice(11, 2), out var hour) || !Digits(s.Slice(14, 2), out var minute) || !Digits(s.Slice(17, 2), out var second)
            || !Digits(s.Slice(20, 7), out var fraction))
        {
            return false;
        }

        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        utc = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).AddTicks(fraction);
        return true;
    }

    private static bool Digits(ReadOnlySpan<byte> s, out int value)
    {
        value = 0;
        foreach (var b in s)
        {
            if (b is < (byte)'0' or > (byte)'9')
            {
                return false;
            }

            value = value * 10 + (b - '0');
        }

        return true;
    }

    /// <summary>
    /// The fields every query filters on, read off the line as it streams by, the last of a repeated key winning as
    /// <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/> has it; the line is parsed whole only when an
    /// entry's <see cref="SidecarEntry.Json"/> is asked for. Null for a line that is not one JSON object.
    /// </summary>
    private static SidecarEntry ParseUtf8(ReadOnlyMemory<byte> line, DateTime utc, string process, string source, int index)
    {
        string kind = null, client = null, game = null, level = null;
        try
        {
            var reader = new Utf8JsonReader(line.Span);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return Parse(System.Text.Encoding.UTF8.GetString(line.Span), utc, process, source, index);
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var field = reader.ValueTextEquals("k"u8) ? 1
                    : reader.ValueTextEquals("c"u8) ? 2
                    : reader.ValueTextEquals("g"u8) ? 3
                    : reader.ValueTextEquals("lvl"u8) ? 4
                    : 0;
                reader.Read();
                if (field == 0)
                {
                    reader.Skip();
                    continue;
                }

                var value = reader.TokenType == JsonTokenType.String ? Shared(ref reader) : null;
                reader.Skip();
                switch (field)
                {
                    case 1: kind = value; break;
                    case 2: client = value; break;
                    case 3: game = value; break;
                    default: level = value; break;
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
            {
                return null;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }

        return new SidecarEntry
        {
            Utc = utc,
            Kind = kind,
            Client = client,
            Game = game,
            Level = level,
            Process = process,
            SourceName = source,
            LineIndex = index,
            Line = line,
            TextsPending = true,
        };
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Strings = new(StringComparer.Ordinal);

    /// <summary>A short string value as one shared instance - kinds, levels, clients and game names repeat on every line.</summary>
    private static string Shared(ref Utf8JsonReader reader)
    {
        if (reader.ValueIsEscaped || reader.HasValueSequence || reader.ValueSpan.Length > 64)
        {
            return reader.GetString();
        }

        var bytes = reader.ValueSpan;
        Span<char> chars = stackalloc char[64];
        var count = System.Text.Encoding.UTF8.GetChars(bytes, chars);
        var lookup = Strings.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(chars[..count], out var known))
        {
            return known;
        }

        var value = new string(chars[..count]);
        return Strings.GetOrAdd(value, value);
    }

    private static SidecarEntry Parse(string line, DateTime utc, string process, string source = null, int index = 0)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }

        // Not disposed: the entry keeps the element, and the pooled buffer goes back to the GC with it.
        {
            var root = document.RootElement;
            string Get(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return new SidecarEntry
            {
                Utc = utc,
                Kind = Get("k"),
                Client = Get("c"),
                Game = Get("g"),
                Level = Get("lvl"),
                Message = Get("msg"),
                Exception = Get("ex"),
                Process = process,
                SourceName = source,
                LineIndex = index,
                Json = root,
            };
        }
    }
}

/// <summary>
/// Which client of a game a log line is about. No log call uses a scope, so it is by text: a line
/// belongs to every client whose label it names, in its message or a property; a line naming
/// "Instance N" belongs to the client that game server instance carried (from the realm traces). A
/// line naming no client of the game belongs to none and is shown for all of them as unattributed.
/// </summary>
public sealed class Attribution
{
    private readonly IReadOnlyList<string> _labels;
    private readonly IReadOnlyDictionary<(string Process, int Instance), string> _instances;

    public Attribution(IReadOnlyList<string> labels, IReadOnlyDictionary<(string Process, int Instance), string> instances = null)
    {
        _labels = labels;
        _instances = instances ?? new Dictionary<(string, int), string>();
    }

    public List<string> ClientsOf(SidecarEntry entry)
    {
        if (!entry.IsLog)
        {
            return entry.Client != null && _labels.Contains(entry.Client) ? [entry.Client] : [];
        }

        var found = new List<string>();
        foreach (var text in entry.Texts())
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            foreach (var label in _labels)
            {
                if (!found.Contains(label) && Names(text, label))
                {
                    found.Add(label);
                }
            }

            if (_instances.Count > 0 && InstanceOf(text) is { } instance
                && _instances.TryGetValue((entry.Process, instance), out var byInstance)
                && _labels.Contains(byInstance) && !found.Contains(byInstance))
            {
                found.Add(byInstance);
            }
        }

        return found;
    }

    private static bool Names(string text, string label)
    {
        var at = 0;
        while ((at = text.IndexOf(label, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            var end = at + label.Length;
            var after = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (before && after)
            {
                return true;
            }

            at = end;
        }

        return false;
    }

    private static int? InstanceOf(string text)
    {
        var at = text.IndexOf("Instance", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var position = at + "Instance".Length;
        while (position < text.Length && text[position] == ' ')
        {
            position++;
        }

        var start = position;
        while (position < text.Length && char.IsDigit(text[position]))
        {
            position++;
        }

        return position > start && int.TryParse(text.AsSpan(start, position - start), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}
