using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using FraudScoreApi.Storage;

namespace FraudScoreApi.FraudScore;

/// <summary>
/// IVF (Inverted File) top-5 search with AVX2 distance kernel.
/// Pre-clusters vectors into K=1024 buckets at build time; at query time scans the K
/// centroids (small, cache-hot) and only the nprobe nearest buckets (~K/nprobe of the data).
/// </summary>
internal sealed class IvfVectorSearch : IVectorSearch
{
    private const int K = 5;
    private const int NProbe = 8;
    private const int Dimensions = VectorStore.Dimensions;
    private const float QuantScale = 127f;

    private static readonly Vector128<sbyte> LaneMask = Vector128.Create(
        (sbyte)-1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, 0, 0);

    private readonly VectorStore _store;

    public IvfVectorSearch(VectorStore store)
    {
        if (!Avx2.IsSupported)
            throw new PlatformNotSupportedException(
                "IvfVectorSearch requires AVX2 (Intel Haswell+ / AMD Excavator+).");
        _store = store;
    }

    public int CountFraudInTop5(ReadOnlySpan<float> query)
    {
        // 1. Quantize query into 14 sbytes (16-byte buffer with 2 zero pad lanes).
        Span<sbyte> qBuf = stackalloc sbyte[16];
        for (var i = 0; i < Dimensions; i++)
        {
            var v = query[i];
            if (float.IsNaN(v) || v < -1f) v = -1f;
            else if (v > 1f) v = 1f;
            qBuf[i] = (sbyte)MathF.Round(v * QuantScale);
        }

        Vector128<sbyte> qv = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(qBuf)) & LaneMask;
        Vector256<short> qvs = Avx2.ConvertToVector256Int16(qv);

        // 2. Distance to every centroid → top-NProbe bucket indices.
        var ncent = _store.CentroidCount;
        ref sbyte centRef = ref MemoryMarshal.GetReference(_store.Centroids);

        Span<int> probeD = stackalloc int[NProbe];
        Span<int> probeI = stackalloc int[NProbe];
        for (var i = 0; i < NProbe; i++) { probeD[i] = int.MaxValue; probeI[i] = -1; }
        var probeWorst = 0;

        for (var c = 0; c < ncent; c++)
        {
            var dist = SimdDistance(ref Unsafe.Add(ref centRef, (nint)c * Dimensions), qvs);
            if (dist < probeD[probeWorst])
            {
                probeD[probeWorst] = dist;
                probeI[probeWorst] = c;
                var maxD = probeD[0];
                probeWorst = 0;
                for (var s = 1; s < NProbe; s++)
                {
                    if (probeD[s] > maxD) { maxD = probeD[s]; probeWorst = s; }
                }
            }
        }

        // 3. Scan only the probed buckets, maintaining top-5.
        var offsets = _store.BucketOffsets;
        ref sbyte vecRef = ref MemoryMarshal.GetReference(_store.Vectors);

        Span<int> topD = stackalloc int[K];
        Span<int> topI = stackalloc int[K];
        for (var i = 0; i < K; i++) { topD[i] = int.MaxValue; topI[i] = -1; }
        var worstSlot = 0;

        for (var p = 0; p < NProbe; p++)
        {
            var b = probeI[p];
            if (b < 0) continue;
            var start = (int)offsets[b];
            var end = (int)offsets[b + 1];

            for (var n = start; n < end; n++)
            {
                var dist = SimdDistance(ref Unsafe.Add(ref vecRef, (nint)n * Dimensions), qvs);
                if (dist < topD[worstSlot])
                {
                    topD[worstSlot] = dist;
                    topI[worstSlot] = n;
                    var maxD = topD[0];
                    worstSlot = 0;
                    for (var s = 1; s < K; s++)
                    {
                        if (topD[s] > maxD) { maxD = topD[s]; worstSlot = s; }
                    }
                }
            }
        }

        // 4. Count fraud labels among the top-5.
        var labels = _store.Labels;
        var fraudCount = 0;
        for (var s = 0; s < K; s++)
        {
            var idx = topI[s];
            if (idx >= 0 && labels[idx] == VectorStore.LabelFraud)
                fraudCount++;
        }
        return fraudCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SimdDistance(ref sbyte candidate, Vector256<short> qvs)
    {
        Vector128<sbyte> cv = Vector128.LoadUnsafe(ref candidate) & LaneMask;
        Vector256<short> cvs = Avx2.ConvertToVector256Int16(cv);
        Vector256<short> diff = Avx2.Subtract(qvs, cvs);
        Vector256<int> sq = Avx2.MultiplyAddAdjacent(diff, diff);
        return Vector256.Sum(sq);
    }
}
