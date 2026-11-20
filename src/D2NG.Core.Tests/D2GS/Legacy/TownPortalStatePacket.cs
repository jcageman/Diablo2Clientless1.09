using D2NG.Core.D2GS.Exceptions;
using System.IO;
using System.Text;

using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;

namespace D2NG.Core.Tests.D2GS.Legacy;

public class TownPortalStatePacket : D2gsPacket
{
    public TownPortalStatePacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
        var id = reader.ReadByte();
        if ((InComingPacket)id != InComingPacket.TownPortalState)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }
        reader.ReadByte();
        Area = (global::D2NG.Core.D2GS.Act.Area)reader.ReadByte();
        TeleportId = reader.ReadUInt32();
        reader.Close();
    }

    public global::D2NG.Core.D2GS.Act.Area Area { get; }
    public uint TeleportId { get; }
}
