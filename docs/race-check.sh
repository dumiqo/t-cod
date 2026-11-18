set -eu

DB=race_check
W=/tmp/race-check
PSQL="psql -X -q -t -A -v ON_ERROR_STOP=1 -U club"

q() { $PSQL -d "$DB" -c "$1"; }

bench() {
  label=$1
  shift
  echo "-- $label"
  pgbench -n -U club -d "$DB" --failures-detailed "$@" 2>&1 \
    | grep -E "actually processed|failed transactions|serialization failures|deadlock failures|transactions retried|total number of retries|latency average" \
    | sed 's/^/   /' || true
}

rm -rf "$W"
mkdir -p "$W"
cd "$W"

$PSQL -d postgres -c "DROP DATABASE IF EXISTS $DB WITH (FORCE)"
$PSQL -d postgres -c "CREATE DATABASE $DB"

q "CREATE TABLE classes (id bigint PRIMARY KEY, capacity int NOT NULL);
   CREATE TABLE passes (id bigint PRIMARY KEY, user_id bigint NOT NULL, remaining_visits int NOT NULL);
   CREATE TABLE enrollments (id bigserial PRIMARY KEY, user_id bigint NOT NULL,
     class_id bigint NOT NULL REFERENCES classes (id), seats int NOT NULL, status text NOT NULL);
   CREATE INDEX ON enrollments (class_id);
   INSERT INTO classes VALUES (1, 5), (2, 10), (3, 10), (4, 100), (5, 100), (6, 100);
   INSERT INTO passes SELECT g, g, 10 FROM generate_series(1, 2000) g;"

FREE="SELECT c.capacity - COALESCE(SUM(e.seats), 0) AS free FROM classes c LEFT JOIN enrollments e ON e.class_id = c.id AND e.status = 'active' WHERE c.id = 1 GROUP BY c.capacity \\gset"

cat > enroll-naive.sql <<EOF
BEGIN;
$FREE
SELECT pg_sleep(0.03);
\\if :free >= 1
INSERT INTO enrollments (user_id, class_id, seats, status) VALUES (:client_id + 1, 1, 1, 'active');
\\endif
COMMIT;
EOF

cat > enroll-lock.sql <<EOF
BEGIN;
SELECT 1 FROM classes WHERE id = 1 FOR NO KEY UPDATE;
$FREE
SELECT pg_sleep(0.03);
\\if :free >= 1
INSERT INTO enrollments (user_id, class_id, seats, status) VALUES (:client_id + 1, 1, 1, 'active');
\\endif
COMMIT;
EOF

cat > enroll-serializable.sql <<EOF
BEGIN ISOLATION LEVEL SERIALIZABLE;
$FREE
SELECT pg_sleep(0.03);
\\if :free >= 1
INSERT INTO enrollments (user_id, class_id, seats, status) VALUES (:client_id + 1, 1, 1, 'active');
\\endif
COMMIT;
EOF

echo "== 1. 20 parallel enrollments, 1 seat each, capacity 5"
for v in naive lock serializable; do
  q "TRUNCATE enrollments"
  bench "$v" -c 20 -j 20 -t 1 -f "enroll-$v.sql"
  echo "   active seats: $(q "SELECT COALESCE(SUM(seats), 0) FROM enrollments WHERE class_id = 1")"
done
q "TRUNCATE enrollments"
bench "serializable, up to 50 tries" -c 20 -j 20 -t 1 --max-tries=50 -f enroll-serializable.sql
echo "   active seats: $(q "SELECT COALESCE(SUM(seats), 0) FROM enrollments WHERE class_id = 1")"

for v in class-lock-only pass-lock; do
  if [ "$v" = pass-lock ]; then PASS_LOCK="FOR UPDATE"; else PASS_LOCK=""; fi
  cat > "spend-$v.sql" <<EOF
\\set cls 2 + :client_id
BEGIN;
SELECT 1 FROM classes WHERE id = :cls FOR NO KEY UPDATE;
SELECT remaining_visits AS rem FROM passes WHERE user_id = 21 $PASS_LOCK \\gset
SELECT pg_sleep(0.03);
\\if :rem >= 2
UPDATE passes SET remaining_visits = :rem - 2 WHERE user_id = 21;
INSERT INTO enrollments (user_id, class_id, seats, status) VALUES (21, :cls, 2, 'active');
\\endif
COMMIT;
EOF
done

echo "== 2. one client with 3 visits, two parallel enrollments of 2 seats on different classes"
for v in class-lock-only pass-lock; do
  q "TRUNCATE enrollments; UPDATE passes SET remaining_visits = 3 WHERE user_id = 21"
  bench "$v" -c 2 -j 2 -t 1 -f "spend-$v.sql"
  echo "   remaining: $(q "SELECT remaining_visits FROM passes WHERE user_id = 21"), seats booked: $(q "SELECT COALESCE(SUM(seats), 0) FROM enrollments WHERE user_id = 21")"
done

echo "== 3. xmin of the class row when an enrollment is inserted"
before=$(q "SELECT xmin FROM classes WHERE id = 1")
q "INSERT INTO enrollments (user_id, class_id, seats, status) VALUES (1, 1, 1, 'active')"
echo "   before: $before, after: $(q "SELECT xmin FROM classes WHERE id = 1")"

cat > passes-crosswise.sql <<EOF
\\set a 1 + :client_id
\\set b 2 - :client_id
BEGIN;
SELECT 1 FROM passes WHERE id = :a FOR UPDATE;
SELECT pg_sleep(0.2);
SELECT 1 FROM passes WHERE id = :b FOR UPDATE;
COMMIT;
EOF

cat > passes-ordered.sql <<EOF
\\set a 1 + :client_id
\\set b 2 - :client_id
BEGIN;
SELECT 1 FROM passes WHERE id IN (:a, :b) ORDER BY id FOR UPDATE;
SELECT pg_sleep(0.005);
COMMIT;
EOF

echo "== 4. two passes locked in opposite order"
bench "separate statements, 2 transactions" -c 2 -j 2 -t 1 -f passes-crosswise.sql
bench "one statement ORDER BY id, 200 transactions" -c 2 -j 2 -t 100 -f passes-ordered.sql

echo "== 5. enrollment transaction without delays, N parallel on one class"
for n in 20 50 90; do
  cls=$((n / 30 + 3))
  cat > "realistic-$n.sql" <<EOF
BEGIN;
SELECT 1 FROM classes WHERE id = $cls FOR NO KEY UPDATE;
SELECT c.capacity - COALESCE(SUM(e.seats), 0) AS free FROM classes c LEFT JOIN enrollments e ON e.class_id = c.id AND e.status = 'active' WHERE c.id = $cls GROUP BY c.capacity \\gset
SELECT remaining_visits FROM passes WHERE user_id = :client_id + 100 FOR UPDATE;
INSERT INTO enrollments (user_id, class_id, seats, status) VALUES (:client_id + 100, $cls, 1, 'active');
UPDATE passes SET remaining_visits = remaining_visits - 1 WHERE user_id = :client_id + 100;
COMMIT;
EOF
  rm -f pgbench_log.*
  echo "-- N=$n"
  pgbench -n -U club -d "$DB" -c "$n" -j "$n" -t 1 -l -f "realistic-$n.sql" 2>&1 \
    | grep -E "actually processed" | sed 's/^/   /' || true
  echo "   slowest transaction: $(cat pgbench_log.* | awk 'BEGIN { m = 0 } $3 > m { m = $3 } END { printf "%.0f ms", m / 1000 }')"
done

echo "== 6. lock 1000 passes in one statement ORDER BY id"
psql -X -U club -d "$DB" -c '\timing on' -c 'BEGIN' \
  -c 'SELECT count(*) FROM (SELECT id FROM passes WHERE id <= 1000 ORDER BY id FOR UPDATE) s' -c 'ROLLBACK' \
  | grep -E "^Time" | sed -n 2p | sed 's/^/   /'

cd /
$PSQL -d postgres -c "DROP DATABASE IF EXISTS $DB WITH (FORCE)"
rm -rf "$W"
