FROM mcr.microsoft.com/dotnet/core/sdk:3.1 as build
COPY ./ .
RUN dotnet publish -c Release

FROM mcr.microsoft.com/dotnet/core/runtime:3.1
COPY --from=build src/ConsoleBot/bin/Release/netcoreapp3.1/publish/ app/
ENTRYPOINT ["dotnet", "app/ConsoleBot.dll"]
