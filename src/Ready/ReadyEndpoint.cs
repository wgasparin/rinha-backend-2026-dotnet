using Microsoft.AspNetCore.Http.HttpResults;

namespace FraudScoreApi.Ready;

internal static class ReadyEndpoint
{
    public static IEndpointRouteBuilder MapReady(this IEndpointRouteBuilder app)
    {
        app.MapGet("/ready", Handle);
        return app;
    }

    private static Results<Ok, StatusCodeHttpResult> Handle(ReadinessProbe probe)
        => probe.IsReady()
            ? TypedResults.Ok()
            : TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
}
