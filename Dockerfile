# syntax=docker/dockerfile:1

# ---------- restore: слой кешируется, пока не изменились csproj/версии пакетов ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src
# .dockerignore отсекает bin/obj хоста — их попадание в контекст
# перезаписывает контейнеровский restore (NETSDK1064)
COPY --link --parents GpsTracker.sln global.json Directory.*.props src/**/*.csproj tests/**/*.csproj ./
RUN dotnet restore GpsTracker.sln

# ---------- build: полный исходный код, сборка Release ----------
FROM restore AS build
COPY --link --parents src/ tests/ ./
RUN dotnet build GpsTracker.sln -c Release --no-restore

# ---------- test: интеграционные тесты поднимают свой TCP-сервер, сеть не нужна ----------
FROM build AS test
RUN dotnet test GpsTracker.sln -c Release --no-build

# ---------- publish: приложение + healthcheck-probe + каталоги данных ----------
# mkdir и публикация probe — здесь: у chiseled-final нет ни shell, ни root.
# Файловые приложения .NET 10 публикуются как NativeAOT — в SDK-образе нет
# линковщика, поэтому -p:PublishAot=false.
FROM build AS publish
COPY --link tools/HealthProbe/Probe.cs /probe/Probe.cs
RUN dotnet publish src/GpsTracker/GpsTracker.csproj -c Release -o /app/publish --no-restore --no-build \
    && mkdir -p /app/publish/App_Data /app/publish/tile-cache \
    && dotnet publish /probe/Probe.cs -c Release -o /app/publish/probe -p:PublishAot=false

# ---------- final: chiseled — distroless (нет shell/root, USER=$APP_UID вшит) ----------
# Владельца ставит сам BuildKit через COPY --chown: COPY --from сбрасывает его
# на 0:0, а бинарника chown в chiseled нет. Named-тома наследуют владельца 1654.
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=publish --chown="$APP_UID:$APP_UID" /app/publish .
USER "$APP_UID"
EXPOSE 5023
ENTRYPOINT ["dotnet", "GpsTracker.dll"]
