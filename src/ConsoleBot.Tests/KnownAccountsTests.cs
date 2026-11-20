using ConsoleBot.Escape;
using System.IO;
using Xunit;

namespace ConsoleBot.Tests;

public class KnownAccountsTests
{
    [Theory]
    [InlineData("Character*account", "account")]
    [InlineData("*account", "account")]
    [InlineData("account", "account")]
    public void WhisperSenderNamesTheAccountAfterTheStar(string sender, string account)
    {
        Assert.Equal(account, KnownAccounts.AccountOfSender(sender));
    }

    [Fact]
    public void AccountsMatchIgnoringCase()
    {
        var known = new KnownAccounts(["farmer", "mule-1"]);

        Assert.True(known.IsKnown("Farmer"));
        Assert.True(known.IsKnown("MULE-1"));
        Assert.False(known.IsKnown("stranger"));
        Assert.False(known.IsKnown(null));
    }

    [Fact]
    public void LoadsTheListFromItsFile()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """{ "knownAccounts": [ "farmer", "", "mule-1" ] }""");

        var known = KnownAccounts.Load(path);

        Assert.Equal(2, known.Count);
        Assert.True(known.IsKnown("mule-1"));
        File.Delete(path);
    }
}
