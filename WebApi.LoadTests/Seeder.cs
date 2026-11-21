using System.Text.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Seeding;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Invariants;

namespace WebApi.LoadTests;

public sealed record LoadUser(long Id, string Email, long Enrollment, long? Waitlist);

public sealed record LoadData(List<LoadUser> Users, List<long> Classes, Dictionary<long, int> InitialBalances);

public static class Seeder
{
    public const int UserCount = 2000;
    public const int ClassCount = 300;
    public const int FullEvery = 5;
    public const int AdminCount = 100;

    public static string AdminEmail(int i)
    {
        return $"loadadmin{i:D3}@club.test";
    }

    public static async Task<int> RunAsync(string connection, string outDir)
    {
        var random = new Random(2904);
        var now = DateTime.UtcNow;
        await using var context = new ClubDbContext(new DbContextOptionsBuilder<ClubDbContext>().UseNpgsql(connection).Options);
        var rooms = await context.Rooms.Select(x => x.Id).ToListAsync();
        var hash = new PasswordHasher<User>().HashPassword(new User(), DatabaseSeeder.DefaultPassword);

        var users = Enumerable.Range(1, UserCount)
            .Select(i => new User { Email = $"load{i:D4}@club.test", PasswordHash = hash, Role = "user", CreatedAt = now })
            .ToList();
        context.Users.AddRange(users);
        // решение: аналитика — 3% смеси, у пяти сидовых админов она упёрлась бы в лимит 60 в минуту; синтетические админы делят её между собой.
        context.Users.AddRange(Enumerable.Range(1, AdminCount)
            .Select(i => new User { Email = AdminEmail(i), PasswordHash = hash, Role = "admin", CreatedAt = now }));
        var classes = Enumerable.Range(0, ClassCount)
            .Select(i => new Class
            {
                RoomId = rooms[i % rooms.Count],
                Title = $"Load {i}",
                StartsAt = now.AddMinutes(random.Next(60, 7 * 24 * 60)),
                DurationMinutes = 60,
                Capacity = random.Next(3, 21)
            })
            .ToList();
        context.Classes.AddRange(classes);
        await context.SaveChangesAsync();

        context.Passes.AddRange(users.Select(x => new Pass { UserId = x.Id, RemainingVisits = 999, ValidUntil = DateOnly.FromDateTime(now).AddYears(1) }));
        var full = classes.Where((_, i) => i % FullEvery == 0).ToList();
        var open = classes.Except(full).ToList();
        var taken = classes.ToDictionary(x => x.Id, _ => 0);
        var enrollments = new List<Enrollment>();
        var openIndex = 0;
        foreach (var user in users)
        {
            var target = full.FirstOrDefault(x => taken[x.Id] < x.Capacity);
            if (target is null)
            {
                // решение: в открытых занятиях засев оставляет одно свободное место, чтобы запись не упиралась только в capacity-exceeded.
                for (var tries = 0; tries < open.Count && (target is null || taken[target.Id] >= target.Capacity - 1); tries++)
                {
                    target = open[openIndex++ % open.Count];
                }
            }

            if (target is null || taken[target.Id] >= target.Capacity)
            {
                throw new InvalidOperationException("Not enough seats to give every load user an enrollment.");
            }

            taken[target.Id]++;
            enrollments.Add(new Enrollment { UserId = user.Id, ClassId = target.Id, Seats = 1, Status = EnrollmentStatuses.Active, CreatedAt = now });
        }

        context.Enrollments.AddRange(enrollments);
        await context.SaveChangesAsync();

        var entries = users.Where((_, i) => i % 2 == 0)
            .Select((x, i) => new WaitlistEntry { UserId = x.Id, ClassId = full[i % full.Count].Id, Seats = 1, Status = WaitlistStatuses.Waiting, CreatedAt = now })
            .Where(x => enrollments.All(e => e.UserId != x.UserId || e.ClassId != x.ClassId))
            .ToList();
        context.WaitlistEntries.AddRange(entries);
        await context.SaveChangesAsync();

        var classIds = classes.Select(x => x.Id).ToList();
        var userIds = users.Select(x => x.Id).ToList();
        var waitlist = entries.ToDictionary(x => x.UserId, x => x.Id);
        var data = new LoadData(
            users.Zip(enrollments, (u, e) => new LoadUser(u.Id, u.Email, e.Id, waitlist.TryGetValue(u.Id, out var w) ? w : null)).ToList(),
            classIds,
            await InvariantChecker.BalancesAsync(connection, classIds, userIds));

        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(Path.Combine(outDir, "data.json"), JsonSerializer.Serialize(data, Json.Options));
        Console.WriteLine($"seeded {users.Count} users, {classes.Count} classes ({full.Count} full), {entries.Count} waitlist entries");
        return 0;
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
