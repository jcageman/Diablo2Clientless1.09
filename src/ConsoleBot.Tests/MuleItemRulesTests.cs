using ConsoleBot.Mule;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace ConsoleBot.Tests;

public sealed class MuleItemRulesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mule-nips-{Guid.NewGuid():N}");

    public MuleItemRulesTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "gems.nip"), "// gems only\n[type] == gem\n");
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private MuleConfiguration Config(params MuleAccount[] accounts)
    {
        return new MuleConfiguration { NipDirectory = _directory, Accounts = [.. accounts] };
    }

    private static Item Of(ClassificationType classification, ItemName name)
    {
        return new Item { Name = name, Classification = classification, Quality = QualityType.Normal, IsIdentified = true, Properties = [] };
    }

    [Fact]
    public void Account_TakesWhatItsNipMatches()
    {
        var account = new MuleAccount { Username = "gems", Password = "x", Nip = "gems.nip" };
        var rules = new MuleItemRules(Config(account));

        Assert.True(rules.Accepts(account, Of(ClassificationType.Gem, ItemName.PerfectRuby)));
        Assert.False(rules.Accepts(account, Of(ClassificationType.Rune, ItemName.SolRune)));
        Assert.False(rules.NeverMule(Of(ClassificationType.Gem, ItemName.PerfectRuby)));
    }

    [Fact]
    public void Account_WithoutNip_StopsStartup_RatherThanAcceptingEverything()
    {
        var account = new MuleAccount { Username = "open", Password = "x" };

        Assert.Throws<InvalidDataException>(() => new MuleItemRules(Config(account)));
    }

    [Fact]
    public void MissingNipFile_StopsStartup()
    {
        var account = new MuleAccount { Username = "typo", Password = "x", Nip = "gemz.nip" };

        Assert.Throws<FileNotFoundException>(() => new MuleItemRules(Config(account)));
    }
}
