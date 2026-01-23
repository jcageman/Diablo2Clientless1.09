using System.IO;
using System.Text;
using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Objects;

namespace D2NG.Core.D2GS.Packet.Incoming;

internal class AssignLevelWarpPacket : D2gsPacket
{
    public uint EntityId { get; }

    public uint WarpId { get; }
    public Point Location { get; }

    public AssignLevelWarpPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
        var id = reader.ReadByte();
        if ((InComingPacket)id != InComingPacket.AssignLevelWarp)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }

        reader.ReadByte();
        EntityId = reader.ReadUInt32();
        WarpId = reader.ReadByte();
        var x = reader.ReadUInt16();
        var y = reader.ReadUInt16();
        Location = new Point(x, y);
    }

    public WarpData AsWarpData()
        => new()
        {
            EntityId = EntityId,
            Location = Location,
            WarpId = WarpId
        };
}
