using System;
using System.Collections.Generic;

namespace D2NG.MuleManager.Contracts;

// The shapes the API sends and the UI reads. The UI compiles this file too, so both sides change together.

public enum GameMode
{
    Classic,
    Expansion,
}

/// <summary>One stat line of an item, with the range one affix could have rolled it in when the tables know it.</summary>
public sealed record StatLineDto(string Stat, string Label, string Text, double Value, int? Min, int? Max);

public sealed record ItemDto(
    string Id,
    string Name,
    string BaseName,
    string Quality,
    string Classification,
    bool Ethereal,
    int Sockets,
    int ItemLevel,
    int? RequiredLevel,
    string Account,
    string Character,
    string Container,
    int X,
    int Y,
    bool? Expansion,
    List<StatLineDto> Stats,
    string Set = null);

/// <summary>Items that come in many identical copies, counted rather than listed: gems, runes, potions.</summary>
public sealed record StackDto(string ItemName, string Name, string Classification, int Count, int Holders, List<HolderDto> TopHolders);

public sealed record HolderDto(string Account, string Character, int Count);

public sealed record FacetDto(string Value, int Count);

public sealed record StockResponse(
    GameMode Mode,
    int Total,
    int Page,
    int PageSize,
    List<ItemDto> Items,
    List<StackDto> Stacks,
    List<FacetDto> Qualities,
    List<FacetDto> Types,
    List<string> Stats,
    DateTimeOffset ReadAt);

public sealed record MuleCharacterDto(string Character, DateTimeOffset SeenAt, int FreeCells, int[] FitProfile, int Items, int Stacked);

public sealed record MuleAccountDto(string Account, List<MuleCharacterDto> Characters);

public sealed record MulesResponse(GameMode Mode, List<MuleAccountDto> Accounts, List<string> UnknownMode, List<string> ConfiguredAccounts);

public sealed record ResistDto(string Element, int Raw, int Effective, int Cap, int OverCap);

/// <summary>A set worn in part or whole: pieces worn, pieces in the set, and the bonus lines that brings.</summary>
public sealed record SetBonusDto(string Name, int Worn, int Total, List<string> Lines);

public sealed record EvaluationDto(double Life, double Mana, double Strength, double Dexterity, double CastFrames, double HitRecoveryFrames, double Block, List<ResistDto> Resists, List<SetBonusDto> Sets = null, double? AttackFrames = null);

public sealed record GearCharacterDto(
    string Account,
    string Character,
    string Class,
    int Level,
    bool Expansion,
    DateTimeOffset SeenAt,
    string DefaultPreset,
    Dictionary<string, ItemDto> Equipped,
    EvaluationDto Hell,
    bool TwoHanded,
    int Charms = 0);

public sealed record CharactersResponse(GameMode Mode, List<GearCharacterDto> Characters, List<string> GearAccounts);

public sealed record StatWeightDto(string Stat, double Weight, double? Cap, string Slot);

public sealed record PresetDto(string Id, string Name, string Description, string Mode, List<StatWeightDto> Weights, Dictionary<string, double> ResistWeights, double ColdOverCap);

public sealed record UpgradeRequest(
    string Account,
    string Character,
    string Slot,
    string PresetId,
    string Mode,
    string Difficulty,
    List<StatWeightDto> Weights,
    int Top = 60);

public sealed record ChangeDto(string Stat, string Label, double Before, double After, double Delta);

/// <summary>
/// <c>Replaces</c> is the carried charm a charm swap takes the place of; null for a worn slot. <c>Copies</c> counts the
/// identical items the same character holds, listed once.
/// </summary>
public sealed record CandidateDto(ItemDto Item, double Score, string Standing, string Reason, List<ChangeDto> Changes, EvaluationDto After, ItemDto Replaces = null, int Copies = 1);

public sealed record UpgradeResponse(
    string Slot,
    ItemDto Current,
    EvaluationDto Before,
    int Considered,
    List<CandidateDto> Candidates,
    List<string> Columns,
    bool TablesLoaded);

public sealed record ModeCountDto(int Characters, int Items, int Stacked);

public sealed record MetaResponse(
    List<PresetDto> Presets,
    bool TablesLoaded,
    ModeCountDto Classic,
    ModeCountDto Expansion,
    int UnknownModeCharacters,
    int GearCharacters,
    int MuleAccounts,
    int GearAccounts,
    DateTimeOffset? LastRead,
    bool RefreshBusy);

public sealed record AccountProgressDto(string Account, string Kind, string State, int Characters, int Recorded, int Skipped, int Failed, string Current, string Problem);

public sealed record RefreshJobDto(Guid Id, string Description, string State, DateTimeOffset QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, List<AccountProgressDto> Accounts);

/// <summary><c>Scope</c> is everything, mules or gear; <c>Account</c> and <c>Character</c> narrow it.</summary>
public sealed record RefreshRequest(string Scope, string Account, string Character, bool Force);
