using D2NG.Core.D2GS.Items;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

/// <summary>
/// Takes an item off the character and onto the cursor, addressed by the body slot it is worn in.
/// </summary>
/// <remarks>
/// Captured from a 1.09 client taking off the amulet of the viper so it could go into the cube:
/// <code>0x1C, 0x02, 0x00</code>
/// One little endian word holding the slot. Every slot was captured in one session and all ten line up
/// with <see cref="DirectoryType"/>: 1 helm, 2 amulet, 3 armor, 4 right hand, 5 left hand, 6 right ring,
/// 7 left ring, 8 belt, 9 boots, 10 gloves.
/// <para>
/// Worn items cannot be moved with <c>0x19 RemoveItemFromBuffer</c>, which addresses an item by its id
/// and only works on items in a container: sending it for an equipped amulet did nothing at all and the
/// item never reached the cursor. Once this has put it on the cursor,
/// <c>0x18 InsertItemToBuffer</c> places it as normal.
/// </para>
/// </remarks>
internal class RemoveBodyItemPacket : D2gsPacket
{
    public RemoveBodyItemPacket(DirectoryType location) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.RemoveBodyItem,
                BitConverter.GetBytes((ushort)location)
            )
        )
    {
    }
}
