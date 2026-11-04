FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Shared/ServiceDefaults/NewStore.ServiceDefaults.csproj src/Shared/ServiceDefaults/
COPY src/Services/Catalog/NewStore.Catalog.csproj src/Services/Catalog/
RUN dotnet restore src/Services/Catalog/NewStore.Catalog.csproj
COPY src/ src/
RUN dotnet publish src/Services/Catalog/NewStore.Catalog.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14
WORKDIR /app
USER $APP_UID
COPY --from=build /app .
ENTRYPOINT ["dotnet", "NewStore.Catalog.dll"]
