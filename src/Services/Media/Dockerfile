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

# Task 5 adds the worker stage (with FFmpeg) after this one.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15 AS api
WORKDIR /app
USER $APP_UID
COPY --from=build /app .
ENTRYPOINT ["dotnet", "NewStore.Media.dll"]
