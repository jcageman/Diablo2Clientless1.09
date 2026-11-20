using D2NG.Core.D2GS.Players;
using D2NG.Leveling.Plans;
using System.Text;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

// Checks the leveling bot's build plans and prints how each one spends its points, level by level,
// for review before a character spends anything.
//
//   LevelingPlans --skills "<bot folder>\data\skills.json" --plans "<bot folder>\leveling-plans" [--to 80] [--out plans.md]

var skillsPath = ValueOf("--skills");
var plansFolder = ValueOf("--plans");
if (skillsPath == null || plansFolder == null)
{
    Console.Error.WriteLine("usage: LevelingPlans --skills <skills.json> --plans <folder> [--to <level>] [--out <file.md>]");
    return 2;
}

var toLevel = int.Parse(ValueOf("--to") ?? "80", System.Globalization.CultureInfo.InvariantCulture);
var catalog = SkillCatalog.Load(skillsPath);
var report = new StringBuilder();
var failed = false;

foreach (var plan in BuildPlan.LoadFolder(plansFolder))
{
    report.AppendLine($"## {plan.Name} ({plan.Class})").AppendLine();
    var problems = PlanCheck.Problems(plan, catalog);
    if (problems.Count > 0)
    {
        failed = true;
        report.AppendLine("Problems:");
        foreach (var problem in problems)
        {
            report.AppendLine($"- {problem}");
        }

        report.AppendLine();
        continue;
    }

    var rows = PlanSimulation.Run(plan, catalog, 99);
    report.AppendLine("| Step | Reached at level |").AppendLine("|---|---|");
    foreach (var step in plan.Skills)
    {
        var reached = step.Save
            ? $"holds until {step.UntilLevel}"
            : rows.FirstOrDefault(row => row.Skills.GetValueOrDefault(step.Skill!.Value) >= step.Level)?.Level.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "never";
        report.AppendLine($"| {step} | {reached} |");
    }

    report.AppendLine().AppendLine("| Level | Points spent | Attack | Str/Dex/Vit/Ene | Unspent |").AppendLine("|---|---|---|---|---|");
    foreach (var row in rows.Take(toLevel))
    {
        var spent = string.Join(", ", row.SkillsSpent.GroupBy(skill => skill)
            .Select(group => $"{group.Key} {row.Skills[group.Key]}" + (group.Count() > 1 ? $" (+{group.Count()})" : "")));
        var combat = row.Combat == null ? "" : row.Combat.Attack
            + (row.Combat.Secondary is { } secondary ? $" + {secondary}" : "")
            + (row.Combat.Aura is { } aura ? $" [{aura}]" : "");
        var stats = string.Join("/", new[] { Attribute.Strength, Attribute.Dexterity, Attribute.Vitality, Attribute.Energy }.Select(stat => row.Stats[stat]));
        report.AppendLine($"| {row.Level} | {spent} | {combat} | {stats} | {(row.UnspentSkillPoints > 0 ? row.UnspentSkillPoints : "")} |");
    }

    var final = rows[toLevel - 1].Skills.Where(pair => pair.Value > 0).OrderByDescending(pair => pair.Value)
        .Select(pair => $"{pair.Key} {pair.Value}");
    report.AppendLine().AppendLine($"At level {toLevel}: {string.Join(", ", final)}").AppendLine();
}

if (ValueOf("--out") is { } outPath)
{
    File.WriteAllText(outPath, report.ToString());
    Console.WriteLine($"Wrote {outPath}");
}
else
{
    Console.Write(report);
}

return failed ? 1 : 0;

string? ValueOf(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
