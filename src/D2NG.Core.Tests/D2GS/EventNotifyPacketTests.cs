using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using System.Linq;
using System.Text;
using Xunit;

namespace D2NG.Core.Tests.D2GS;

public class EventNotifyPacketTests
{
    // A captured "joined the game" message with the names swapped for roles. The realm reuses the name
    // fields, so leftovers of a longer earlier name follow the terminator.
    private static byte[] Joined(string character, string characterLeftover, string account)
    {
        var bytes = new byte[40];
        bytes[0] = 0x5A;
        bytes[1] = 0x02;
        bytes[2] = 0x04;
        Write(bytes, 8, character + "\0" + characterLeftover);
        Write(bytes, 24, account + "\0");
        return bytes;
    }

    private static void Write(byte[] bytes, int offset, string text)
    {
        var encoded = Encoding.ASCII.GetBytes(text).Take(16).ToArray();
        encoded.CopyTo(bytes, offset);
    }

    [Fact]
    public void JoinedTheGameCarriesCharacterAndAccount()
    {
        var packet = new EventNotifyPacket(new D2gsPacket(Joined("Paladin", "ted", "party-account")));

        Assert.Equal(EventType.JoinedTheGame, packet.EventType);
        Assert.Equal("Paladin", packet.CharacterName);
        Assert.Equal("party-account", packet.AccountName);
    }

    [Fact]
    public void NameFillingTheWholeFieldHasNoTerminator()
    {
        var packet = new EventNotifyPacket(new D2gsPacket(Joined("Sixteen-letters1", "", "sixteen-letters2")));

        Assert.Equal("Sixteen-letters1", packet.CharacterName);
        Assert.Equal("sixteen-letters2", packet.AccountName);
    }
}
