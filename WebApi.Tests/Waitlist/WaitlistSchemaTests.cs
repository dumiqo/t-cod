using Npgsql;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Waitlist;

[TestClass]
public class WaitlistSchemaTests
{
    private const string FirstClass = "(SELECT min(id) FROM classes)";

    private const string FirstUser = "(SELECT min(id) FROM users)";

    private const string SecondUser = "(SELECT min(id) FROM users WHERE id > (SELECT min(id) FROM users))";

    [TestMethod]
    public async Task Id_DefaultsToEnrollmentsSequence()
    {
        await using var db = await TestDb.CloneAsync();

        Assert.AreEqual(
            "public.enrollments_id_seq",
            await ScalarAsync(db, "SELECT pg_get_serial_sequence('public.enrollments', 'id')"));
        Assert.AreEqual(
            "nextval('enrollments_id_seq'::regclass)",
            await ScalarAsync(
                db,
                "SELECT column_default FROM information_schema.columns WHERE table_name = 'waitlist_entries' AND column_name = 'id'"));
        Assert.AreEqual(
            "NO",
            await ScalarAsync(
                db,
                "SELECT is_identity FROM information_schema.columns WHERE table_name = 'waitlist_entries' AND column_name = 'id'"));
    }

    [TestMethod]
    public async Task EnrollmentsSequence_CacheSizeIsOne()
    {
        await using var db = await TestDb.CloneAsync();

        Assert.AreEqual(1L, await ScalarAsync(db, "SELECT cache_size FROM pg_sequences WHERE sequencename = 'enrollments_id_seq'"));
    }

    [TestMethod]
    public async Task Insert_WaitlistThenEnrollment_IdsComeFromOneSequence()
    {
        await using var db = await TestDb.CloneAsync();
        var maxEnrollment = (long)(await ScalarAsync(db, "SELECT max(id) FROM enrollments"))!;

        var waitlistId = (long)(await InsertAsync(db, FirstClass, FirstUser, 1, "waiting"))!;
        var enrollmentId = (long)(await ScalarAsync(
            db,
            $"INSERT INTO enrollments (user_id, class_id, seats, status, created_at) VALUES ({FirstUser}, {FirstClass}, 1, 'active', now()) RETURNING id"))!;

        Assert.AreEqual(maxEnrollment + 1, waitlistId);
        Assert.AreEqual(waitlistId + 1, enrollmentId);
    }

    [TestMethod]
    public async Task Indexes_PartialOnWaiting()
    {
        await using var db = await TestDb.CloneAsync();

        var indexes = await ColumnAsync(
            db,
            "SELECT indexdef FROM pg_indexes WHERE tablename = 'waitlist_entries' AND indexname <> 'PK_waitlist_entries' ORDER BY indexname");

        CollectionAssert.Contains(
            indexes,
            "CREATE INDEX \"IX_waitlist_entries_class_id_id\" ON public.waitlist_entries USING btree (class_id, id) WHERE (status = 'waiting'::text)");
        CollectionAssert.Contains(
            indexes,
            "CREATE UNIQUE INDEX \"IX_waitlist_entries_class_id_user_id\" ON public.waitlist_entries USING btree (class_id, user_id) WHERE (status = 'waiting'::text)");
    }

    [TestMethod]
    [DataRow(0, "waiting", "ck_waitlist_entries_seats")]
    [DataRow(-1, "waiting", "ck_waitlist_entries_seats")]
    [DataRow(1, "active", "ck_waitlist_entries_status")]
    [DataRow(1, "", "ck_waitlist_entries_status")]
    public async Task Insert_InvalidSeatsOrStatus_ViolatesCheckConstraint(int seats, string status, string constraint)
    {
        await using var db = await TestDb.CloneAsync();

        var exception = await Assert.ThrowsExceptionAsync<PostgresException>(() => InsertAsync(db, FirstClass, FirstUser, seats, status));

        Assert.AreEqual(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.AreEqual(constraint, exception.ConstraintName);
    }

    [TestMethod]
    [DataRow("waiting")]
    [DataRow("cancelled")]
    [DataRow("promoted")]
    public async Task Insert_EachStatus_Saves(string status)
    {
        await using var db = await TestDb.CloneAsync();

        await InsertAsync(db, FirstClass, FirstUser, 1, status);

        Assert.AreEqual(status, await ScalarAsync(db, "SELECT status FROM waitlist_entries"));
    }

    [TestMethod]
    [DataRow("-1", FirstUser, "FK_waitlist_entries_classes_class_id")]
    [DataRow(FirstClass, "-1", "FK_waitlist_entries_users_user_id")]
    public async Task Insert_MissingClassOrUser_ViolatesForeignKey(string classId, string userId, string constraint)
    {
        await using var db = await TestDb.CloneAsync();

        var exception = await Assert.ThrowsExceptionAsync<PostgresException>(() => InsertAsync(db, classId, userId, 1, "waiting"));

        Assert.AreEqual(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.AreEqual(constraint, exception.ConstraintName);
    }

    [TestMethod]
    public async Task Insert_SecondWaitingOfSameUserAndClass_ViolatesUniqueIndex()
    {
        await using var db = await TestDb.CloneAsync();
        await InsertAsync(db, FirstClass, FirstUser, 1, "waiting");

        var exception = await Assert.ThrowsExceptionAsync<PostgresException>(() => InsertAsync(db, FirstClass, FirstUser, 2, "waiting"));

        Assert.AreEqual(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.AreEqual("IX_waitlist_entries_class_id_user_id", exception.ConstraintName);
        Assert.AreEqual(1L, await ScalarAsync(db, "SELECT count(*) FROM waitlist_entries"));
    }

    [TestMethod]
    public async Task Insert_WaitingNextToCancelledAndPromotedOrOtherUser_Saves()
    {
        await using var db = await TestDb.CloneAsync();
        await InsertAsync(db, FirstClass, FirstUser, 1, "cancelled");
        await InsertAsync(db, FirstClass, FirstUser, 1, "promoted");
        await InsertAsync(db, FirstClass, FirstUser, 1, "cancelled");

        await InsertAsync(db, FirstClass, FirstUser, 1, "waiting");
        await InsertAsync(db, FirstClass, SecondUser, 1, "waiting");

        CollectionAssert.AreEqual(
            new object[] { "cancelled", "promoted", "cancelled", "waiting", "waiting" },
            await ColumnAsync(db, "SELECT status FROM waitlist_entries ORDER BY id"));
    }

    private static Task<object?> InsertAsync(TestDb db, string classId, string userId, int seats, string status)
    {
        return ScalarAsync(
            db,
            $"INSERT INTO waitlist_entries (class_id, user_id, seats, status, created_at) VALUES ({classId}, {userId}, {seats}, '{status}', now()) RETURNING id");
    }

    private static async Task<object?> ScalarAsync(TestDb db, string sql)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        var value = await command.ExecuteScalarAsync();

        return value is DBNull ? null : value;
    }

    private static async Task<List<object>> ColumnAsync(TestDb db, string sql)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<object>();

        while (await reader.ReadAsync())
        {
            values.Add(reader.GetValue(0));
        }

        return values;
    }
}
