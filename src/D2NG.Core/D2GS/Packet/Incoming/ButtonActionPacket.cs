using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Exceptions;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming;

public class ButtonActionPacket : D2gsPacket
{
    public ButtonActionPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
        if ((InComingPacket)reader.ReadByte() != InComingPacket.ButtonAction)
        {
            throw new D2GSPacketException("Expected Packet Type Not Found");
        }
        Action = (ButtonAction)reader.ReadByte();
    }
    public ButtonAction Action { get; }
}