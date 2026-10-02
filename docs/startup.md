# Локальный запуск Solo AI на Windows

Согласованный контур AI-05: локальные Solo → Solo AI → Visograph с моками.
Docker, Windows Service, reverse proxy и системные расписания для этого запуска
не требуются. Нужен .NET 10 SDK; запускать только один host с одной постоянной БД.

Из `D:/GitHub/solo.ai`:

```powershell
dotnet restore Solo.Ai.slnx
dotnet build Solo.Ai.slnx --no-restore
if (-not (Test-Path src/Solo.Ai.Host/appsettings.Secrets.json)) {
    Copy-Item docs/local-settings.example.json src/Solo.Ai.Host/appsettings.Secrets.json
}
$dataDirectory = Join-Path $env:LOCALAPPDATA 'SoloAi'
New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null
$env:SoloAiStorage__DatabasePath = Join-Path $dataDirectory 'chat.db'
```

В `appsettings.Secrets.json` установить issuer/JWKS локального Butler, audience,
client ID и service ApiKey проекта локального Visograph. Пример рассчитан на ранее
подготовленный BOX; адреса нужно сверить со своим запуском. Visograph должен быть
отдельно настроен на моки; Solo AI не подменяет JWT и не переключает provider в mock.
Не публиковать этот файл и не вставлять реальные токены в команды или логи.
Для SDK consumer адрес Solo AI — **http://127.0.0.1:5081/**.

```powershell
dotnet run --project src/Solo.Ai.Host --launch-profile Development
```

Эта команда остаётся в открытом терминале. Остановка — **Ctrl+C**; host даёт
фоновым сервисам 30 секунд на shutdown. Повторный запуск использует тот же
`DatabasePath`. Каталог данных не должен находиться в `bin`, `obj`, temp или
каталоге, который очищается при сборке. Для published host задайте рабочий каталог
с `appsettings.json`/`appsettings.Secrets.json` и переменные окружения явно;
launch profile применяется только к `dotnet run`.

Проверка из второго PowerShell (без браузера):

```powershell
Invoke-RestMethod http://127.0.0.1:5081/health/live
Invoke-RestMethod http://127.0.0.1:5081/health/ready
```

`alive` означает обслуживающий процесс. `ready` требует включённой генерации,
успешных migrations/recovery и **новой committed записи** в служебную таблицу
SQLite при каждой проверке. Модель и Butler из health не вызываются. `/health`
совпадает с readiness. Частоты раз в 10–30 секунд достаточно для локального наблюдения.
Ошибка storage/finalization фиксирует readiness=503 до restart; сначала устранить
причину. Если startup не удался, порта нет: в stderr только `host_failure`, без raw
exception. Проверить обязательные настройки, существование/права каталога,
свободное место, версию схемы и отсутствие другого host.

Файл `chat.db.lock` удерживается host до завершения worker. Второй host на той же
БД не запускается. После штатной или аварийной остановки ОС освобождает lock;
оставшийся файл нормален, удалять его при работающем host нельзя. Другие пути,
hard links или копии БД не являются способом запуска второй реплики.

## Retention и локальные данные

`SoloAiStorage:ChatRetentionDays` — `null`/отсутствует для бессрочного хранения либо
положительное целое число суток по UTC. Ноль, отрицательное, дробное, пустая строка
и неверный формат останавливают startup. По умолчанию очистка выключена.

При включении cleanup проходит при старте и затем раз в минуту, максимум 100 чатов
за проход. Удаляется целый чат, если `LastMessageAt ?? CreatedAt <= now - days` и нет
pending/running/cancellation_requested. Возраст и active run проверяются внутри
write transaction; триггер записывает DeletedChat вместе с удалением истории/runs.
Read, rename и dedup срок не продлевают; новое user/assistant сообщение продлевает.
Старые активные runs сначала защищены, после recovery/финализации чат может удалить
следующий проход. При ошибке cleanup приём/dispatch закрываются до restart.

Данные хранятся под текущей Windows-учётной записью. Проверьте ACL каталога через
`Get-Acl $dataDirectory`: доступ нужен запускающему сервис пользователю и назначенным
администраторам, не всем пользователям машины. Не выдавайте каталог обычной поддержке.
`appsettings.Secrets.json` также должен быть доступен только этой учётной записи и
назначенным администраторам. Перед реальными данными владелец контура определяет
операторов и утверждает права; текущие проверки используют синтетические данные.

Backup/restore, расписание и хранение копий **отложены по решению пользователя**.
Обычный restart с тем же файлом сохраняет committed транзакции через WAL/FULL;
это не восстановление после потери диска. Не копировать один `.db` при работающем
WAL и не удалять `-wal`/`-shm` вручную. Старый snapshot откатывает также DeletedChat
и может вернуть более поздние удаления; гарантий сохранения удалений вне snapshot нет.

## Логи

Host разрешает только сообщения собственных worker/exception boundary: IDs,
фиксированные state/code и счётчики. Framework HTTP/auth/exception logging отключён
даже при `Logging:LogLevel:Default=Trace`; raw exception, request/response body,
headers, title/text/schema, user/service tokens и provider body не выводятся.
У named HttpClient SDK и Butler удалены factory loggers; транспорт Visograph не
подключает logging. Consumer не должен добавлять body/header capture в свои handlers
или telemetry. Reverse proxy в локальном контуре отсутствует; при его появлении
body/header capture и raw upstream errors должны быть отключены до использования.

Host пишет только в консоль и не создаёт файлового журнала или его ротации.
Если консоль сохраняется внешним средством, срок и доступ настраивает это средство;
14 дней из общего плана остаются предложением для будущего контура с реальными данными.

Перед запуском обязательна конфигурация строгой Butler-схемы ниже. Отсутствующие
параметры останавливают startup. CRUD/history/status/cancel требуют delegated User;
send/retry доступны при включённом worker; до его готовности новые runs и readiness возвращают 503. `/health/live` — 200.
Старый recommendation endpoint удалён, настроек для его повторного включения нет.
Принятые сообщения выполняются независимо от HTTP connection; status/cancel/retry используют сохранённое состояние.

Local secret configuration: `src/Solo.Ai.Host/appsettings.Secrets.json` (ignored),
затем environment variables. Старые SoloAgentModelApi/SoloAgentModel settings
сами по себе не включают генерацию: нужен `SoloAiGeneration:Enabled=true`.

## Butler resource server

Пример для подготовленного локального BOX, **только Development**:

```json
{
  "SoloAiAuthentication": {
    "Issuer": "http://localhost:34100/butler",
    "Audience": "solo-ai.api",
    "AllowedClientId": "solo-ai-delegation",
    "JwksUri": "http://localhost:34100/butler/.well-known/jwks",
    "AllowHttpMetadata": true,
    "ClockSkewSeconds": 5
  },
  "SoloAiApi": {
    "MaxRequestBytes": 524288,
    "MaxResponseBytes": 524288
  }
}
```

В Production нужны точные HTTPS issuer/JWKS своего контура и
`AllowHttpMetadata=false` (default). HTTP разрешён лишь при явном флаге в Development;
проверка подписи и claims сохраняется. Fake-auth отсутствует; `Butler:UseFakes=true`
или `SoloAiAuthentication:UseFakes=true` прерывает startup в любом окружении.
Environment keys: например `SoloAiAuthentication__Issuer`, `SoloAiAuthentication__JwksUri`.

JWKS URI задаётся явно доверенной server configuration: установленный BOX возвращает
неправильный `jwks_uri` в discovery. HTTP redirects при загрузке ключей запрещены.
JwtBearer/IdentityModel проверяют RS256, `typ=at+jwt`, точный issuer/audience, подпись,
expiration/not-before; стандартный ConfigurationManager кэширует и обновляет ключи.
Skew настраивается в пределах 0–30 секунд (default 5). При default токен может
приниматься ещё 5 секунд после exp; отзыв сессии не отзывает уже выданный JWT мгновенно.

Policy требует ровно один `account_type=User`, согласованные непустые GUID `sub`
и `user_id`, разрешённый `client_id` и JSON `act={"sub":"<тот же client>"}`.
Цепочки/nested actors не входят в этот flow. Owner — нормализованный `sub`.
Scope не требуется: проверенный Butler fixture его не выдаёт. Credentials нужны
Solo для exchange; resource server Solo AI получает только публичные ключи.

Минимальный MaxResponseBytes — 16384: валидные Chat/Run responses помещаются до
любого commit. History pages уменьшаются с сохранением cursor при превышении cap;
если одна запись не помещается — 413. MaxRequestBytes проверяется по длине и потоку.

## SQLite

По умолчанию `SoloAiStorage:Enabled=false`: файл не создаётся и storage не используется.
Для включения задать в secret configuration или environment variables:

```json
{
  "SoloAiStorage": {
    "Enabled": true,
    "DatabasePath": "D:/SoloAiData/chat.db",
    "BusyTimeoutSeconds": 5,
    "RunTimeout": "00:04:00"
  }
}
```

`DatabasePath` — обязательный абсолютный путь к файлу на постоянном локальном
диске; каталог необходимо создать и выдать сервису права заранее. В Linux пример
пути: `/var/lib/solo-ai/chat.db`. Environment override: `SoloAiStorage__DatabasePath`.
В production нельзя использовать временный каталог, файловую систему контейнера
без постоянного volume или сетевой диск. Поддерживается один процесс/одна реплика.
Файлы `.db`, `-wal`, `-shm` доступны только сервису и назначенным infra operators.

Перед открытием host выполняет миграции включённого storage. Неполная конфигурация,
недоступный диск, ошибка миграции или будущая версия схемы прерывают startup.
SQL-миграции встроены в сборку, версия хранится в `PRAGMA user_version`.
Текущая версия — 3; недостающие скрипты и смена версии применяются одной транзакцией.
Выпущенные миграции не переписывать; downgrade не поддерживается.
Переключения на in-memory нет. Обычные операции открывают существующий файл в
ReadWrite и не создают пустую замену при его исчезновении. Включение storage
делает CRUD доступным только после Butler authorization; readiness требует включённого worker и успешного recovery.

Один файл содержит Chat, Message, GenerationRun и DeletedChat. Удаление физически
убирает строки переписки и runs, оставляя только ChatId/DeletedAt. Оно не обещает
затирание свободных страниц/WAL/backup. Восстановление старого backup может вернуть
данные, удалённые после его создания: таблица DeletedChat восстанавливается вместе
с БД. Независимого журнала нет по решению пользователя от 1 октября 2026.
Backup/restore исключены из текущей AI-05 по уточнённому объёму; работающую WAL-БД
нельзя копировать как один `.db` файл. Схема — в [SQL-миграциях](../src/Solo.Ai/Storage/Migrations).

Solo подключает новый SDK по [инструкции пакета](client-package.md). Login и Butler
token exchange принадлежат consumer handler; X-Solo-User-Id больше не является
входом HTTP API. Обновления соседнего репозитория эта итерация не делает.

Для изолированной проверки достаточно [build/tests/pack](../README.md).
Тесты запускают in-process TestServer, без браузера и живых зависимостей.
Отдельная opt-in live-проверка BOX: `pwsh -File tests/Live/Invoke-ButlerLiveCheck.ps1`.
Она использует выделенную fixture S-01, отдельный host и временную SQLite в ignored
`artifacts/ai03-live`, не печатает токены/секреты и отзывает созданную тестовую сессию.
Нужны собранный Debug host, работающий BOX/локальный Solo и доступ к защищённой
конфигурации и Docker API. Production HTTPS deployment этим запуском не проверяется.
Локальная сборка и synthetic runtime checks не означают publication, live model verification или UI E2E.

## Генерация AI-04

Включение требует настроенных SQLite и Visograph. Дополнить secret configuration:

```json
{
  "SoloAiGeneration": {
    "Enabled": true,
    "MaximumParallelRuns": 4,
    "MaximumPendingRuns": 100,
    "FinalizationAttempts": 3
  },
  "SoloAgentModel": {
    "Enabled": true,
    "MaxRequestBytes": 32768,
    "TotalDeadline": "00:03:50",
    "ModelTimeout": "00:03:40"
  },
  "VisographModelClient": {
    "Enabled": true,
    "BaseUri": "http://127.0.0.1:1235/",
    "ApiKey": "<service project key from secret configuration>",
    "RequestTimeout": "00:03:30",
    "MaxRequestBytes": 32768,
    "MaxResponseBytes": 524288
  }
}
```

BaseUri — адрес настроенного Visograph, не подтверждение наличия работающего стенда.
Ключ хранится вне tracked файлов. User token используется только на входном API;
worker не обновляет и не пересылает его. Недостающая/невалидная конфигурация при
Enabled=true прерывает startup. По умолчанию генерация выключена.

MaximumParallelRuns: 1–64, MaximumPendingRuns: 1–10000, FinalizationAttempts: 1–10.
Ожидание pending входит в `SoloAiStorage:RunTimeout` от acceptance; pipeline и
transport timeout дополнительно ограничивают вызов, не продлевая deadline run.
Слишком большой контекст даёт limit_exceeded без обрезки истории и без model retry.

До readiness применяются миграции (текущая schema 3) и recovery. При исчерпании
финализации readiness остаётся 503, приём новых runs и dispatch остановлены до
перезапуска. Устранить причину отказа диска/БД, затем перезапустить единственный
процесс; неизвестные running станут interrupted и допускают только явный retry.
Liveness остаётся 200 и не должна автоматически маскировать эту диагностику.
Штатная остановка сразу прерывает локальные модельные вызовы.

Документ AI-03 live runner сохраняет свой прежний сценарий с выключенной генерацией;
его запуск не проверяет модель. Для AI-04 выполнены synthetic checks через настоящий
VisographModelClient с подменённым HTTP handler; live Visograph и browser smoke не запускались.
