using System.Net.Http.Json;
using DataAccessLayer.Seeding;
using WebApi.Auth;

namespace WebApi.Tests.Infrastructure;

public static class AuthClient
{
    public const string SeededUser = "user001@club.test";

    public const string SeededAdmin = "admin001@club.test";

    public static Task<HttpResponseMessage> LoginAsync(
        this HttpClient client,
        string email,
        string password = DatabaseSeeder.DefaultPassword)
    {
        return client.PostAsJsonAsync("/auth/login", new { email, password });
    }

    public static async Task<string> GetTokenAsync(
        this HttpClient client,
        string email,
        string password = DatabaseSeeder.DefaultPassword)
    {
        using var response = await client.LoginAsync(email, password);

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();

        Assert.IsNotNull(body, "Login response body is empty.");

        return body.AccessToken;
    }
}
