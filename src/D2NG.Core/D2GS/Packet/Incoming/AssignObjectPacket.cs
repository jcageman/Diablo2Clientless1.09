using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Objects;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming;

internal class AssignObjectPacket : D2gsPacket
{
    public AssignObjectPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
        var id = reader.ReadByte();
        if (InComingPacket.AssignObject != (InComingPacket)id)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }
        EntityType = (EntityType)reader.ReadByte();
        EntityId = reader.ReadUInt32();
        ObjectCode = (EntityCode)reader.ReadUInt16();
        Location = new Point(reader.ReadUInt16(), reader.ReadUInt16());
        State = (EntityState)reader.ReadByte();
        InteractionType = reader.ReadByte();
        reader.Close();
    }

    public WorldObject AsWorldObject()
        => new(EntityType, EntityId, ObjectCode, Location, State, InteractionType);

    public EntityType EntityType { get; }
    public uint EntityId { get; }
    public EntityCode ObjectCode { get; }
    public Point Location { get; }
    public EntityState State { get; }
    public byte InteractionType { get; }
}
