using D2NG.Core.D2GS.Exceptions;

namespace D2NG.Core.D2GS.Packet.Incoming;

public class CorpseAssignPacket : D2gsPacket
{
    public CorpseAssignPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new PacketReader(packet.Raw);
        if ((InComingPacket)reader.ReadByte() != InComingPacket.CorpseAssign)
        {
            throw new D2GSPacketException("Expected Packet Type Not Found");
        }
        CorpseAdded = reader.ReadBoolean();
        PlayerId = reader.ReadUInt32();
        CorpseId = reader.ReadUInt32();
    }

    public bool CorpseAdded { get; }
    public uint PlayerId { get; }
    public uint CorpseId { get; }
}