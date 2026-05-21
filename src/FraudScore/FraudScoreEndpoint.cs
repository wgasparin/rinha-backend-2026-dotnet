using System.Buffers;
using Microsoft.AspNetCore.Http.HttpResults;
using FraudScoreApi.Storage;

namespace FraudScoreApi.FraudScore;

internal static class FraudScoreEndpoint
{
    /// <summary>Anything bigger than this is rejected up front (rinha payloads are ~500 B).</summary>
    private const int MaxBodySize = 4096;

    public static IEndpointRouteBuilder MapFraudScore(this IEndpointRouteBuilder app)
    {
        app.MapPost("/fraud-score", Handle);
        return app;
    }

    private static async Task<Results<Ok<FraudScoreResponse>, BadRequest>> Handle(
        HttpRequest request,
        IVectorSearch search)
    {
        if (request.ContentLength is > MaxBodySize) return TypedResults.BadRequest();

        var rented = ArrayPool<byte>.Shared.Rent(MaxBodySize);
        try
        {
            int totalRead = 0;
            while (totalRead < MaxBodySize)
            {
                int read = await request.Body.ReadAsync(
                    rented.AsMemory(totalRead, MaxBodySize - totalRead)).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }
            if (totalRead == 0) return TypedResults.BadRequest();

            // Span<float> lives entirely after the awaits — no cross-await capture.
            Span<float> vector = stackalloc float[VectorStore.Dimensions];
            if (!FraudScoreParser.TryParseAndVectorize(rented.AsSpan(0, totalRead), vector))
                return TypedResults.BadRequest();

            var fraudCount = search.CountFraudInTop5(vector);
            var fraudScore = fraudCount / 5.0;
            return TypedResults.Ok(new FraudScoreResponse(fraudScore < 0.6, fraudScore));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
