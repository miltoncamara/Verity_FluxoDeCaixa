# Build a partir da raiz do repositório: docker build -f src/Lancamentos.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore em camada separada para aproveitar o cache do Docker.
COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/Contracts/Contracts.csproj src/Contracts/
COPY src/Lancamentos.Api/Lancamentos.Api.csproj src/Lancamentos.Api/
RUN dotnet restore src/Lancamentos.Api/Lancamentos.Api.csproj

COPY src/Contracts/ src/Contracts/
COPY src/Lancamentos.Api/ src/Lancamentos.Api/
RUN dotnet publish src/Lancamentos.Api/Lancamentos.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Lancamentos.Api.dll"]
