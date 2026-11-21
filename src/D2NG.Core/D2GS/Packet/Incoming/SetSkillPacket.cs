using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Players;
using Serilog;
using Serilog.Events;

namespace D2NG.Core.D2GS.Packet.Incoming;

internal class SetActiveSkillPacket : D2gsPacket
{
    public byte UnitType { get; }
    public uint UnitGid { get; }
    public Hand Hand { get; }
    public Skill Skill { get; }
    public uint ItemGid { get; }

    public SetActiveSkillPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new PacketReader(packet.Raw);
        var id = reader.ReadByte();
        if ((InComingPacket)id != InComingPacket.SetSkill)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }
        UnitType = reader.ReadByte();
        UnitGid = reader.ReadUInt32();
        Hand = (Hand)reader.ReadByte();
        Skill = (Skill)reader.ReadUInt16();
        ItemGid = reader.ReadUInt32();

        if (Log.IsEnabled(LogEventLevel.Verbose))
        {
            Log.Verbose($"(0x{packet.Raw[0],2:X2}) Set Skill:\n" +
                $"\tUnit Type: {UnitType}\n" +
                $"\tUnit GID: {UnitGid}\n" +
                $"\tHand: {Hand}\n" +
                $"\tSkill: {Skill}\n" +
                $"\tItemGid: {ItemGid}");
        }
    }
}