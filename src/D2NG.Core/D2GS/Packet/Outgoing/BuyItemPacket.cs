using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

internal class BuyItemPacket : D2gsPacket
{
    public BuyItemPacket(Entity entity, Item item, bool buyStack, bool gamble) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.BuyItem,
                BitConverter.GetBytes(entity.Id),
                BitConverter.GetBytes(item.Id),
                new byte[] { (byte)(gamble ? 0x02 : 0x00), 0x00, 0x00, (byte)(buyStack ? 0x80 : 0x00) },
                BitConverter.GetBytes((uint)0x00)
            )
        )
    {
    }
    public BuyItemPacket(byte[] packet) : base(packet)
    {
    }

    public uint GetItemId()
    {
        return BitConverter.ToUInt32(Raw, 5);
    }
}
