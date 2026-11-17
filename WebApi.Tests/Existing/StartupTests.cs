using System.Net;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Existing;

[TestClass]
public class StartupTests
{
    [TestMethod]
    public async Task Health_ReturnsHealthy()
    {
        using var client = ApiHost.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Healthy", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Swagger_ServesDocument()
    {
        using var client = ApiHost.CreateClient();

        using var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
