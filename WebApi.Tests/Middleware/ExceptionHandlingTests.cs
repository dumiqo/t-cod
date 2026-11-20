using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Middleware;

[TestClass]
public class ExceptionHandlingTests
{
    private const string Category = "WebApi.Middleware.ExceptionHandlingMiddleware";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task ClientAborts_EndsQuietlyWithoutErrorLogOr500()
    {
        var outcome = new PipelineOutcome();
        await using var app = await StartAsync(outcome);
        using var client = app.CreateClient();
        using var cts = new CancellationTokenSource();

        var send = client.GetAsync("/probe/exceptions/aborted", cts.Token);
        await outcome.Entered.Task.WaitAsync(Wait);
        await cts.CancelAsync();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => send);

        var result = await outcome.Completed.Task.WaitAsync(Wait);

        Assert.IsNull(result.Error);
        Assert.AreEqual(200, result.StatusCode);
        Assert.IsFalse(result.HasStarted);
        Assert.IsFalse(app.Logs.GetSnapshot().Any(x => x.Category == Category));
    }

    [TestMethod]
    public async Task CanceledWithoutAbort_Returns500ProblemAndLogsError()
    {
        var outcome = new PipelineOutcome();
        await using var app = await StartAsync(outcome);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/probe/exceptions/canceled");

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.AreEqual(500, body.RootElement.GetProperty("status").GetInt32());
        Assert.AreEqual("Internal server error", body.RootElement.GetProperty("title").GetString());

        var result = await outcome.Completed.Task.WaitAsync(Wait);

        Assert.IsNull(result.Error);

        var record = app.Logs.GetSnapshot().Single(x => x.Category == Category);

        Assert.AreEqual(LogLevel.Error, record.Level);
        Assert.IsInstanceOfType<OperationCanceledException>(record.Exception);
        Assert.AreEqual("probe cancellation", record.Exception.Message);
        Assert.AreEqual("Unhandled exception while processing GET /probe/exceptions/canceled", record.Message);
    }

    [TestMethod]
    public async Task FailureAfterResponseStarted_LogsErrorAndRethrowsOriginal()
    {
        var outcome = new PipelineOutcome();
        await using var app = await StartAsync(outcome);
        using var client = app.CreateClient();

        var thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => client.GetAsync("/probe/exceptions/started", HttpCompletionOption.ResponseHeadersRead));

        Assert.AreEqual(ExceptionProbeController.StartedMessage, thrown.Message);

        var result = await outcome.Completed.Task.WaitAsync(Wait);

        Assert.IsInstanceOfType<InvalidOperationException>(result.Error);
        Assert.AreEqual(ExceptionProbeController.StartedMessage, result.Error.Message);
        Assert.AreEqual(200, result.StatusCode);
        Assert.IsTrue(result.HasStarted);

        var record = app.Logs.GetSnapshot().Single(x => x.Category == Category);

        Assert.AreEqual(LogLevel.Error, record.Level);
        Assert.AreEqual(ExceptionProbeController.StartedMessage, record.Exception?.Message);
        Assert.AreEqual("Unhandled exception while processing GET /probe/exceptions/started", record.Message);
    }

    private static Task<TestApp> StartAsync(PipelineOutcome outcome)
    {
        return TestApp.StartAsync(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(outcome);
            services.AddTransient<IStartupFilter, PipelineOutcomeStartupFilter>();
        }));
    }
}
