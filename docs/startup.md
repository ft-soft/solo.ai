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

Конфигурация обоих приложений дополняется локальным `appsettings.Secrets.json`
в каталоге проекта; этот файл исключён из Git. Переменные окружения и аргументы
командной строки имеют приоритет. Стендовых hosts, DLL-модулей, environment
bootstrap и подготовки одной заявки нет.

В Solo задаются `SoloAgent:Client` (URL Solo AI, Enabled, deadlines и byte caps)
и `SoloAgent:ServiceKey`. В Solo AI тот же ключ задаётся как `SoloBackend:ApiKey`,
а также настраиваются `SoloAgentModelApi`, `SoloAgentModel`, `VisographModelClient`.
Ключ Visograph принадлежит backend Solo AI. Его и служебный ключ клиент Solo
не отправляет. Defaults модельного пути выключены; локальные значения таймаутов
не являются принятой production SLA.

В Visograph отдельный блок `ModelExchange` не нужен: прокси использует активный
LLM-профиль распознавания и существующий API-ключ проекта. После обновления кода
перезапустите Visograph. [Пример прямого вызова](../../visograph/docs/reference/model-exchange.md#usage-example).

Backend Solo передаёт Bearer service credential и `X-Solo-User-Id`, вычисленный из
текущего `IUserService.User.RealEmployeeId`. Входящий browser header не используется.
Solo AI принимает delegated identity только после проверки служебного ключа.
На HTTP этот credential принимается только через loopback; для удалённого сервиса
нужен HTTPS. Правами чтения каталога продолжает управлять Solo.

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
Один чат принадлежит одному delegated user, одновременно исполняется один run;
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
