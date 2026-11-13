FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Shared/ServiceDefaults/NewStore.ServiceDefaults.csproj src/Shared/ServiceDefaults/
COPY src/Fakes/FakePay/NewStore.FakePay.csproj src/Fakes/FakePay/
RUN dotnet restore src/Fakes/FakePay/NewStore.FakePay.csproj
COPY src/Shared/ServiceDefaults/ src/Shared/ServiceDefaults/
COPY src/Fakes/FakePay/ src/Fakes/FakePay/
RUN dotnet publish src/Fakes/FakePay/NewStore.FakePay.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15
WORKDIR /app
USER $APP_UID
COPY --from=build /app .
ENTRYPOINT ["dotnet", "NewStore.FakePay.dll"]
