using D2NG.Core.D2GS.Exceptions;

using D2NG.Core.D2GS.Objects;

using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;

namespace D2NG.Core.Tests.D2GS.Legacy;

public class PetActionPacket : D2gsPacket
{
    public PetActionPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new LegacyBitReader(packet.Raw);
        var id = (InComingPacket)reader.ReadByte();
        if (InComingPacket.PetAction != id)
        {
            throw new D2GSPacketException("Invalid Packet Id");
        }
        AddingSummon = reader.ReadByte() == 1;
        SkillTree = reader.ReadByte();
        UniqueCode = (NPCCode)reader.ReadUInt16();
        PlayerId = reader.ReadUInt32();
        SummonId = reader.ReadUInt32();
    }
    public bool AddingSummon { get; }
    public byte SkillTree { get; }
    public NPCCode UniqueCode { get; }
    public uint PlayerId { get; }
    public uint SummonId { get; }
}
