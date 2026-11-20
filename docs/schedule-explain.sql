-- План запроса страницы расписания на синтетической базе (задача 16.4).
-- База — отдельная, из чистой миграции, без сидов:
--   docker compose exec -T postgres createdb -U club club_explain
--   dotnet dotnet-ef database update --project DataAccessLayer --startup-project WebApi --connection "Host=localhost;Port=5433;Database=club_explain;Username=club;Password=club"
--   docker compose exec -T postgres psql -U club -d club_explain < docs/schedule-explain.sql
--   docker compose exec -T postgres dropdb -U club club_explain

\set ON_ERROR_STOP on

INSERT INTO users (email, password_hash, role, created_at)
SELECT 'load' || g || '@club.test', '-', 'Client', '2026-01-01' FROM generate_series(1, 1000) g;

INSERT INTO rooms (name) SELECT 'Room ' || g FROM generate_series(1, 50) g;

INSERT INTO classes (room_id, title, starts_at, duration_minutes, capacity, is_cancelled)
SELECT g % 50 + 1, 'Class ' || g, timestamptz '2026-01-01' + g * interval '1 minute', 60, 20, g % 10 = 0
FROM generate_series(1, 500000) g;

INSERT INTO enrollments (user_id, class_id, seats, status, created_at, cancelled_at, refunded_visits)
SELECT g / 3 % 1000 + 1, g % 500000 + 1, g % 3 + 1,
       CASE WHEN g % 7 = 0 THEN 'cancelled' ELSE 'active' END,
       '2026-01-01',
       CASE WHEN g % 7 = 0 THEN timestamptz '2026-01-01' END,
       CASE WHEN g % 7 = 0 THEN g % 3 + 1 END
FROM generate_series(1, 3000000) g;

VACUUM ANALYZE;

PREPARE page_all (timestamptz, timestamptz, bigint, int) AS
SELECT c0.id, c0.title, c0.starts_at, c0.duration_minutes, c0.capacity, c0.room_id, r.name, (
    SELECT COALESCE(sum(e.seats::bigint), 0.0)::bigint
    FROM enrollments AS e
    WHERE c0.id = e.class_id AND e.status = 'active'), (
    SELECT COALESCE(sum(e0.seats::bigint), 0.0)::bigint
    FROM enrollments AS e0
    WHERE c0.id = e0.class_id AND e0.status = 'active' AND e0.user_id = $3)
FROM (
    SELECT c.id, c.capacity, c.duration_minutes, c.room_id, c.starts_at, c.title
    FROM classes AS c
    WHERE NOT (c.is_cancelled) AND c.starts_at >= $1 AND c.starts_at < $2
    ORDER BY c.starts_at, c.id
    LIMIT $4
) AS c0
INNER JOIN rooms AS r ON c0.room_id = r.id
ORDER BY c0.starts_at, c0.id;

PREPARE page_all_after (timestamptz, timestamptz, timestamptz, bigint, bigint, int) AS
SELECT c0.id, c0.title, c0.starts_at, c0.duration_minutes, c0.capacity, c0.room_id, r.name, (
    SELECT COALESCE(sum(e.seats::bigint), 0.0)::bigint
    FROM enrollments AS e
    WHERE c0.id = e.class_id AND e.status = 'active'), (
    SELECT COALESCE(sum(e0.seats::bigint), 0.0)::bigint
    FROM enrollments AS e0
    WHERE c0.id = e0.class_id AND e0.status = 'active' AND e0.user_id = $5)
FROM (
    SELECT c.id, c.capacity, c.duration_minutes, c.room_id, c.starts_at, c.title
    FROM classes AS c
    WHERE NOT (c.is_cancelled) AND c.starts_at >= $1 AND c.starts_at < $2 AND (c.starts_at, c.id) > ($3, $4)
    ORDER BY c.starts_at, c.id
    LIMIT $6
) AS c0
INNER JOIN rooms AS r ON c0.room_id = r.id
ORDER BY c0.starts_at, c0.id;

PREPARE page_room (timestamptz, timestamptz, bigint, bigint, int) AS
SELECT c0.id, c0.title, c0.starts_at, c0.duration_minutes, c0.capacity, c0.room_id, r.name, (
    SELECT COALESCE(sum(e.seats::bigint), 0.0)::bigint
    FROM enrollments AS e
    WHERE c0.id = e.class_id AND e.status = 'active'), (
    SELECT COALESCE(sum(e0.seats::bigint), 0.0)::bigint
    FROM enrollments AS e0
    WHERE c0.id = e0.class_id AND e0.status = 'active' AND e0.user_id = $4)
FROM (
    SELECT c.id, c.capacity, c.duration_minutes, c.room_id, c.starts_at, c.title
    FROM classes AS c
    WHERE NOT (c.is_cancelled) AND c.starts_at >= $1 AND c.starts_at < $2 AND c.room_id = $3
    ORDER BY c.starts_at, c.id
    LIMIT $5
) AS c0
INNER JOIN rooms AS r ON c0.room_id = r.id
ORDER BY c0.starts_at, c0.id;

PREPARE page_room_after (timestamptz, timestamptz, bigint, timestamptz, bigint, bigint, int) AS
SELECT c0.id, c0.title, c0.starts_at, c0.duration_minutes, c0.capacity, c0.room_id, r.name, (
    SELECT COALESCE(sum(e.seats::bigint), 0.0)::bigint
    FROM enrollments AS e
    WHERE c0.id = e.class_id AND e.status = 'active'), (
    SELECT COALESCE(sum(e0.seats::bigint), 0.0)::bigint
    FROM enrollments AS e0
    WHERE c0.id = e0.class_id AND e0.status = 'active' AND e0.user_id = $6)
FROM (
    SELECT c.id, c.capacity, c.duration_minutes, c.room_id, c.starts_at, c.title
    FROM classes AS c
    WHERE NOT (c.is_cancelled) AND c.starts_at >= $1 AND c.starts_at < $2 AND c.room_id = $3 AND (c.starts_at, c.id) > ($4, $5)
    ORDER BY c.starts_at, c.id
    LIMIT $7
) AS c0
INNER JOIN rooms AS r ON c0.room_id = r.id
ORDER BY c0.starts_at, c0.id;

\echo '== первая страница, без зала'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF) EXECUTE page_all ('2026-01-01', '2027-01-01', 7, 21);
\echo '== середина по курсору, без зала'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF) EXECUTE page_all_after ('2026-01-01', '2027-01-01', '2026-07-01', 250000, 7, 21);
\echo '== первая страница, зал 7'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF) EXECUTE page_room ('2026-01-01', '2027-01-01', 7, 7, 21);
\echo '== середина по курсору, зал 7'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF) EXECUTE page_room_after ('2026-01-01', '2027-01-01', 7, '2026-07-01', 250000, 7, 21);

-- Загруженность залов (задача 17.3), SQL из лога EF.

PREPARE load_all (timestamptz, timestamptz) AS
SELECT s."Date", s.room_id, s.name, count(*)::int, COALESCE(sum(s.capacity::bigint), 0.0)::bigint, COALESCE(sum((
    SELECT COALESCE(sum(e.seats::bigint), 0.0)::bigint
    FROM enrollments AS e
    WHERE s.id = e.class_id AND e.status = 'active')), 0.0)::bigint
FROM (
    SELECT c.id, c.capacity, c.room_id, r.name, CAST(c.starts_at AT TIME ZONE 'UTC' AS date) AS "Date"
    FROM classes AS c
    INNER JOIN rooms AS r ON c.room_id = r.id
    WHERE NOT (c.is_cancelled) AND c.starts_at >= $1 AND c.starts_at < $2
) AS s
GROUP BY s."Date", s.room_id, s.name
ORDER BY s."Date", s.room_id;

PREPARE load_room (timestamptz, timestamptz, bigint) AS
SELECT s."Date", s.room_id, s.name, count(*)::int, COALESCE(sum(s.capacity::bigint), 0.0)::bigint, COALESCE(sum((
    SELECT COALESCE(sum(e.seats::bigint), 0.0)::bigint
    FROM enrollments AS e
    WHERE s.id = e.class_id AND e.status = 'active')), 0.0)::bigint
FROM (
    SELECT c.id, c.capacity, c.room_id, r.name, CAST(c.starts_at AT TIME ZONE 'UTC' AS date) AS "Date"
    FROM classes AS c
    INNER JOIN rooms AS r ON c.room_id = r.id
    WHERE NOT (c.is_cancelled) AND c.starts_at >= $1 AND c.starts_at < $2 AND c.room_id = $3
) AS s
GROUP BY s."Date", s.room_id, s.name
ORDER BY s."Date", s.room_id;

\echo '== загруженность, месяц, без зала'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF) EXECUTE load_all ('2026-03-01', '2026-04-01');
\echo '== загруженность, год, без зала'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF) EXECUTE load_all ('2026-01-01', '2027-01-01');
\echo '== загруженность, месяц, зал 7'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF) EXECUTE load_room ('2026-03-01', '2026-04-01', 7);
\echo '== загруженность, год, зал 7'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF) EXECUTE load_room ('2026-01-01', '2027-01-01', 7);
