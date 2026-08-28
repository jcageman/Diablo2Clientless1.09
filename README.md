# D2NG
[![CircleCI branch](https://img.shields.io/circleci/project/github/jcageman/Diablo2Clientless1.09/master.svg)](https://app.circleci.com/pipelines/github/jcageman/Diablo2Clientless1.09?branch=master)
![GitHub](https://img.shields.io/github/license/jcageman/D2NG.svg)
![GitHub contributors](https://img.shields.io/github/contributors/jcageman/D2NG.svg)
![GitHub commit activity](https://img.shields.io/github/commit-activity/m/jcageman/D2NG.svg)

## Building the project
This project builds with .NET 10 and can be built by running `dotnet build` from the solution root.

## Functionality
Initially based on https://github.com/dkuwahara/D2NG, but now with a lot more features.

- Bots: Mephisto, Travincal, Pindle, Cows, CS, and Baal
- Pathing through d2mapapi
- NIP-based pickup and keep rules
- Gambling and muling

## Configuring
The bot accepts three configuration files:

```text
ConsoleBot.exe config="C:\path\to\bot.json" muleconfig="C:\path\to\mule.json" pickitconfig="C:\path\to\pickitconfig-expansion.json"
```

Do not commit credentials, tokens, server addresses, or machine-specific paths. Use placeholders while documenting configuration:

```json
{
  "bot": {
    "realm": "<realm-address>",
    "keyOwner": "<key-owner>",
    "gameNamePrefix": "<game-prefix>",
    "gamePassword": "<game-password>",
    "gameDescriptions": ["<description>"],
    "difficulty": "hell",
    "channelToJoin": "",
    "gamefolder": "C:\\path\\to\\diablo-ii",
    "botType": "mephisto",
    "logFile": "bot.log",
    "logLevel": "Information",
    "pickitLogFile": "bot-pickit.log",
    "chicken": {
      "lifeChickenPercent": 0.2,
      "lifeChickenAbsolute": 0,
      "useHealthPotionPercent": 0.9,
      "useRejuvenationPercent": 0.3,
      "useManaPotionPercent": 0.3,
      "minPotionIntervalMs": 700
    },
    "mephisto": {
      "username": "<username>",
      "password": "<password>",
      "character": "<character>",
      "chicken": {
        "lifeChickenAbsolute": 400
      }
    }
  },
  "externalMessaging": {
    "telegramApiKey": "<telegram-api-key>",
    "telegramChatId": 0
  },
  "map": {
    "apiUrl": "http://localhost:8080"
  }
}
```

The required `pickitconfig` file contains the NIP root and configurable bot decisions:

```json
{
  "pickit": {
    "nipDirectory": "C:\\path\\to\\nips\\Expansion",
    "gamble": {
      "rules": [
        { "itemNames": ["amulet"], "minimumCharacterLevel": 90 },
        { "itemNames": ["boots", "heavyBoots"] }
      ]
    },
    "externalItemBlacklist": [
      { "itemNames": ["ring"], "quality": "unique" },
      { "classification": "gem" },
      { "itemNames": ["solRune", "nefRune"] },
      { "itemNames": ["heavyGloves", "sharkskinGloves", "vampireboneGloves"], "quality": "magical" }
    ]
  }
}
```

`nipDirectory` points directly to either the `Classic` or `Expansion` NIP folder. Select the matching pickit configuration in the launcher; the bot no longer chooses a NIP folder from the character game mode. Gamble rules are evaluated in list order; keep them sorted by `minimumCharacterLevel` descending. The first rule whose minimum level is reached controls which item names can be gambled. An omitted minimum level means `0`. The external item list is a blacklist: matching items are not sent to the external messaging client.

`logLevel` sets the minimum level written to `logFile` (`Verbose` through `Fatal`, default
`Information`); it can also be given on the command line as `logLevel=Debug`. `pickitLogFile` is
optional and receives one line per item the pickit judged - what was picked up, what was left on the
ground, and after identification what was kept versus sold - each with the NIP rule (`file:line`)
behind the decision.

`chicken` controls when the bot drinks and when it abandons a game. Thresholds are fractions of
maximum, except `lifeChickenAbsolute`, which is a raw hit point floor and is disabled at `0`. A
`chicken` block on an account replaces the bot-level block entirely, so a run mixing builds can give
each character its own numbers. On patch 1.09 Battle Orders raises maximum life and mana without
raising the current values, so a fraction can collapse without any damage being taken; the bot
detects that, drinks a single potion, and then only acts on fractions once it has seen real damage.

The `pickit` section also takes an optional `inventory` block. `bottomCharmRowsToNotTouch` (default
4) is the number of inventory rows, counted from the bottom, in which charms are left alone. Items
the bot itself needs - the town portal and identify tomes, the cube, rejuvenation potions and an
amazon's ammunition - are always kept and are not configurable.

The `muleconfig` file controls mule accounts and filters. An optional `neverMule` list of filters marks items that are sold rather than given a mule slot; omit it to use the built-in list of flawless gems, or give an empty list to mule everything. Keep account credentials in private local files and never publish them.

Example mule configuration using deliberately fake credentials:

```json
{
  "mule": {
    "accounts": [
      {
        "username": "example-mule-one",
        "password": "not-a-real-password",
        "excludedCharacters": ["example-character"],
        "matchesAny": [
          {
            "matchesAll": [
              { "itemName": "ring", "qualityType": "unique" }
            ]
          },
          {
            "matchesAll": [
              { "itemName": "perfectSkull" }
            ]
          }
        ]
      },
      {
        "username": "example-mule-two",
        "password": "not-a-real-password",
        "includedCharacters": ["example-character"],
        "matchesAny": [
          {
            "matchesAll": [
              { "itemName": "perfectDiamond" }
            ]
          },
          {
            "matchesAll": [
              { "itemName": "perfectAmethyst" }
            ]
          }
        ]
      }
    ]
  }
}
```

The bot also requires a configured MapClient from https://github.com/jcageman/d2mapapi. A local development instance commonly uses port 8080, but the address should remain environment-specific.

## Analyzing Packets
The PacketSniffer project can analyze packets sent by the game server. It is useful for checking protocol behavior and version differences.

## Future ideas
1. Add auto-leveling and rushing bots.
