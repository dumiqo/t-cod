using System.Net.Http.Json;
using System.Text.Json;
using DataAccessLayer.Seeding;

namespace WebApi.LoadTests;

public sealed record LoadToken(long Id, string Token, long Enrollment, long? Waitlist);

public static class Login
{
    public static async Task<int> RunAsync(string baseUrl, string outDir)
    {
        var data = JsonSerializer.Deserialize<LoadData>(await File.ReadAllTextAsync(Path.Combine(outDir, "data.json")), Json.Options)!;
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

        var users = new LoadToken[data.Users.Count];
        await Parallel.ForAsync(0, users.Length, options, async (i, ct) =>
        {
            var user = data.Users[i];
            users[i] = new LoadToken(user.Id, await TokenAsync(http, user.Email, ct), user.Enrollment, user.Waitlist);
        });
        var admins = new string[Seeder.AdminCount];
        await Parallel.ForAsync(0, admins.Length, options, async (i, ct) => admins[i] = await TokenAsync(http, Seeder.AdminEmail(i + 1), ct));

        await File.WriteAllTextAsync(Path.Combine(outDir, "tokens.json"), JsonSerializer.Serialize(new { users, admins }, Json.Options));
        Console.WriteLine($"logged in {users.Length} users and {admins.Length} admins");
        return 0;
    }

    private static async Task<string> TokenAsync(HttpClient http, string email, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("auth/login", new { email, password = DatabaseSeeder.DefaultPassword }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("accessToken").GetString()!;
    }
}
