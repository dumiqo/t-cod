# 7. Общий каркас

Статус: готово (8919ab8)

По итогам задач 1–6. Разбит на подзадачи, каждая — свой коммит и своя строка статуса. Порядок
7.1 → 7.4 → 7.3 → 7.2: обвязка нужна всем; основа наблюдаемости — до кода, который логирует
и считает свои отказы (`409` — 7.3, `503` — 7.2), чтобы не встраивать логирование задним числом;
7.3 даёт фабрику ProblemDetails, на которой строится ответ `503`.

- Каркас проверяется пробным контроллером в тестовой сборке (подключается к
  `WebApplicationFactory` как application part, как в задаче 2), в продакшен-код он не попадает.
  Задача 8 добавляет свои тесты уже на настоящем эндпоинте.
- TDD — и для обвязки тестов: хук-гейт её не проверяет, правило из `CLAUDE.md` действует.
- Старт на чистой базе (`docker compose down -v`, команды из `README`, `/health`
  и `/health/ready`) — в конце 7.1, 7.4 и 7.2: они меняют старт или схему. В 7.3 — полный
  `dotnet test`.
- Решения, которых контракт не диктует, записываются в заметки в коммите своей подзадачи,
  вместе с пометками `// решение:`.

Вынесено в другие задачи: когда читать часы в пишущих запросах и `validUntil` включительно —
задача 8; ключ идемпотентности в логе — задача 10; `/health/ready` вне лимита — задача 28;
счётчики и события операций — задачи, которые их порождают.

## 7.1 Обвязка тестов

Статус: готово (0c572fe)

По решению задачи 5:

- `TestDatabase` и `README` по серверам не меняются: серверы — из переменных или из
  `docker compose`, Testcontainers не берём;
- одноразовые объекты на тест вместо базового класса: `await using var app = await
  TestApp.StartAsync();` — клон, свой `WebApplicationFactory`, часы (`app.Clock`), префикс Redis,
  `app.CreateContext()` для проверки базы, вход сидовым пользователем; для тестов репозиториев —
  `TestDb.CloneAsync()`. Имена уточняются при реализации;
- шаблонная база — один раз за прогон, отдельно от базы `ApiHost`, строится лениво при первом
  запросе клона (`Lazy<Task>`): прогон одних юнит-тестов её не строит. К шаблону с открытыми
  соединениями клон не создаётся: после засева закрыть соединения и сбросить пул (проверено:
  после `NpgsqlConnection.ClearAllPools()` клон проходит). Клон — стратегией по умолчанию,
  `FILE_COPY` в 7 раз медленнее. Клон удаляется после теста, в том числе упавшего, как
  в `ScratchDatabase`: `ClearPool` и `DROP … WITH (FORCE)`. При `TEST_POSTGRES` роли нужен
  `CREATEDB` — выданная обвязка и так создаёт базу;
- шаблон удаляется в конце прогона одной строкой в выданном `TestDatabase.CleanupAsync`:
  `[AssemblyCleanup]` уже занят им, прецедент — вызов `ApiHost.DisposeAsync()` там же. Что второй
  `[AssemblyCleanup]` в сборке недопустим, проверить до кода. В заметках уточнить «`TestDatabase`
  не меняется»: меняется только очистка;
- хост теста: `FakeTimeProvider` вместо `TimeProvider` в DI, стартует с момента засева шаблона;
  `IConnectionMultiplexer` — обёртка с `WithKeyPrefix` на `GetDatabase()`. Пакет
  `Microsoft.Extensions.TimeProvider.Testing` 9.10.0 есть в кэше NuGet, `AdjustTime` в нём есть;
- `TimeProvider.System` регистрируется в `Program.cs` в этой подзадаче: без него обвязке нечего
  подменять;
- момент засева — `min(starts_at)` по всем занятиям с `IgnoreQueryFilters()`: самое раннее
  занятие сидер отменяет, с фильтром часы уйдут на 432 минуты назад;
- часы приложения — `TimeProvider` из DI, нигде не `DateTime.UtcNow` в нашем коде;
- `[assembly: Parallelize(Workers = 8, Scope = ExecutionScope.MethodLevel)]`, тесты гонок —
  `[DoNotParallelize]`. Без `Workers` MSTest берёт число ядер, и на многоядерной машине тесты
  упрутся в `max_connections`;
- `ApiHost` поднимает сервер под своим замком: сейчас под замком создаётся только фабрика,
  а сервер стартует при первом обращении, и параллельные тесты `Existing` могут поднять два хоста
  на одну базу. Правится в самом `ApiHost` — тесты `Existing` зовут `ApiHost.CreateClient()`.
  Есть ли у `WebApplicationFactory` 9.0 публичный `StartServer()`, проверить; иначе — обращение
  к `Server` под замком;
- TDD обвязки: сначала `TestApp` без подмен (настоящие часы, без префикса), тесты фактов падают
  на утверждениях; затем подмены. Тесты обвязки — в `WebApi.Tests/Harness/`, код обвязки —
  в `Infrastructure/`;
- не проверено, закрепить постоянными тестами — от этого зависит наш код:
  - выданный `RedisCacheService` через обёртку пишет ключи с префиксом (обёртка
    над интерфейсом через `DispatchProxy`);
  - момент `min(starts_at) + 43200` минут совпадает с `now` сидера: занятия через 20, 50, 80,
    100 и 115 минут по фейковым часам;
  - срок JWT сверяется с реальными часами: токен из входа проходит после перевода часов вперёд
    на срок жизни токена;
  - параллельные клоны из одного шаблона создаются без ошибок;
- не проверено, проверить разово и записать вывод сюда — это поведение MSTest и прогона,
  постоянный тест вышел бы косвенным:
  - `[DoNotParallelize]` в MSTest 3.9 не идёт одновременно с параллельными тестами:
    из XML-доков это не видно;
  - тесты `Existing` проходят при параллельном запуске;
- после обвязки обновить `CLAUDE.md`: разделы про `ApiHost`, `ScratchDatabase`, «Running tests»
  и ссылку на решение задачи 5.

Найдено при реализации:

- имена: `TestDb`, `TestApp`, `TestAppFactory` (наследник выданного `ApiApplicationFactory`),
  `KeyPrefixedConnection` (обёртка через `DispatchProxy`), `HarnessController` с `[Authorize]
  GET /harness/me`. `TestApp` всегда подключает тестовую сборку как application part: пробные
  контроллеры 7.2–7.3 просто кладутся в тестовую сборку, параметра настройки у `StartAsync` нет;
- второй `[AssemblyCleanup]` недопустим, проверено: MSTest 3.9.3 даёт UTA014, и тесты сборки
  перестают находиться. Шаблон удаляет `TestDatabase.CleanupAsync`;
- `TimeProvider.System` в DI уже есть — его регистрирует `AddAuthentication()` (проверено на пустом
  `ServiceCollection`; `AddControllers`, `AddHealthChecks`, `AddProblemDetails` — нет). `Program.cs`
  не менялся: подменять и так есть что, а своя регистрация не дала бы честного red;
- публичного `StartServer()` в `WebApplicationFactory` 9.0.7 нет. `EnsureServer` в исходнике
  release/9.0 проверяет и присваивает `_server` без синхронизации. `ApiHost` обращается к `Server`
  под замком в геттере `Factory` (тесты `Existing` зовут и `Factory.Services` напрямую).
  Воспроизводящего теста нет: синглтон на прогон не сбросить, гонка недетерминирована;
- JWT: перевода «на срок жизни» мало — срок токена отсчитан от реального времени, а фейковые часы
  стоят на моменте засева, чуть раньше. Тест переводит на срок жизни плюс сутки. Что тест ловит
  регрессию, проверено: временный `LifetimeValidator` по фейковым часам роняет его на `401`.
  Контрольный тест — токен, истёкший по реальному времени, получает `401`;
- `[DoNotParallelize]` в MSTest 3.9.3 идёт после всех параллельных тестов и по одному. Проверено
  по TRX временной парой: последний параллельный тест кончился в 33.013, первый последовательный
  начался в 33.014, второй — после него;
- тесты `Existing` при параллельном запуске проходят: 3 полных прогона подряд, 37 из 37.

## 7.4 Наблюдаемость

Статус: готово (9eae322)

Основа по решению задачи 6: JSON-логи, готовность, доступ к логам в тестах, тест на утечки.
Инструментов здесь нет: `ClubMetrics`, класс `Log` и `club.requests.rejected` заводит 7.3,
событие `503` — 7.2, каждый со своим сквозным тестом. Без вызывающего кода их проверял бы
только тест, который сам их и вызывает.

- `appsettings.json`: `Logging:Console:FormatterName = json`, `FormatterOptions`:
  `IncludeScopes = true`, `UseUtcTimestamp = true`, `TimestampFormat` с `Z` — JSON-форматтер
  без него время не пишет. `Microsoft.AspNetCore` остаётся на Warning. Автотеста нет — он
  повторил бы конфиг; проверка — запуск по `README`, строка лога с `TraceId` в отчёте;
- `/health/ready`: свои `IHealthCheck` без доп. пакетов, регистрация — расширение
  `AddClubReadinessChecks()`, его зовут `Program` и тест проверок. База — `SELECT 1`
  (`failureStatus: Unhealthy`), Redis — `PingAsync` (`Degraded`), тег `ready`,
  `AddCheck<T>(name, failureStatus, tags, timeout)` с `timeout` 3 секунды. Анонимный
  (`AllowAnonymous`), ответ — только статус. Вне лимита — в задаче 28;
- причину падения пишет встроенный `DefaultHealthCheckService`, своего события нет;
- у `MapHealthChecks("/health")` фильтр `Predicate = _ => false`, иначе он запустит новые
  проверки;
- тесты проверок: сломать базу или Redis в хосте теста нельзя — приложение при старте мигрирует
  и подключается к Redis. Поэтому: сама проверка через настоящий `HealthCheckService` (база
  на закрытом порту — `Unhealthy` и Error в логе, Redis на закрытом соединении — `Degraded`
  и Warning), тайм-аут на «молчащем» `TcpListener` и проводка (хост теста добавляет падающую
  проверку с тегом `ready`: `/health` отвечает `200 Healthy`, `/health/ready` — `503`);
- `TestApp.StartAsync(Action<IWebHostBuilder>? configure)` и `app.Logs` (`FakeLogCollector`
  из `Microsoft.Extensions.Diagnostics.Testing` 9.10.0);
- тест на утечки: все категории на Trace, вход и запрос с токеном — в тексте, полях, scope
  и исключениях нет email, пароля и токена; записей EF и JwtBearer больше нуля. Что тест ловит
  регрессию — проверить временным `EnableSensitiveDataLogging`.

Проверено до кода (пробная программа на тех же пакетах):

- `CanConnectAsync` на закрытом порту возвращает `false`, исключение проглочено — причина
  не попала бы в лог. Отсюда `SELECT 1`;
- проверка, которая бросает, пишется встроенным логом как `HealthCheckError` (#104) на Error
  при любом `failureStatus`, тайм-аут тоже. Возвращённый статус — `HealthCheckEnd` (#103):
  `Degraded` на Warning, `Unhealthy` на Error, с исключением. Поэтому проверки ловят исключение
  и возвращают `context.Registration.FailureStatus` с ним;
- `timeout` регистрации только отменяет токен: проверка, которая его не слушает, шла 5 секунд
  при тайм-ауте 300 мс. У `PingAsync` токена нет, отсюда `WaitAsync(ct)`. Ping на закрытый порт
  без него — `RedisConnectionException` через 5 секунд (`AsyncTimeout`);
- проверка со scoped-зависимостью (`ClubDbContext`) разрешается при `ValidateScopes`;
- в пакете 9.10.0 есть `FakeLogCollector` и `MetricCollector(IMeterFactory, meter, instrument)`.
  `IMeterFactory` в голом `ServiceCollection` нет (нужен `AddMetrics`); есть ли он в хосте —
  проверить первым тестом 7.3.

Найдено при реализации:

- Npgsql не прерывает по токену подключение к молчащему серверу: проверка базы шла 10 секунд
  (`Timeout` строки теста). `WaitAsync` вокруг запроса EF не годится: scope уничтожает контекст,
  пока соединение в `Connecting`, и `HealthCheckService` бросает `InvalidOperationException`
  («Can't close, connection is in state Connecting») — на `/health/ready` это был бы `500`.
  Проверка базы открывает своё `NpgsqlConnection` со строкой контекста и `Timeout`,
  `CommandTimeout` = 3 секунды (та же константа, что у регистрации); по тайм-ауту —
  `NpgsqlException` с `TimeoutException` внутри;
- `GetConnectionString()` контекста строит модель EF, и в лог попадает её предупреждение
  валидации (`Class` с фильтром — обязательная сторона связи с `Enrollment`). Тест лога
  готовности смотрит только категорию `DefaultHealthCheckService`;
- что тесты ловят регрессию, проверено поломкой: без `WaitAsync` у Redis — 5 секунд вместо 3;
  без тайм-аутов Npgsql — 10,3 секунды; Redis-проверка бросает — `HealthCheckError` вместо
  `HealthCheckEnd`; `EnableSensitiveDataLogging` — тест утечек находит email в 4 записях EF;
- запуск по `README` на чистой базе: `/health` и `/health/ready` — `200 Healthy`. Postgres
  остановлен — `/health/ready` `503 Unhealthy` за 0,08 секунды, в логе Error с `NpgsqlException`,
  `/health` по-прежнему `200`; Redis остановлен — `200 Degraded` за 3,0 секунды, Warning
  с `TaskCanceledException`. `traceId` ответа `500` найден `grep` в трёх JSON-строках лога, email
  и пароль в лог не попали.

## 7.3 Правила контракта

Статус: готово (94db797)

- инструменты из 7.4 (решение задачи 6): `Meter` `FitnessClub.Api` через `IMeterFactory`,
  singleton `ClubMetrics` со счётчиком `club.requests.rejected{operation, reason}`; события —
  `[LoggerMessage]` с постоянными `EventId` в `static partial class Log`
  (`WebApi/Observability/`), диапазоны: 1xxx — отказы и инфраструктура, 2xxx — запись и брони,
  3xxx — очередь и т. д. События успеха — после коммита. Тесты — `app.Logs` по `EventId`
  и `MetricCollector` от `IMeterFactory` хоста;
- свой базовый контроллер `ClubApiController : BaseApiController`, выданный не меняется;
- `[Authorize]` — на `ClubApiController`: `FallbackPolicy` закроет `/auth/login`, `/health`
  и Swagger и сломает тесты в `Existing`;
- ответ 409 — `Reject(operation, reason)` в `ClubApiController`: поле `reason` верхнего уровня
  в ProblemDetails, через `ProblemDetailsFactory`, чтобы в ответе был `traceId`; `title`
  стандартный. `operation` — явной константой. Там же отказ логируется и считается
  (`club.requests.rejected{operation, reason}`). Тест: `traceId` ответа совпадает с `TraceId`
  события;
- нечисловой id → `400`: ограничение маршрута `{id:long}` дало бы `404`;
- время: ASP.NET переводит значение с `Z` или смещением в локальное время сервера, а без
  смещения оставляет «неопределённым» — разбирать самим как UTC;
- даты (`from`/`to` загруженности) — строго `yyyy-MM-dd`. Привязка к `DateOnly`
  принимает `2026-09-22T10:00`, молча отбрасывая время, а отсутствующий параметр превращает
  в `0001-01-01` — оба случая дают `200` вместо `400`;
- время и даты во входе бывают только в query (§5 и §6), в телах их нет — JSON-конвертер
  не нужен. Разбор — чистый парсер с юнит-тестами и тонкие model binder'ы поверх него
  (`[ModelBinder(typeof(...))]`): ошибка разбора и отсутствие параметра — в `ModelState`,
  `400` отвечает `[ApiController]` тем же видом, что на нечисловой id. В эндпоинты binder'ы
  попадают в задачах 16 и 17;
- параметр «дата и время» принимает `yyyy-MM-dd` (полночь UTC), `yyyy-MM-ddTHH:mm`
  с необязательными секундами и дробью, с `Z`, смещением `±HH:mm` или без смещения; остальное,
  в том числе пробел вместо `T`, — `400`. Голую дату принимаем: контракт её не запрещает,
  а автотест с ней скорее ждёт `200` (вопрос из задачи 15);
- обязательные числовые поля тела: отсутствующее поле без `[Required]` на nullable-типе
  становится `0`;
- число в кавычках (`"seats": "2"`) по умолчанию принимается: веб-настройки System.Text.Json
  читают числа из строк. По решению задачи 2 это `400`: `JsonNumberHandling.Strict` в
  `JsonOptions` даёт `400`, вход при этом работает;
- тело без содержимого и без `Content-Type`: ASP.NET отвечает `415`, по решению задачи 2 нужно
  `400`. С nullable-параметром (`[FromBody] T? request`) отсутствующее тело, пустое тело
  с `application/json` и литерал `null` приходят как `null` — `400` отвечаем сами; тело
  `text/plain` с содержимым остаётся `415`. Без этого `POST /enrollments/abc/seats` без тела
  даёт `415`, а не `400`. Исключение — пустое тело кусками (`Transfer-Encoding: chunked`) без
  `Content-Type`: `415` и на Kestrel, и на TestServer. Решает `CanHaveBody`, а при chunked он
  `true` даже для нуля байт (проверено).

Решено при разборе (вопросы и ответы до кода):

- `operation` — явный аргумент `Reject`, значения в kebab-case, как у `reason`: `enroll`,
  `change-seats`, `cancel-enrollment`, `get-enrollment`, `schedule`, `occupancy`, `join-waitlist`,
  `leave-waitlist`. Классы `Operations` и `RejectReasons` заводит задача 8 — в 7.3 их константами
  никто не пользуется, проба берёт свои;
- тело `409` — `type`, `title`, `status`, `traceId`, `reason`, без `detail`;
- событие отказа — `EventId` 1001 `RequestRejected`, Information, поля `Operation`, `Reason`,
  `UserId`; id сущностей не пишутся — путь виден в scope хостинга (`RequestPath`). 1002 оставлен
  для `503` (7.2), 1003 — для `429` (28);
- дата-время — `DateTime` с `Kind = Utc`: дробь от 1 до 7 цифр (формат `O` в .NET даёт 7), `T`
  и `Z` только заглавные, смещение не больше 14:00, секунда `60` и выход за диапазон `DateTime`
  после приведения к UTC — `400`;
- обязательные числа тела — `[Required] int?`, не `[JsonRequired] int`: ошибка по имени поля.

Найдено при реализации:

- `IMeterFactory` в хосте есть: `MetricCollector` от `IMeterFactory` хоста видит счётчик,
  `AddMetrics` не нужен. `RequestPath` и `TraceId` есть в scope записи `FakeLogCollector`;
- пустое тело решено без своего кода: `[FromBody, Required] T? request` — nullable не даёт `415`,
  `[Required]` превращает `null` в стандартный `400` «The body field is required.» под ключом
  `""`. Фильтр не понадобился. Число в кавычках даёт две ошибки: `$.seats` и `body` (тело после
  ошибки разбора — `null`);
- `reason` кладётся в `Extensions` того, что вернула `ProblemDetailsFactory`; `title` и `type`
  для `409` она берёт из `ClientErrorMapping`;
- у отсутствующего параметра `ModelName` в binder'е пустой: без подстановки `FieldName` ошибка
  уходит под ключ `""` с текстом «The  field is required.» (проверено поломкой);
- binder'ы — одна общая логика `RequiredQueryValueBinding`: отсутствие и пустое значение —
  «The from field is required.», неверный формат — стандартный текст
  `AttemptedValueIsInvalidAccessor`.

Найдено при сверке с контрактом (после задачи 30):

- клиент, не закодировавший `+` в query (`from=2026-09-22T13:00+03:00`), шлёт пробел: query
  декодирует `+` в пробел, и разбор отвечал `400`. Пробел на месте знака смещения — только между
  временем и `HH:mm` — теперь читается как `+`. Пробел вместо `T`, пробел перед `Z`, два пробела,
  табуляция и пробел перед смещением без двоеточия по-прежнему `400`. Тесты:
  `UtcDateTimeParserTests` (строки с пробелом в обеих группах) и
  `ScheduleTests.Get_FromWithUnencodedPlusOffset_ConvertsToUtc`.

## 7.2 Транзакции и отказы

Статус: готово (5148cf7)

По решению задачи 4:

- свой исполнитель транзакций рядом с выданным `UnitOfWork`: scoped-сервис с делегатом,
  `RunAsync(async ct => { … return result; }, ct)` — `BeginTransactionAsync(ReadCommitted)`,
  первой командой `SET LOCAL lock_timeout`, делегат, коммит или откат. Результат — наружу,
  события успеха пишет вызывающий после коммита. Что сервис и `UnitOfWork` получают один
  `ClubDbContext` в scope, проверить до кода;
- `lock_timeout` — из настройки `Transactions:LockTimeout` (TimeSpan, по умолчанию
  `00:00:10`), проверка при старте (`Validate` + `ValidateOnStart`): больше нуля и меньше
  30 секунд. Строку подключения менять нельзя, а без тайм-аута Postgres ждёт блокировку
  бесконечно. Это предел одного ожидания, не транзакции. До 30 секунд не поднимать:
  `CommandTimeout` Npgsql по умолчанию 30 секунд прервёт команду раньше, и вместо `503` будет
  `500`. Тест: хост с `00:00:30` не стартует;
- тайм-аут блокировки (`55P03`) и взаимоблокировка (`40P01`) → `503` с `Retry-After: 1`
  и ProblemDetails через глобальный фильтр исключений MVC. Что фильтр срабатывает раньше
  `ExceptionHandlingMiddleware` — проверить первым тестом. `503` логируется и считается в фильтре
  (`club.requests.rejected`); событие `503` на Warning заводится здесь, в `Log` из 7.3;
- EF без стратегии повторов оборачивает эти ошибки в `InvalidOperationException`, нарушение
  ограничения — в `DbUpdateException`: распознавать по внутреннему `PostgresException.SqlState`.
  `IsTransient` не подходит: он `true` и для тайм-аута блокировки. Обёртки закрепить тестом —
  на них держится распознавание;
- глубина `PostgresException` разная, обходить всю цепочку (проверено на EF 9.0.7, Npgsql 9.0.3):
  - запрос (`FromSql ... FOR UPDATE`), 55P03 и 40P01: `InvalidOperationException` →
    `PostgresException`;
  - `SaveChanges` (ожидание на уникальном индексе ключа, взаимоблокировка при `UPDATE`), 55P03
    и 40P01: `InvalidOperationException` → `DbUpdateException` → `PostgresException`, поэтому
    `catch (DbUpdateException)` их не ловит;
  - `SaveChanges`, 23505: `DbUpdateException` → `PostgresException`;
  - исчерпан пул Npgsql (ожидание до `Timeout`, по умолчанию 15 секунд): `NpgsqlException` →
    `TimeoutException` без `SqlState`; из `BeginTransactionAsync` — без обёртки
    `InvalidOperationException`. По `SqlState` не распознаётся, сейчас это `500`.

  Тест на обёртки — на оба пути, запрос и `SaveChanges`, и `23505` на `SaveChanges`. При
  `lock_timeout` не больше `deadlock_timeout` (1 секунда) взаимоблокировка приходит как 55P03,
  а не 40P01;
- тесты `503` — на пробном эндпоинте: 55P03 — тест держит замок занятия на своём соединении,
  `lock_timeout` хоста теста 300 мс, ответ `503`, `Retry-After: 1`, ProblemDetails; 40P01 —
  встречные замки двух соединений теста, `lock_timeout` 2 секунды (больше `deadlock_timeout`),
  `[DoNotParallelize]`;
- `EnableRetryOnFailure` не подходит: с ним `BeginTransaction` вне `ExecuteAsync` бросает
  `InvalidOperationException`. Тайм-аут блокировки он, судя по `IsTransient = true`, тоже
  повторял бы — это не проверено;
- миграция: `CHECK (remaining_visits >= 0)`, `CHECK (seats >= 1)`, уникальный индекс
  на `passes.user_id` вместо обычного. Сиды и тесты в `Existing` им не противоречат.
  Нарушение — ошибка нашего кода, `500`.

Решено при разборе (вопросы и ответы до кода):

- исполнитель — `DataAccessLayer/Transactions/TransactionRunner`, без интерфейса, зависит
  от `IUnitOfWork` и работает через `uow.DbContext`: контекст общий по построению;
- `RunAsync(operation, ct => Task<TransactionResult<T>>, ct)`: делегат возвращает
  `TransactionResult.Commit(value)` или `Rollback(value)`. Явный откат нужен для отказа `409`:
  ключ идемпотентности пишется до проверки абонементов, и отказ после него коммитить нельзя;
- 55P03/40P01 распознаёт исполнитель: откатывает и бросает
  `TransactionConflictException(operation, reason)`, `reason` — `lock-timeout` / `deadlock`.
  Фильтр ловит только этот тип, так он знает `operation` для счётчика;
- `Transactions:LockTimeout` — от 1 мс включительно: Postgres считает `lock_timeout` в целых мс,
  и `0` — «без предела». Значение ставит `set_config('lock_timeout', @ms, true)` — то же, что
  `SET LOCAL`, но с параметром;
- тело `503` — как у `409`: `type`, `title`, `status`, `traceId`, `reason`; событие 1002
  `TransactionConflict` (Warning, `Operation`, `Reason`, `UserId`, с исключением);
- ограничения описаны в выданных `PassConfiguration` и `EnrollmentConfiguration` (добавлены,
  прежнее не менялось), миграция `AddIntegrityConstraints` сгенерирована, а не написана
  руками: иначе снимок модели разошёлся бы с базой.

Найдено при реализации:

- в `ClientErrorMapping` ASP.NET 9 запись для 503 есть: `title` «Service Unavailable»,
  `type` — rfc9110 §15.6.4;
- глобальный фильтр исключений MVC срабатывает раньше `ExceptionHandlingMiddleware`: на `503`
  записи middleware в логе нет;
- без `lock_timeout` ожидание замка обрывает `CommandTimeout` Npgsql через 30 секунд:
  `InvalidOperationException` → `500` (заглушка исполнителя в red-фазе, 31 с на тест);
- `ValidateOnStart` у `WebApplicationFactory` бросает `OptionsValidationException` без обёртки;
- `SET LOCAL` действует только в транзакции: после неё на том же соединении `lock_timeout` — `0`
  (закреплено тестом);
- взаимоблокировка в тесте: жертва — сессия, которая ждёт дольше, у неё раньше истекает
  `deadlock_timeout`. Эндпоинт встаёт в ожидание первым (тест ждёт этого по `pg_blocking_pids`),
  поэтому `40P01` получает он, а соединение теста получает замок. Хелпер — `HeldLock`;
- после `Rollback` сущности остаются в `ChangeTracker` изменёнными: `SaveChanges` в том же scope
  после отката записал бы их вне транзакции. Вызывающий после `Rollback` ничего не сохраняет;
- тест обёртки `23505` — в `ConstraintsTests` (тот же точный тип `DbUpdateException`).
  Что тесты обёрток ловят регрессию, проверено временной заменой ожидаемого типа: оба красные
  с «Actual exception type: InvalidOperationException»;
- запуск по `README` на чистой базе: миграции `Initial` и `AddIntegrityConstraints` применены,
  в базе `ck_passes_remaining_visits`, `ck_enrollments_seats` и уникальный `IX_passes_user_id`;
  `/health` и `/health/ready` — `200 Healthy`.
