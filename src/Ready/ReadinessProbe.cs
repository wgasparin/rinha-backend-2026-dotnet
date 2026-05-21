using FraudScoreApi.Storage;

namespace FraudScoreApi.Ready;

internal sealed class ReadinessProbe
{
    private readonly VectorStore _store;

    public ReadinessProbe(VectorStore store)
    {
        _store = store;
    }

    public bool IsReady() => _store.Count > 0;
}
