namespace D2NG.Packet
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