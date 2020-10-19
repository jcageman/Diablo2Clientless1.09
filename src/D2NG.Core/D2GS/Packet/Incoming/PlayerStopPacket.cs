using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.Extensions;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming
{
    public class PlayerStopPacket : D2gsPacket
    {
        public PlayerStopPacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
            var id = reader.ReadByte();
            if (InComingPacket.PlayerStop != (InComingPacket)id)
            {
                throw new D2GSPacketException($"Invalid Packet Id {id}");
            }
            EntityType = (EntityType)reader.ReadByte();
            EntityId = reader.ReadUInt32();
            EntityEffect = (EntityEffect)reader.ReadByte();
            Location = new Point(reader.ReadUInt16(), reader.ReadUInt16());
            reader.ReadByte();
            reader.ReadByte();
        }
        public EntityType EntityType { get; }
        public uint EntityId { get; }
        public EntityEffect EntityEffect{ get; }
        public Point Location { get; }
    }
}
