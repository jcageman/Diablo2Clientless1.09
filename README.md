# D2NG
[![CircleCI branch](https://img.shields.io/circleci/project/github/jcageman/Diablo2Clientless1.09/master.svg)](https://app.circleci.com/pipelines/github/jcageman/Diablo2Clientless1.09?branch=master)
[![Codacy Badge](https://api.codacy.com/project/badge/Grade/0b90f6cdc4b0445296de25748e066738)](https://www.codacy.com?utm_source=github.com&amp;utm_medium=referral&amp;utm_content=jcageman/D2NG&amp;utm_campaign=Badge_Grade)
[![CodeFactor](https://www.codefactor.io/repository/github/jcageman/D2NG-1.09/badge)](https://www.codefactor.io/repository/github/jcageman/D2NG-1.09)
![GitHub](https://img.shields.io/github/license/jcageman/D2NG.svg)
![GitHub contributors](https://img.shields.io/github/contributors/jcageman/D2NG.svg)
![GitHub commit activity](https://img.shields.io/github/commit-activity/m/jcageman/D2NG.svg)

## Building the project
This project builds with .NET Core 3.1 and can be built by running `dotnet build` on the command line from the root of the Solution.

## Funtionality
- Bots: Mephisto, Travincal, Cows (and it's easy to add new bots)
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
		"mephisto" : {"username": "test", "password": "testpass", "character" : "testcharacter"}
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

## Analyzing Packets
Besides the ConsoleBot there is another CLI project called PacketSniffer, which you can use to analyse packets send by the bot, but also by any started diablo client connected to a realm. The PacketSniffer currently only monitors packets send by the game server (i.e. the packets send when you are in a game). This is 100% safe to use in all cases and undetectable. You could use this is you are not sure if your server is using the same version of 1.09d or if you simply want to analyze the game server yourself.
