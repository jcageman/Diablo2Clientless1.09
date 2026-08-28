using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Pickit.Nip;

namespace D2NG.Pickit.Tests;

public class NipParserCoverageTests
{
    private static readonly string NipRoot = Path.Combine(AppContext.BaseDirectory, "Nips");

    public static IEnumerable<object[]> NipDirectories()
    {
        yield return [Path.Combine(NipRoot, "Classic")];
        yield return [Path.Combine(NipRoot, "Expansion")];
    }

    public static IEnumerable<object[]> NipRules()
    {
        foreach (var directory in NipDirectories().Select(data => (string)data[0]))
        {
            foreach (var rule in NipParser.ParseDirectory(directory))
            {
                yield return [rule.Source, rule.Line];
            }
        }
    }

    // Classic has no runes, jewels or charms, so its tree is a subset of the expansion one rather
    // than a mirror of it.
    [Fact]
    public void EveryClassicCategoryAlsoExistsInExpansion()
    {
        var classicFiles = GetNipFiles("Classic").Select(Path.GetFileName).Order().ToArray();
        var expansionFiles = GetNipFiles("Expansion").Select(Path.GetFileName).Order().ToArray();

        Assert.NotEmpty(classicFiles);
        Assert.NotEmpty(expansionFiles);
        Assert.Empty(classicFiles.Except(expansionFiles));
    }

    [Theory]
    [InlineData("Runes.nip")]
    [InlineData("Jewels.nip")]
    [InlineData("GrandCharms.nip")]
    [InlineData("LargeCharms.nip")]
    [InlineData("SmallCharms.nip")]
    public void ExpansionOnlyCategoriesAreAbsentFromClassic(string fileName)
    {
        var classicFiles = GetNipFiles("Classic").Select(Path.GetFileName).ToArray();

        Assert.DoesNotContain(fileName, classicFiles);
    }

    [Theory]
    [MemberData(nameof(NipDirectories))]
    public void EveryNipFileParsesToAtLeastOneRule(string directory)
    {
        var files = GetNipFiles(Path.GetFileName(directory));

        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var rules = NipParser.ParseFile(file);

            Assert.NotEmpty(rules);
            Assert.All(rules, rule => Assert.False(string.IsNullOrWhiteSpace(rule.Raw)));
        }
    }

    [Theory]
    [MemberData(nameof(NipRules))]
    public void EveryNipRuleHasAnIndividualEvaluationCase(string source, int line)
    {
        var rule = NipParser.ParseFile(source).Single(parsedRule => parsedRule.Line == line);
        var item = CreateRepresentativeItem(ClassificationType.Ring);
        var context = new NipEvaluationContext(item, CharacterClass.Sorceress);

        Assert.NotNull(rule.PropertyCondition);
        rule.PropertyCondition!.Evaluate(context);
        rule.StatCondition?.Evaluate(context);
        rule.MatchesPickup(context);
        rule.MatchesKeep(context);
    }

    [Theory]
    [MemberData(nameof(NipDirectories))]
    public void CombinedRulesetContainsEveryRuleFromEveryFile(string directory)
    {
        var files = GetNipFiles(Path.GetFileName(directory));
        var combinedRules = NipParser.ParseDirectory(directory);
        var expectedRules = files.Sum(file => NipParser.ParseFile(file).Count);

        Assert.Equal(expectedRules, combinedRules.Count);
        Assert.Equal(files, combinedRules.Select(rule => rule.Source).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Theory]
    [MemberData(nameof(NipDirectories))]
    public void EveryParsedExpressionEvaluatesForEveryItemClassification(string directory)
    {
        var rules = NipParser.ParseDirectory(directory);

        Assert.NotEmpty(rules);
        foreach (var classification in Enum.GetValues<ClassificationType>())
        {
            var item = CreateRepresentativeItem(classification);
            var context = new NipEvaluationContext(item, CharacterClass.Sorceress);

            foreach (var rule in rules)
            {
                rule.PropertyCondition?.Evaluate(context);
                rule.StatCondition?.Evaluate(context);
                rule.MatchesPickup(context);
                rule.MatchesKeep(context);
            }
        }
    }

    [Theory]
    [MemberData(nameof(NipDirectories))]
    public void EveryFieldReferenceInEveryNipFileResolves(string directory)
    {
        var context = new NipEvaluationContext(CreateRepresentativeItem(ClassificationType.Ring), CharacterClass.Sorceress);
        var unresolved = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in GetNipFiles(Path.GetFileName(directory)))
        {
            foreach (var line in File.ReadAllLines(file))
            {
                var commentStart = line.IndexOf("//", StringComparison.Ordinal);
                var code = commentStart >= 0 ? line[..commentStart] : line;

                foreach (var field in Regex.Matches(code, @"\[([A-Za-z0-9_]+)\]").Select(match => match.Groups[1].Value))
                {
                    // An unknown field is not a parse error: it falls back to a text literal of its
                    // own name, which compares as zero and silently makes the rule unmatchable.
                    if (context.TryResolve(field, out var value)
                        && value.Number == null
                        && string.Equals(value.Text, field, StringComparison.Ordinal))
                    {
                        unresolved.Add($"{Path.GetFileName(file)}: [{field}]");
                    }
                }
            }
        }

        Assert.True(unresolved.Count == 0, $"Unresolved nip fields, these silently never match:{Environment.NewLine}{string.Join(Environment.NewLine, unresolved)}");
    }

    [Theory]
    [MemberData(nameof(NipDirectories))]
    public void GoldItemRulesArePickupOnly(string directory)
    {
        var goldItemsFile = Path.Combine(directory, "GoldItems.nip");
        var rules = NipParser.ParseFile(goldItemsFile);

        Assert.NotEmpty(rules);
        Assert.All(rules, rule =>
        {
            Assert.True(rule.PickupOnly);
            Assert.Null(rule.StatCondition);
        });
    }

    // Gold reaches the bot with the identified flag set, so it is only ever judged by the pickup
    // side of a rule. Both trees have to carry the amount rule the old C# pickit had, or a run walks
    // past every pile it drops.
    [Theory]
    [MemberData(nameof(NipDirectories))]
    public void HighValueGoldMatchesAPickupRule(string directory)
    {
        var rules = NipParser.ParseDirectory(directory);

        Assert.Contains(rules, rule => rule.MatchesPickup(GoldContext(5001)));
        Assert.DoesNotContain(rules, rule => rule.MatchesPickup(GoldContext(5000)));
    }

    private static NipEvaluationContext GoldContext(uint amount)
    {
        var gold = TestItemFactory.Create(ClassificationType.Gold, ItemName.Gold, QualityType.Normal);
        gold.IsGold = true;
        gold.Amount = amount;

        return new NipEvaluationContext(gold, CharacterClass.Sorceress);
    }

    [Theory]
    [MemberData(nameof(NipDirectories))]
    public void NoNipFileContainsDuplicateRules(string directory)
    {
        foreach (var file in GetNipFiles(Path.GetFileName(directory)))
        {
            var rules = NipParser.ParseFile(file);
            var duplicateRules = rules
                .GroupBy(rule => rule.Raw.Trim(), StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();

            Assert.True(duplicateRules.Length == 0, $"Duplicate rules in {file}:{Environment.NewLine}{string.Join(Environment.NewLine, duplicateRules)}");
        }
    }

    private static string[] GetNipFiles(string tree)
    {
        var directory = Path.Combine(NipRoot, tree);
        return Directory.EnumerateFiles(directory, "*.nip", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Item CreateRepresentativeItem(ClassificationType classification)
    {
        return new Item
        {
            Classification = classification,
            Name = ItemName.Ring,
            Quality = QualityType.Rare,
            IsIdentified = true,
            Properties = []
        };
    }
}