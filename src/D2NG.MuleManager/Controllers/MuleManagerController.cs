using D2NG.Mule;
using D2NG.Mule.Upgrades;
using D2NG.MuleManager.Configuration;
using D2NG.MuleManager.Contracts;
using D2NG.MuleManager.Services;
using D2NG.MuleManager.Services.Refresh;
using D2NG.MuleManager.Services.Stock;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace D2NG.MuleManager.Controllers;

[ApiController]
[Route("api")]
public class MuleManagerController : ControllerBase
{
    private const int PageSize = 100;
    private static readonly string[] QualityOrder = ["Unique", "Set", "Crafted", "Rare", "Magical", "Superior", "Normal", "Inferior"];

    private readonly StockCache _stock;
    private readonly GearTablesProvider _tables;
    private readonly PresetProvider _presets;
    private readonly RefreshQueue _refresh;
    private readonly MuleManagerConfiguration _configuration;

    public MuleManagerController(StockCache stock, GearTablesProvider tables, PresetProvider presets, RefreshQueue refresh, IOptions<MuleManagerConfiguration> configuration)
    {
        _stock = stock;
        _tables = tables;
        _presets = presets;
        _refresh = refresh;
        _configuration = configuration.Value;
    }

    [HttpGet("meta")]
    public async Task<MetaResponse> Meta()
    {
        var stock = await _stock.Get();
        ModeCountDto Count(GameMode mode)
        {
            var items = stock.Items.Where(i => ApiMapper.InMode(i.Expansion, mode) && ApiMapper.Relevant(i, mode)).ToList();
            return new ModeCountDto(stock.Mules.Count(m => ApiMapper.InMode(m.Expansion, mode)), items.Count(i => !ApiMapper.IsStacked(i)), items.Count(ApiMapper.IsStacked));
        }

        var seen = stock.Mules.Select(m => m.SeenAt).Concat(stock.Gear.Select(g => g.SeenAt)).ToList();
        return new MetaResponse(
            [.. _presets.Presets.Select(ApiMapper.Preset)],
            _tables.Tables != null,
            Count(GameMode.Classic),
            Count(GameMode.Expansion),
            stock.Mules.Count(m => m.Expansion == null),
            stock.Gear.Count,
            _configuration.Accounts.Count,
            _configuration.GearAccounts.Count,
            seen.Count > 0 ? seen.Max() : null,
            _refresh.IsBusy);
    }

    [HttpGet("stock")]
    public async Task<StockResponse> Stock(
        [FromQuery] string mode,
        [FromQuery] string q,
        [FromQuery(Name = "quality")] string[] qualities,
        [FromQuery(Name = "type")] string[] types,
        [FromQuery(Name = "stat")] string[] stats,
        [FromQuery(Name = "sort")] string[] sorts,
        [FromQuery] int page = 0)
    {
        var gameMode = ApiMapper.ParseMode(mode);
        var stock = await _stock.Get();
        var tables = _tables.Tables;
        var inMode = stock.Items.Where(i => ApiMapper.InMode(i.Expansion, gameMode) && ApiMapper.Relevant(i, gameMode)).ToList();
        var gear = inMode.Where(i => !ApiMapper.IsStacked(i)).Select(i => ApiMapper.Item(i, tables)).ToList();

        bool Matches(ItemDto item) =>
            string.IsNullOrWhiteSpace(q)
            || $"{item.Name} {item.BaseName} {item.Account} {item.Character} {string.Join(' ', item.Stats.Select(s => s.Text))}".Contains(q.Trim(), StringComparison.OrdinalIgnoreCase);

        var searched = gear.Where(Matches).ToList();
        var typed = searched.Where(i => types.Length == 0 || types.Contains(i.Classification)).ToList();
        var qualified = typed.Where(i => qualities.Length == 0 || qualities.Contains(i.Quality)).ToList();
        var filtered = qualified.Where(i => stats.All(s => i.Stats.Any(l => l.Stat == s))).ToList();

        IOrderedEnumerable<ItemDto> ordered = null;
        foreach (var sort in sorts.Where(s => !string.IsNullOrEmpty(s)))
        {
            double Value(ItemDto i) => i.Stats.Where(l => l.Stat == sort).Sum(l => l.Value);
            ordered = ordered == null ? filtered.OrderByDescending(Value) : ordered.ThenByDescending(Value);
        }

        ordered = ordered == null
            ? filtered.OrderBy(i => Array.IndexOf(QualityOrder, i.Quality)).ThenBy(i => i.Name).ThenBy(i => i.Classification)
            : ordered.ThenBy(i => i.Name);

        var stacks = inMode.Where(ApiMapper.IsStacked)
            .Where(i => string.IsNullOrWhiteSpace(q) || $"{tables?.DisplayName(i) ?? i.ItemName} {i.ItemName}".Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(i => types.Length == 0 || types.Contains(i.ClassificationType))
            .GroupBy(i => i.ItemName)
            .Select(g =>
            {
                var holders = g.GroupBy(i => (i.AccountName, i.CharacterName)).Select(h => new HolderDto(h.Key.AccountName, h.Key.CharacterName, h.Count())).OrderByDescending(h => h.Count).ToList();
                return new StackDto(g.Key, tables?.DisplayName(g.First()) ?? g.Key, g.First().ClassificationType, g.Count(), holders.Count, [.. holders.Take(5)]);
            })
            .OrderBy(s => s.Classification).ThenByDescending(s => s.Count)
            .ToList();

        return new StockResponse(
            gameMode,
            filtered.Count,
            page,
            PageSize,
            [.. ordered.Skip(page * PageSize).Take(PageSize)],
            stacks,
            [.. typed.GroupBy(i => i.Quality).OrderBy(g => Array.IndexOf(QualityOrder, g.Key)).Select(g => new FacetDto(g.Key, g.Count()))],
            [.. searched.Where(i => qualities.Length == 0 || qualities.Contains(i.Quality)).GroupBy(i => i.Classification).OrderByDescending(g => g.Count()).Select(g => new FacetDto(g.Key, g.Count()))],
            [.. gear.SelectMany(i => i.Stats.Select(s => s.Stat)).Distinct().Order()],
            stock.ReadAt);
    }

    [HttpGet("mules")]
    public async Task<MulesResponse> Mules([FromQuery] string mode)
    {
        var gameMode = ApiMapper.ParseMode(mode);
        var stock = await _stock.Get();
        var items = stock.Items.ToLookup(i => (i.AccountName, i.CharacterName));
        var accounts = stock.Mules
            .Where(m => ApiMapper.InMode(m.Expansion, gameMode))
            .GroupBy(m => m.AccountName)
            .OrderBy(g => g.Key)
            .Select(g => new MuleAccountDto(g.Key, [.. g.OrderBy(m => m.CharacterName).Select(m =>
            {
                var held = items[(m.AccountName, m.CharacterName)].Where(i => ApiMapper.Relevant(i, gameMode)).ToList();
                return new MuleCharacterDto(m.CharacterName, m.SeenAt, m.FreeCells, m.FitProfile, held.Count(i => !ApiMapper.IsStacked(i)), held.Count(ApiMapper.IsStacked));
            })]))
            .ToList();
        return new MulesResponse(
            gameMode,
            accounts,
            [.. stock.Mules.Where(m => m.Expansion == null).Select(m => $"{m.AccountName}/{m.CharacterName}").Order()],
            [.. _configuration.Accounts.Select(a => a.Name)]);
    }

    [HttpGet("characters")]
    public async Task<CharactersResponse> Characters([FromQuery] string mode)
    {
        var gameMode = ApiMapper.ParseMode(mode);
        var stock = await _stock.Get();
        var tables = _tables.Tables;
        var characters = stock.Gear
            .Where(g => g.Expansion == (gameMode == GameMode.Expansion) && g.Level > 1)
            .OrderBy(g => g.CharacterName)
            .Select(g =>
            {
                var worn = g.Equipped.ToDictionary(e => e.Key, e => GearItem.From(e.Value, tables, g.Level));
                var charms = (g.Charms ?? []).Select(c => GearItem.From(c, tables, g.Level));
                var hell = GearEvaluation.Of(g, worn, charms, tables, Difficulty.Hell);
                return new GearCharacterDto(
                    g.AccountName, g.CharacterName, g.Class, g.Level, g.Expansion, g.SeenAt,
                    UpgradePresets.DefaultFor(_presets.Presets, g.Class, g.Equipped.GetValueOrDefault(GearSlots.RightHand)?.ClassificationType)?.Id,
                    g.Equipped.ToDictionary(e => e.Key, e => ApiMapper.Item(e.Value, tables)),
                    ApiMapper.Evaluation(hell),
                    worn.TryGetValue(GearSlots.RightHand, out var right) && GearEvaluation.TakesBothHands(right),
                    g.Charms?.Count ?? 0);
            })
            .ToList();
        return new CharactersResponse(gameMode, characters, [.. _configuration.GearAccounts.Select(a => a.Name)]);
    }

    [HttpPost("upgrades")]
    public async Task<ActionResult<UpgradeResponse>> Upgrades([FromBody] UpgradeRequest request)
    {
        var stock = await _stock.Get();
        var character = stock.Gear.FirstOrDefault(g =>
            g.AccountName.Equals(MuleNames.Normalize(request.Account ?? ""), StringComparison.OrdinalIgnoreCase)
            && g.CharacterName.Equals(MuleNames.Normalize(request.Character ?? ""), StringComparison.OrdinalIgnoreCase));
        if (character == null)
        {
            return NotFound($"No gear recorded for {request.Character}");
        }

        if (!GearSlots.All.Contains(request.Slot) && request.Slot != GearSlots.Charms)
        {
            return BadRequest($"Unknown slot {request.Slot}");
        }

        var preset = _presets.Presets.FirstOrDefault(p => p.Id == request.PresetId) ?? UpgradePresets.DefaultFor(_presets.Presets, character.Class, character.Equipped.GetValueOrDefault(GearSlots.RightHand)?.ClassificationType);
        preset = ApiMapper.WithWeights(preset, request.Weights);
        var mode = Enum.TryParse<ResistMode>(request.Mode, true, out var resistMode) ? resistMode : preset.Mode;
        var difficulty = Enum.TryParse<Difficulty>(request.Difficulty, true, out var parsed) ? parsed : Difficulty.Hell;
        var tables = _tables.Tables;

        var result = UpgradeFinder.Rank(character, request.Slot, stock.Items, preset, mode, difficulty, tables);
        var columns = preset.Weights
            .Where(w => (w.Slot == null || w.Slot == request.Slot) && w.Weight != 0)
            .OrderByDescending(w => Math.Abs(w.Weight))
            .Select(w => w.Stat)
            .Distinct()
            .Take(5)
            .ToList();

        return new UpgradeResponse(
            request.Slot,
            result.Current == null ? null : ApiMapper.Item(result.Current, tables),
            ApiMapper.Evaluation(result.Before),
            result.Considered,
            [.. result.Candidates
                .Select(c => (Candidate: c, Item: ApiMapper.Item(c.Item, tables)))
                .GroupBy(c => (c.Item.Account, c.Item.Character, c.Candidate.Score, c.Item.Name, c.Item.Ethereal, c.Item.Sockets, Pre: c.Item.RequiredLevel == 0,
                    Lines: string.Join("|", c.Item.Stats.Select(s => s.Text))))
                .Take(Math.Clamp(request.Top, 1, 200))
                .Select(g => (g.First().Candidate, g.First().Item, Copies: g.Count()))
                .Select(x => new CandidateDto(
                    x.Item,
                    x.Candidate.Score,
                    x.Candidate.Standing.ToString(),
                    x.Candidate.Reason,
                    [.. x.Candidate.Changes.Select(ch => new ChangeDto(ch.Stat, StatText.Short(ch.Stat), ch.Before, ch.After, ch.Delta))],
                    ApiMapper.Evaluation(x.Candidate.After),
                    x.Candidate.Replaces == null ? null : ApiMapper.Item(x.Candidate.Replaces, tables),
                    x.Copies))],
            columns,
            tables != null);
    }

    [HttpGet("refresh")]
    public IEnumerable<RefreshJobDto> RefreshJobs() => _refresh.Jobs.Select(ApiMapper.Job);

    [HttpPost("refresh")]
    public ActionResult<RefreshJobDto> Refresh([FromBody] RefreshRequest request)
    {
        var job = request.Scope?.ToLowerInvariant() switch
        {
            "everything" => _refresh.EnqueueEverything(),
            "mules" => _refresh.EnqueueMules(request.Account, request.Force),
            "gear" => _refresh.EnqueueGear(request.Account, request.Character),
            _ => null,
        };
        return job == null ? NotFound("No configured account matches") : ApiMapper.Job(job);
    }
}
