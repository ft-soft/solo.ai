# solo.ai

.NET 10 сервис личного чата Solo AI и NuGet SDK `Solo.Ai.Client`.
Контракт — [wire v2 API и DTO](docs/solo-agent-api.md), async SDK `0.2.0-preview.2`
доступен через [локальный источник пакетов](docs/client-package.md).

**AI-02:** реализовано файловое SQLite-хранилище с миграцией v1, одним чатом на
owner, историей, persistent send/retry dedup и таблицей удалённых ChatId в той же БД.
**AI-03:** отдельная строгая Butler JWT-схема защищает все chat/run routes.
CRUD, история, status и сохранение запроса отмены подключены к SQLite.
**AI-04:** send/retry сохраняют работу до acknowledgement; один `BackgroundService`
выполняет очередь SQLite через Visograph независимо от HTTP connection. Есть cancel,
startup recovery, ограничение очереди/параллелизма и health при отказе финализации.
Ответ чата — строгий `{text: string}`, без каталога и model retry.
Генерация включается настройкой `SoloAiGeneration:Enabled` при корректной конфигурации
storage/модели; до включения readiness — 503. `/health/live` — 200.

**AI-05 (локальный Windows-контур):** постоянная SQLite schema **3**, блокировка
второго host на том же файле, readiness с реальной записью в storage, фоновый
retention и безопасные логи. `ChatRetentionDays=null` — бессрочно. Запуск вручную
через `dotnet run`, остановка Ctrl+C; [инструкция](docs/startup.md).
Solo, Solo AI и Visograph с моками запускаются локально; настройка реального provider
не является условием synthetic проверок. Backup/restore и их автоматизация отложены
пользователем 1 октября 2026. Доступность при утрате диска не гарантируется.

```powershell
dotnet restore Solo.Ai.slnx
dotnet build Solo.Ai.slnx --no-restore
dotnet run --project tests/Solo.Ai.Tests --no-build --no-restore
dotnet pack src/Solo.Ai.Client/Solo.Ai.Client.csproj -c Release -o artifacts/packages
```

Solution и пакет собираются без исходников соседних checkout. Тесты используют
synthetic HTTP handlers и ASP.NET TestServer, не живой Butler/Visograph или браузер.
SDK не владеет login flow; consumer Solo добавляет Butler delegation handler.

Файлы сгруппированы по назначению:

```text
src/
  Solo.Ai/          Generation, Recommendations, Storage, Visograph
  Solo.Ai.Api/      Authentication, Chat, Generation, Http, Storage
  Solo.Ai.Client/   Contracts, Http, Protocol; клиент и регистрация в корне
  Solo.Ai.Host/     запуск и конфигурация
tests/
  Solo.Ai.Tests/    Api, Authentication, Client, Generation, Storage, Visograph
    Fixtures/      SoloAgent, ModelExchange — JSON для тестов
  Live/            отдельная opt-in проверка Butler
```

Папки группируют исходники; публичные namespace и контракты SDK сохраняются.

[Упаковка и подключение](docs/client-package.md) · [Запуск](docs/startup.md) ·
[Границы runtime](docs/runtime-integration.md)
