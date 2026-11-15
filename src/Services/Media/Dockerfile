FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Shared/ServiceDefaults/NewStore.ServiceDefaults.csproj src/Shared/ServiceDefaults/
COPY src/Shared/Messaging/NewStore.Messaging.csproj src/Shared/Messaging/
COPY src/Shared/Contracts/NewStore.Contracts.csproj src/Shared/Contracts/
COPY src/Services/Media/NewStore.Media.csproj src/Services/Media/
RUN dotnet restore src/Services/Media/NewStore.Media.csproj
COPY src/Shared/ src/Shared/
COPY src/Services/Media/ src/Services/Media/
RUN dotnet publish src/Services/Media/NewStore.Media.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15 AS api
WORKDIR /app
USER $APP_UID
COPY --from=build /app .
ENTRYPOINT ["dotnet", "NewStore.Media.dll"]

# The worker (Media:Role Worker) and the media-migrate job, whose seed encodes the sample: the API's image plus Ubuntu's FFmpeg.
# The package is not pinned, because the archive replaces old versions; CI checks the version and the build flags (ADR 0020).
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15 AS worker
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
WORKDIR /app
USER $APP_UID
COPY --from=build /app .
ENTRYPOINT ["dotnet", "NewStore.Media.dll"]
