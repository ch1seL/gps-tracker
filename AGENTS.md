# AGENTS.md — инструкция для AI-агентов

Файл — «карта местности» для агента, работающего с этим репозиторием: что где лежит,
какие инварианты нельзя ломать, как проверять изменения. README.md — описание проекта
для человека; здесь — всё, что нужно для разработки.

## Команды

```bash
dotnet build                         # сборка (0 ошибок / 0 предупреждений — норма)
dotnet test                          # 18 тестов: парсеры + интеграционные, ~4 сек, без сети
dotnet run --project src/GpsTracker  # запуск приложения (TCP :5023 + Telegram-бот)
dotnet run --project tools/MapTest   # проверка рендера → map-position.png / map-history.png
```

- После проверок приложение **останавливать** (пользователь предпочитает не оставлять фоновые процессы).
- Порт 5023: перед запуском убедиться, что старая копия остановлена — `Address already in use`
  валит хост (`BackgroundServiceExceptionBehavior.StopHost`).

## Git

- **Каждый созданный или изменённый файл — сразу в индекс** (`git add <путь>`).
  Незатреканный файл не попадёт в коммит: задача не считается завершённой, пока
  результат работы не виден в `git status` как застейдженный.
- Перед завершением задачи проверять `git status --short`:
  - `??` — незатреканные файлы, которые вы создавали → добавить в индекс (или в
    `.gitignore`, если это сгенерированное);
  - `AM`/`AD` — файл менялся/удалялся уже после добавления в индекс → повторно
    `git add` или `git rm --cached <путь>`;
  - `M` во втором столбце — правка не застейджена → `git add <путь>`.
- Сгенерированное в индекс не добавлять — `.gitignore` закрывает `bin/`, `obj/`,
  `App_Data/`, `tile-cache*/`, `map-*.png`, `.env`; личные настройки IDE (`*.user`,
  `.idea/`) — кандидаты в `.gitignore`, а не в коммит.
- `appsettings.json` содержит реальный токен бота — копировать его содержимое в другие
  файлы, документацию и вывод команд нельзя.

## Карта кода

```
src/GpsTracker/                     Worker Service (net10.0)
├── Program.cs                      DI: AddDbContextFactory, HttpClient "tiles", IOptions, HostedServices
├── appsettings.json                порт, токен бота, tile-сервер, строка подключения (секреты не трогать)
├── Models/GpsPoint.cs              сущность точки (Imei, Latitude, Longitude, Speed, Course, Satellites, Timestamp, CreatedAt)
├── Database/AppDbContext.cs        EF Core + SQLite, индексы (Imei, Timestamp)
├── Configuration/                  TcpSettings, TelegramSettings, MapSettings — биндятся через IOptions
├── Protocol/                       чистые статические парсеры БЕЗ IO — только string/byte[] → Gt06Packet
│   ├── Gt06PacketParser.cs         бинарный GT06: заголовок 7878, CRC-16/IBM, BCD, билдеры ACK
│   ├── Gt02TextProtocolParser.cs   текстовый ConCox GT02: кадры "( ... )", ExtractFrames + ParseFrame
│   ├── Gt06Packet.cs               DTO пакета + enum Gt06PacketType
│   └── Gt06ProtocolConstants.cs    номера протоколов и заголовки
├── Services/
│   ├── TcpListenerService.cs       BackgroundService: TcpListener, автодетект протокола, буферы, SavePointAsync
│   ├── TelegramBotService.cs       BackgroundService: Telegram.Bot v22, команды /start /pos /history
│   └── MapRendererService.cs       SkiaSharp: сетка OSM-тайлов (дисковый кеш), polyline по скорости
tests/GpsTracker.Tests/             xunit (net10.0), включён в GpsTracker.sln
├── Gt02TextProtocolParserTests.cs  11 юнит-тестов на реальных кадрах трекера
├── TcpProtocolIntegrationTests.cs  7 интеграционных: реальный TcpListenerService на свободном порту
├── Helpers/Gt02Frames.cs           конструктор кадров GT02 (логин/локация)
├── Helpers/Gt06Packets.cs          конструктор пакетов GT06 (CRC, BCD, битый CRC, проверка ACK)
├── Helpers/TrackerTestClient.cs    TcpClient-«трекер» с таймаут-чтением
├── Helpers/TcpServerFixture.cs     хост с TcpListenerService + временная SQLite, автоудаление
└── ModuleInitializer.cs            фиксирует InvariantCulture на весь тестовый хост
tools/MapTest/                      консольная утилита проверки рендера (не автотест)
```

## Модель данных и БД

- Таблица `GpsPoints`, схема создаётся `EnsureCreatedAsync()` при старте.
- **Миграций EF нет**: изменение модели = удалить файл `App_Data/gps-tracker.db`.
- База и кеш тайлов в gitignored-каталогах (`App_Data/`, `tile-cache*/`) — генерируются сами.

## Спецификации протоколов

Протокол определяется **один раз на TCP-сессию** по первому байту данных:
`(` (0x28) → текстовый GT02, иначе бинарный GT06. Состояние сессии — класс
`TrackerSession` (`Protocol`, `Imei`, `BufferedLength`); async-методы не могут принимать
`ref`, поэтому состояние именно в классе.

### GT06 — бинарный

```
[78 78][Length][Protocol][Data...][CRC_H][CRC_L][0D 0A]
```
- `Length` — от Protocol до CRC_L включительно. CRC-16/IBM (poly 0xA001, init 0xFFFF)
  от байта Length до последнего байта данных. Битый CRC → пакет молча пропускается.
- Границы пакетов в потоке: скан последнего `0D 0A`, хвост без завершения остаётся в буфере.
- `0x01` Login: IMEI в BCD, 8 байт; IMEI начинается с `data[6]`, если `data[2] >= 15`,
  иначе с `data[4]` (два формата прошивки). Ответ — ACK.
- `0x12` Location: байты 4–9 дата/время BCD (YY MM DD HH MM SS, UTC); байт 10 — GPS info
  (бит 7 или 4 = фикс, биты 0–3 = спутники); байты 11–14 широта и 15–18 долгота —
  uint32 big-endian, `raw / 1800000.0`; байт 19 — скорость км/ч; байты 20–21 — курс+флаги
  uint16: биты 0–9 курс (0–359), бит 10 = запад, бит 11 = юг. IMEI в пакете НЕТ.
- `0x23` Heartbeat: серийник — последние 2 байта тела.
- ACK на любой пакет: `78 78 05 [Protocol] [Serial_H] [Serial_L] [CRC_H] [CRC_L] 0D 0A`,
  CRC по первым 4 байтам после заголовка.

### GT02 — текстовый (реальные устройства ConCox GT02)

```
(027046781654 BP05 355227046781654 260903 A 6006.9472 N 03122.7910 E 120.02 24043 ...)
 └ID 12 цифр   └логин   └IMEI 15 цифр  └YYMMDD └фикс └ddmm.mmmm └N/S └dddmm.mmmm └E/W └км/ч └курс×100

(027046781654 BR00 260903 A ...)   — BR: локация, IMEI в кадре НЕТ
(027046781654 BA00 ...)            — BA: тревога, формат как у BR
```
- Поля фиксированной ширины; координаты `ddmm.mmmm`/`dddmm.mmmm` → градусы:
  `int(v/100) + (v%100)/60`. Скорость `XXX.XX` км/ч, курс `XXXXX` = градусы × 100.
- Кадры идут пачками; парсер выбирает сбалансированные `( ... )`, хвост без `)` ждёт
  в буфере (перемещается в начало через `Array.Copy`).
- Логин (`BP`) → ответ plain ASCII `LOAD`; на `BR`/`BA` сервер не отвечает.
- **Времени суток в кадре нет**: `Timestamp = DateTime.UtcNow` (момент приёма);
  дата YYMMDD — только валидация, кадр с невозможной датой отбрасывается.
- IMEI есть только в логине и привязывается к TCP-сессии; локация без логина не сохраняется.
- В `SavePointAsync` точки без GPS-фикса (обе координаты < 0.0001) не пишутся в БД.

## Инварианты и подводные камни

1. **.NET 10**: пакеты EF Core/Hosting/Http версии 10.0.11; SkiaSharp 4.151.2;
   Telegram.Bot 22.10.3; `global.json` фиксирует SDK 10.0.302 (rollForward latestFeature).
   Понижение TFM ломает сборку.
2. **Telegram.Bot v22 API**: методы без суффикса Async — `SendMessage`/`SendPhoto`
   (`SendTextMessageAsync`/`SendPhotoAsync` из v19 не существуют); приём —
   `bot.StartReceiving(updateHandler:, errorHandler:, receiverOptions:, cancellationToken:)`
   (в v19 параметр назывался pollingErrorHandler); `InputFileStream` НЕ IDisposable
   (оборачивать `MemoryStream` в `using` отдельно).
3. **SkiaSharp 4**: `SKColors.Parse` не существует — только `new SKColor(r, g, b)`;
   `with`-выражения на классах не работают (например `TileGrid` — ctor-based класс);
   `canvas.DrawBitmap` требует оверлоад с `SKSamplingOptions` (без него CS0618).
4. **Парсеры — чистые статические функции** без IO; всё чтение полей — фиксированная
   ширина + `CultureInfo.InvariantCulture`.
5. **Культура**: `double.Parse`/интерполяция чисел без InvariantCulture в ru-RU даёт
   запятую (`6006,96`), что молча искажает координаты (тестовый хост зафиксирован в
   `ModuleInitializer.cs`). Тот же риск в любом новом коде разбора.
6. IMEI живёт в сессии, а не в пакете локации (оба протокола).
7. `SQLitePCLRaw.bundle_e_sqlite3 3.0.5` закреплён явно — закрывает GHSA-2m69-gcr7-jv3q,
   не удалять.
8. Тестовый проект включён в sln вручную (`dotnet new xunit`/`dotnet sln add` падают из-за
   песочницы) — новые тесты добавлять файлами в существующий проект.

## Конвенции тестов

- Юнит-тесты парсеров — на **реальных кадрах** трекера (константы из выгрузки `nc`),
  не на синтетике.
- Интеграционные поднимают настоящий хост (`TcpServerFixture`: свободный порт, временная
  SQLite, автоудаление) — никакой сети и моков TCP-слоя.
- Ожидания координат: `Assert.Equal(expected, actual, 4)` — точность 4 знака; пересчёт
  ddmm.mmmm проверять на бумаге (уже дважды ловили неверные ожидания).
- Новый кейс протокола = сначала кадр/пакет в `Helpers`, потом тест.
