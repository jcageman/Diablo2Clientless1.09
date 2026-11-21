using D2NG.Core.D2GS.Exceptions;
using System.IO;
using System.Text;

using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;

namespace D2NG.Core.Tests.D2GS.Legacy;

public class CorpseAssignPacket : D2gsPacket
{
    public CorpseAssignPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
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
