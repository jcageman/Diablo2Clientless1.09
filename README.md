# D2NG
[![CircleCI branch](https://img.shields.io/circleci/project/github/jcageman/D2NG/master.svg)](https://circleci.com/gh/jcageman/D2NG/tree/master)
[![Codacy Badge](https://api.codacy.com/project/badge/Grade/0b90f6cdc4b0445296de25748e066738)](https://www.codacy.com?utm_source=github.com&amp;utm_medium=referral&amp;utm_content=jcageman/D2NG&amp;utm_campaign=Badge_Grade)
[![CodeFactor](https://www.codefactor.io/repository/github/jcageman/d2ng/badge)](https://www.codefactor.io/repository/github/jcageman/d2ng)
![GitHub](https://img.shields.io/github/license/jcageman/D2NG.svg)
![GitHub contributors](https://img.shields.io/github/contributors/jcageman/D2NG.svg)
![GitHub commit activity](https://img.shields.io/github/commit-activity/m/jcageman/D2NG.svg)

## Building the project
This project builds with .NET Core 3.0 and can be built by running `dotnet build` on the command line from the root of the Solution.

### Building Docker
You can build the `ConsoleBot` in to a docker image by executing `docker build -t "jcageman/d2ng:$TAG ."` from the root of the project.

## Configuring
ConsoleBot expects a `config.yml` file that can be passed in via the "--config" flag. The `config.yml` should look as follows:
```
realm: string
username: string
password: string
character: string
keyOwner: string (name used for the diablo registration, which you normally fill in when installing diablo)
gamefolder: string (location of your diablo 2 installation, although currently unused)
telegramApiKey: string (required for communicating status of the bot via telegram. Mainly to detect whispers/crashes)
telegramChatId: string (chat id to post to)
```
See https://core.telegram.org/bots for configuration of the telegram bot

## Running ConsoleBot Docker Image
You'll need to mount the directory that has your `config.yml`so that the program can find it. Example: 
```
docker run \
  --mount src="${pwd}/config",target=/config,type=bind \
  jcageman/d2ng:$TAG \
  --config /config/config.yml
```
