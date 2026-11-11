FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Shared/ServiceDefaults/NewStore.ServiceDefaults.csproj src/Shared/ServiceDefaults/
COPY src/Shared/Messaging/NewStore.Messaging.csproj src/Shared/Messaging/
COPY src/Shared/Contracts/NewStore.Contracts.csproj src/Shared/Contracts/
COPY src/Services/Catalog/NewStore.Catalog.csproj src/Services/Catalog/
RUN dotnet restore src/Services/Catalog/NewStore.Catalog.csproj
COPY src/Shared/ src/Shared/
COPY src/Services/Catalog/ src/Services/Catalog/
RUN dotnet publish src/Services/Catalog/NewStore.Catalog.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0
WORKDIR /app
USER $APP_UID
COPY --from=build /app .
ENTRYPOINT ["dotnet", "NewStore.Catalog.dll"]
