using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace WebApi.Tests.Middleware;

public sealed record PipelineResult(Exception? Error, int StatusCode, bool HasStarted);

public sealed class PipelineOutcome
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<PipelineResult> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class PipelineOutcomeStartupFilter : IStartupFilter
{
    private readonly PipelineOutcome _outcome;

    public PipelineOutcomeStartupFilter(PipelineOutcome outcome)
    {
        _outcome = outcome;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (HttpContext context, RequestDelegate inner) =>
            {
                if (!context.Request.Path.StartsWithSegments("/probe/exceptions"))
                {
                    await inner(context);
                    return;
                }

                try
                {
                    await inner(context);
                    _outcome.Completed.TrySetResult(new PipelineResult(null, context.Response.StatusCode, context.Response.HasStarted));
                }
                catch (Exception exception)
                {
                    _outcome.Completed.TrySetResult(new PipelineResult(exception, context.Response.StatusCode, context.Response.HasStarted));
                    throw;
                }
            });

            next(app);
        };
    }
}
