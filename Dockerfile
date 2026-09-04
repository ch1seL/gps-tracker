# syntax=docker/dockerfile:1-labs

# ---------- restore: слой кешируется, пока не изменились csproj/версии пакетов ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

# Файлы корня решения + csproj всех проектов (--parents сохраняет структуру каталогов)
COPY --parents GpsTracker.sln global.json Directory.Packages.props ./
COPY --parents src/**/*.csproj tests/GpsTracker.Tests/*.csproj tools/MapTest/*.csproj ./
RUN dotnet restore GpsTracker.sln

# ---------- build: полный исходный код, сборка Release ----------
FROM restore AS build
COPY --parents src/ tests/ tools/ ./
RUN dotnet build GpsTracker.sln -c Release --no-restore

# ---------- test: интеграционные тесты поднимают свой TCP-сервер, сеть не нужна ----------
FROM build AS test
RUN dotnet test GpsTracker.sln -c Release --no-build

# ---------- publish: только приложение ----------
FROM build AS publish
RUN dotnet publish src/GpsTracker/GpsTracker.csproj -c Release -o /app/publish --no-restore --no-build

# ---------- final: runtime без SDK, не-root пользователь ----------
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Каталоги данных: владелец — пользователь контейнера, чтобы named-тома получили права
RUN mkdir -p /app/App_Data /app/tile-cache \
    && chown -R "$APP_UID:$APP_UID" /app/App_Data /app/tile-cache

USER "$APP_UID"

# Конфигурация приходит из переменных окружения (compose env_file: .env):
#   Telegram__BotToken, Telegram__AllowedChatIds__0, ... — перекрывают appsettings.json
EXPOSE 5023

ENTRYPOINT ["dotnet", "GpsTracker.dll"]
