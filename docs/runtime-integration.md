# Runtime integration

Нормативный wire v2 находится в [solo-agent-api.md](solo-agent-api.md), общие
lifecycle/data правила — в [контракте](plans/solo-agent/contract.md).
SDK `Solo.Ai.Client` — `0.2.0-preview.2` из локального source; model-exchange Visograph — v1.

Host сначала регистрирует SQLite (`AddSoloAiStorage`), затем API и, при
`SoloAiGeneration:Enabled=true`, единственный `SoloAiGenerationWorker`.
`SoloAiStorageWorker.StartAsync` удерживает process lock, применяет миграции,
проверяет committed write и запускает первый проход retention; затем generation
worker выполняет recovery до readiness/dispatch. SQLite schema 3 добавляет только
служебную таблицу StorageHealth; история и wire v2 не меняются.
Текущий запуск — один процесс на Windows, постоянный локальный диск, локальный
Visograph с моками. Второй host на той же БД отклоняется файловой блокировкой.

HTTP send/retry выполняет короткую транзакцию SqliteRunStore: ownership,
idempotency, chat_busy, readiness/capacity, message/run insertion и commit.
Только затем возвращается 202. Дубликат возвращает сохранённый исход, даже при
полной очереди или неготовом worker. RequestAborted никогда не передаётся worker.

Worker раз в 250 мс выбирает pending по порядку Number, до общего лимита выполнения.
Никакого обязательного in-memory сигнала или broker нет. Pending с истёкшим deadline
становится timed_out; отменённый pending — cancelled. TryStartRun фиксирует running
до dispatch. Во время модели нет SQL transaction, HttpContext или user token.
SoloAgentRunRuntime хранит только readiness и локальные CancellationTokenSource,
а не очередь, сообщения или результаты. Короткий общий gate координирует acceptance,
регистрацию локального вызова, cancel/delete и блокировку приёма.

SqliteGenerationQueue читает собственную историю текущего чата по Sequence:
user messages, включая неуспешные прошлые попытки, и только completed assistant.
Текущий user message включается ровно один раз; retry не создаёт новый message.
История не ограничивается API pagination и не сокращается автоматически. Проверяется
суммарный текстовый объём, затем полный сериализованный model request.

SoloAgentModelPipeline добавляет доверенную server instruction, запрашивает
json_schema `{text: string}` с minLength=1 и additionalProperties=false. Проверяет
correlation/full outcome, JSON без повторных/лишних полей и непустой text до 65536
UTF-8 bytes. Каталог, RecommendationSchema и DocumentRecommendationValidator не
используются. Старые самостоятельные recommendation-типы не входят в runtime.
VisographModelClient передаёт исключительно service ApiKey, не делает redirects,
model retry, repair или fallback; сохраняет byte caps и транспортный timeout.

SoloAiRunExecutor выполняет один dispatch и до трёх попыток записи результата
(между попытками 100 мс; каждая DB-операция ограничена SQLite busy timeout).
Completed и assistant message становятся видимыми только после общего commit.
Cancel сначала фиксируется в БД, затем посылает локальный сигнал; сохранённая
отмена/удаление выигрывает у позднего ответа, включая повтор финализации.

При исчерпании записи или отказе storage readiness становится 503 до restart;
новые runs и dispatch остановлены. Status/history/cancel/delete доступны, если БД
работает. Уже запущенные вызовы могут завершить свою финализацию. Неизвестный исход
остаётся running до recovery, UI не должен считать его успешным или автоматически
повторять модель. Startup переводит running → interrupted и
cancellation_requested → cancelled. Неистёкшие pending продолжают обработку.

Штатный shutdown немедленно отменяет локальные модельные вызовы. При доступной БД
записывается interrupted (либо ранее запрошенный cancelled); при потере процесса
это делает следующий startup. Удалённый provider может продолжить вычисления.

Retention по `SoloAiStorage:ChatRetentionDays` удаляет целые неактивные чаты с
атомарным DeletedChat/cascade. Null выключает очистку; возраст определяется
последним сообщением или созданием пустого чата. Проход — при старте и раз в минуту,
до 100 чатов; активные runs исключены условием под SQL write lock.

Логи содержат только RunId, фиксированные state/code и счётчики. Host применяет
allowlist категорий, перехватывает неожиданные HTTP/startup exceptions без raw
exception; HTTP factory logging у Butler и SDK отключён. Неожиданный HTTP exception
даёт generic 503 вне wire envelope: SDK сохраняет неизвестный исход команды,
поскольку сбой мог произойти после commit.

`/health/live` — liveness; `/health/ready` и `/health` проверяют готовность worker и
каждый раз выполняют committed write в StorageHealth. При сбое readiness/приём
закрываются до restart. Ни модель, ни Butler из health не вызываются.
Backup/restore отложены пользователем; обычный restart не требует копирования БД.
Подключение consumer — в [инструкции SDK](client-package.md).
