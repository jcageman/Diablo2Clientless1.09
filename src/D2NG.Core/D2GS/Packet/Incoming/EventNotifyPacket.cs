using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Exceptions;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming;

public class EventNotifyPacket : D2gsPacket
{
    public uint EntityId { get; }

    public byte Action { get; }
    public EventType EventType { get; }

    public PlayerRelationType PlayerRelationType { get; }

    public EventNotifyPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
        var id = reader.ReadByte();
        if ((InComingPacket)id != InComingPacket.EventMessage)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }

        EventType = (EventType)reader.ReadByte();
        Action = reader.ReadByte();
        EntityId = reader.ReadUInt32();
        PlayerRelationType = (PlayerRelationType)reader.ReadByte();
    }
}
