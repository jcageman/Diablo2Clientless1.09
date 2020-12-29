using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.Extensions;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming
{
    public class PortalOwnerPacket : D2gsPacket
    {
        public PortalOwnerPacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
            var id = reader.ReadByte();
            if (InComingPacket.PortalOwner != (InComingPacket)id)
            {
                throw new D2GSPacketException($"Invalid Packet Id {id}");
            }

            OwnerId = reader.ReadUInt32();
            Name = reader.ReadNullTerminatedString();
            var count = Name.Length + 1;
            while(count++ < 16)
            {
                reader.ReadByte();
            }
            TeleportOurSideId = reader.ReadUInt32();
            TeleportOtherSideId = reader.ReadUInt32();
        }
        public uint OwnerId { get; }
        public string Name { get; }
        public uint TeleportOurSideId { get; }

        public uint TeleportOtherSideId { get; }
    }
}
