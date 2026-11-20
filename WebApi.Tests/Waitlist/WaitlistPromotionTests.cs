using DataAccessLayer.Entities;
using WebApi.Waitlist;

namespace WebApi.Tests.Waitlist;

[TestClass]
public class WaitlistPromotionTests
{
    private const long OwnUserId = 1000;

    private static readonly DateOnly Today = new(2026, 11, 20);

    [TestMethod]
    public void Plan_EntriesOutOfOrder_WalksByIdAndPromotesWhileSeatsLast()
    {
        var steps = Plan(2, Entry(30, 1), Entry(10, 1), Entry(20, 1));

        CollectionAssert.AreEqual(new[] { "10:", "20:", "30:no-seats" }, Describe(steps));
    }

    [TestMethod]
    public void Plan_FirstDoesNotFit_SkipsItAndPromotesNext()
    {
        var steps = Plan(2, Entry(1, 3), Entry(2, 2), Entry(3, 1));

        CollectionAssert.AreEqual(new[] { "1:no-seats", "2:", "3:no-seats" }, Describe(steps));
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    public void Plan_NoFreeSeats_PromotesNobody(long freeSeats)
    {
        var steps = Plan(freeSeats, Entry(1, 1), Entry(2, 1, OwnUserId));

        CollectionAssert.AreEqual(new[] { "1:no-seats", "2:own-release" }, Describe(steps));
    }

    [TestMethod]
    public void Plan_OwnEntryFits_SkippedAndSeatsGoToNext()
    {
        var steps = Plan(2, Entry(1, 2, OwnUserId), Entry(2, 2));

        CollectionAssert.AreEqual(new[] { "1:own-release", "2:" }, Describe(steps));
    }

    [TestMethod]
    public void Plan_ExpiredAndExhausted_SkipsAsExpired()
    {
        var entry = Entry(1, 2);

        var steps = WaitlistPromotion.Plan(2, [entry], Passes((entry, 0, Today.AddDays(-1))), OwnUserId, Today);

        CollectionAssert.AreEqual(new[] { "1:pass-expired" }, Describe(steps));
    }

    [TestMethod]
    public void Plan_NoSeatsAndBadPass_SkipsAsNoSeats()
    {
        var entry = Entry(1, 2);

        var steps = WaitlistPromotion.Plan(1, [entry], Passes((entry, 0, Today.AddDays(-1))), OwnUserId, Today);

        CollectionAssert.AreEqual(new[] { "1:no-seats" }, Describe(steps));
    }

    [TestMethod]
    [DataRow(0, "1:")]
    [DataRow(-1, "1:pass-expired")]
    public void Plan_ValidUntilBoundary_PromotesOnLastDay(int validUntilDays, string expected)
    {
        var entry = Entry(1, 2);

        var steps = WaitlistPromotion.Plan(2, [entry], Passes((entry, 2, Today.AddDays(validUntilDays))), OwnUserId, Today);

        CollectionAssert.AreEqual(new[] { expected }, Describe(steps));
    }

    [TestMethod]
    [DataRow(2, "1:")]
    [DataRow(1, "1:pass-exhausted")]
    public void Plan_RemainingVisitsBoundary_PromotesWhenEqualToSeats(int remainingVisits, string expected)
    {
        var entry = Entry(1, 2);

        var steps = WaitlistPromotion.Plan(2, [entry], Passes((entry, remainingVisits, Today)), OwnUserId, Today);

        CollectionAssert.AreEqual(new[] { expected }, Describe(steps));
    }

    [TestMethod]
    public void Plan_SkippedByPass_SeatsGoToNext()
    {
        var first = Entry(1, 2);
        var second = Entry(2, 2);

        var steps = WaitlistPromotion.Plan(2, [first, second], Passes((first, 1, Today), (second, 2, Today)), OwnUserId, Today);

        CollectionAssert.AreEqual(new[] { "1:pass-exhausted", "2:" }, Describe(steps));
    }

    [TestMethod]
    public void Plan_WaiterWithoutPassAndSeatsFit_Throws()
    {
        var entry = Entry(7, 1);

        var error = Assert.ThrowsException<InvalidOperationException>(
            () => WaitlistPromotion.Plan(1, [entry], new Dictionary<long, Pass>(), OwnUserId, Today));

        StringAssert.Contains(error.Message, entry.UserId.ToString());
    }

    private static IReadOnlyList<PromotionStep> Plan(long freeSeats, params WaitlistEntry[] entries)
    {
        return WaitlistPromotion.Plan(
            freeSeats,
            entries,
            Passes(entries.Select(x => (x, 100, Today.AddDays(30))).ToArray()),
            OwnUserId,
            Today);
    }

    private static WaitlistEntry Entry(long id, int seats, long? userId = null)
    {
        return new WaitlistEntry { Id = id, UserId = userId ?? id, ClassId = 1, Seats = seats, Status = WaitlistStatuses.Waiting };
    }

    private static Dictionary<long, Pass> Passes(params (WaitlistEntry Entry, int RemainingVisits, DateOnly ValidUntil)[] passes)
    {
        return passes.ToDictionary(
            x => x.Entry.UserId,
            x => new Pass { UserId = x.Entry.UserId, RemainingVisits = x.RemainingVisits, ValidUntil = x.ValidUntil });
    }

    private static string[] Describe(IReadOnlyList<PromotionStep> steps)
    {
        return steps.Select(x => $"{x.Entry.Id}:{x.SkipReason}").ToArray();
    }
}
