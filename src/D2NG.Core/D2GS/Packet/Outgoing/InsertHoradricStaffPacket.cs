using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

/// <summary>
/// Puts the horadric staff into the horadric orifice, which opens the portal to Duriel's lair.
/// </summary>
/// <remarks>
/// Captured from a 1.09 client on 2026-08-23, inserting a staff into the orifice of Tal Rasha's tomb 7:
/// <code>
/// 0x44, 0x01, 0x00, 0x00, 0x00, 0xBC, 0x01, 0x00, 0x00, 0xAB, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00
/// </code>
/// Four little endian words after the id. The second is certain: 0x1BC is 444, and the bot had already
/// logged that game's orifice as entity 444 from its own socket. The third is the staff, by elimination.
/// <para>
/// Two things have to happen first, and without them the server ignores this packet entirely. The client
/// interacts with the orifice (<c>EntityInteract</c>, object), then picks the staff onto the cursor with
/// <c>0x19 RemoveItemFromBuffer</c>. Only then does the insert take.
/// </para>
/// <para>
/// Send it once. A second insert after a successful one had the server drop the connection.
/// </para>
/// <para>
/// The first and fourth are not explained. The first was 1 in the capture, and so was the inserting
/// character's own unit id, so it is taken here as the player id rather than as a constant - but one
/// sample cannot tell those apart. The fourth was 3 and is emitted as the literal that was seen.
/// Resolving either needs a second capture from a game where the inserting player's unit id is not 1,
/// which is worth taking before this is relied on outside the probe.
/// </para>
/// </remarks>
internal class InsertHoradricStaffPacket : D2gsPacket
{
    public InsertHoradricStaffPacket(uint playerId, uint orificeId, uint staffId) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.InsertHoradricStaff,
                BitConverter.GetBytes(playerId),
                BitConverter.GetBytes(orificeId),
                BitConverter.GetBytes(staffId),
                BitConverter.GetBytes(ObservedTrailingWord)
            )
        )
    {
    }

    public InsertHoradricStaffPacket(uint playerId, Entity orifice, Item staff)
        : this(playerId, orifice.Id, staff.Id)
    {
    }

    /// <summary>
    /// The trailing word as the client sent it. Unexplained, so it is reproduced rather than computed.
    /// </summary>
    private const uint ObservedTrailingWord = 3;
}
