FROM node:26.12.1-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Shared/ServiceDefaults/NewStore.ServiceDefaults.csproj src/Shared/ServiceDefaults/
COPY src/Gateway/NewStore.Gateway.csproj src/Gateway/
RUN dotnet restore src/Gateway/NewStore.Gateway.csproj
COPY src/Shared/ServiceDefaults/ src/Shared/ServiceDefaults/
COPY src/Gateway/ src/Gateway/
RUN dotnet publish src/Gateway/NewStore.Gateway.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15
WORKDIR /app
USER $APP_UID
COPY --from=build /app .
# The Angular build replaces the placeholder page in the image; the repo's wwwroot/index.html stays a placeholder.
COPY --from=web /web/dist/web/browser ./wwwroot
ENTRYPOINT ["dotnet", "NewStore.Gateway.dll"]
