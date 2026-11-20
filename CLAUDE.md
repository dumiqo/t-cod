# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Fitness club API (.NET 9, ASP.NET Core, EF Core + Npgsql, Redis). Test assignment: the given code
has auth, data access and infrastructure; the job is the eight enrollment / schedule / analytics /
waitlist endpoints from `ЗАДАНИЕ.md`. Only `AuthController` exists so far. Before using any given
code, check the list of known traps in `docs/tasks/01-code-audit.md`.

## Commands

```
dotnet run --project WebApi                      # http://localhost:5080, /swagger, /health
docker compose down -v                           # fresh DB: seed times are relative to first start
docker compose up -d
dotnet tool restore
dotnet dotnet-ef migrations add <Name> --project DataAccessLayer --startup-project WebApi
dotnet test --filter "FullyQualifiedName~AuthTests.Login_WrongPassword_Returns401"
```

Postgres is on host port 5433 and Redis on 6380 (not the defaults). Tests honour `TEST_POSTGRES` /
`TEST_REDIS`. Seeded logins: `user001..200@club.test`, `admin001..005@club.test`, password
`Password123!` (`DatabaseSeeder.DefaultPassword`).

## Architecture

- `Common` — generic infrastructure: `GenericRepository<T>`, `UnitOfWorkBase` (caches repository
  instances per scope, `SaveChangesAsync` only — no transactions yet), `ICacheService` /
  `RedisCacheService`.
- `DataAccessLayer` — `ClubDbContext` (configurations applied from the assembly), entities
  `User/Room/Class/Enrollment/Pass`, typed repositories exposed as `IUnitOfWork.Room/Class/Enrollment/Pass`,
  `DatabaseSeeder`, migrations. `Class` has a global query filter `!IsCancelled`. Schema guards:
  `CHECK remaining_visits >= 0`, `CHECK seats >= 1`, unique `passes.user_id` (violation → 500).
  Writes go through `Transactions/TransactionRunner.RunAsync(operation, ct => …Commit(v)|Rollback(v), ct)`:
  Read Committed + `lock_timeout` from `Transactions:LockTimeout` (1 ms ≤ x < 30 s, default 10 s);
  55P03/40P01 → `TransactionConflictException(operation, "lock-timeout"|"deadlock")`. After
  `Rollback` do not `SaveChanges` in the same scope — tracked changes are still there.
- `WebApi` — `/health` (liveness, runs no checks) and `/health/ready` (Postgres and Redis checks
  in `Observability/`, JSON console logs). Everything is wired in `Program.cs`: on startup it migrates, seeds and connects to Redis
  eagerly (no Redis → no start). JWT validation with `ClockSkew = Zero`. `ExceptionHandlingMiddleware`
  turns any unhandled exception (including DB conflicts) into a 500 ProblemDetails. Controllers
  derive from `BaseApiController` (`CurrentUserId`, `IsAdmin`); our endpoints derive from
  `ClubApiController` (`[Authorize]`, `Reject(operation, reason)` → 409 with `reason`, event 1001 and
  `club.requests.rejected`). `Filters/TransactionConflictFilter` (global MVC filter) turns
  `TransactionConflictException` into 503 + `Retry-After: 1` with `reason`, event 1002 and
  `club.requests.rejected`. Instruments: `Observability/ClubMetrics`, `Observability/Log`.
  Query time/date params bind with `[ModelBinder(typeof(UtcDateTimeModelBinder|IsoDateModelBinder))]`
  (`Binding/`); required body: `[FromBody, Required] T? request`; JSON numbers are strict.
- `WebApi.Tests` — test isolation per the decision of task 5 (`NOTES.md`, «Изоляция тестов»). Tests run
  in parallel (`Parallelization.cs`, 8 workers); race tests are `[DoNotParallelize]`.
  - `TestApp.StartAsync()` — one per HTTP test (`await using`): its own clone of a seeded template DB,
    its own `WebApplicationFactory`, a stopped `FakeTimeProvider` at the seed moment (`app.Clock`,
    `app.SeededAt`), its own Redis key prefix (`app.Redis` sees keys as the app does),
    `app.CreateContext()` to check the DB, `app.CreateClientAsync(email)` for a logged-in client,
    `app.Logs` (`FakeLogCollector`, filtered by the host's log levels) to check log events.
    `StartAsync(builder => ...)` customises the host (runs last: `ConfigureTestServices`, config).
    Controllers from the test assembly are mounted (`HarnessController`, `GET /harness/me`).
  - `TestDb.CloneAsync()` — just a clone, for repository tests. The template is built lazily once per
    run and dropped in `TestDatabase.CleanupAsync`; a clone is dropped on dispose.
  - `HeldLock.AcquireAsync(connectionString, sql)` — a test's own connection holding row locks;
    `WaitForBlockedAsync(n)` waits until n sessions queue behind it (`pg_blocking_pids` chain).
  - Given code, kept for `Existing/`: `TestDatabase` (`club_tests_<guid>`, flushes Redis DB 1),
    `ApiHost` (ONE shared factory, server started under its lock), `ScratchDatabase`, `AuthClient`.

# Development rules

## Verify, don't recall

- Before editing a file or making a claim about code, read its current state. Do not rely on what
  you saw earlier in the conversation: files change (hooks reformat them, the user edits them).
- Before using a library API (EF Core 9, Npgsql 9, ASP.NET Core 9, MSTest 3.9, Moq,
  StackExchange.Redis), confirm it exists and behaves as you assume in the version referenced by the
  `.csproj` — through existing usages in the repo, the package's XML docs or sources, or a quick
  test. Never invent methods, options or configuration keys.
- Do not state facts about the repo (a file exists, a method is unused, a test covers X) unless you
  checked them in this session.

## Claims need evidence

- Say "done", "works", "fixed" or "passes" only together with the output of the command that shows
  it (build, `dotnet test`, an HTTP request). Quote the relevant lines.
- Say explicitly what was not verified (e.g. not run on a clean database, race not exercised).
- If a command failed or was skipped, say so. Never present a failure or a partial result as success.

## TDD is mandatory

Every new feature and every bug fix follows red → green → refactor:

1. **Test first.** Before touching the production code that implements a behaviour, write the tests
   that describe that behaviour. Do not change production code in the same step.
2. **See it red.** Run the new tests and confirm they fail for the right reason — on an assertion,
   not on a compile or infrastructure error. A stub is fine to make the test compile
   (`throw new NotImplementedException()`, an empty endpoint), but the failure must be an `Assert`.
3. **Minimal code to green.** Only as much as the tests require.
4. **Refactor** with tests green; run the full suite afterwards.
5. **Bug → reproducing test first**, watch it fail, then fix.

Order within a task is outside-in: first an integration test for the endpoint contract (status code,
body, database state), then unit tests for the logic that gets extracted along the way.

When reporting a task: list the added tests, what each one checks, and the red run output before
the code and the green run after.

## Tests must actually verify behaviour

- Assert observable results with exact values: status code, body fields, `reason` in 409 responses,
  database state after the request (read it through a fresh scope / `DbContext`, not the one the
  code under test used).
- No box-ticking tests: never assert only `200`, `IsNotNull` or "did not throw".
- Do not mirror the implementation: a mock `Verify` must never be a test's only assertion.
- Do not mock the code under test, do not mock EF Core / `DbContext`, do not use the InMemory
  provider: query filters, SQL translation, transactions and locking behave differently there.
- If it is not obvious that a test catches a regression, temporarily break the implementation and
  confirm the test goes red.
- Never weaken or rewrite existing tests to get green. If an existing test is in the way, stop and
  discuss it with the user.
- The readiness criteria in `docs/tasks.md` (a refusal leaves no trace, exact time boundaries,
  races) are tests, not just code.
- Every behaviour gets three groups of tests, not just the happy path. Before calling the tests
  for a task complete, go through each group and add what is missing:
  - **positive**: the valid request with exact response and state;
  - **negative**: every error row of the contract table (each `400` cause, `401`, `403`, `404`,
    each `409` `reason`), malformed input (unparsable JSON, missing / `null` / wrong-type fields,
    non-numeric ids), and that a refusal leaves no trace (DB state, no success event or metric);
  - **corner**: both sides of every boundary (equal / one tick before / one tick after, `0`, `1`,
    max, overflow), empty and duplicate values, time zones and offsets, leap days, concurrent
    requests, rollback after a partial write.

  Each case asserts the exact status, body and side effects; the task report lists the tests by
  group. A behaviour whose result is not yet decided is not pinned by a test: raise it with the
  user instead.

## Kinds of tests

| Kind | Covers | How |
|---|---|---|
| Integration (primary) | endpoint contract: auth, roles, status codes, ProblemDetails, body, database state, cache | `await using var app = await TestApp.StartAsync()`, real Postgres and Redis; the test creates the data it checks |
| Repository | new repository methods, queries, filters | `await using var db = await TestDb.CloneAsync()`, real Postgres |
| Unit | pure logic: visit refunds, the two-hour rule, date parsing, queue ordering | no database; Moq only for external boundaries (clock, cache) |
| Concurrency | invariants under parallel requests | concurrent HTTP requests via `Task.WhenAll` |

Test conventions: MSTest, names `Method_Scenario_Expected` (as in `Existing/`), new tests go into
per-feature folders, not `Existing/`. Time in tests comes from a replaceable clock — no
`Thread.Sleep`, no dependence on the current moment.

## Repository conventions

Source: `ЗАДАНИЕ.md` and `docs/tasks.md`. When they disagree with this file, they win.

- `ЗАДАНИЕ.md` is the source of truth for the contract. `docs/tasks.md` is the index: rules,
  readiness criteria, the task list with statuses. Each task's text (traps and non-obvious points
  only) lives in `docs/tasks/NN-<name>.md`. From task 20 on, each subtask has its own file
  `docs/tasks/NN.M-<name>.md` and the task file keeps the shared text and links. Nuances found while
  working on a task are appended to that task's (or subtask's) file; a status change is made in the
  task's (or subtask's) file and in the index together.
- **No comments in code.** The single exception is `// решение: ...` — one or two lines next to code
  that implements a decision the contract does not dictate, saying what was chosen and why. "Our code"
  is everything added after the `init` commit, tests included (`316d590` too); the given code is not
  ours to restyle.
- **Given code is extended, not changed.** Add a method next to it or an extension; what worked
  before must keep working. The given code may contain deliberate traps: fix them in the task that
  uses that code, usually with our own method alongside.
- Do not rename or move existing projects or files; README launch commands stay the same. New files,
  classes and projects are allowed.
- Seed data stays as is. If the schema changes, add a migration and make the seeder fill the new
  columns with meaningful values.
- Research tasks end with a decision recorded in the submission notes, not with code.
- Style per `.editorconfig`: file-scoped namespaces, braces on a new line, `System` usings first,
  CRLF, UTF-8.

## When stuck

- After two failed attempts at the same fix, stop: report what you tried, what you observed and your
  hypotheses, and wait for the user. Do not keep trying random changes.
- Never get to green by weakening or deleting a test, wrapping code in a catch-all `try/catch`,
  suppressing a warning or analyzer, silencing nullability with `!` without a reason, or
  special-casing test inputs in production code.

## Running tests

```
docker compose up -d
dotnet test
dotnet test --filter "FullyQualifiedName~WebApi.Tests.Enrollments"
```

`TestDatabase` (`[AssemblyInitialize]`) creates a separate database on every run, so even unit tests
need Postgres and Redis running. Servers come from `TEST_POSTGRES` / `TEST_REDIS`, otherwise from
`docker compose`; the Postgres role needs `CREATEDB`. Tests run in parallel: never share mutable
state between tests, move time with `app.Clock`, not by waiting.

## Enforced by hooks

`.claude/settings.json` runs the scripts in `.claude/hooks/`:

- **Test-first gate** (`tdd.mjs gate`, before Edit/Write): editing a production `.cs` file is denied
  unless some `.cs` file under a `*Tests/` directory is changed relative to `HEAD`. Do not work
  around it: no editing production code through the shell, no dummy test edits.
- **Existing tests** (`tdd.mjs gate`): editing a file under `*Tests/Existing/` asks the user for
  approval — those tests pin the behaviour of the given code. Explain why the change is needed
  before attempting it.
- **Formatting** (`format.mjs`, after Edit/Write): an edited `.cs` file is reformatted with
  `dotnet format whitespace` per `.editorconfig`. Re-read a file before editing it again.
- **Green on finish** (`tdd.mjs stop`): if build inputs (`.cs`, `.csproj`, `.json`, …) changed and
  this exact change set has not passed yet, runs `dotnet test`; a red run blocks finishing the turn.
  Stopping in the red phase on purpose (the user asked to review tests before the implementation)
  is allowed: say so and finish again without changing anything.

## Definition of done

A task is not done until: tests were written before the code and seen red, they are green now, the
full `dotnet test` passes, and the readiness criteria in `docs/tasks.md` are met.
