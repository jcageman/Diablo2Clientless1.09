using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Helpers;
using D2NG.Core.MCP;
using D2NG.Core.MCP.Packet;
using Xunit;

namespace D2NG.Core.Tests.MCP;

/// <summary>
/// Byte fixtures are 1.09 client captures of six character creations, which between them cover two
/// classes, classic and expansion, hardcore, and a name the realm refused.
/// </summary>
public class CreateCharacterPacketTests
{
    [Theory]
    // A classic softcore character sends no flags at all.
    [InlineData("amaa", CharacterClass.Amazon, CharacterFlags.None, "0e0002000000000000616d616100")]
    [InlineData("lama", CharacterClass.Barbarian, CharacterFlags.None, "0e00020400000000006c616d6100")]
    // Expansion is bit 5, seen on both an amazon and a barbarian.
    [InlineData("bama", CharacterClass.Amazon, CharacterFlags.Expansion, "0e000200000000200062616d6100")]
    [InlineData("cama", CharacterClass.Barbarian, CharacterFlags.Expansion, "0e000204000000200063616d6100")]
    // Hardcore adds bit 2 on top of expansion.
    [InlineData("dama", CharacterClass.Barbarian, CharacterFlags.Expansion | CharacterFlags.Hardcore, "0e000204000000240064616d6100")]
    [InlineData("eama", CharacterClass.Barbarian, CharacterFlags.Expansion | CharacterFlags.Hardcore, "0e000204000000240065616d6100")]
    public void RequestMatchesCapturedBytes(string name, CharacterClass characterClass, CharacterFlags flags, string expected)
    {
        var packet = new CreateCharacterRequestPacket(name, characterClass, flags);
        Assert.Equal(expected.StringToByteArray(), packet.Raw);
    }

    [Fact]
    public void ResponseReportsSuccess()
    {
        var response = new CreateCharacterResponsePacket(new McpPacket("07000200000000".StringToByteArray()));

        Assert.Equal(0U, response.Result);
        Assert.True(response.Success);
    }

    [Fact]
    public void ResponseReportsRefusedName()
    {
        // The realm answers 0x14 for a name it will not take. Two captures hit this, and in both the
        // same class and flags succeeded immediately afterwards under a free name, so the code is
        // about the name rather than about the flags.
        var response = new CreateCharacterResponsePacket(new McpPacket("07000214000000".StringToByteArray()));

        Assert.Equal(CreateCharacterResponsePacket.NameUnavailable, response.Result);
        Assert.False(response.Success);
    }

    [Theory]
    // Captured deletions of the throwaway characters, request id zero in every sample.
    [InlineData("lama", "0a000a00006c616d6100")]
    [InlineData("bama", "0a000a000062616d6100")]
    [InlineData("eama", "0a000a000065616d6100")]
    public void DeleteRequestMatchesCapturedBytes(string name, string expected)
    {
        var packet = new DeleteCharacterRequestPacket(0, name);
        Assert.Equal(expected.StringToByteArray(), packet.Raw);
    }

    [Fact]
    public void DeleteResponseReportsSuccess()
    {
        var response = new DeleteCharacterResponsePacket(new McpPacket("09000a000000000000".StringToByteArray()));

        Assert.Equal(0, response.RequestId);
        Assert.True(response.Success);
    }
}
