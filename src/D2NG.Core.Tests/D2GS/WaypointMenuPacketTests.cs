using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using System.Linq;
using Xunit;

namespace D2NG.Core.Tests.D2GS;

/// <summary>
/// The waypoint menu bitfield is packed in panel order, which is not the order the
/// <see cref="Waypoint"/> values sort in. Reading it with <c>Enum.GetValues</c> was correct for act 1
/// and wrong for every act after it.
/// </summary>
public class WaypointMenuPacketTests
{
    /// <summary>
    /// A menu packet captured from a live 1.09 game, sent to a classic rusher that had cleared act 4.
    /// </summary>
    private static readonly byte[] CapturedMenu =
    [
        0x63,
        0x1A, 0x00, 0x00, 0x00,
        0x02, 0x01,
        0x0D, 0x77, 0x87, 0x3E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    private static WaypointMenuPacket Parse() => new(new D2gsPacket(CapturedMenu));

    [Fact]
    public void ReadsTheWaypointIdOfTheMenusOwner()
    {
        Assert.Equal(26u, Parse().WaypointId);
    }

    /// <summary>
    /// The whole point of the fix. Numeric order put this character's act 2 bits one place out and
    /// reported Dry Hills instead of Far Oasis, so the bot refused to take a waypoint it owned.
    /// </summary>
    [Fact]
    public void ReadsFarOasisRatherThanDryHills()
    {
        var allowed = Parse().AllowedWaypoints;

        Assert.Contains(Waypoint.FarOasis, allowed);
        Assert.DoesNotContain(Waypoint.DryHills, allowed);
    }

    /// <summary>
    /// A character cannot own the Arcane Sanctuary waypoint without having passed Far Oasis and the
    /// Lost City, which is the cross check that told the two readings apart.
    /// </summary>
    [Fact]
    public void ReadsAnInternallyConsistentActTwo()
    {
        var allowed = Parse().AllowedWaypoints;

        Assert.Contains(Waypoint.LutGholein, allowed);
        Assert.Contains(Waypoint.SewersLevel2, allowed);
        Assert.Contains(Waypoint.HallsOfTheDeadLevel2, allowed);
        Assert.Contains(Waypoint.FarOasis, allowed);
        Assert.Contains(Waypoint.LostCity, allowed);
        Assert.Contains(Waypoint.ArcaneSanctuary, allowed);
        Assert.Contains(Waypoint.CanyonOfTheMagi, allowed);
        Assert.DoesNotContain(Waypoint.PalaceCellarLevel1, allowed);
    }

    [Fact]
    public void ReadsTheActsEitherSideOfActTwo()
    {
        var allowed = Parse().AllowedWaypoints;

        // Act 1: a character that waypointed straight to the catacombs and skipped the cloister.
        Assert.Contains(Waypoint.RogueEncampment, allowed);
        Assert.Contains(Waypoint.StonyFields, allowed);
        Assert.Contains(Waypoint.CatacombsLevel2, allowed);
        Assert.DoesNotContain(Waypoint.ColdPlains, allowed);
        Assert.DoesNotContain(Waypoint.OuterCloister, allowed);

        // Act 3 and 4: the travincal and chaos sanctuary route this character actually runs.
        Assert.Contains(Waypoint.Travincal, allowed);
        Assert.Contains(Waypoint.DuranceOfHateLevel2, allowed);
        Assert.Contains(Waypoint.RiverOfFlame, allowed);
        Assert.DoesNotContain(Waypoint.SpiderForest, allowed);
    }

    /// <summary>
    /// Nothing beyond act 4 is set in this capture, so a stray act 5 bit would mean the bit offsets had
    /// drifted rather than that the character owned anything there.
    /// </summary>
    [Fact]
    public void ReadsNoExpansionWaypointsFromAClassicCharacter()
    {
        var allowed = Parse().AllowedWaypoints;
        var expansion = WaypointExtensions.BitOrder.Skip(30);

        Assert.Empty(allowed.Intersect(expansion));
    }

    [Fact]
    public void CoversEveryWaypointExactlyOnce()
    {
        Assert.Equal(WaypointExtensions.BitOrder.Count, WaypointExtensions.BitOrder.Distinct().Count());
        Assert.Equal(39, WaypointExtensions.BitOrder.Count);
    }
}
