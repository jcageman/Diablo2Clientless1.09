using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;
using Legacy = D2NG.Core.Tests.D2GS.Legacy;

namespace D2NG.Core.Tests.D2GS;

/// <summary>
/// Every rewritten parser against its pre-rewrite copy in <see cref="Legacy"/>: each packet of
/// data/packet-corpus.txt (real game server packets, none that carries a name or chat text), every
/// truncation of it, a longer copy and a copy with another type byte must give the same public
/// properties, or the same exception with the same message.
/// </summary>
public class PacketParserDifferentialTests
{
    private static readonly Lazy<List<byte[]>> Corpus = new(() =>
        [.. File.ReadLines(Path.Combine(AppContext.BaseDirectory, "data", "packet-corpus.txt"))
            .Where(l => l.Length > 0)
            .Select(Convert.FromHexString)]);

    private static readonly Dictionary<string, (byte[] Types, Func<D2gsPacket, object> New, Func<D2gsPacket, object> Old)> Parsers = new()
    {
        ["PlayerStop"] = ([0x0D], p => new PlayerStopPacket(p), p => new Legacy.PlayerStopPacket(p)),
        ["EntityMove"] = ([0x0F], p => new EntityMovePacket(p), p => new Legacy.EntityMovePacket(p)),
        ["RemoveObject"] = ([0x0A], p => new RemoveObjectPacket(p), p => new Legacy.RemoveObjectPacket(p)),
        ["AddEntityEffect"] = ([0x13], p => new AddEntityEffectPacket(p), p => new Legacy.AddEntityEffectPacket(p)),
        ["AddEntityEffect2"] = ([0x14], p => new AddEntityEffectPacket2(p), p => new Legacy.AddEntityEffectPacket2(p)),
        ["AssignObject"] = ([0x51], p => new AssignObjectPacket(p), p => new Legacy.AssignObjectPacket(p)),
        ["NpcHit"] = ([0x0C], p => new NpcHitPacket(p), p => new Legacy.NpcHitPacket(p)),
        ["NPCStop"] = ([0x6D], p => new NPCStopPacket(p), p => new Legacy.NPCStopPacket(p)),
        ["NPCMove"] = ([0x67], p => new NPCMovePacket(p), p => new Legacy.NPCMovePacket(p)),
        ["NpcState"] = ([0x69], p => new NpcStatePacket(p), p => new Legacy.NpcStatePacket(p)),
        ["NPCMoveToTarget"] = ([0x68], p => new NPCMoveToTargetPacket(p), p => new Legacy.NPCMoveToTargetPacket(p)),
        ["ReassignPlayer"] = ([0x15], p => new ReassignPlayerPacket(p), p => new Legacy.ReassignPlayerPacket(p)),
        ["MapReveal"] = ([0x07], p => new MapRevealPacket(p), p => new Legacy.MapRevealPacket(p)),
        ["AllyPartyInfo"] = ([0x7F], p => new AllyPartyInfoPacket(p), p => new Legacy.AllyPartyInfoPacket(p)),
        ["UpdateItemStats"] = ([0x3E], p => new UpdateItemStatsPacket(p), p => new Legacy.UpdateItemStatsPacket(p)),
        ["ObjectState"] = ([0x0E], p => new ObjectStatePacket(p), p => new Legacy.ObjectStatePacket(p)),
        ["PartyAutomapInfo"] = ([0x90], p => new PartyAutomapInfoPacket(p), p => new Legacy.PartyAutomapInfoPacket(p)),
        ["TownPortalState"] = ([0x60], p => new TownPortalStatePacket(p), p => new Legacy.TownPortalStatePacket(p)),
        ["AssignLevelWarp"] = ([0x09], p => new AssignLevelWarpPacket(p), p => new Legacy.AssignLevelWarpPacket(p)),
        ["UseStackableItem"] = ([0x3F], p => new UseStackableItemPacket(p), p => new Legacy.UseStackableItemPacket(p)),
        ["SetActiveSkill"] = ([0x23], p => new SetActiveSkillPacket(p), p => new Legacy.SetActiveSkillPacket(p)),
        ["SetItemSkill"] = ([0x21, 0x22], p => new SetItemSkillPacket(p), p => new Legacy.SetItemSkillPacket(p)),
        ["AddExp"] = ([0x1A, 0x1B, 0x1C], p => new AddExpPacket(p), p => new Legacy.AddExpPacket(p)),
        ["BaseAttribute"] = ([0x1D, 0x1E, 0x1F], p => new BaseAttributePacket(p), p => new Legacy.BaseAttributePacket(p)),
        ["QuestInfo"] = ([0x28], p => new QuestInfoPacket(p), p => new Legacy.QuestInfoPacket(p)),
        ["GameQuestInfo"] = ([0x29], p => new GameQuestInfoPacket(p), p => new Legacy.GameQuestInfoPacket(p)),
        ["NpcTransaction"] = ([0x2A], p => new NpcTransactionPacket(p), p => new Legacy.NpcTransactionPacket(p)),
        ["AssignPlayerToParty"] = ([0x8D], p => new AssignPlayerToPartyPacket(p), p => new Legacy.AssignPlayerToPartyPacket(p)),
        ["CorpseAssign"] = ([0x8E], p => new CorpseAssignPacket(p), p => new Legacy.CorpseAssignPacket(p)),
        ["PlayerLeftGame"] = ([0x5C], p => new PlayerLeftGamePacket(p), p => new Legacy.PlayerLeftGamePacket(p)),
        ["ActData"] = ([0x03], p => new ActDataPacket(p), p => new Legacy.ActDataPacket(p)),
        ["GameFlags"] = ([0x01], p => new GameFlags(p), p => new Legacy.GameFlags(p)),
        ["AssignNpc"] = ([0x55, 0x56], p => new AssignNpcPacket(p), p => new Legacy.AssignNpcPacket(p)),
        ["LifeManaUpdate"] = ([0x95, 0x18], p => new LifeManaUpdatePacket(p), p => new Legacy.LifeManaUpdatePacket(p)),
        ["LifeManaPotUpdate"] = ([0x18, 0x95], p => new LifeManaPotUpdatePacket(p), p => new Legacy.LifeManaPotUpdatePacket(p)),
        ["UpdateEntityEffects"] = ([0x12], p => new UpdateEntityEffectsPacket(p), p => new Legacy.UpdateEntityEffectsPacket(p)),
        ["WalkVerify"] = ([0x96], p => new WalkVerifyPacket(p), p => new Legacy.WalkVerifyPacket(p)),
        ["WaypointMenu"] = ([0x63], p => new WaypointMenuPacket(p), p => new Legacy.WaypointMenuPacket(p)),
        ["PetAction"] = ([0x7A], p => new PetActionPacket(p), p => new Legacy.PetActionPacket(p)),
        ["AssignMerc"] = ([0x81], p => new AssignMercPacket(p), p => new Legacy.AssignMercPacket(p)),
        ["BaseSkillLevels"] = ([0x94], p => new BaseSkillLevelsPacket(p), p => new Legacy.BaseSkillLevelsPacket(p)),
        ["ParseItem"] = ([0x9C, 0x9D], p => new ParseItemPacket(p), p => new Legacy.ParseItemPacket(p)),
    };

    /// <summary>Types no recorded game carried: hand-made bytes of the right lengths, with no name in them.</summary>
    private static readonly Dictionary<byte, int[]> Synthetic = new()
    {
        [0x14] = [18],
        [0x7A] = [16],
        [0x81] = [20],
        [0x8E] = [10],
        [0x01] = [8],
        [0x29] = [97],
        [0x5C] = [5],
    };

    public static TheoryData<string> Names() => [.. Parsers.Keys];

    [Theory]
    [MemberData(nameof(Names))]
    public void RewrittenParserReadsEveryPacketAsTheOldOneDid(string name)
    {
        var (types, parseNew, parseOld) = Parsers[name];
        var packets = Corpus.Value.Where(p => types.Contains(p[0])).ToList();
        var random = new Random(name.GetHashCode(StringComparison.Ordinal) & 0x7FFF);
        foreach (var type in types)
        {
            foreach (var length in Synthetic.GetValueOrDefault(type, []))
            {
                for (var i = 0; i < 50; i++)
                {
                    var bytes = new byte[length];
                    random.NextBytes(bytes);
                    bytes[0] = type;
                    packets.Add(bytes);
                }
            }
        }

        Assert.NotEmpty(packets);
        var compared = 0;
        foreach (var packet in packets)
        {
            foreach (var variant in Variants(packet, types, random))
            {
                var expected = Outcome(parseOld, variant);
                var actual = Outcome(parseNew, variant);
                if (expected != actual)
                {
                    Assert.Fail($"{name} differs on {Convert.ToHexString(variant)}:\nold {expected}\nnew {actual}");
                }

                compared++;
            }
        }

        Assert.True(compared >= packets.Count);
    }

    [Fact]
    public void CorpusHoldsNoPacketThatCarriesAName()
    {
        Assert.DoesNotContain(Corpus.Value, p => p[0] is 0x26 or 0x59 or 0x5A or 0x5B or 0x82 or 0x8F or 0x75);
    }

    private static IEnumerable<byte[]> Variants(byte[] packet, byte[] types, Random random)
    {
        yield return packet;
        for (var length = 0; length < packet.Length; length++)
        {
            yield return packet[..length];
        }

        var longer = new byte[packet.Length + 3];
        packet.CopyTo(longer, 0);
        random.NextBytes(longer.AsSpan(packet.Length));
        yield return longer;
        var other = (byte[])packet.Clone();
        other[0] = (byte)(types.Max() + 1);
        yield return other;
    }

    private static string Outcome(Func<D2gsPacket, object> parse, byte[] bytes)
    {
        object parsed;
        try
        {
            parsed = parse(new D2gsPacket((byte[])bytes.Clone()));
        }
        catch (Exception e)
        {
            return $"threw {e.GetType().FullName}: {e.Message}";
        }

        var text = new StringBuilder();
        Render(text, parsed, 0);
        return text.ToString();
    }

    private static void Render(StringBuilder text, object value, int depth)
    {
        switch (value)
        {
            case null:
                text.Append("null");
                return;
            case string s:
                text.Append('"').Append(s).Append('"');
                return;
            case double d:
                text.Append(BitConverter.DoubleToInt64Bits(d).ToString(CultureInfo.InvariantCulture));
                return;
            case float f:
                text.Append(BitConverter.SingleToInt32Bits(f).ToString(CultureInfo.InvariantCulture));
                return;
            case byte[] bytes:
                text.Append(Convert.ToHexString(bytes));
                return;
            case Enum or IFormattable when value.GetType().IsPrimitive || value is Enum || value is decimal:
                text.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            case bool b:
                text.Append(b);
                return;
            case IDictionary dictionary:
                var entries = new List<string>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    var item = new StringBuilder();
                    Render(item, entry.Key, depth + 1);
                    item.Append(" => ");
                    Render(item, entry.Value, depth + 1);
                    entries.Add(item.ToString());
                }

                entries.Sort(StringComparer.Ordinal);
                text.Append('{').Append(string.Join(", ", entries)).Append('}');
                return;
            case IEnumerable sequence:
                text.Append('[');
                foreach (var item in sequence)
                {
                    Render(text, item, depth + 1);
                    text.Append(", ");
                }

                text.Append(']');
                return;
        }

        var type = value.GetType();
        if (depth > 4)
        {
            text.Append('<').Append(type.Name).Append('>');
            return;
        }

        text.Append(type.Name).Append('(');
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            text.Append(property.Name).Append('=');
            try
            {
                Render(text, property.GetValue(value), depth + 1);
            }
            catch (TargetInvocationException e)
            {
                text.Append("threw ").Append(e.InnerException?.GetType().Name);
            }

            text.Append("; ");
        }

        text.Append(')');
    }
}
