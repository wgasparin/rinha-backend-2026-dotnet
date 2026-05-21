using System.Text.Json.Serialization;

namespace FraudScoreApi.FraudScore;

// Request DTOs were removed when the hot path switched to FraudScoreParser
// (Utf8JsonReader direct). Only the response type goes through source-gen now —
// it's small (~30 B), trivially serialized, and the savings from skipping
// the response DTO are negligible vs the parsing cost on the request side.

public readonly record struct FraudScoreResponse(
    [property: JsonPropertyName("approved")] bool Approved,
    [property: JsonPropertyName("fraud_score")] double FraudScore);

[JsonSerializable(typeof(FraudScoreResponse))]
internal partial class FraudScoreJsonContext : JsonSerializerContext;
