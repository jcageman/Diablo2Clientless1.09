using D2NG.Core.D2GS.Exceptions;
using System.IO;
using System.Text;

using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;

namespace D2NG.Core.Tests.D2GS.Legacy;

public class PartyAutomapInfoPacket : D2gsPacket
{
    public PartyAutomapInfoPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
        if ((InComingPacket)reader.ReadByte() != InComingPacket.PartyAutomapInfo)
        {
            throw new D2GSPacketException("Expected Packet Type Not Found");
        }
        Id = reader.ReadUInt32();
        Location = new Point((ushort)reader.ReadInt32(), (ushort)reader.ReadInt32());
    }

    public Point Location { get; }
    public uint Id { get; }
}
