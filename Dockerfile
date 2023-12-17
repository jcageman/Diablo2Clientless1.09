FROM mcr.microsoft.com/dotnet/sdk:8.0 as build
COPY ./ .
RUN dotnet publish -c Release

FROM mcr.microsoft.com/dotnet/runtime:8.0
COPY --from=build src/ConsoleBot/bin/Release/net8.0/publish/ app/
ENTRYPOINT ["dotnet", "app/ConsoleBot.dll"]
