using D2NG.Core.D2GS.Exceptions;

using Serilog;

using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;

namespace D2NG.Core.Tests.D2GS.Legacy;

internal class LifeManaUpdatePacket : D2gsPacket
{
    public LifeManaUpdatePacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new LegacyBitReader(Raw);
        var id = reader.ReadByte();
        if ((InComingPacket)id != InComingPacket.LifeManaUpdate && (InComingPacket)id != InComingPacket.LifeManaUpdatePot)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }
        Life = reader.Read(15);
        Mana = reader.Read(15);
        Stamina = reader.Read(15);
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
