using D2NG.Core.D2GS.Exceptions;
using Serilog;

namespace D2NG.Core.D2GS.Packet.Incoming
{
    internal class LifeManaPotUpdatePacket : D2gsPacket
    {
        public LifeManaPotUpdatePacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BitReader(Raw);
            var id = reader.ReadByte();
            if ((InComingPacket)id != InComingPacket.LifeManaUpdate && (InComingPacket)id != InComingPacket.LifeManaUpdatePot)
            {
                throw new D2GSPacketException($"Invalid Packet Id {id}");
            }
            Life = reader.Read(15);
            Mana = reader.Read(15);
            Stamina = reader.Read(15);
            _ = reader.Read(14);
            var x = reader.ReadUInt16();
            var y = reader.ReadUInt16();
            Location = new Point(x, y);
            Log.Verbose($"(0x{id,2:X2})Update Self Packet:\n" +
                        $"\tLife: {Life}\n" +
                        $"\tMana: {Mana}\n" +
                        $"\tLocation: {Location}");
        }

        public int Life { get; }
        public int Mana { get; }
        public int Stamina { get; }
        public Point Location { get; }
    }
}