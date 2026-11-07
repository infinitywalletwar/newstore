FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Shared/ServiceDefaults/NewStore.ServiceDefaults.csproj src/Shared/ServiceDefaults/
COPY src/Shared/Messaging/NewStore.Messaging.csproj src/Shared/Messaging/
COPY src/Shared/Contracts/NewStore.Contracts.csproj src/Shared/Contracts/
COPY src/Services/Inventory/NewStore.Inventory.csproj src/Services/Inventory/
RUN dotnet restore src/Services/Inventory/NewStore.Inventory.csproj
COPY src/Shared/ src/Shared/
COPY src/Services/Inventory/ src/Services/Inventory/
RUN dotnet publish src/Services/Inventory/NewStore.Inventory.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14
WORKDIR /app
USER $APP_UID
COPY --from=build /app .
ENTRYPOINT ["dotnet", "NewStore.Inventory.dll"]
