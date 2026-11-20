using WebApi.RateLimiting;

namespace WebApi.Tests.RateLimiting;

[TestClass]
public class LocalRateJournalTests
{
    private const long T = 1_800_000_000_000;

    [TestMethod]
    public void TryReserve_60InWindow_61stRefusedWithOldestMoment()
    {
        var journal = new LocalRateJournal();
        for (var i = 0; i < 60; i++)
        {
            Assert.IsTrue(journal.TryReserve(1, T + i, out _), $"reservation {i + 1}");
        }

        Assert.IsFalse(journal.TryReserve(1, T + 100, out var oldest));

        Assert.AreEqual(T, oldest);
    }

    [TestMethod]
    public void TryReserve_WindowBoundary_RefusedAt59999MsAndAcceptedAt60000Ms()
    {
        var journal = new LocalRateJournal();
        Fill(journal, 1, T, 60);

        Assert.IsFalse(journal.TryReserve(1, T + 59_999, out _));
        Assert.IsTrue(journal.TryReserve(1, T + 60_000, out _));
    }

    [TestMethod]
    public void TryReserve_Refused_IsNotJournaled()
    {
        var journal = new LocalRateJournal();
        Fill(journal, 1, T, 59);
        Assert.IsTrue(journal.TryReserve(1, T + 30_000, out _));
        for (var i = 0; i < 10; i++)
        {
            Assert.IsFalse(journal.TryReserve(1, T + 40_000 + i, out _));
        }

        Assert.IsTrue(journal.TryReserve(1, T + 60_000, out _));
    }

    [TestMethod]
    public void Release_RemovesReservation_SlotIsFreeAgain()
    {
        var journal = new LocalRateJournal();
        Fill(journal, 1, T, 59);
        Assert.IsTrue(journal.TryReserve(1, T + 5, out _));

        journal.Release(1, T + 5);

        Assert.IsTrue(journal.TryReserve(1, T + 6, out _));
        Assert.IsFalse(journal.TryReserve(1, T + 7, out var oldest));
        Assert.AreEqual(T, oldest);
    }

    [TestMethod]
    public void Release_SameMomentTwice_RemovesOnlyOneReservationEachTime()
    {
        var journal = new LocalRateJournal();
        Fill(journal, 1, T, 60);

        journal.Release(1, T);

        Assert.IsTrue(journal.TryReserve(1, T + 1, out _));
        Assert.IsFalse(journal.TryReserve(1, T + 2, out _));
    }

    [TestMethod]
    public void Release_UnknownUser_DoesNothing()
    {
        var journal = new LocalRateJournal();

        journal.Release(42, T);

        Assert.AreEqual(0, journal.UserCount);
    }

    [TestMethod]
    public void TryReserve_DifferentUsers_HaveSeparateJournals()
    {
        var journal = new LocalRateJournal();
        Fill(journal, 1, T, 60);

        Fill(journal, 2, T, 60);

        Assert.IsFalse(journal.TryReserve(1, T, out _));
        Assert.IsFalse(journal.TryReserve(2, T, out _));
    }

    [TestMethod]
    public void TryReserve_AfterWindow_IdleUsersAreRemoved()
    {
        var journal = new LocalRateJournal();
        Assert.IsTrue(journal.TryReserve(1, T, out _));
        Assert.IsTrue(journal.TryReserve(2, T + 30_000, out _));
        Assert.IsTrue(journal.TryReserve(3, T + 59_999, out _));
        Assert.AreEqual(3, journal.UserCount);

        Assert.IsTrue(journal.TryReserve(3, T + 60_000, out _));

        Assert.AreEqual(2, journal.UserCount);

        Assert.IsTrue(journal.TryReserve(3, T + 120_000, out _));

        Assert.AreEqual(1, journal.UserCount);
    }

    [TestMethod]
    public void TryReserve_70Concurrent_Exactly60Accepted()
    {
        var journal = new LocalRateJournal();

        var accepted = 0;
        Parallel.For(0, 70, _ =>
        {
            if (journal.TryReserve(1, T, out _))
            {
                Interlocked.Increment(ref accepted);
            }
        });

        Assert.AreEqual(60, accepted);
    }

    [TestMethod]
    public void TryReserve_ConcurrentUsersAcrossSweeps_KeepEveryReservation()
    {
        var journal = new LocalRateJournal();

        for (var window = 0; window < 3; window++)
        {
            var now = T + 60_000L * window;
            var accepted = new int[50];
            Parallel.For(0, 50 * 70, i =>
            {
                if (journal.TryReserve(i % 50, now, out _))
                {
                    Interlocked.Increment(ref accepted[i % 50]);
                }
            });

            Assert.IsTrue(accepted.All(x => x == 60), $"window {window}: {string.Join(",", accepted)}");
        }
    }

    private static void Fill(LocalRateJournal journal, long userId, long now, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.IsTrue(journal.TryReserve(userId, now, out _), $"reservation {i + 1}");
        }
    }
}
