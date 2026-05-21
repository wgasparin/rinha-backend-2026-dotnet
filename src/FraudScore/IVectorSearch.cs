namespace FraudScoreApi.FraudScore;

internal interface IVectorSearch
{
    /// <summary>Returns the count of "fraud"-labeled vectors among the 5 nearest neighbors of <paramref name="query"/>.</summary>
    int CountFraudInTop5(ReadOnlySpan<float> query);
}
