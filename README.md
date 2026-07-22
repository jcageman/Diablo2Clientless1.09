# D2NG
[![CircleCI branch](https://img.shields.io/circleci/project/github/jcageman/Diablo2Clientless1.09/master.svg)](https://app.circleci.com/pipelines/github/jcageman/Diablo2Clientless1.09?branch=master)
![GitHub](https://img.shields.io/github/license/jcageman/D2NG.svg)
![GitHub contributors](https://img.shields.io/github/contributors/jcageman/D2NG.svg)
![GitHub commit activity](https://img.shields.io/github/commit-activity/m/jcageman/D2NG.svg)

## Building the project
This project builds with .NET 10 and can be built by running `dotnet build` on the command line from the root of the Solution.

## Functionality
Initially based on https://github.com/dkuwahara/D2NG, but now with a lot more features.

- Bots: Mephisto, Travincal, Pindle, Cows, Cs, Baal (and it's easy to add new bots)
- Pathing module using https://github.com/jcageman/d2mapapi
- Gambling, pickit, muling

## Configuring
Commandline parameters: config="D:\projects\diablo2bot\config.json" muleconfig="D:\projects\diablo2bot\muleconfig.json" 
The above `config.json` should look as follows:
```
{
    "bot": {
        "realm": "xx.xxx.xxx.xxx",
        "keyOwner": "test",
        "gameNamePrefix": "test",
        "gamePassword": "x",
        "gameDescriptions": ["trade","offer soj"],
        "difficulty" : "hell",
        "channelToJoin": "",
        "gamefolder": "C:\\Diablo II1.09D",
        "botType" : "mephisto",
	"logFile": "meph1log.txt",
	"mephisto" : {"username": "test", "password": "testpass", "character" : "testcharacter"},
	"humanization": {
		"enabled": true,
		"actionJitterMinMs": 0,
		"actionJitterMaxMs": 150,
		"townTaskPauseMinMs": 300,
		"townTaskPauseMaxMs": 1500,
		"waypointPauseMinMs": 400,
		"waypointPauseMaxMs": 1800,
		"preGameCreateDelayMinSeconds": 3,
		"preGameCreateDelayMaxSeconds": 15,
		"shortBreakEveryGamesMin": 8,
		"shortBreakEveryGamesMax": 20,
		"shortBreakDurationMinSeconds": 90,
		"shortBreakDurationMaxSeconds": 300,
		"longBreakEveryGamesMin": 40,
		"longBreakEveryGamesMax": 80,
		"longBreakDurationMinSeconds": 600,
		"longBreakDurationMaxSeconds": 1800,
		"joinStaggerMinSeconds": 5,
		"joinStaggerMaxSeconds": 25
	},
	"pickitThresholdScaling": {
		"defaultPercent": 100,
		"ringPercent": 100,
		"glovesPercent": 100,
		"bootsPercent": 100,
		"helmsPercent": 100,
		"armorsPercent": 100,
		"amuletsPercent": 100,
		"shieldsPercent": 100,
		"weaponsPercent": 100,
		"beltsPercent": 100
	}
	},
    "externalMessaging" : {
        "telegramApiKey": "5231-xxerew",
        "telegramChatId": 1234
    },
    "map" : {
       "apiUrl" : "http://localhost:8080"
    }
}
```

The `bot.humanization` block is optional and disabled by default (omit it, or set `"enabled": false`, to keep the bot's timing exactly as before). When enabled, it adds randomized delays to make the bot's pacing feel less mechanical:
- `actionJitterMinMs`/`actionJitterMaxMs`: extra random delay added on top of existing NPC/cube/inventory interaction pauses.
- `townTaskPauseMinMs`/`townTaskPauseMaxMs`: pause inserted between town chores (identify, sell, repair, resurrect merc, stash).
- `waypointPauseMinMs`/`waypointPauseMaxMs`: pause before and after taking a waypoint.
- `preGameCreateDelayMinSeconds`/`preGameCreateDelayMaxSeconds`: random delay before creating a new game.
- `shortBreakEveryGamesMin`/`Max` and `shortBreakDurationMinSeconds`/`MaxSeconds`: periodic short breaks based on games played.
- `longBreakEveryGamesMin`/`Max` and `longBreakDurationMinSeconds`/`MaxSeconds`: periodic longer breaks based on games played.
- `joinStaggerMinSeconds`/`joinStaggerMaxSeconds`: randomizes how long multi-client bots wait before joining a game.

The `bot.pickitThresholdScaling` block is optional and defaults to `100` for all values. Use it to make keep rules more or less strict by item type for threshold-based stats.
- `defaultPercent`: fallback percent used when a type-specific percent is not set.
- `ringPercent`, `glovesPercent`, `bootsPercent`, `helmsPercent`, `armorsPercent`, `amuletsPercent`, `shieldsPercent`, `weaponsPercent`, `beltsPercent`: per-item-type threshold scaling.
- Scaling formula: `floor(baseThreshold * percent / 100)`.
- Example: if a ring rule requires `GetTotalResistFrLrCr() >= 60` and `ringPercent` is `50`, the effective threshold becomes `>= 30`.

The realm ip address can be retrieved with a tool like https://www.wireshark.org/. Start filtering on tcp port 6112 (in wireshark the filter would be `tcp.port == 6112` as filter). You should receive packets and as soon as you enter the login screen. The source or destination address of the packets is the ip address you need to fill in for the bot.realm parameter. Source/Destination can be your own ip or the ip of the diablo 2 server, so double check the ip you enter is not your own by using something like https://www.whatismyip.com/

The above `muleconfig.json` should look as follows:
```
{
	"mule": {
		"accounts": [
			{
				"username": "test1",
				"password": "testpass",
				"excludedCharacters" : ["testchar1"],
				"matchesAny": [
					{
						"matchesAll" : [{"itemName" : "ring", "qualityType" : "unique" }]
					},
					{
						"matchesAll" : [{"itemName" : "perfectSkull"}]
					}
				]
			},
			{
				"username": "test2",
				"password": "testpass",
				"excludedCharacters" : ["testchar2", "testchar3"],
				"matchesAny": [
					{
						"matchesAll" : [{"notFilter" : true, "itemName" : "ring", "qualityType" : "unique" }, {"notFilter" : true, "classificationType" : "gem"}]
					}
				]
			},
			{
				"username": "test2",
				"password": "testpass",
				"includedCharacters" : ["testchar3"],
				"matchesAny": [
					{
						"matchesAll" : [{"itemName" : "perfectDiamond"}]
					},
					{
						"matchesAll" : [{"itemName" : "perfectAmethyst"}]
					},
					{
						"matchesAll" : [{"itemName" : "perfectEmerald"}]
					},
					{
						"matchesAll" : [{"itemName" : "perfectRuby"}]
					},
					{
						"matchesAll" : [{"itemName" : "perfectDiamond"}]
					}
				]
			}
		]
	}
}
```
See https://core.telegram.org/bots for configuration of the telegram bot

To be able to run the bot you also need to configure the MapClient from https://github.com/jcageman/d2mapapi
If you run this locally this runs on localhost port 8080 (which is also the default in the above config)

## Future ideas
1. Implement pickit using .nip files (used in many other bots)
2. Improve chicken/pot behavior (currently runs in a separate thread, probably better to use task scheduling)
3. Auto leveling / rushing bots

## Analyzing Packets
Besides the ConsoleBot there is another CLI project called PacketSniffer, which you can use to analyse packets send by the bot, but also by any started diablo client connected to a realm. The PacketSniffer currently only monitors packets send by the game server (i.e. the packets send when you are in a game). This is 100% safe to use in all cases and undetectable. You could use this is you are not sure if your server is using the same version of 1.09d or if you simply want to analyze the game server yourself.
