# Solo.Ai.Client

.NET 10 HTTP SDK для Solo AI chats/messages/runs, wire **2**, пакет **0.2.0-preview.2**.
Старые recommendation DTO и GenerateAsync удалены; wire v1 несовместим.

Методы: GetCurrentChatAsync, CreateChatAsync, GetChatAsync, GetMessagesAsync,
RenameChatAsync, DeleteChatAsync, SendMessageAsync, GetRunAsync, CancelRunAsync,
RetryRunAsync. Все принимают CancellationToken; GetCurrentChatAsync возвращает
null при 204. GetMessagesAsync загружает последние 50 сообщений (максимум 200),
затем страницы назад по nextCursor; внутри страницы порядок sequence возрастающий.

Регистрация: services.AddSoloAiClient(configuration), секция SoloAgent:Client.
Обязательны Enabled, BaseUri, RequestTimeout, TotalDeadline, MaxRequestBytes,
MaxResponseBytes. Defaults выключены. Named HttpClient: solo-ai; redirects,
cookies, factory HTTP logging и автоматические retries отключены. Consumer добавляет DelegatingHandler
для Butler Bearer token и сам владеет login/token exchange. SDK не отправляет user ID.

Проверяются wire version, HTTP request correlation и resource IDs, обязательные,
дублирующиеся и неизвестные JSON поля, состояния run и полные byte limits.
Create 200 может вернуть канонический ChatId, отличный от запрошенного.

SoloAiExchangeException: безопасный Code, StatusCode, OutcomeUnknown. После
потери ответа команды OutcomeUnknown=true: сначала восстановите состояние с сервера,
не создавайте новый idempotency key автоматически. User auth rejection отличается
от dependency failure принятого Run. HTTP 200 не означает completed.

CancellationToken отменяет только ожидание HTTP. После отправки отмена возвращает
SoloAiRequestCanceledException : OperationCanceledException с OutcomeUnknown.
Для отмены run явно вызывается CancelRunAsync. SDK не делает polling/model retry.

Source contract: docs/solo-agent-api.md в репозитории solo.ai. Host использует
Butler authentication, файловую SQLite и фоновую генерацию; health не вызывает модель.
Локальный endpoint: http://127.0.0.1:5081/, настройка consumer — SoloAgent:Client.
Источник проверки пакета: D:/GitHub/solo.ai/artifacts/packages; зафиксируйте
PackageReference Version="[0.2.0-preview.2]". Инструкция: docs/client-package.md.
Пакет предоставлен локально, публикация в feed не выполнялась.
