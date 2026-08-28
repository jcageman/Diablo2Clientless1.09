using D2NG.Core.D2GS.Packet.Outgoing;
using Xunit;

namespace D2NG.Core.Tests.D2GS;

/// <summary>
/// Pins the staff insertion to the bytes a real 1.09 client sent, because nothing else can justify them:
/// the packet had a name in the enum and no implementation, and the rule for this project is that
/// unobserved bytes do not get sent.
/// </summary>
public class InsertHoradricStaffPacketTests
{
    /// <summary>
    /// Captured 2026-08-23 from a client putting the horadric staff into the orifice of Tal Rasha's tomb
    /// 7. The bot had independently logged that game's orifice as entity 444, which is the 0x01BC below,
    /// and the inserting character's own unit id was 1.
    /// </summary>
    private static readonly byte[] Captured =
    [
        0x44,
        0x01, 0x00, 0x00, 0x00,
        0xBC, 0x01, 0x00, 0x00,
        0xAB, 0x00, 0x00, 0x00,
        0x03, 0x00, 0x00, 0x00
    ];

    private const uint CapturedPlayerId = 1;
    private const uint CapturedOrificeId = 444;
    private const uint CapturedStaffId = 171;

    [Fact]
    public void ReproducesTheCapturedInsertionExactly()
    {
        var packet = new InsertHoradricStaffPacket(CapturedPlayerId, CapturedOrificeId, CapturedStaffId);

        Assert.Equal(Captured, packet.Raw);
    }

    /// <summary>
    /// The orifice id is the one field the capture proves, so it has to land in the second word rather
    /// than anywhere else that would also happen to match for id 444.
    /// </summary>
    [Fact]
    public void PutsTheOrificeInTheSecondWord()
    {
        var packet = new InsertHoradricStaffPacket(CapturedPlayerId, 0x11223344, CapturedStaffId);

        Assert.Equal(0x44, packet.Raw[0]);
        Assert.Equal<byte[]>([0x44, 0x33, 0x22, 0x11], packet.Raw[5..9]);
    }

    [Fact]
    public void PutsTheStaffInTheThirdWord()
    {
        var packet = new InsertHoradricStaffPacket(CapturedPlayerId, CapturedOrificeId, 0x55667788);

        Assert.Equal<byte[]>([0x88, 0x77, 0x66, 0x55], packet.Raw[9..13]);
    }

    /// <summary>
    /// The trailing word was 3 and is not understood, so it is reproduced verbatim and must not start
    /// varying with anything.
    /// </summary>
    [Fact]
    public void AlwaysEmitsTheObservedTrailingWord()
    {
        var packet = new InsertHoradricStaffPacket(9, 9, 9);

        Assert.Equal<byte[]>([0x03, 0x00, 0x00, 0x00], packet.Raw[13..17]);
        Assert.Equal(17, packet.Raw.Length);
    }
}
