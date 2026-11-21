using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.MCP;
using D2NG.Core.Tracing;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace D2NG.Trace;

/// <summary>
/// <paramref name="AfterGap"/>: records were dropped earlier in the file, so the replayed state was
/// already unreliable. Without a gap a mismatch is a replay fidelity problem.
/// </summary>
public sealed record DigestMismatch(TimeSpan Elapsed, string Field, string Expected, string Actual, bool AfterGap, IReadOnlyList<string> Preceding);

public sealed record HandlerFailure(TimeSpan Elapsed, byte PacketType, bool Sent, string Error);

public sealed class ReplayResult
{
    public long PacketsReplayed { get; internal set; }
    public long ClientActions { get; internal set; }
    public int Digests { get; internal set; }
    public int MismatchedDigests { get; internal set; }
    public int MismatchedDigestsAfterGap { get; internal set; }
    public List<DigestMismatch> FirstMismatches { get; } = [];
    public List<DigestMismatch> FirstMismatchesAfterGap { get; } = [];
    public int HandlerFailures { get; internal set; }
    public List<HandlerFailure> FirstHandlerFailures { get; } = [];
    public int Gaps { get; internal set; }
    public long DroppedRecords { get; internal set; }
    public bool Truncated { get; internal set; }
    public TimeSpan ReachedElapsed { get; internal set; }

    /// <summary>Received ids the bot has no name for; their handlers cannot exist.</summary>
    public Dictionary<byte, long> UnknownReceived { get; } = [];

    /// <summary>Received ids the bot knows but registers no handler for: they change nothing in a replay.</summary>
    public Dictionary<byte, long> UnhandledReceived { get; } = [];

    /// <summary>Sent ids the bot has no name for: a real client sends some the bot never does.</summary>
    public Dictionary<byte, long> UnknownSent { get; } = [];

    /// <summary>For a capture: sent packets whose size the client table does not know, so the capture decided their length.</summary>
    public Dictionary<byte, long> UnsizedSent { get; } = [];

    /// <summary>Client action ids this build does not know, from a trace a newer bot wrote; each counts one handler failure, not one per record.</summary>
    public Dictionary<byte, long> UnknownClientActions { get; } = [];

    /// <summary>For a sniffer trace: the client actions derived from what the client sent, by kind.</summary>
    public Dictionary<ClientAction, long> DerivedClientActions { get; internal set; } = [];

    /// <summary>What the replay had to assume, worded for a reader: a character guessed, game flags made up.</summary>
    public List<string> Notes { get; } = [];
}

/// <summary>
/// Feeds a client's recorded game server packets and client actions, in file order, through a real
/// <see cref="Game"/> that has no connection. Each call continues where the previous one stopped.
/// </summary>
public sealed class Replayer : IDisposable
{
    private const int KeptExamples = 10;
    private const int PrecedingShown = 5;
    private const byte GameLogon = (byte)OutGoingPacket.GameLogon;
    private const int GameLogonNameOffset = 13;

    private readonly TraceReader _reader;
    private readonly IEnumerator<TraceRecord> _records;
    private readonly GameServer _gameServer = new();
    private readonly (TraceRecordKind Kind, byte First, bool HasFirst)[] _recent = new (TraceRecordKind, byte, bool)[PrecedingShown];
    private int _recentCount;
    private TraceRecord? _pending;
    private bool _finished;
    private bool _gapSeen;

    /// <param name="rules">Null: the header's rules, or <see cref="ReplayRules.Override"/>.</param>
    public Replayer(string path, ClientRules rules = null) : this(new TraceReader(path), rules)
    {
    }

    public Replayer(TraceReader reader, ClientRules rules = null)
    {
        _reader = reader;
        _records = reader.Records().GetEnumerator();
        Rules = rules ?? ReplayRules.For(reader.Header, reader.Path);
        using (Profile.Time("replay: new Game"))
        {
            Game = new Game(_gameServer, Clock, Rules);
        }

        if (ReplayRules.NewerThanKnown(reader.Header))
        {
            Result.Notes.Add(ReplayRules.NewerRulesNote(reader.Header.RulesVersion.Value));
        }

        if (reader.Header.Source == TraceSource.Sniffer)
        {
            _derived = new SnifferClientActions(Game);
            Result.DerivedClientActions = _derived.Derived;
            PrepareCapturedStart();
        }
    }

    private const int StartSearch = 2000;
    private const byte GameFlags = 0x01;
    private const byte AssignPlayer = 0x59;
    private const int AssignNameOffset = 6;

    private readonly SnifferClientActions _derived;
    private readonly Queue<TraceRecord> _lookahead = new();

    /// <summary>
    /// A capture can start after the logon, so no character is selected, and after the game flags, so the game has no
    /// state to fill. The first player assign stands in for the logon - the server assigns the joining player first,
    /// but mid-game it can be anyone's - and the flags are made up as Normal; both are noted in the result.
    /// </summary>
    private void PrepareCapturedStart()
    {
        TraceRecord? firstAssign = null;
        var logonFirst = false;
        var flagsSeen = false;
        while (_lookahead.Count < StartSearch && _records.MoveNext())
        {
            var record = _records.Current;
            _lookahead.Enqueue(record);
            if (flagsSeen && firstAssign != null)
            {
                break;
            }

            if (record.Payload.Length == 0)
            {
                continue;
            }

            if (record.Kind == TraceRecordKind.D2gsSent && record.Payload[0] == GameLogon && !flagsSeen && firstAssign == null)
            {
                logonFirst = true;
                break;
            }

            if (record.Kind == TraceRecordKind.D2gsReceived && record.Payload[0] == GameFlags)
            {
                flagsSeen = true;
            }
            else if (record.Kind == TraceRecordKind.D2gsReceived && record.Payload[0] == AssignPlayer && record.Payload.Length > AssignNameOffset && firstAssign == null)
            {
                firstAssign = record;
            }
        }

        if (logonFirst)
        {
            return;
        }

        if (firstAssign is { } assign)
        {
            var payload = assign.Payload;
            var end = Array.IndexOf(payload, (byte)0, AssignNameOffset);
            var name = Encoding.ASCII.GetString(payload, AssignNameOffset, (end < 0 ? payload.Length : end) - AssignNameOffset);
            var stats = new byte[32];
            stats[13] = (byte)(payload[5] + 1);
            Game.SelectCharacter(new Character(name, stats));
            Result.Notes.Add($"no logon in this capture: the character is taken from the first player assign at {assign.Elapsed.TotalSeconds:F1}s, which mid-game may be another player");
        }
        else
        {
            Game.SelectCharacter(new Character("unknown", new byte[32]));
            Result.Notes.Add("no logon and no player assign in this capture: the character is unknown and the client's own player is never set");
        }

        if (!flagsSeen)
        {
            _syntheticFlags = true;
            Result.Notes.Add("the game flags were sent before the capture started: Normal, softcore, classic assumed");
        }
    }

    private bool _syntheticFlags;

    /// <summary>Record time: monster motion is timed by when each packet was recorded, not by how fast the replay runs.</summary>
    public ReplayClock Clock { get; } = new();

    public TraceHeader Header => _reader.Header;

    /// <summary>The client rules <see cref="Game"/> interprets the recorded packets under.</summary>
    public ClientRules Rules { get; }

    public Game Game { get; }

    public ReplayResult Result { get; } = new();

    /// <summary>Called with each record before <see cref="Game"/> applies it, so an observer can note the state it changes.</summary>
    public Action<TraceRecord> Applying { get; set; }

    /// <summary>Called with each record after <see cref="Game"/> applied it.</summary>
    public Action<TraceRecord> Applied { get; set; }

    /// <summary>
    /// Builds a throwaway game with no packets, so the first real replay does not wait for the game's code to be
    /// compiled; meant to run on a spare thread while the trace files are being opened.
    /// </summary>
    public static void WarmUp()
    {
        try
        {
            using var server = new GameServer();
            _ = new Game(server, new ReplayClock());
        }
        catch (Exception)
        {
        }
    }

    public ReplayResult ReplayToEnd() => ReplayTo(TimeSpan.MaxValue);

    public ReplayResult ReplayTo(TimeSpan until)
    {
        if (_syntheticFlags)
        {
            _syntheticFlags = false;
            _gameServer.Replay(new D2gsPacket([GameFlags, 0, 0, 0, 0, 0, 0, 0]), sent: false);
        }

        while (!_finished)
        {
            TraceRecord record;
            if (_pending is { } pending)
            {
                record = pending;
                _pending = null;
            }
            else if (_lookahead.TryDequeue(out var buffered))
            {
                record = buffered;
            }
            else if (_records.MoveNext())
            {
                record = _records.Current;
            }
            else
            {
                _finished = true;
                break;
            }

            if (record.Elapsed > until)
            {
                _pending = record;
                break;
            }

            Clock.Elapsed = record.Elapsed;
            _derived?.Reached(record.Elapsed);
            Applying?.Invoke(record);
            Apply(record);
            Applied?.Invoke(record);
            Remember(record);
            Result.ReachedElapsed = record.Elapsed;
        }

        if (_derived != null && until != TimeSpan.MaxValue && !_finished)
        {
            _derived.Reached(until);
        }

        Result.Truncated = _reader.Truncated;
        Result.Gaps = _reader.Gaps;
        Result.DroppedRecords = _reader.DroppedRecords;
        return Result;
    }

    private void Apply(TraceRecord record)
    {
        switch (record.Kind)
        {
            case TraceRecordKind.D2gsReceived:
                if (_derived != null)
                {
                    Derive(record, () => _derived.BeforeReceived(record.Payload));
                }

                Dispatch(record, sent: false);
                _derived?.Received(record.Payload);
                break;
            case TraceRecordKind.D2gsSent:
                if (record.Payload.Length > GameLogonNameOffset && record.Payload[0] == GameLogon)
                {
                    Game.SelectCharacter(CharacterFromLogon(record.Payload));
                }

                if (_derived == null)
                {
                    Dispatch(record, sent: true);
                    break;
                }

                Derive(record, () => _derived.BeforeSent(record.Payload));
                Dispatch(record, sent: true);
                Derive(record, () => _derived.AfterSent(record.Payload, record.Elapsed));
                break;
            case TraceRecordKind.ClientAction:
                ApplyClientAction(record);
                break;
            case TraceRecordKind.Digest:
                Verify(record);
                break;
            case TraceRecordKind.Gap:
                _gapSeen = true;
                break;
        }
    }

    private void Dispatch(TraceRecord record, bool sent)
    {
        if (record.Payload.Length == 0)
        {
            return;
        }

        Result.PacketsReplayed++;
        var type = record.Payload[0];
        var (known, handled) = _gameServer.Handling(type, sent);
        if (sent && _derived != null && Core.Capture.D2gsClientStream.Size(record.Payload) == Core.Capture.D2gsClientStream.Unknown)
        {
            Result.UnsizedSent[type] = Result.UnsizedSent.GetValueOrDefault(type) + 1;
        }

        if (!known)
        {
            var unknown = sent ? Result.UnknownSent : Result.UnknownReceived;
            unknown[type] = unknown.GetValueOrDefault(type) + 1;
        }
        else if (!handled && !sent)
        {
            Result.UnhandledReceived[type] = Result.UnhandledReceived.GetValueOrDefault(type) + 1;
        }

        try
        {
            _gameServer.Replay(new D2gsPacket(record.Payload), sent);
        }
        catch (Exception e)
        {
            Fail(record, sent, e);
        }
    }

    private void Derive(TraceRecord record, Action derive)
    {
        try
        {
            derive();
        }
        catch (Exception e)
        {
            Fail(record, sent: true, e);
        }
    }

    private void ApplyClientAction(TraceRecord record)
    {
        if (record.Payload.Length < 5)
        {
            return;
        }

        Result.ClientActions++;
        var action = (ClientAction)record.Payload[0];
        if (!Enum.IsDefined(action))
        {
            var seen = Result.UnknownClientActions.GetValueOrDefault(record.Payload[0]);
            Result.UnknownClientActions[record.Payload[0]] = seen + 1;
            if (seen == 0)
            {
                Result.Notes.Add(UnknownClientActionNote(record.Payload[0]));
                Fail(record, sent: true, $"{record.Kind} client action {record.Payload[0]} is unknown to this build: the trace was written by a newer version of the bot than this tool");
            }

            return;
        }

        try
        {
            Game.Apply(action, BinaryPrimitives.ReadUInt32LittleEndian(record.Payload.AsSpan(1)));
        }
        catch (Exception e)
        {
            Fail(record, sent: true, e);
        }
    }

    /// <summary>The one line a tool prints for each client action id a trace uses that this build does not know.</summary>
    public static string UnknownClientActionNote(byte action)
        => $"this trace uses client action {action}, which this build does not know: state after it may differ";

    private void Fail(TraceRecord record, bool sent, Exception e) => Fail(record, sent, $"{record.Kind} {e.GetType().Name}: {e.Message}");

    private void Fail(TraceRecord record, bool sent, string error)
    {
        Result.HandlerFailures++;
        if (Result.FirstHandlerFailures.Count < KeptExamples)
        {
            Result.FirstHandlerFailures.Add(new HandlerFailure(record.Elapsed, record.Payload[0], sent, error));
        }
    }

    private void Verify(TraceRecord record)
    {
        var expected = TraceDigest.FromBytes(record.Payload);
        if (expected == null)
        {
            return;
        }

        Result.Digests++;
        TraceDigest actual;
        try
        {
            actual = TraceDigest.Capture(Game);
        }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException)
        {
            actual = null;
        }

        var examples = _gapSeen ? Result.FirstMismatchesAfterGap : Result.FirstMismatches;
        var mismatched = false;
        IReadOnlyList<string> preceding = null;
        foreach (var (field, expectedValue, actualValue) in expected.Differences(actual))
        {
            mismatched = true;
            if (examples.Count < KeptExamples)
            {
                preceding ??= Preceding();
                examples.Add(new DigestMismatch(record.Elapsed, field, expectedValue, actualValue, _gapSeen, preceding));
            }
        }

        if (mismatched && _gapSeen)
        {
            Result.MismatchedDigestsAfterGap++;
        }
        else if (mismatched)
        {
            Result.MismatchedDigests++;
        }
    }

    private void Remember(TraceRecord record)
    {
        _recent[_recentCount % PrecedingShown] = (record.Kind, record.Payload.Length > 0 ? record.Payload[0] : (byte)0, record.Payload.Length > 0);
        _recentCount++;
    }

    private List<string> Preceding()
    {
        var shown = new List<string>();
        for (var i = Math.Max(0, _recentCount - PrecedingShown); i < _recentCount; i++)
        {
            var (kind, first, hasFirst) = _recent[i % PrecedingShown];
            shown.Add(kind switch
            {
                TraceRecordKind.ClientAction when hasFirst => $"{kind} {(ClientAction)first}",
                TraceRecordKind.D2gsReceived or TraceRecordKind.D2gsSent when hasFirst => $"{kind} 0x{first:X2}",
                _ => kind.ToString(),
            });
        }

        return shown;
    }

    /// <summary>The 0x68 logon names the character and its class, which is all the game state needs of it.</summary>
    internal static Character CharacterFromLogon(byte[] logon)
    {
        var end = Array.IndexOf(logon, (byte)0, GameLogonNameOffset);
        var name = Encoding.ASCII.GetString(logon, GameLogonNameOffset, (end < 0 ? logon.Length : end) - GameLogonNameOffset);
        var stats = new byte[32];
        stats[13] = (byte)(logon[7] + 1);
        return new Character(name, stats);
    }

    public void Dispose()
    {
        _records.Dispose();
        _reader.Dispose();
        _gameServer.Dispose();
    }
}

/// <summary>A clock that stands still between records; its timestamps are record ticks.</summary>
public sealed class ReplayClock : TimeProvider
{
    private long _ticks;

    public TimeSpan Elapsed
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));
        set => Interlocked.Exchange(ref _ticks, value.Ticks);
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
}
