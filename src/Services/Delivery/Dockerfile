FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Shared/ServiceDefaults/NewStore.ServiceDefaults.csproj src/Shared/ServiceDefaults/
COPY src/Shared/Messaging/NewStore.Messaging.csproj src/Shared/Messaging/
COPY src/Shared/Contracts/NewStore.Contracts.csproj src/Shared/Contracts/
COPY src/Services/Delivery/NewStore.Delivery.csproj src/Services/Delivery/
RUN dotnet restore src/Services/Delivery/NewStore.Delivery.csproj
COPY src/Shared/ src/Shared/
COPY src/Services/Delivery/ src/Services/Delivery/
RUN dotnet publish src/Services/Delivery/NewStore.Delivery.csproj -c Release --no-restore -o /app

# One image for every Delivery container (delivery-migrate, delivery, delivery-b): the command and Delivery:Role pick what runs.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15
WORKDIR /app
USER $APP_UID
COPY --from=build /app .
ENTRYPOINT ["dotnet", "NewStore.Delivery.dll"]
