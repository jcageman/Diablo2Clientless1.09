using System.IO;
using System.Text;

namespace D2NG.D2GS.Packet
{
    internal class GameHandshakePacket : D2gsPacket
    {
        public GameHandshakePacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
            var id = reader.ReadByte();
            if (InComingPacket.GameHandshake != (InComingPacket)id)
            {
                throw new D2GSPacketException($"Invalid Packet Id {id}");
            }
            
        }
    }
}