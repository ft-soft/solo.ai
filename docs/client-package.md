# Solo.Ai.Client package

SDK `0.2.0-preview.2`, wire **2**, target framework `net10.0`.
Нормативный [HTTP API](solo-agent-api.md). Recommendation API/DTO v1 из пакета удалены;
обратная совместимость не заявлена. Пакет не содержит сервер, storage, Butler login,
worker или Visograph adapter. Зависимости прежние: Microsoft.Extensions.Http 10.0.10,
Solo.Toolbox 1.2.0. Исходники соседних checkout не нужны.

1 октября 2026 перед выбором версии проверены оба источника из NuGet.Config:
nuget.org — пакет отсутствует (404); FT-SOFT — только `0.1.0-preview.3`.
Локальная `0.2.0-preview.1` сохранена. `0.2.0-preview.2` свободна на момент проверки.
В ней отключены HttpClient factory loggers и обновлена документация runtime.
Это проверка, не резервирование:
публикация должна повторно проверить feed и не перезаписывать выпущенную версию.

```powershell
dotnet restore Solo.Ai.slnx
dotnet build Solo.Ai.slnx --no-restore
dotnet run --project tests/Solo.Ai.Tests --no-build --no-restore
dotnet pack src/Solo.Ai.Client/Solo.Ai.Client.csproj -c Release -o artifacts/packages
```

Выход: `artifacts/packages/Solo.Ai.Client.0.2.0-preview.2.nupkg`.
**Предоставление для S-02: локальный источник `D:/GitHub/solo.ai/artifacts/packages`.**
Публикации в remote feed не было. Не пересобирать эту версию с иным содержимым
после передачи consumer; для изменений выбрать следующую свободную версию.
Версии immutable: изменённый публикуемый пакет всегда получает новую свободную версию.

Consumer добавляет `PackageReference Include="Solo.Ai.Client" Version="[0.2.0-preview.2]"`,
фиксируя точную версию; при central
package management версия находится в Directory.Packages.props. Для локальной
проверки передайте каталог пакета дополнительным source, не прописывая соседние
checkout в tracked project/config:

```powershell
dotnet restore <consumer.csproj> -p:RestoreAdditionalProjectSources=D:/GitHub/solo.ai/artifacts/packages
```

Регистрация SDK в Solo:

```csharp
services.AddSoloAiClient(configuration)
    .AddHttpMessageHandler<SoloAgentDelegatedTokenHandler>(); // consumer-owned Butler handler
```

Имя handler в примере условное; реализуется и регистрируется в Solo, не в SDK.
Для подготовленного локального BOX: issuer `http://localhost:34100/butler`,
target audience `solo-ai.api`, client/actor `solo-ai-delegation`.
SubjectTokenAudience Solo — `75b1693c-58bc-4cbd-b4da-a04877c9935e`;
exchange преобразует legacy subject JWT в `at+jwt`. Значения сверить с конфигурацией BOX.
`AddSoloAiClient` возвращает IHttpClientBuilder named client `solo-ai`, запрещает
redirects/cookies, удаляет factory loggers и не добавляет retries. Не добавляйте retry policy для команд;
consumer handlers должны соблюдать CancellationToken и не менять URI/correlation headers.
При ручном предоставлении IHttpClientFactory consumer обязан сохранить эти свойства.
Consumer handlers и telemetry не должны логировать body/headers, Bearer tokens,
текст сообщений и raw exceptions. Проверка пакета не меняет соседний Solo.

Локальный configuration для S-02 (Butler handler регистрируется отдельно):

```json
{
  "SoloAgent": {
    "Client": {
      "Enabled": true,
      "BaseUri": "http://127.0.0.1:5081/",
      "RequestTimeout": "00:00:15",
      "TotalDeadline": "00:00:20",
      "MaxRequestBytes": 524288,
      "MaxResponseBytes": 524288
    }
  }
}
```

Настройки секции `SoloAgent:Client`: Enabled, BaseUri (deployment root с завершающим
`/`, без user info/query/fragment), RequestTimeout, TotalDeadline, MaxRequestBytes,
MaxResponseBytes. Options зарегистрированы через Toolbox AddConfiguration; прямое
внедрение и IOptions используют один экземпляр. Default выключен, caps/timeouts
обязательны. SDK не задаёт модельный deadline и не управляет сессией пользователя.

Все операции async, принимают CancellationToken. Пример вызовов:

```csharp
var chat = await client.GetCurrentChatAsync(ct)
    ?? await client.CreateChatAsync(pendingChatId, ct);
var page = await client.GetMessagesAsync(chat.ChatId, cancellationToken: ct);
var run = await client.SendMessageAsync(chat.ChatId, pendingMessageId, text, ct);
var state = await client.GetRunAsync(chat.ChatId, run.RunId, ct);
```

Pending IDs создаёт и сохраняет consumer для восстановления неизвестного исхода.
Он принимает канонический ChatId из create, проверяет run state даже при HTTP 200,
различает `authentication_required`, dependency failure внутри Run и OutcomeUnknown
у исключения SDK. Отмена ожидания не является отменой run. Детали — в API-документе.
