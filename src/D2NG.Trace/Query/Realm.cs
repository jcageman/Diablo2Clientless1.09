using D2NG.Core.Tracing;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace D2NG.Trace.Query;

/// <summary>A game create or join the realm answered, from a process's realm trace.</summary>
public sealed record RealmAnswer(DateTime Utc, string Process, string Instance, string Character, string Request, string Game, uint Result)
{
    public bool Refused => Result != 0;

    public string Meaning => Request == "create" ? CreateMeaning(Result) : JoinMeaning(Result);

    public static string CreateMeaning(uint result) => result switch
    {
        0x00 => "ok",
        0x1E => "invalid game name",
        0x1F => "game name exists (d2cs NAME_EXIST)",
        0x20 => "game servers down",
        0x6E => "dead hardcore character",
        _ => "unknown reason",
    };

    public static string JoinMeaning(uint result) => result switch
    {
        0x00 => "ok",
        0x29 => "password incorrect",
        0x2A => "game does not exist (d2cs NOT_EXIST)",
        0x2B => "game full",
        0x2C => "level requirements not met",
        0x6E => "dead hardcore character",
        0x71 => "non-hardcore character in a hardcore game",
        0x73 => "cannot join a nightmare game",
        0x74 => "cannot join a hell game",
        0x78 => "non-expansion character in an expansion game",
        0x79 => "expansion character in a non-expansion game",
        0x7D => "non-ladder character in a ladder game",
        _ => "unknown reason",
    };
}

/// <summary>
/// The realm traces of a process: &lt;date&gt;/realm/instance&lt;N&gt;-&lt;HHmmss&gt;.d2trace, one per client.
/// Which character an instance carried comes from its character logon, and creates and joins are
/// paired with the game name of their request.
/// </summary>
public sealed class RealmTraces
{
    private const byte CreateGame = 0x03;
    private const byte JoinGame = 0x04;
    private const byte CharLogon = 0x07;
    private const int CreateNameOffset = 12;
    private const int JoinNameOffset = 5;
    private const int CreateResultOffset = 9;
    private const int JoinResultOffset = 17;

    public Dictionary<(string Process, int Instance), string> Characters { get; } = [];

    public List<RealmAnswer> Answers { get; } = [];

    /// <summary>By time, then process, instance, request and game: answers stamped alike come in one order whatever order the files were read in.</summary>
    internal static readonly Comparison<RealmAnswer> ByTime = (a, b) =>
    {
        var order = a.Utc.CompareTo(b.Utc);
        order = order != 0 ? order : string.CompareOrdinal(a.Process, b.Process);
        order = order != 0 ? order : string.CompareOrdinal(a.Instance, b.Instance);
        order = order != 0 ? order : string.CompareOrdinal(a.Request, b.Request);
        order = order != 0 ? order : string.CompareOrdinal(a.Game, b.Game);
        return order != 0 ? order : a.Result.CompareTo(b.Result);
    };

    public static RealmTraces Read(IEnumerable<string> files)
    {
        var realm = new RealmTraces();
        foreach (var file in files)
        {
            try
            {
                realm.ReadFile(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        realm.Answers.Sort(ByTime);
        return realm;
    }

    private void ReadFile(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var dash = name.IndexOf('-');
        var instanceLabel = dash < 0 ? name : name[..dash];
        var process = dash < 0 ? "" : name[(dash + 1)..].Split('-')[0];
        int? instance = instanceLabel.StartsWith("instance", StringComparison.Ordinal) && int.TryParse(instanceLabel["instance".Length..], out var number) ? number : null;
        using var reader = new TraceReader(file);
        string character = null;
        string createName = null;
        string joinName = null;
        foreach (var record in reader.Records())
        {
            var payload = record.Payload;
            if (payload.Length < 3)
            {
                continue;
            }

            var type = payload[2];
            var utc = reader.Header.StartUtc + record.Elapsed;
            switch (record.Kind)
            {
                case TraceRecordKind.McpSent when type == CharLogon:
                    character = Text(payload, 3);
                    if (instance != null)
                    {
                        Characters[(process, instance.Value)] = character;
                    }

                    break;
                case TraceRecordKind.McpSent when type == CreateGame:
                    createName = Text(payload, CreateNameOffset);
                    break;
                case TraceRecordKind.McpSent when type == JoinGame:
                    joinName = Text(payload, JoinNameOffset);
                    break;
                case TraceRecordKind.McpReceived when type == CreateGame && payload.Length >= CreateResultOffset + 4:
                    Answers.Add(new RealmAnswer(utc, process, instanceLabel, character, "create", createName, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(CreateResultOffset))));
                    break;
                case TraceRecordKind.McpReceived when type == JoinGame && payload.Length >= JoinResultOffset + 4:
                    Answers.Add(new RealmAnswer(utc, process, instanceLabel, character, "join", joinName, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(JoinResultOffset))));
                    break;
            }
        }
    }

    private static string Text(byte[] payload, int offset)
    {
        if (offset >= payload.Length)
        {
            return null;
        }

        var end = Array.IndexOf(payload, (byte)0, offset);
        return Encoding.ASCII.GetString(payload, offset, (end < 0 ? payload.Length : end) - offset);
    }

    public static RealmTraces Merge(IEnumerable<RealmTraces> parts)
    {
        var merged = new RealmTraces();
        foreach (var part in parts)
        {
            foreach (var (key, character) in part.Characters)
            {
                merged.Characters[key] = character;
            }

            merged.Answers.AddRange(part.Answers);
        }

        merged.Answers.Sort(ByTime);
        return merged;
    }

    public IEnumerable<RealmAnswer> AnswersFor(string game)
        => Answers.Where(a => string.Equals(a.Game, game, StringComparison.OrdinalIgnoreCase));
}
