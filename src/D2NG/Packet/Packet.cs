using System.IO;
using System.Text;

namespace D2NG
{
    public class Packet
    {
        public byte[] Raw { get; }

        public Packet(byte[] packet)
        {
            Raw = packet;
        }
    }
}