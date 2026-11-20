using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Quest;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

public sealed class TraceCompareOptions
{
    public TimeSpan FromA { get; init; } = TimeSpan.Zero;
    public TimeSpan ToA { get; init; } = TimeSpan.MaxValue;
    public TimeSpan FromB { get; init; } = TimeSpan.Zero;
    public TimeSpan ToB { get; init; } = TimeSpan.MaxValue;

    /// <summary>Only these sent ids take part; empty means all but the noise.</summary>
    public IReadOnlySet<byte> Types { get; init; } = new HashSet<byte>();

    /// <summary>Compare only what was sent, not what the server answered to the steps both sides took.</summary>
    public bool SentOnly { get; init; }

    /// <summary>Two target points this close count as the same.</summary>
    public double Tolerance { get; init; } = 10;

    public TimeSpan ReplyWindow { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Leave out sends an import gave to the client without evidence.</summary>
    public bool EvidenceOnly { get; init; }
}

/// <summary>
/// A sent packet as the comparison sees it: the key it is matched by, and what it reads as. <paramref name="Raw"/> is
/// the packet's own unit or item ids, which the key replaces by what the unit is; <paramref name="Guessed"/> marks a
/// send an import gave to this client without evidence.
/// </summary>
public sealed record CompareStep(TimeSpan Elapsed, string Key, Point Target, string Text, int Collapsed, long Sequence, string Raw = null, bool Guessed = false);

public sealed record ComparePacket(TimeSpan Elapsed, bool Sent, string Text);

/// <summary>One place the two sides part: steps only one side took, or the same step answered differently.</summary>
public sealed record Divergence(
    string Kind,
    IReadOnlyList<CompareStep> Before,
    IReadOnlyList<CompareStep> OnlyA,
    IReadOnlyList<CompareStep> OnlyB,
    IReadOnlyList<ComparePacket> AroundA,
    IReadOnlyList<ComparePacket> AroundB);

public sealed class CompareResult
{
    public string LabelA { get; init; }
    public string LabelB { get; init; }
    public int StepsA { get; init; }
    public int StepsB { get; init; }
    public int Matched { get; init; }

    /// <summary>Matched steps whose unit or item ids differ: they matched only because the units are the same kind (same NPC code, object code or item name).</summary>
    public int MatchedByKind { get; init; }

    /// <summary>Matched steps naming a unit a side's game never saw, which matches any unseen unit of that type.</summary>
    public int MatchedUnseen { get; init; }

    /// <summary>Sends left out because they were attributed without evidence (with <see cref="TraceCompareOptions.EvidenceOnly"/>), per side.</summary>
    public int LeftOutA { get; init; }

    public int LeftOutB { get; init; }

    /// <summary>Steps that are sends attributed without evidence, per side; marked ~ in the output.</summary>
    public int GuessedA { get; init; }

    public int GuessedB { get; init; }

    /// <summary>Whether the steps were aligned in windows rather than as a whole: true for long captures.</summary>
    public bool Windowed { get; init; }

    public List<Divergence> Divergences { get; } = [];

    /// <summary>Character quest words that differ at the start of the windows: (quest, A, B).</summary>
    public List<(string Quest, string A, string B)> QuestsAtStart { get; } = [];
}

/// <summary>
/// Lines up what two clients sent - a real client and the bot doing the same thing - and finds where they
/// part. Normalised away: pings (0x6A), timing, unit and item ids (a unit is matched by what it is: its NPC
/// or object code, an item by its name, a player as "player"), and runs of consecutive moves, which count as
/// one move to where the last one went and match within <see cref="TraceCompareOptions.Tolerance"/>. Steps both
/// sides took are also compared on the server's answer: the reply packets that follow within the reply
/// window, by type and, for NPC offers, quest words and items, by content.
/// </summary>
public static class TraceCompare
{
    private const int ContextSteps = 2;
    private const byte Ping = 0x6A;

    private static readonly HashSet<byte> MoveTypes = [0x01, 0x03, 0x5F];

    /// <summary>Received ids that are the world going on rather than an answer: motion, map, sounds, effects, life.</summary>
    private static readonly HashSet<byte> NotReplies =
    [
        0x07, 0x08, 0x0A, 0x0C, 0x0D, 0x0F, 0x10, 0x12, 0x13, 0x14, 0x15, 0x18, 0x1A, 0x1B, 0x1C, 0x2C, 0x4C, 0x4D, 0x51, 0x55, 0x56,
        0x59, 0x5A, 0x67, 0x68, 0x69, 0x6B, 0x6C, 0x6D, 0x76, 0x7F, 0x8A, 0x8F, 0x90, 0x95, 0x96, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB,
    ];

    public static CompareResult Compare(string fileA, string fileB, TraceCompareOptions options)
    {
        var a = Collect(fileA, options.FromA, options.ToA, options);
        var b = Collect(fileB, options.FromB, options.ToB, options);
        var pairs = Align(a.Steps, b.Steps, options.Tolerance, out var windowed);
        var matched = pairs.Where(p => p.A >= 0 && p.B >= 0).Select(p => (A: a.Steps[p.A], B: b.Steps[p.B])).ToList();
        var result = new CompareResult
        {
            LabelA = a.Label,
            LabelB = b.Label,
            StepsA = a.Steps.Count,
            StepsB = b.Steps.Count,
            Matched = matched.Count,
            MatchedUnseen = matched.Count(m => m.A.Key.Contains("unseen", StringComparison.Ordinal)),
            MatchedByKind = matched.Count(m => !m.A.Key.Contains("unseen", StringComparison.Ordinal) && m.A.Raw != null && m.A.Raw != m.B.Raw),
            LeftOutA = a.LeftOut,
            LeftOutB = b.LeftOut,
            GuessedA = a.Steps.Count(s => s.Guessed),
            GuessedB = b.Steps.Count(s => s.Guessed),
            Windowed = windowed,
        };
        foreach (var (quest, valueA, valueB) in QuestDifferences(a.QuestsAtStart, b.QuestsAtStart))
        {
            result.QuestsAtStart.Add((quest, valueA, valueB));
        }

        var common = new List<CompareStep>();
        var onlyA = new List<CompareStep>();
        var onlyB = new List<CompareStep>();
        void Flush()
        {
            if (onlyA.Count + onlyB.Count == 0)
            {
                return;
            }

            result.Divergences.Add(new Divergence(
                onlyA.Count > 0 && onlyB.Count > 0 ? "different steps" : onlyA.Count > 0 ? $"only {a.Label}" : $"only {b.Label}",
                [.. common.TakeLast(ContextSteps)],
                [.. onlyA],
                [.. onlyB],
                Around(a, onlyA, options.ReplyWindow),
                Around(b, onlyB, options.ReplyWindow)));
            onlyA.Clear();
            onlyB.Clear();
        }

        foreach (var (ia, ib) in pairs)
        {
            if (ia >= 0 && ib >= 0)
            {
                Flush();
                var stepA = a.Steps[ia];
                var stepB = b.Steps[ib];
                if (!options.SentOnly && stepA.Target == null)
                {
                    var repliesA = Replies(a, stepA, ia, options.ReplyWindow);
                    var repliesB = Replies(b, stepB, ib, options.ReplyWindow);
                    if (!repliesA.Select(r => r.Key).SequenceEqual(repliesB.Select(r => r.Key)))
                    {
                        result.Divergences.Add(new Divergence(
                            "same step, different answer",
                            [.. common.TakeLast(ContextSteps), stepA],
                            [],
                            [],
                            [new ComparePacket(stepA.Elapsed, true, stepA.Text), .. repliesA.Select(r => new ComparePacket(r.Elapsed, false, r.Text))],
                            [new ComparePacket(stepB.Elapsed, true, stepB.Text), .. repliesB.Select(r => new ComparePacket(r.Elapsed, false, r.Text))]));
                    }
                }

                common.Add(stepA);
            }
            else if (ia >= 0)
            {
                onlyA.Add(a.Steps[ia]);
            }
            else
            {
                onlyB.Add(b.Steps[ib]);
            }
        }

        Flush();
        return result;
    }

    private sealed class Side
    {
        public string Label;
        public List<CompareStep> Steps = [];
        public List<(TimeSpan Elapsed, string Key, string Text, long Sequence)> Received = [];
        public ushort[] QuestsAtStart;
        public int LeftOut;
    }

    private static Side Collect(string file, TimeSpan from, TimeSpan to, TraceCompareOptions options)
    {
        using var replayer = new Replayer(file);
        var side = new Side { Label = replayer.Header.ClientLabel };
        var game = replayer.Game;
        long sequence = 0;
        var guessed = false;
        replayer.Applying = record =>
        {
            sequence++;
            if (record.Kind == TraceRecordKind.SentWithoutEvidence)
            {
                guessed = true;
                return;
            }

            var thisGuessed = guessed && record.Kind == TraceRecordKind.D2gsSent;
            if (record.Kind == TraceRecordKind.D2gsSent)
            {
                guessed = false;
            }

            if (record.Elapsed < from || record.Elapsed > to || record.Payload.Length == 0)
            {
                return;
            }

            side.QuestsAtStart ??= Words(game);
            if (record.Kind == TraceRecordKind.D2gsSent)
            {
                var type = record.Payload[0];
                if (type == Ping || options.Types.Count > 0 && !options.Types.Contains(type))
                {
                    return;
                }

                if (thisGuessed && options.EvidenceOnly)
                {
                    side.LeftOut++;
                    return;
                }

                var step = Step(game, record, sequence) is var made && thisGuessed ? made with { Guessed = true, Text = "~ " + made.Text } : made;
                if (step.Target != null && MoveTypes.Contains(type) && side.Steps.Count > 0 && side.Steps[^1] is { Target: not null } last && MoveTypes.Contains(KeyType(last.Key)))
                {
                    side.Steps[^1] = step with { Collapsed = last.Collapsed + 1, Text = $"{step.Text} (last of {last.Collapsed + 1} moves)", Guessed = step.Guessed || last.Guessed };
                    return;
                }

                side.Steps.Add(step);
            }
            else if (record.Kind == TraceRecordKind.D2gsReceived && !NotReplies.Contains(record.Payload[0]))
            {
                side.Received.Add((record.Elapsed, ReplyKey(record.Payload), $"{PacketDecoder.Name(record.Kind, record.Payload),-22} {PacketDecoder.Fields(record.Kind, record.Payload)}", sequence));
            }
        };
        replayer.ReplayTo(to);
        side.QuestsAtStart ??= Words(game);
        return side;
    }

    private static byte KeyType(string key) => Convert.ToByte(key[..2], 16);

    private static CompareStep Step(Game game, TraceRecord record, long sequence)
    {
        var p = record.Payload;
        var type = p[0];
        var name = Names.Outgoing(type);
        var text = $"{name,-22} {PacketDecoder.Fields(record.Kind, p)}";
        string key = null;
        string raw = null;
        Point target = null;
        switch (type)
        {
            case 0x01 or 0x03 or 0x05 or 0x08 or 0x0C or 0x0F or 0x5F when p.Length >= 5:
                target = new Point(PacketDecoder.U16(p, 1), PacketDecoder.U16(p, 3));
                key = $"{type:X2} {name}";
                break;
            case 0x02 or 0x04 or 0x06 or 0x07 or 0x09 or 0x0A or 0x0D or 0x0E or 0x10 or 0x11 or 0x13 or 0x2F or 0x30 or 0x4B when p.Length >= 9:
                var unit = UnitName(game, (EntityType)PacketDecoder.U32(p, 1), PacketDecoder.U32(p, 5));
                key = $"{type:X2} {name} {unit}";
                raw = $"{PacketDecoder.U32(p, 1)}:{PacketDecoder.U32(p, 5)}";
                text += $" ({unit})";
                break;
            case 0x31 when p.Length >= 9:
                var npc = UnitName(game, EntityType.NPC, PacketDecoder.U32(p, 1));
                key = $"{type:X2} {name} {npc} 0x{PacketDecoder.U32(p, 5):X}";
                raw = $"{PacketDecoder.U32(p, 1)}";
                text += $" ({npc})";
                break;
            case 0x38 when p.Length >= 13:
                var actor = UnitName(game, EntityType.NPC, PacketDecoder.U32(p, 5));
                key = $"{type:X2} {name} {PacketDecoder.U32(p, 1)} {actor} {PacketDecoder.U32(p, 9)}";
                raw = $"{PacketDecoder.U32(p, 5)}";
                text += $" ({actor})";
                break;
            case 0x3C when p.Length >= 5:
                key = $"{type:X2} {name} {PacketDecoder.Fields(record.Kind, p)}";
                break;
            case 0x59 when p.Length >= 9:
                var moved = UnitName(game, EntityType.NPC, PacketDecoder.U32(p, 5));
                key = $"{type:X2} {name} {moved}";
                raw = $"{PacketDecoder.U32(p, 5)}";
                text += $" ({moved})";
                break;
        }

        if (key == null && ItemOffset(type) is var offset and > 0 && p.Length >= offset + 4)
        {
            var item = ItemName(game, PacketDecoder.U32(p, offset));
            key = $"{type:X2} {name} {item}";
            raw = $"{PacketDecoder.U32(p, offset)}";
            text += $" ({item})";
        }

        return new CompareStep(record.Elapsed, key ?? $"{type:X2} {name}", target, text, 1, sequence, raw);
    }

    private static int ItemOffset(byte type) => type switch
    {
        0x16 or 0x32 or 0x33 => 5,
        0x17 or 0x18 or 0x19 or 0x1A or 0x1D or 0x1F or 0x20 or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x29 or 0x2A or 0x37 or 0x63 => 1,
        _ => 0,
    };

    private static string UnitName(Game game, EntityType type, uint id)
    {
        try
        {
            if (type == EntityType.Player)
            {
                return game.Me?.Id == id ? "self" : "player";
            }

            if (type == EntityType.Item)
            {
                return ItemName(game, id);
            }

            return game.WorldObjects.TryGetValue((id, type), out var unit)
                ? type == EntityType.NPC ? Names.Npc(unit.NPCCode) : unit.Code.ToString()
                : $"unseen {type}";
        }
        catch (NullReferenceException)
        {
            return $"unseen {type}";
        }
    }

    private static string ItemName(Game game, uint id)
    {
        try
        {
            return game.Items.TryGetValue(id, out var item) ? item.Name.ToString() : game.CursorItem?.Id == id ? game.CursorItem.Name.ToString() : "unseen item";
        }
        catch (NullReferenceException)
        {
            return "unseen item";
        }
    }

    /// <summary>What an answer is matched by: its type, and for offers, quest state and items, what they say.</summary>
    private static string ReplyKey(byte[] payload)
    {
        var type = payload[0];
        return type switch
        {
            0x27 or 0x28 or 0x29 or 0x9C or 0x9D or 0x2A or 0x5D => $"{type:X2} {PacketDecoder.Fields(TraceRecordKind.D2gsReceived, payload)}",
            _ => $"{type:X2}",
        };
    }

    /// <summary>What the server sent after a step and before the next one, within the reply window.</summary>
    private static List<(TimeSpan Elapsed, string Key, string Text, long Sequence)> Replies(Side side, CompareStep step, int index, TimeSpan window)
    {
        var end = step.Elapsed + window;
        var next = index + 1 < side.Steps.Count ? side.Steps[index + 1].Sequence : long.MaxValue;
        return side.Received.Where(r => r.Sequence > step.Sequence && r.Sequence < next && r.Elapsed <= end).ToList();
    }

    /// <summary>Each step one side alone took, with what the server answered before that side's next step.</summary>
    private static List<ComparePacket> Around(Side side, List<CompareStep> steps, TimeSpan window)
    {
        var packets = new List<ComparePacket>();
        foreach (var step in steps)
        {
            packets.Add(new ComparePacket(step.Elapsed, true, step.Text));
            packets.AddRange(Replies(side, step, side.Steps.IndexOf(step), window).Select(r => new ComparePacket(r.Elapsed, false, r.Text)));
        }

        return packets;
    }

    private static ushort[] Words(Game game)
    {
        try
        {
            return [.. game.Quests.CharacterWords];
        }
        catch (NullReferenceException)
        {
            return [];
        }
    }

    private static IEnumerable<(string, string, string)> QuestDifferences(ushort[] a, ushort[] b)
    {
        for (var word = 0; word < Math.Max(a.Length, b.Length); word++)
        {
            var valueA = word < a.Length ? a[word] : (ushort)0;
            var valueB = word < b.Length ? b[word] : (ushort)0;
            if (valueA != valueB)
            {
                yield return (Enum.IsDefined(typeof(QuestId), word) ? ((QuestId)word).ToString() : $"word{word}", $"0x{valueA:X4}", $"0x{valueB:X4}");
            }
        }
    }

    /// <summary>Steps per side aligned at once: the table of a window holds (Window + 1)² ints, 16 MB.</summary>
    public const int Window = 2000;

    /// <summary>
    /// Longest common subsequence of the two step lists, as index pairs (-1 where a side has no partner). Up to
    /// <see cref="Window"/> steps a side it is exact. Longer lists are aligned window by window: the next
    /// <see cref="Window"/> steps of each side are aligned exactly, the first half of that alignment is kept and the
    /// next window starts where it ends. Memory stays at one window's table; what is lost is a match that needs one
    /// side to skip more than half a window (1,000 steps) at once, which the windowed alignment cannot see.
    /// </summary>
    internal static List<(int A, int B)> Align(List<CompareStep> a, List<CompareStep> b, double tolerance, out bool windowed)
    {
        var n = a.Count;
        var m = b.Count;
        windowed = n > Window || m > Window;
        var lengths = new int[Math.Min(n, Window) + 1, Math.Min(m, Window) + 1];
        var pairs = new List<(int, int)>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            var rows = Math.Min(n - x, Window);
            var columns = Math.Min(m - y, Window);
            var last = x + rows == n && y + columns == m;
            for (var i = 0; i <= rows; i++)
            {
                lengths[i, columns] = 0;
            }

            for (var j = 0; j <= columns; j++)
            {
                lengths[rows, j] = 0;
            }

            for (var i = rows - 1; i >= 0; i--)
            {
                for (var j = columns - 1; j >= 0; j--)
                {
                    lengths[i, j] = Same(a[x + i], b[y + j], tolerance) ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
                }
            }

            int i0 = 0, j0 = 0;
            while (i0 < rows && j0 < columns && (last || i0 < Math.Max(1, rows / 2) && j0 < Math.Max(1, columns / 2)))
            {
                if (Same(a[x + i0], b[y + j0], tolerance))
                {
                    pairs.Add((x + i0++, y + j0++));
                }
                else if (lengths[i0 + 1, j0] >= lengths[i0, j0 + 1])
                {
                    pairs.Add((x + i0++, -1));
                }
                else
                {
                    pairs.Add((-1, y + j0++));
                }
            }

            x += i0;
            y += j0;
        }

        while (x < n)
        {
            pairs.Add((x++, -1));
        }

        while (y < m)
        {
            pairs.Add((-1, y++));
        }

        return pairs;
    }

    private static bool Same(CompareStep a, CompareStep b, double tolerance)
        => a.Key == b.Key && (a.Target == null || b.Target == null || a.Target.Distance(b.Target) <= tolerance);
}
