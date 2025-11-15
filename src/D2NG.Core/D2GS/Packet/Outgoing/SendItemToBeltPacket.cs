using D2NG.Core.D2GS.Items;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

internal class SendItemToBeltPacket : D2gsPacket
{
    public SendItemToBeltPacket(Item item) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.SendItemToBelt,
                BitConverter.GetBytes(item.Id)
            )
        )
    {
    }
    public SendItemToBeltPacket(byte[] packet) : base(packet)
    {
    }

    public uint GetItemId()
    {
        return BitConverter.ToUInt32(Raw, 1);
    }
}
