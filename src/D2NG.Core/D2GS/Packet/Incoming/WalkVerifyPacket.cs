using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Helpers;
using Serilog;

namespace D2NG.Core.D2GS.Packet.Incoming;

internal class WalkVerifyPacket : D2gsPacket
{
    public Point Location { get; }
    public WalkVerifyPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BitReader(packet.Raw);
        var id = reader.ReadByte();
        if ((InComingPacket)id != InComingPacket.WalkVerify)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }

        _ = reader.Read(15);
        var x = (ushort)reader.Read(15);
        _ = reader.Read(1);
        var y = (ushort)reader.Read(15);

        Location = new Point(x, y);

        Log.Verbose($"WalkVerify:\n" +
            $"\tLocation: {Location}");
    }
}
