using D2NG.Core.D2GS.Enums;
using D2NG.Core.ItemData;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace D2NG.Core.Tests.ItemData;

/// <summary>
/// The offsets are 1.09's and were worked out against the patch_d2.mpq tables, so these pin them:
/// a 132 byte affix record with single-byte item type references, and a 344 byte cube recipe that
/// keeps its item strings verbatim.
/// </summary>
public class ItemAffixReaderTests
{
    private static readonly string[] Properties = ["ac", "ac%", "res-fire", "gethit-skill"];
    private static readonly string[] TypeCodes = ["", "armo", "ring", "staf", "wand"];

    private static byte[] Table(int recordSize, int count)
    {
        var raw = new byte[4 + recordSize * count];
        BitConverter.GetBytes(count).CopyTo(raw, 0);
        return raw;
    }

    private static void Put(byte[] raw, int at, string text) => Encoding.ASCII.GetBytes(text).CopyTo(raw, at);

    private static void Put(byte[] raw, int at, int value) => BitConverter.GetBytes(value).CopyTo(raw, at);

    private static byte[] Affix(Action<byte[]> fill = null)
    {
        var raw = Table(ItemAffixReader.AffixSize, 1);
        const int s = 4;
        Put(raw, s, "of Warmth");
        BitConverter.GetBytes((ushort)100).CopyTo(raw, s + 0x22);
        Put(raw, s + 0x24, 2);
        Put(raw, s + 0x2C, 5);
        Put(raw, s + 0x30, 10);
        Put(raw, s + 0x34, -1);
        Put(raw, s + 0x44, -1);
        raw[s + 0x54] = 1;
        Put(raw, s + 0x58, 12);
        Put(raw, s + 0x5C, 33);
        Put(raw, s + 0x60, 40);
        raw[s + 0x64] = 1;
        raw[s + 0x65] = 9;
        raw[s + 0x66] = 0xFF;
        raw[s + 0x69] = 1;
        raw[s + 0x6A] = 2;
        raw[s + 0x70] = 3;
        raw[s + 0x71] = 4;
        raw[s + 0x73] = 0xFF;
        raw[s + 0x74] = 0xFF;
        raw[s + 0x75] = 4;
        fill?.Invoke(raw);
        return raw;
    }

    [Fact]
    public void AnAffixDecodesAtThe109Offsets()
    {
        var affix = Assert.Single(ItemAffixReader.DecodeAffixes(Affix(), "magicsuffix.bin", AffixKind.Suffix, Properties, TypeCodes));

        Assert.Equal("of Warmth", affix.Name);
        Assert.Equal(AffixKind.Suffix, affix.Kind);
        Assert.Equal(100, affix.Version);
        Assert.True(affix.IsExpansionOnly);
        Assert.True(affix.Spawnable);
        Assert.True(affix.Rare);
        Assert.Equal(12, affix.Level);
        Assert.Equal(40, affix.MaxLevel);
        Assert.Equal(9, affix.LevelRequirement);
        Assert.Equal(33, affix.Group);
        Assert.Equal(4, affix.Frequency);
        Assert.Null(affix.ClassSpecific);

        var mod = Assert.Single(affix.Mods);
        Assert.Equal("res-fire", mod.Property);
        Assert.Equal((5, 10), (mod.Min, mod.Max));

        Assert.Equal(["armo", "ring"], affix.ItemTypes);
        Assert.Equal(["staf", "wand"], affix.ExcludedItemTypes);
    }

    [Fact]
    public void AClassSpecificAffixNamesItsClass()
    {
        var raw = Affix(r => r[4 + 0x66] = (byte)CharacterClass.Amazon);

        var affix = Assert.Single(ItemAffixReader.DecodeAffixes(raw, "magicprefix.bin", AffixKind.Prefix, Properties, TypeCodes));

        Assert.Equal(CharacterClass.Amazon, affix.ClassSpecific);
    }

    [Fact]
    public void AnItemTypePastTheEndOfItemTypesIsRefused()
    {
        var raw = Affix(r => r[4 + 0x69] = 40);

        var error = Assert.Throws<InvalidDataException>(
            () => ItemAffixReader.DecodeAffixes(raw, "magicprefix.bin", AffixKind.Prefix, Properties, TypeCodes));
        Assert.Contains("past the 5 rows of itemtypes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APropertyPastTheEndOfPropertiesIsRefused()
    {
        var raw = Affix(r => Put(r, 4 + 0x24, 99));

        var error = Assert.Throws<InvalidDataException>(
            () => ItemAffixReader.DecodeAffixes(raw, "magicprefix.bin", AffixKind.Prefix, Properties, TypeCodes));
        Assert.Contains("past the 4 rows of properties", error.Message, StringComparison.Ordinal);
    }

    /// <summary>1.10's records are 144 bytes; a table of them must not be read with 1.09 offsets.</summary>
    [Fact]
    public void AnotherRecordSizeIsRefusedRatherThanGuessed()
    {
        var raw = Table(144, 2);

        var error = Assert.Throws<InvalidDataException>(
            () => ItemAffixReader.DecodeAffixes(raw, "magicprefix.bin", AffixKind.Prefix, Properties, TypeCodes));
        Assert.Contains("132 byte records", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameFieldThatIsNotTextIsRefused()
    {
        var raw = Affix(r => r[4] = 0x01);

        var error = Assert.Throws<InvalidDataException>(
            () => ItemAffixReader.DecodeAffixes(raw, "magicprefix.bin", AffixKind.Prefix, Properties, TypeCodes));
        Assert.Contains("non-printable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PropertiesAreTheirCodes()
    {
        var raw = Table(ItemAffixReader.PropertySize, 2);
        Put(raw, 4, "ac");
        Put(raw, 4 + ItemAffixReader.PropertySize, "red-dmg%");

        Assert.Equal(["ac", "red-dmg%"], ItemAffixReader.DecodeProperties(raw, "properties.bin"));
    }

    [Fact]
    public void ItemTypesLinkThroughSingleByteEquivalents()
    {
        var raw = Table(ItemAffixReader.ItemTypeSize, 3);
        Put(raw, 4, "    ");
        Put(raw, 4 + ItemAffixReader.ItemTypeSize, "armo");
        var boots = 4 + 2 * ItemAffixReader.ItemTypeSize;
        Put(raw, boots, "boot");
        raw[boots + 0x04] = 1;
        raw[boots + 0x05] = 0xFF;
        raw[boots + 0x19] = 1;
        raw[boots + 0x23] = 0xFF;
        raw[boots + 0x25] = 0xFF;

        var types = ItemAffixReader.DecodeItemTypes(raw, "itemtypes.bin");

        Assert.Equal("", types[0].Code);
        Assert.Equal("boot", types[2].Code);
        Assert.Equal("armo", types[2].Equivalent1);
        Assert.Null(types[2].Equivalent2);
        Assert.True(types[2].CanBeRare);
        Assert.Null(types[2].Class);
    }

    [Fact]
    public void ABaseItemDecodesItsCodeTypeAndVersion()
    {
        var raw = Table(ItemAffixReader.BaseItemSize, 1);
        Put(raw, 4, "Ring");
        Put(raw, 4 + 0x144, "rin ");
        BitConverter.GetBytes((ushort)0).CopyTo(raw, 4 + 0x190);
        raw[4 + 0x197] = 1;
        raw[4 + 0x1B3] = 2;
        raw[4 + 0x1B4] = 0xFF;

        var item = Assert.Single(ItemAffixReader.DecodeBaseItems(raw, "misc.bin", BaseItemKind.Misc, TypeCodes));

        Assert.Equal(("Ring", "rin", "ring"), (item.Name, item.Code, item.Type));
        Assert.Null(item.Type2);
        Assert.False(item.IsExpansionOnly);
        Assert.Equal(1, item.Level);
    }

    [Fact]
    public void ACubeRecipeKeepsItsStringsAndFixedMods()
    {
        var raw = Table(ItemAffixReader.CubeRecipeSize, 1);
        const int s = 4;
        raw[s] = 1;
        raw[s + 0x05] = 0xFF;
        raw[s + 0x06] = 2;
        BitConverter.GetBytes((ushort)100).CopyTo(raw, s + 0x08);
        Put(raw, s + 0x0A, "\"ring,mag\"");
        Put(raw, s + 0x2A, "jew");
        Put(raw, s + 0xEC, "\"usetype,crf\"");
        raw[s + 0x10E] = 50;
        raw[s + 0x10F] = 50;
        Put(raw, s + 0x11C, 3);
        BitConverter.GetBytes((ushort)44).CopyTo(raw, s + 0x120);
        BitConverter.GetBytes((short)5).CopyTo(raw, s + 0x122);
        BitConverter.GetBytes((short)4).CopyTo(raw, s + 0x124);
        for (var mod = 1; mod < 5; mod++)
        {
            Put(raw, s + 0x11C + mod * 12, -1);
        }

        var recipe = Assert.Single(ItemAffixReader.DecodeCubeRecipes(raw, "cubemain.bin", Properties));

        Assert.True(recipe.Enabled);
        Assert.True(recipe.IsCraft);
        Assert.Null(recipe.Class);
        Assert.Equal(["ring,mag", "jew"], recipe.Inputs);
        Assert.Equal("usetype,crf", recipe.Output);
        Assert.Equal((50, 50), (recipe.PlayerLevelPercent, recipe.ItemLevelPercent));
        var mod0 = Assert.Single(recipe.Mods);
        Assert.Equal(("gethit-skill", 44, 5, 4), (mod0.Property, mod0.Param, mod0.Min, mod0.Max));
    }
}
