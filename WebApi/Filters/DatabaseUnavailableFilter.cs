using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Npgsql;
using WebApi.Observability;

namespace WebApi.Filters;

public sealed class DatabaseUnavailableFilter : IExceptionFilter
{
    private const string Reason = "database-unavailable";

    private readonly ProblemDetailsFactory _problemDetailsFactory;
    private readonly ClubMetrics _metrics;
    private readonly ILogger<DatabaseUnavailableFilter> _logger;

    public DatabaseUnavailableFilter(
        ProblemDetailsFactory problemDetailsFactory,
        ClubMetrics metrics,
        ILogger<DatabaseUnavailableFilter> logger)
    {
        _problemDetailsFactory = problemDetailsFactory;
        _metrics = metrics;
        _logger = logger;
    }

    public void OnException(ExceptionContext context)
    {
        // решение: EF Core оборачивает транзиентную ошибку Npgsql в InvalidOperationException — смотрим и на обёртку.
        if ((context.Exception as NpgsqlException ?? context.Exception.InnerException as NpgsqlException)
            is not { InnerException: TimeoutException })
        {
            return;
        }

        var httpContext = context.HttpContext;
        var operation = context.ActionDescriptor.RouteValues["action"] ?? string.Empty;
        long? userId = long.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

        Log.DatabaseUnavailable(_logger, context.Exception, operation, userId);
        _metrics.RequestRejected(operation, Reason);

        var problem = _problemDetailsFactory.CreateProblemDetails(httpContext, StatusCodes.Status503ServiceUnavailable);
        problem.Extensions["reason"] = Reason;

        // решение: пул исчерпан или БД не отвечает — управляемая деградация 503 с Retry-After, а не 500 (задача 29.5).
        httpContext.Response.Headers.RetryAfter = "1";
        context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status503ServiceUnavailable };
        context.ExceptionHandled = true;
    }
}
