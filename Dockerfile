# syntax=docker/dockerfile:1

# ------------------------------------------------------------------
#  Genesis Market API — многоступенчатая сборка.
#  Прим.: стек проекта — .NET 10, поэтому образы 10.0-alpine
#  (в исходном ТЗ был указан 9.0-alpine — несовместим с .NET 10).
# ------------------------------------------------------------------

# ---- 1. build: восстановление и публикация ----
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Сначала только манифесты — кешируем restore.
COPY Directory.Build.props ./
COPY src/GenesisMarket.Domain/GenesisMarket.Domain.csproj src/GenesisMarket.Domain/
COPY src/GenesisMarket.Infrastructure/GenesisMarket.Infrastructure.csproj src/GenesisMarket.Infrastructure/
COPY src/GenesisMarket.Api/GenesisMarket.Api.csproj src/GenesisMarket.Api/
RUN dotnet restore src/GenesisMarket.Api/GenesisMarket.Api.csproj

# Затем весь исходный код.
COPY . .
RUN dotnet publish src/GenesisMarket.Api/GenesisMarket.Api.csproj \
    -c Release -o /app/publish \
    /p:UseAppHost=false

# ---- 2. final: только рантайм, без SDK, от непривилегированного пользователя ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app

# Непривилегированный пользователь (uid 64198 предопределён в образах .NET).
USER $APP_UID

COPY --from=build /app/publish .

# Kestrel слушает порт из ASPNETCORE_HTTP_PORTS (задаётся в окружении).
ENTRYPOINT ["dotnet", "GenesisMarket.Api.dll"]
