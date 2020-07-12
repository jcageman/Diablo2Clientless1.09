using System;

namespace D2NG.D2GS.Packet.Outgoing
{
    internal class RunToLocationPacket : D2gsPacket
    {
        public RunToLocationPacket(Point point) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.Run,
                    BitConverter.GetBytes(point.X),
                    BitConverter.GetBytes(point.Y)
                )
            )
        {
        }
        public RunToLocationPacket(byte[] packet) : base(packet)
        {
        }

        public Point GetLocation()
        {
            var x = BitConverter.ToUInt16(Raw, 1);
            var y = BitConverter.ToUInt16(Raw, 3);
            return new Point(x, y);
        }
    }
}
