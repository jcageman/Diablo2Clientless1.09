using System.Collections.Generic;
using System.Linq;

namespace D2NG.Core.D2GS.Packet
{
    public class D2gsPacket : Core.Packet.Packet
    {
        public D2gsPacket(byte[] packet) : base(packet)
        {
        }

        public byte Type { get => Raw[0]; }

        public static byte[] BuildPacket(byte command, params IEnumerable<byte>[] args)
        {
            var packet = new List<byte>
            {
                command,
            };
            packet.AddRange(args.SelectMany(a => a));
            return packet.ToArray();
        }
    }
}
