using D2NG.Core.D2GS.Items;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

/// <summary>
/// Puts the item on the cursor down on top of another item; the server hands that item to the
/// cursor. Captured from a 1.09 client (2026-10-10): the held item, the item it lands on and that
/// item's cell. There is no container field - the item it lands on decides where it goes.
/// </summary>
internal class SwapCursorAndBufferItemPacket : D2gsPacket
{
    public SwapCursorAndBufferItemPacket(Item held, Item target) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.SwapCursorAndBufferItem,
                BitConverter.GetBytes(held.Id),
                BitConverter.GetBytes(target.Id),
                BitConverter.GetBytes((uint)target.Location.X),
                BitConverter.GetBytes((uint)target.Location.Y)
            )
        )
    {
    }
}
