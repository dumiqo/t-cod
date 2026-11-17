using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using DataAccessLayer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using WebApi.Auth;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Existing;

[TestClass]
public class AuthTests
{
    [TestMethod]
    public async Task Login_SeededUser_ReturnsTokenWithUserClaims()
    {
        using var client = ApiHost.CreateClient();
        var lifetime = TimeSpan.FromMinutes(ApiHost.Factory.Services.GetRequiredService<JwtOptions>().LifetimeMinutes);
        var before = DateTime.UtcNow;

        using var response = await client.LoginAsync(AuthClient.SeededUser);

        var after = DateTime.UtcNow;

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.IsNotNull(body);
        var principal = Validate(body.AccessToken);
        var userId = await GetUserIdAsync(AuthClient.SeededUser);

        Assert.AreEqual(userId.ToString(), principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.AreEqual(AuthClient.SeededUser, principal.FindFirstValue(ClaimTypes.Name));
        Assert.AreEqual("user", principal.FindFirstValue(ClaimTypes.Role));
        Assert.IsTrue(body.ExpiresAt >= before + lifetime && body.ExpiresAt <= after + lifetime);
    }

    [TestMethod]
    public async Task Login_SeededAdmin_ReturnsTokenWithAdminRole()
    {
        using var client = ApiHost.CreateClient();

        var token = await client.GetTokenAsync(AuthClient.SeededAdmin);

        Assert.AreEqual("admin", Validate(token).FindFirstValue(ClaimTypes.Role));
    }

    [TestMethod]
    public async Task Login_WrongPassword_Returns401()
    {
        using var client = ApiHost.CreateClient();

        using var response = await client.LoginAsync(AuthClient.SeededUser, "wrong-password");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Login_UnknownEmail_Returns401()
    {
        using var client = ApiHost.CreateClient();

        using var response = await client.LoginAsync("nobody@club.test");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Login_MissingFields_Returns400()
    {
        using var client = ApiHost.CreateClient();

        using var response = await client.PostAsJsonAsync("/auth/login", new { });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task Login_EmptyStrings_Returns400()
    {
        using var client = ApiHost.CreateClient();

        using var response = await client.LoginAsync("", "");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static ClaimsPrincipal Validate(string token)
    {
        var options = ApiHost.Factory.Services.GetRequiredService<JwtOptions>();

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = options.Issuer,
            ValidAudience = options.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key)),
            ClockSkew = TimeSpan.Zero
        };

        return new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);
    }

    private static async Task<long> GetUserIdAsync(string email)
    {
        using var scope = ApiHost.Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();

        return await dbContext.Users.Where(x => x.Email == email).Select(x => x.Id).SingleAsync();
    }
}
