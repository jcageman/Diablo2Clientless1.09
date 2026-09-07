using D2NG.Core.D2GS.Enums;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots;

/// <summary>
/// Root configuration for a bot run, bound from the <c>bot</c> section of the JSON config file
/// passed on the command line. Bot specific sections (<c>cows</c>, <c>cs</c>, <c>baal</c>, ...)
/// live underneath this section and are bound to their own configuration types.
/// </summary>
public class BotConfiguration
{
    /// <summary>
    /// Battle.net realm (gateway) host to connect to, for example <c>localhost</c> or a private
    /// server address. Passed to the client on connect together with <see cref="KeyOwner"/> and
    /// <see cref="GameFolder"/>.
    /// </summary>
    [Required]
    public string Realm { get; set; }

    /// <summary>
    /// Prefix used to build game names. The bot appends an incrementing counter, so a prefix of
    /// <c>run</c> produces <c>run1</c>, <c>run2</c>, ... Both game creation and joining use this
    /// prefix, so every client in a multi-client run must share it.
    /// </summary>
    [Required]
    public string GameNamePrefix { get; set; }

    /// <summary>
    /// Password set on created games and used when joining them. May be empty for open games.
    /// </summary>
    public string GamePassword { get; set; }

    /// <summary>
    /// Game descriptions to create games with. The bot uses the first entry and rotates to the
    /// next one after repeated failures, which is how it moves off a bad game server.
    /// </summary>
    [Required]
    public List<string> GameDescriptions { get; set; }

    /// <summary>
    /// Difficulty that created games are opened on (Normal, Nightmare or Hell).
    /// </summary>
    [Required]
    public Difficulty Difficulty { get; set; }

    /// <summary>
    /// Optional chat channel to join after logging on to the realm. Leave empty to skip joining
    /// a channel.
    /// </summary>
    public string ChannelToJoin { get; set; }

    /// <summary>
    /// Name registered as the owner of the CD keys, sent during the Battle.net logon handshake.
    /// </summary>
    [Required]
    public string KeyOwner { get; set; }

    /// <summary>
    /// Path to the Diablo II installation folder. Used to hash the game executables during logon
    /// and to read the MPQ data files.
    /// </summary>
    [Required]
    public string GameFolder { get; set; }

    /// <summary>
    /// Which bot to run. Must match one of the registered bot names:
    /// <c>assist</c>, <c>baal</c>, <c>cows</c>, <c>cs</c>, <c>cube</c>, <c>mephisto</c>,
    /// <c>pindle</c>, <c>rush</c>, <c>test</c> or <c>travincal</c>.
    /// </summary>
    [Required]
    public string BotType { get; set; }

    /// <summary>
    /// Default drinking and chicken thresholds for every character in this configuration. An
    /// account may replace the whole block with its own. Omit both to use the built-in defaults.
    /// </summary>
    public Chicken.ChickenConfiguration Chicken { get; set; }

    /// <summary>
    /// Optional second log file receiving one line per item the pickit judged, with the item, the
    /// verdict and the nip rule (<c>file:line</c>) behind it - including the items that were left
    /// on the ground. When unset, picked-up items are logged to the main log at Information and
    /// left items at Debug instead.
    /// </summary>
    public string PickitLogFile { get; set; }

    /// <summary>
    /// Minimum log level to write, one of <c>Verbose</c>, <c>Debug</c>, <c>Information</c>,
    /// <c>Warning</c>, <c>Error</c> or <c>Fatal</c>. Defaults to <c>Information</c>; use
    /// <c>Debug</c> to get the detailed movement and combat tracing. Can also be given on the
    /// command line as <c>logLevel=Debug</c>. Read directly from configuration before the host is
    /// built.
    /// </summary>
    public string LogLevel { get; set; }

    /// <summary>
    /// Path of the log file to write to. The file is deleted on startup and then rolled at 20 MB.
    /// Read directly from configuration before the host is built, so it must always be present.
    /// </summary>
    [Required]
    public string LogFile { get; set; }
}
