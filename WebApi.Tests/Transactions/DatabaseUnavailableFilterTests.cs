using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Testing;
using Npgsql;
using WebApi.Filters;
using WebApi.Observability;

namespace WebApi.Tests.Transactions;

[TestClass]
public class DatabaseUnavailableFilterTests
{
    public static IEnumerable<object[]> NotUnavailable =>
    [
        [new NpgsqlException("broken")],
        [new NpgsqlException("broken", new IOException("reset"))],
        [new PostgresException("duplicate", "ERROR", "ERROR", "23505")],
        [new TimeoutException("not npgsql")],
        [new InvalidOperationException("other", new TimeoutException())],
        [new InvalidOperationException("transient", new NpgsqlException("broken", new IOException("reset")))]
    ];

    [TestMethod]
    [DynamicData(nameof(NotUnavailable))]
    public void OnException_NotNpgsqlTimeout_LeavesExceptionUnhandled(Exception exception)
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meters = services.GetRequiredService<IMeterFactory>();
        using var rejected = new MetricCollector<long>(meters, ClubMetrics.MeterName, "club.requests.rejected");
        var logger = new FakeLogger<DatabaseUnavailableFilter>();
        var filter = new DatabaseUnavailableFilter(
            new ThrowingProblemDetailsFactory(),
            new ClubMetrics(meters),
            logger);
        var context = new ExceptionContext(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), [])
        {
            Exception = exception
        };

        filter.OnException(context);

        Assert.IsFalse(context.ExceptionHandled);
        Assert.IsNull(context.Result);
        Assert.AreEqual(0, logger.Collector.Count);
        Assert.AreEqual(0, rejected.GetMeasurementSnapshot().Count);
    }

    private sealed class ThrowingProblemDetailsFactory : ProblemDetailsFactory
    {
        public override ProblemDetails CreateProblemDetails(
            HttpContext httpContext,
            int? statusCode = null,
            string? title = null,
            string? type = null,
            string? detail = null,
            string? instance = null)
        {
            throw new AssertFailedException("ProblemDetails must not be created");
        }

        public override ValidationProblemDetails CreateValidationProblemDetails(
            HttpContext httpContext,
            Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary modelStateDictionary,
            int? statusCode = null,
            string? title = null,
            string? type = null,
            string? detail = null,
            string? instance = null)
        {
            throw new AssertFailedException("ProblemDetails must not be created");
        }
    }
}
