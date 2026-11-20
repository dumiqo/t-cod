using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using WebApi.Auth;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Contract.ContractSetup;

namespace WebApi.Tests.Contract;

[TestClass]
public class AuthMatrixTests
{
    private const string None = "none";
    private const string Garbage = "garbage";
    private const string Forged = "forged";
    private const string Expired = "expired";
    private const string Unsigned = "alg-none";

    public static IEnumerable<object[]> InvalidTokens()
    {
        string[] endpoints = [Enroll, ChangeSeats, Cancel, Card, GetSchedule, RoomLoad, Join, Leave];
        string[] tokens = [None, Garbage, Forged, Expired, Unsigned];

        return endpoints.SelectMany(endpoint => tokens.Select(token => new object[] { endpoint, token }));
    }

    [TestMethod]
    [DynamicData(nameof(InvalidTokens), DynamicDataSourceType.Method)]
    public async Task Request_InvalidToken_Returns401WithoutTrace(string endpoint, string token)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var setup = await CreateAsync(app, user);
        using var client = app.CreateClient();
        using var request = setup.Request(endpoint);
        var bearer = Token(app, token, user.Id, endpoint == RoomLoad ? "admin" : "user");
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        var before = await setup.TraceAsync();

        using var response = await client.SendAsync(request);

        await AssertUnauthorizedAsync(response);
        Assert.AreEqual(before, await setup.TraceAsync());
    }

    [TestMethod]
    [DataRow(Enroll, "/enrollments", "{")]
    [DataRow(ChangeSeats, "/enrollments/abc/seats", "{")]
    [DataRow(Cancel, "/enrollments/abc/cancel", null)]
    [DataRow(Card, "/enrollments/abc", null)]
    [DataRow(GetSchedule, "/schedule", null)]
    [DataRow(RoomLoad, "/analytics/load", null)]
    [DataRow(Join, "/enrollments/waitlist", "{")]
    [DataRow(Leave, "/enrollments/waitlist/abc/cancel", null)]
    public async Task Request_NoTokenAndInvalidInput_Returns401(string endpoint, string path, string? json)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var setup = await CreateAsync(app, user);
        using var client = app.CreateClient();
        using var request = endpoint.StartsWith("GET")
            ? new HttpRequestMessage(HttpMethod.Get, path)
            : Post(path, json);
        var before = await setup.TraceAsync();

        using var response = await client.SendAsync(request);

        await AssertUnauthorizedAsync(response);
        Assert.AreEqual(before, await setup.TraceAsync());
    }

    private static async Task AssertUnauthorizedAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, text);
        Assert.AreEqual("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.AreEqual(string.Empty, text);
    }

    private static string? Token(TestApp app, string kind, long userId, string role)
    {
        var options = app.Services.GetRequiredService<JwtOptions>();
        Claim[] claims = [new(ClaimTypes.NameIdentifier, userId.ToString()), new(ClaimTypes.Role, role)];
        var now = DateTime.UtcNow;

        return kind switch
        {
            None => null,
            Garbage => "not-a-token",
            Forged => Write(new JwtSecurityToken(options.Issuer, options.Audience, claims, now.AddMinutes(-1), now.AddHours(1), Sign(new string('x', 64)))),
            Expired => Write(new JwtSecurityToken(options.Issuer, options.Audience, claims, now.AddHours(-1), now.AddMinutes(-1), Sign(options.Key))),
            Unsigned => Unsign(Write(new JwtSecurityToken(options.Issuer, options.Audience, claims, now.AddMinutes(-1), now.AddHours(1)))),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static SigningCredentials Sign(string key)
    {
        return new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
    }

    private static string Write(JwtSecurityToken token)
    {
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string Unsign(string token)
    {
        var header = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(token.Split('.')[0]));
        Assert.AreEqual("{\"alg\":\"none\",\"typ\":\"JWT\"}", header);
        StringAssert.EndsWith(token, ".");

        return token;
    }
}
