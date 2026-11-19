using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Observability;

[TestClass]
public class ReadinessEndpointTests
{
    [TestMethod]
    public async Task HealthReady_DependenciesUp_Returns200Healthy()
    {
        await using var app = await TestApp.StartAsync();
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Healthy", await response.Content.ReadAsStringAsync());

        var ready = app.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Where(x => x.Tags.Contains("ready"))
            .Select(x => x.Name)
            .Order()
            .ToArray();

        CollectionAssert.AreEqual(new[] { "postgres", "redis" }, ready);
    }

    [TestMethod]
    public async Task HealthReady_ReadyCheckUnhealthy_Returns503Unhealthy()
    {
        await using var app = await StartWithCheckAsync(HealthStatus.Unhealthy, "ready");
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual("Unhealthy", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task HealthReady_ReadyCheckDegraded_Returns200Degraded()
    {
        await using var app = await StartWithCheckAsync(HealthStatus.Degraded, "ready");
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Degraded", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task HealthReady_UntaggedCheckUnhealthy_Returns200Healthy()
    {
        await using var app = await StartWithCheckAsync(HealthStatus.Unhealthy, "other");
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Healthy", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Health_ReadyCheckUnhealthy_Returns200Healthy()
    {
        await using var app = await StartWithCheckAsync(HealthStatus.Unhealthy, "ready");
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Healthy", await response.Content.ReadAsStringAsync());
    }

    private static Task<TestApp> StartWithCheckAsync(HealthStatus status, string tag)
    {
        return TestApp.StartAsync(builder => builder.ConfigureTestServices(services =>
            services.AddHealthChecks().AddCheck("probe", () => new HealthCheckResult(status), [tag])));
    }
}
