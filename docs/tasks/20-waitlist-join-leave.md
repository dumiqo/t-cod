# 20. Встать в очередь и уйти (§7, §8)

Статус: проработано

Таблица очереди, позиция, причины отказа, идемпотентность для §7. Отменённое занятие скрыто
фильтром — как в задаче 8: без `IgnoreQueryFilters()` вместо `409 class-cancelled` будет `404`.

Порядок причин — по задаче 3: тест на каждую пару причин (6 пар, 20.2) и ещё 4 пары
с `idempotency-key-reused` (20.4).

§7 и §8 — под замком занятия (решение задачи 4). Без него постановка может увидеть «мест
не хватает», пока параллельная отмена освобождает места и продвигает очередь без неё, и запись
будет ждать при свободных местах. Ключ идемпотентности §7 — после замка занятия, как в задаче 10.

Порядок очереди: `createdAt`, взятый до ожидания замка, расходится с порядком коммитов. A пришёл
раньше, B первым получил замок и ответ `position: 1`; затем A коммитится с меньшим `createdAt`,
тоже получает `position: 1` и встаёт перед B. Решено в задаче 19: порядок по `id` — он
выдаётся при вставке под замком занятия; `created_at` берётся после замка, как у всех пишущих
запросов (задача 8).

События (задача 6): постановка и уход — `club.waitlist.joined` и `club.waitlist.left`, после
коммита; отказы §7 — `operation = join-waitlist`; повторный уход событий не пишет.

Разбита на подзадачи 20.1–20.6, каждая — свой коммит и своя строка статуса, порядок по номерам.
Схема таблицы и порядок очереди — по решениям задач 18 и 19, идемпотентность — механизм
задачи 10. Вопрос о свободных местах при непустой очереди решён заранее и записан в задаче 21:
места общие, `seats-available` — `capacity − SumActiveSeatsAsync(classId) ≥ seats`, §1 не меняется.

Повторный разбор пройден: тексты подзадач уточнены, у каждой — статус «проработано». До 20.4
заголовок `Idempotency-Key` в §7 обязателен, но не сохраняется.

Согласовано при первом разборе:

- код: отдельный `WaitlistController` с `[Route("enrollments/waitlist")]`, `WaitlistService`
  в `WebApi/Waitlist/`, свои `JoinWaitlistRequest`, `JoinWaitlistResponse`,
  `LeaveWaitlistResponse` и результаты-объединения по образцу `EnrollResult` (`EnrollRequest`
  не переиспользуется); сущность `WaitlistEntry`, константы `WaitlistStatuses` (`waiting`,
  `cancelled`, `promoted`), репозиторий `IUnitOfWork.Waitlist`; в `Operations` —
  `JoinWaitlist`, `LeaveWaitlist`, в `RejectReasons` — `SeatsAvailable`, `AlreadyWaiting`,
  `AlreadyPromoted`. Тесты — `WebApi.Tests/Waitlist/`, помощник `WaitlistApi` по умолчанию шлёт
  новый ключ, как `EnrollApi` после 10.1;
- модель — зеркально `EnrollmentConfiguration`: навигация `Class` (для `IgnoreQueryFilters()`
  в §8) с `WithMany()` без коллекции в выданном `Class`, FK на `User` без навигации, удаление
  по умолчанию;
- `400` и `404` §7 — по образцу §1 (задача 8): `classId` `0`, отрицательный или несуществующий —
  `404`; нет, `null` или не число в `classId` / `seats`, `seats ≤ 0`, переполнение `int`, тело
  не JSON, нет заголовка — `400`;
- §8: быстрый путь для конечных статусов — запись без отслеживания `cancelled` → сразу `200`,
  `promoted` → сразу `409 already-promoted`, без транзакции и замка; через замок
  и перечитывание идёт только `waiting`;
- метрики `club.waitlist.joined`, `left` (и `promoted` в задаче 23) считают места, как
  `club.enrollments.created`: «ждут мест» = joined − left − promoted. События: 3001
  `WaitlistJoined` (`waitlistId`, `classId`, `seats`, `userId`, `position`), 3002 `WaitlistLeft`
  (`waitlistId`, `classId`, `seats`, `userId`); 3003 — продвижение, задача 23.

## Подзадачи

- [20.1 Таблица очереди и постановка (§7)](20.1-waitlist-table-join.md)
- [20.2 Причины 409 §7 и границы](20.2-join-conflicts.md)
- [20.3 Уход из очереди (§8)](20.3-leave-waitlist.md)
- [20.4 Идемпотентность §7](20.4-join-idempotency.md)
- [20.5 Конкурентность и часы](20.5-waitlist-concurrency.md)
- [20.6 Наблюдаемость](20.6-waitlist-observability.md)
