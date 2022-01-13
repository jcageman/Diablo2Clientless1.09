FROM mcr.microsoft.com/dotnet/core/sdk:6.0 as build
COPY ./ .
RUN dotnet publish -c Release

FROM mcr.microsoft.com/dotnet/core/runtime:6.0
COPY --from=build src/ConsoleBot/bin/Release/net6.0/publish/ app/
ENTRYPOINT ["dotnet", "app/ConsoleBot.dll"]
