# 28. Ограничение частоты

Статус: проработано

С поведением при отказе Redis по итогам задачи 24.

- место в конвейере по решению задачи 2: между `UseAuthentication` и `UseAuthorization`, ключ —
  id пользователя из токена; запросы без пользователя не считать, им ответит `401`;
- ограничитель свой, не встроенный `AddRateLimiter` (решение задачи 27): у встроенного код
  отказа по умолчанию `503` (проверено), а состояние в памяти процесса;
- лимит в тестах тот же, 60: счётчики тестов разведены префиксом ключей (решение задачи 5).
  Тесты на 61-й запрос и на новое окно — переводом фейковых часов;
- `429` — событие 1003 и `club.requests.rejected` по схеме задачи 6, с `Retry-After`
  (решение задачи 27). Встроенных метрик `aspnetcore.rate_limiting.*` нет — счётчик свой;
  теги `operation: rate-limit`, `reason: too-many-requests`, в событии `userId` и `retryAfter`;
  тело — ProblemDetails из фабрики с `traceId`, без `reason`;
- вне лимита — `/health`, `/health/ready`, `/auth/login` и Swagger; тест на это (перенесено
  из задачи 7.4).

Разбита на подзадачи 28.1–28.2, каждая — свой коммит и своя строка статуса, порядок по номерам.
28.1 зависит только от задачи 27, 28.2 — от задачи 24 и от «стража» вызова Redis из 26.4.

## Проработка

Факты:

- в `ClientErrorMapping` ASP.NET 9 (`Microsoft.AspNetCore.Mvc.Core` 9.0.23) только коды
  из RFC 9110 и 4918, описания `429` нет: фабрика ProblemDetails отдала бы его без `type`
  и `title`;
- `AuthController` и `HarnessController` наследуют `BaseApiController`, а не `ClubApiController`:
  метка на `ClubApiController` их не захватывает;
- `[Authorize]` на абстрактном `ClubApiController` уже попадает в метаданные эндпоинтов
  наследников — метка рядом с ним попадёт так же;
- существующие тесты укладываются в 60 запросов от одного пользователя при стоящих часах:
  больше всех — обход расписания по одной записи в `SchedulePagingTests`, около 43 страниц
  (поиск по циклам, не полный прогон — его даст 28.1);
- «стража» вызова Redis в коде ещё нет, он делается в 26.4, которая по порядку раньше 28.2.
  Между коммитами 28.1 и 28.2 отказ Redis на эндпоинтах задания даёт `500`.

Решения:

- **Тело `429`** — `ClientErrorMapping[429]` в `ConfigureApiBehaviorOptions`: `Title =
  "Too Many Requests"`, `Link = "https://tools.ietf.org/html/rfc6585#section-4"`, с `// решение:`.
  Ответ — `application/problem+json`, как `409`.
- **Метка и middleware** — `RateLimitedAttribute` на `ClubApiController` и `RateLimitMiddleware`
  в `WebApi/RateLimiting/`; middleware читает `GetEndpoint()?.Metadata.GetMetadata<RateLimitedAttribute>()`.
  Ключ — `ClaimTypes.NameIdentifier` через `long.Parse`, как `CurrentUserId`; отдельной обработки
  битого claim нет — наши токены его всегда несут.
- **Скрипт** — строка Lua с `KEYS[1]` / `ARGV` через
  `IDatabase.ScriptEvaluateAsync(string, RedisKey[], RedisValue[])`, не `LuaScript.Prepare`:
  префикс `WithKeyPrefix` у `keys[]` проверяет первый тест 28.1.
- **Часы экземпляров** считаются синхронными; `Retry-After` сверху не ограничивается
  (заметки, «За рамками»).
- **Отказ Redis** — лимитер не повторяет ожидающие надгробия `CardCache`: общий у них только
  «страж». Запросы, пропущенные во время отказа, в журнал не попадают — после восстановления
  у пользователя снова полные 60 (заметки, «За рамками»).

## Подзадачи

- [28.1 Ограничитель](28.1-rate-limiter.md)
- [28.2 Ограничение при отказе Redis](28.2-rate-limit-redis-failure.md)
