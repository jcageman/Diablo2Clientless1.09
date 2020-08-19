# D2NG
[![CircleCI branch](https://img.shields.io/circleci/project/github/jcageman/D2NG-1.09/master.svg)](https://circleci.com/gh/jcageman/D2NG-1.09/tree/master)
[![Codacy Badge](https://api.codacy.com/project/badge/Grade/0b90f6cdc4b0445296de25748e066738)](https://www.codacy.com?utm_source=github.com&amp;utm_medium=referral&amp;utm_content=jcageman/D2NG&amp;utm_campaign=Badge_Grade)
[![CodeFactor](https://www.codefactor.io/repository/github/jcageman/D2NG-1.09/badge)](https://www.codefactor.io/repository/github/jcageman/D2NG-1.09)
![GitHub](https://img.shields.io/github/license/jcageman/D2NG.svg)
![GitHub contributors](https://img.shields.io/github/contributors/jcageman/D2NG.svg)
![GitHub commit activity](https://img.shields.io/github/commit-activity/m/jcageman/D2NG.svg)

## Building the project
This project builds with .NET Core 3.1 and can be built by running `dotnet build` on the command line from the root of the Solution.

## Configuring
ConsoleBot expects a `config.json` file that can be passed in via the "--config" flag. The `config.json` should look as follows:
```
{
    "bot": {
        "realm": "xx.xxx.xxx.xxx",
        "username": "yourusername",
        "password": "yourpassword",
        "character": "yourcharacter",
        "keyOwner": "nameofdiabloregistrationkey",
        "gameNamePrefix": "d2ng",
        "gamePassword": "d2ng",
        "gameDescription": "",
        "difficulty" : "hell",
        "channelToJoin": "d2ng",
        "gamefolder": "C:\\Diablo II1.09d",
        "botType" : "mephisto", -- current options are mephisto (sorc) or travincal (barb)
		"logFile": "bot1log.txt"
    },
    "externalMessaging" : {
        "telegramApiKey": "xxxxxxxxxxxxxxx",
        "telegramChatId": 123456
    },
    "map" : {
       "apiUrl" : "http://localhost:8080"
    }
}
```
See https://core.telegram.org/bots for configuration of the telegram bot

To be able to run the bot you also need to configure the MapClient from https://github.com/jcageman/d2mapapi
If you run this locally this runs on localhost port 8080 (which is also the default in the above config)

## Future ideas
1. Implement pickit using .nip files (used in many other bots)
2. Improve chicken/pot behavior (currently runs in a separate thread, probably better to use task scheduling)
3. Automuling

## Analyzing Packets
Besides the ConsoleBot there is another CLI project called PacketSniffer, which you can use to analyse packets send by the bot, but also by any started diablo client connected to a realm. The PacketSniffer currently only monitors packets send by the game server (i.e. the packets send when you are in a game). This is 100% safe to use in all cases and undetectable. You could use this is you are not sure if your server is using the same version of 1.09d or if you simply want to analyze the game server yourself.
