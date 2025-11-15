using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Objects;
using Serilog;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming;

public class EntityMovePacket : D2gsPacket
{
    public EntityMovePacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
        var id = reader.ReadByte();
        if ((InComingPacket)id != InComingPacket.EntityMove)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }
        UnitType = (EntityType)reader.ReadByte();
        UnitId = reader.ReadUInt32();
        MovementType = reader.ReadByte();
        MoveToLocation = new Point(reader.ReadUInt16(), reader.ReadUInt16());
        _ = reader.ReadByte();
        CurrentLocation = new Point(reader.ReadUInt16(), reader.ReadUInt16());
        reader.Close();

        Log.Verbose($"(0x{id,2:X2}) Reassign Player:\n" +
            $"\tUnitType: {UnitType}\n" +
            $"\tUnitId: {UnitId}\n" +
            $"\tCurrent Location: {CurrentLocation}" +
            $"\tMoving to Location: {MoveToLocation}");
    }

    public EntityType UnitType { get; }
    public uint UnitId { get; }

    public byte MovementType { get; }
    public Point MoveToLocation { get; }

    public Point CurrentLocation { get; }
}
