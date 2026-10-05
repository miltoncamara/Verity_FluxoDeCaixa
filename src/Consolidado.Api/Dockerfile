# Build a partir da raiz do repositório: docker build -f src/Consolidado.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore em camada separada para aproveitar o cache do Docker.
COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/Contracts/Contracts.csproj src/Contracts/
COPY src/Consolidado.Api/Consolidado.Api.csproj src/Consolidado.Api/
RUN dotnet restore src/Consolidado.Api/Consolidado.Api.csproj

COPY src/Contracts/ src/Contracts/
COPY src/Consolidado.Api/ src/Consolidado.Api/
RUN dotnet publish src/Consolidado.Api/Consolidado.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Consolidado.Api.dll"]
