using D2NG.Core.D2GS.Items;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

/// <summary>
/// Puts an item from the cursor onto the character, in the given body slot.
/// </summary>
/// <remarks>
/// Captured from a 1.09 client, four slots in one session:
/// <code>
/// 0x1A, 06 00 00 00, 01 00 00 00   helm
/// 0x1A, 1E 00 00 00, 04 00 00 00   weapon, right hand
/// 0x1A, 07 00 00 00, 08 00 00 00   belt
/// 0x1A, 0A 00 00 00, 09 00 00 00   boots
/// </code>
/// Two little endian words: the item, then the slot, and the slot values line up with
/// <see cref="DirectoryType"/> throughout.
/// </remarks>
internal class EquipItemPacket : D2gsPacket
{
    public EquipItemPacket(uint itemId, DirectoryType location) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.EquipItem,
                BitConverter.GetBytes(itemId),
                BitConverter.GetBytes((uint)location)
            )
        )
    {
    }
}
