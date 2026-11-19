using System.Security.Claims;
using DataAccessLayer.Transactions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using WebApi.Observability;

namespace WebApi.Filters;

public sealed class TransactionConflictFilter : IExceptionFilter
{
    private readonly ProblemDetailsFactory _problemDetailsFactory;
    private readonly ClubMetrics _metrics;
    private readonly ILogger<TransactionConflictFilter> _logger;

    public TransactionConflictFilter(
        ProblemDetailsFactory problemDetailsFactory,
        ClubMetrics metrics,
        ILogger<TransactionConflictFilter> logger)
    {
        _problemDetailsFactory = problemDetailsFactory;
        _metrics = metrics;
        _logger = logger;
    }

    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not TransactionConflictException conflict)
        {
            return;
        }

        var httpContext = context.HttpContext;
        var userId = long.Parse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        Log.TransactionConflict(_logger, conflict, conflict.Operation, conflict.Reason, userId);
        _metrics.RequestRejected(conflict.Operation, conflict.Reason);

        var problem = _problemDetailsFactory.CreateProblemDetails(httpContext, StatusCodes.Status503ServiceUnavailable);
        problem.Extensions["reason"] = conflict.Reason;

        // решение: операция не выполнена и откатилась целиком — повтор безопасен, отсюда 503 с Retry-After, а не 500.
        httpContext.Response.Headers.RetryAfter = "1";
        context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status503ServiceUnavailable };
        context.ExceptionHandled = true;
    }
}
