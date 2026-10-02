# Solo AI chat HTTP API — wire v2

Нормативный владелец HTTP-контракта — этот документ и `Solo.Ai.Client`.
План и границы доверия: [общий контракт](plans/solo-agent/contract.md).
Wire v1 recommendation API удалён; совместимости с его envelope и DTO нет.
Visograph model-exchange остаётся отдельным протоколом v1.

**Состояние после AI-05:** все десять маршрутов защищены одной delegated-user policy.
Send/retry подтверждаются после commit в SQLite; worker выполняет очередь независимо
от HTTP connection. Повтор принятой команды возвращает исходный run, даже при
заполненной очереди или остановленном приёме. Новая работа при полном pending-лимите
получает `429 capacity_exceeded`; при выключенной/неготовой генерации — `503 unavailable`.
Cancel сохраняет намерение до локального сигнала. Delete запрещает позднюю запись
и останавливает локальный вызов. `/health/ready` и `/health` возвращают 200 после migrations,
recovery и успешной проверки committed записи SQLite при каждом запросе;
503 при выключенном worker, остановке или отказе storage/finalization; `/health/live` — 200.
Health не вызывает платную модель и не проверяет live-доступность Butler/Visograph.
Настройки SQLite и Butler описаны в [инструкции запуска](startup.md). Старого HTTP-маршрута
`/api/v1/solo-agent/generations` нет (404); включение старых настроек его не возвращает.
Каталог и recommendation validator не входят в путь чата. Lifecycle и диагностические
сигналы реализации описаны в [runtime integration](runtime-integration.md).

## Общие правила обмена

Префикс: `/api/v2/chats`. Имена JSON полей camelCase и регистрозависимы.
Вход и выход — UTF-8 JSON без неизвестных, повторных или пропущенных обязательных
полей, в том числе внутри вложенных объектов. Nullable поля также обязательны:
отсутствие отличается от явного `null`. Корневой JSON `null` недопустим.
UUID — непустой, каноническая запись в URI/headers `D` (`xxxxxxxx-...`).
Времена — серверные ISO-8601 UTC; sequence — положительный Int64.

Каждый запрос несёт два заголовка ровно с одним значением:

- `X-Solo-Ai-Contract-Version: 2`;
- `X-Solo-Ai-Request-Id: <UUID>` — новый ID HTTP-попытки, **не** idempotency key.

Ответ повторяет оба заголовка, включая ошибки и `204`. SDK проверяет точное
совпадение request ID и версии. Для запросов с телом обязателен `contractVersion: 2`.
Для bodyless GET/DELETE/cancel версия передаётся в заголовке и URL.
Неподдерживаемая версия: `400 unsupported_contract`. Ответ новой версии либо
отсутствие/дубликат заголовка версии не интерпретируются как совместимые.

Любой непустой ответ имеет один envelope:

```json
{"contractVersion":2,"data":{},"error":null}
```

Успех: `data` — DTO операции, `error: null`. Отказ:
`{"contractVersion":2,"data":null,"error":"<safe_code>"}`.
Ошибки не содержат text, raw exception, provider body, credentials или метаданные
чужого ресурса. `204` строго без тела. SDK допускает bare 401/403 от auth middleware
без envelope/заголовков, классифицируя их по HTTP status.

Consumer handler Solo получает и ставит Butler Bearer token. SDK не реализует
login/token exchange и не отправляет `X-Solo-User-Id`. Только проверенный Butler
subject определяет owner; IDs в URI/body/headers не являются авторизацией.
Issuer/audience/signature/lifetime/тип токена проверяет именованная схема SoloAiButler;
policy SoloAiDelegatedUser требует исходного пользователя и разрешённого client/actor.
Криптографический отказ — 401, отказ delegated-user policy — 403; оба могут быть без envelope.

## Операции

Суффиксы относительны префиксу. Все указанные свойства тела обязательны;
`contractVersion` опущен только в таблице для краткости.

| Метод / суффикс | Тело | Успех / data | SDK |
| --- | --- | --- | --- |
| POST `/` | `chatId` | 201 Chat при создании; 200 фактический существующий Chat | `CreateChatAsync` |
| GET `/current` | нет | 200 Chat; 204, если чата нет | `GetCurrentChatAsync` → nullable |
| GET `/{chatId}` | нет | 200 Chat | `GetChatAsync` |
| GET `/{chatId}/messages?cursor=…&limit=…` | нет | 200 MessagePage | `GetMessagesAsync` |
| PATCH `/{chatId}` | `title` | 200 Chat | `RenameChatAsync` |
| DELETE `/{chatId}` | нет | 204 | `DeleteChatAsync` |
| POST `/{chatId}/messages` | `messageId`, `text` | 202 Run, включая duplicate | `SendMessageAsync` |
| GET `/{chatId}/runs/{runId}` | нет | 200 Run | `GetRunAsync` |
| POST `/{chatId}/runs/{runId}/cancel` | нет | 200 Run с фактическим состоянием | `CancelRunAsync` |
| POST `/{chatId}/messages/{messageId}/retry` | `retryId` | 202 Run, включая duplicate | `RetryRunAsync` |

На owner хранится максимум один чат, включая пустой. POST при существующем чате
возвращает его канонический ID, даже если запрошен другой новый UUID; не очищает
историю и не переименовывает чат. Ответ 201 обязан содержать запрошенный ChatId,
200 может содержать другой. До возврата существующего чата запрошенный чужой или
ранее удалённый ID отклоняется одинаковым `404 not_found`.

Чужие и отсутствующие chat/message/run IDs дают одинаковый `404 not_found`.
DELETE удаляет конкретный ID; повтор после удаления также даёт 404, без раскрытия
владения. Это идемпотентность эффекта, не обещание одинакового HTTP status.
Повтор удаления старого ID никогда не удаляет новый текущий чат.
GET не создаёт чат и не начинает генерацию; read/rename не продлевают retention.
Начальный title: `Новый чат`; rename хранит введённое название без model call.

`SoloAiStorage:ChatRetentionDays=null` сохраняет историю бессрочно. Положительный
срок включает удаление целых чатов по UTC на границе `LastMessageAt ?? CreatedAt <=
now - days`; активные runs защищены. Cleanup — при старте и раз в минуту, партиями
до 100. После cleanup current возвращает 204, старый ChatId — 404; новый чат
получает новый ID. Настройка server-only, wire DTO не изменены.

## DTO

Публичные record-типы SDK лежат в одноимённых файлах `src/Solo.Ai.Client/Contracts`.

| DTO | Обязательные поля |
| --- | --- |
| `SoloAiChat` | `chatId`, `title`, `createdAt`, `lastMessageAt: UTC|null`, `latestRun: Run|null` |
| `SoloAiRun` | `runId`, `chatId`, `userMessageId`, `retryId: UUID|null`, `state`, `createdAt`, `startedAt: UTC|null`, `finishedAt: UTC|null`, `deadlineAt`, `outcomeCode: string|null`, `assistantMessageId: UUID|null` |
| `SoloAiMessage` | `messageId`, `chatId`, `sequence`, `role`, `text`, `createdAt`, `run: Run` |
| `SoloAiMessagePage` | `chatId`, `messages: Message[]`, `nextCursor: string|null` |

`latestRun` — последний принятый run, активный либо терминальный; null у пустого
чата. `lastMessageAt` — время последнего сохранённого сообщения, null у пустого чата.
`Message.run` для user — его последняя попытка, для assistant — создавший его
completed run. Это позволяет восстановить состояние неуспешного user message.
У user `messageId == run.userMessageId`, у assistant — `run.assistantMessageId`.
Role только `user`/`assistant`; user ID и assistant ID различаются.

`Run.deadlineAt > createdAt`. StartedAt/finishedAt не предшествуют созданию;
finishedAt не предшествует startedAt. Только completed содержит assistantMessageId.
Все терминальные состояния имеют finishedAt, все активные — null.

| State | startedAt | outcomeCode | assistantMessageId |
| --- | --- | --- | --- |
| pending | null | null | null |
| running | UTC | null | null |
| cancellation_requested | UTC или null | null | null |
| completed | UTC | null | UUID |
| failed | UTC или null | один из кодов ниже | null |
| timed_out | UTC или null | timed_out | null |
| incomplete | UTC | incomplete_response | null |
| cancelled | UTC или null | cancelled | null |
| interrupted | UTC | interrupted | null |

Коды failed: `configuration_failure`, `dependency_authentication_failed`,
`dependency_unavailable`, `provider_error`, `transport_error`, `limit_exceeded`,
`malformed_model_response`. Ошибка credentials Visograph/Butler dependency не
возвращается как пользовательский 401. Чтение принятого failed run — HTTP 200,
SDK возвращает Run; consumer проверяет state, а не только успешность HTTP.

## История и ограничения

Без cursor возвращаются последние сообщения. По умолчанию `limit=50`, диапазон
1–200. Сервер выбирает последние limit записей ниже границы и возвращает их
по возрастанию sequence. При наличии более ранних сообщений nextCursor указывает
на sequence первого сообщения страницы; иначе null. Пустая страница имеет null.
Новые сообщения после чтения страницы не сдвигают границу старых страниц.

Формат cursor v2: `<chatId в lowercase N>:<beforeSequence в decimal>`.
Граница исключительная, больше нуля; без пробелов, знака и ведущих нулей.
SDK/consumer передаёт полученный cursor без изменений, URL-encoding выполняет SDK.
`SoloAiMessageCursor` кодирует и проверяет связь с ChatId; cursor не доказывает
ownership. Полной неизменяемой snapshot всех страниц API не обещает: run states
могут обновляться. Порядок определяется sequence, не клиентскими часами.

Title — непустой после проверки whitespace, максимум 512 UTF-8 bytes; user/assistant
text — непустой, максимум 65 536 UTF-8 bytes. Невалидные Unicode strings отклоняются.
Сериализованный JSON дополнительно ограничен `MaxRequestBytes`/`MaxResponseBytes`:
это весь документ, включая escaping, envelope и metadata. Проверяется и Content-Length,
и фактический поток, включая chunked; переполнение не обрезает текст или JSON.
Для длинных страниц сервер может вернуть меньше limit сообщений в пределах byte cap,
с корректным nextCursor; если даже одна запись не помещается — `413 limit_exceeded`.

Положительные byte caps и конечный RequestTimeout задаются consumer конфигурацией.
Существующий TotalDeadline служит верхним конфигурационным пределом RequestTimeout;
SDK не выполняет polling и не держит deadline фонового run. RequestTimeout покрывает
HTTP headers и чтение body. Default configuration выключена; новые зависимости не добавлены.

## Идемпотентность и неизвестный исход

- Create: клиент хранит UUID попытки и принимает канонический ID из 200. Удалённые
  chat IDs не переиспользуются; concurrency сходится к одному чату owner.
- Send: messageId уникален среди хранимых сообщений owner. Дубликат проверяется
  до chat_busy и возвращает **первоначальный** run с актуальным состоянием,
  `retryId: null`; новый run/message/model call не создаётся. Другой chat/text
  при том же ID — `409 idempotency_conflict`. Текст сравнивается точно, без trim.
- Retry: retryId уникален в пределах owner. Повтор на том же chat/message возвращает
  тот же run; другой chat/message — `409 idempotency_conflict`. Новый retry допустим
  только последнему user message, без активного run, после failed/timed_out/incomplete/
  cancelled/interrupted. Нельзя повторять неизменённый переполненный контекст;
  validation/auth/deletion не превращаются в model retry. User message не дублируется.
- Cancel: отдельная идемпотентная команда. Завершённый run возвращается как есть;
  cancellation_requested не обещает остановку удалённого provider и ещё занимает слот.

`SoloAiExchangeException` содержит безопасный Code, nullable StatusCode и
`OutcomeUnknown`. Для команды после начала HTTP-отправки потеря соединения,
таймаут, malformed/foreign response либо переполнение ответа означают
`OutcomeUnknown=true`: команда могла быть сохранена. Известный валидный отказ API
и ошибки до отправки имеют false. GET не меняет состояние, его ошибки имеют false;
это не утверждение о текущем состоянии наблюдаемого run.

Локальные коды SDK: `configuration_failure`, `validation_failed`, `limit_exceeded`,
`unsupported_contract`, `invalid_response`, `transport_error`, `http_timeout`.
Http timeout отличается от сохранённого run `timed_out`.
Отмена caller token после начала запроса бросает `SoloAiRequestCanceledException`
(подтип OperationCanceledException) с OutcomeUnknown. Уже отменённый token до
отправки бросает OperationCanceledException. Ни один вариант не посылает cancel run.

SDK выполняет одну попытку, не повторяет команды, не делает polling, redirects или
model retry. Consumer сначала восстанавливает состояние из current/history/run;
при необходимости явно повторяет ту же команду с прежним idempotency key. Run ID
назначает сервер. Новый retryId означает новую попытку модели и требует явного действия.
Алгоритм «Начать заново» — подтверждение → DELETE старого ID → POST нового ID;
при неизвестном исходе сначала GET current, как описано в общем контракте.

## HTTP ошибки

| HTTP | Code |
| --- | --- |
| 400 | validation_failed, unsupported_contract |
| 401 | authentication_required |
| 403 | forbidden_actor |
| 404 | not_found |
| 409 | chat_busy, idempotency_conflict, retry_not_allowed |
| 413 | limit_exceeded |
| 429 | capacity_exceeded |
| 503 | unavailable |

Валидный error envelope означает, что эта команда не была принята. Ошибка записи
результата уже принятого run не маскируется таким отказом исходного send: consumer
читает ресурс, а runtime восстанавливает interrupted по политике AI-04.
Auth handler Solo отдельно различает истечение пользовательской сессии и собственный
сбой token exchange. SDK не интерпретирует внешний service credential как user expiry.
Неожиданный exception на HTTP boundary возвращает generic 503 без wire envelope
и без деталей исключения; исход команды считается неизвестным, поскольку commit
мог уже состояться. Такой ответ нельзя трактовать как доказанный отказ принятия.

## Проверки и потребление

Независимые [synthetic fixtures](../tests/Solo.Ai.Tests/Fixtures/SoloAgent) и xUnit tests покрывают все
операции, состояния, ошибки, correlation, строгий JSON, byte limits и cancellation.
Файловые проверки AI-02 покрывают CRUD/idempotency persistence, owner isolation,
ограничения БД и гонки storage. Auth и HTTP ownership реализованы в AI-03, worker относится к AI-04;
client fixtures подтверждают wire-контракт, не готовность live runtime.
SDK `0.2.0-preview.2`: [упаковка и consumer registration](client-package.md).
Backup/restore исключены из текущей итерации;
сохранность при restart относится к той же файловой БД, а не к восстановлению копии.
