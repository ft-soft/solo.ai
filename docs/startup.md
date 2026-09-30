# Обычный запуск Solo и Solo AI

Solo AI запускается из этого репозитория:

```powershell
dotnet run --project src/Solo.Ai.Host --launch-profile Development
```

Box запускается из репозитория Solo:

```powershell
dotnet run --project src/Demands.Web.Box --launch-profile Development
```

Для работы после перезапуска Codex запускайте эти команды в самостоятельном
Windows Terminal. Фоновые дочерние процессы Codex завершаются вместе с приложением.
Текущий запуск использует две вкладки Windows Terminal; их закрытие или Ctrl+C
останавливает соответствующий сервер. Логи видны непосредственно в этих вкладках.

Локальные настройки Box находятся в `src/Demands.Web.Box/appsettings.Development.json`
репозитория Solo. Solo AI использует `src/Solo.Ai.Host/appsettings.Secrets.json`.
Оба файла исключены из Git. Переменные окружения и аргументы командной строки
имеют приоритет. Стендовых hosts, DLL-модулей, environment bootstrap и подготовки
одной заявки нет.

В Solo задаётся `SoloAgent:Client` (URL Solo AI, Enabled, deadlines и byte caps).
В Solo AI настраиваются `SoloAgentModelApi`, `SoloAgentModel`, `VisographModelClient`.
Входящая авторизация Solo AI временно удалена: `SoloAgent:ServiceKey` и
`SoloBackend:ApiKey` больше не используются. Ключ Visograph по-прежнему принадлежит
backend Solo AI и не отправляется клиентом Solo. Defaults модельного пути выключены; локальные значения таймаутов
не являются принятой production SLA.

В Visograph отдельный блок `ModelExchange` не нужен: прокси использует активный
LLM-профиль распознавания и существующий API-ключ проекта. После обновления кода
перезапустите Visograph. [Пример прямого вызова](../../visograph/docs/reference/model-exchange.md#usage-example).

Backend Solo передаёт только `X-Solo-User-Id`, вычисленный из текущего
`IUserService.User.RealEmployeeId`. Входящий browser header не используется.
Solo AI требует один непустой GUID в этом заголовке, но не проверяет подлинность
отправителя. Это контекст для разделения чатов, а не авторизация.
Правами чтения каталога продолжает управлять Solo.

Для новой отправки достаточно:

```http
POST http://127.0.0.1:5540/api/v2/soloAgent/SendMessage
Content-Type: application/json

{"message":"Нужно подготовить служебную записку. Какой тип документа выбрать?"}
```

ID возвращаются в ответе. Для продолжения диалога добавьте полученный `chatId`.
Для защиты от повторной отправки клиент задаёт непустой `messageId` и сохраняет
его при повторе. Без `messageId` каждый вызов считается новой отправкой; автоматических
повторов нет. Swagger описывает request body и минимальный пример с сообщением.

На текущем локальном Box используется существующий Development fake-auth;
его FakeUserDefaults.Id указывает на реального сотрудника Box, поэтому `imid`
не требуется. При штатной авторизации клиент использует сессию Solo.

Solo AI владеет chat ownership, полной разрешённой историей и принятием run.
Входящая history должна быть пустой; runtime сам добавляет историю перед моделью.
Один чат привязан к одному переданному user ID, одновременно исполняется один run;
Повтор messageId того же пользователя отклоняется во всех чатах, включая повтор первой отправки с
новым chatId; request/run IDs также не принимаются повторно. Ошибки ownership и replay возвращают
HTTP 403. Завершение, ошибка или отмена освобождают
чат для новой отправки. Поздний результат не заменяет результат нового run.
Каталог заново читается Solo для каждого вызова и не сохраняется в истории.

Текущая история и replay state находятся в памяти процесса, максимум 1024 принятых
попытки до явного `limit_exceeded`. Restart стирает их; durable storage, восстановление
после сбоя, CRUD чатов и UI DOC-5748 не реализованы. При превышении byte caps история
и каталог не обрезаются. Проверка полного provider context/output reserve DG-02
остаётся открытой; локальный запуск не означает production-приёмку.
