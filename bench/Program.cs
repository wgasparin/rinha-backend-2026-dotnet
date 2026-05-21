using System.Diagnostics;
using System.Runtime.Intrinsics.X86;
using FraudScoreApi.FraudScore;
using FraudScoreApi.Storage;

const int Warmup = 5_000;
const int Iters = 20_000;
const int Dims = 14;

var path = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "references_v1.bin");
path = Path.GetFullPath(path);

if (!File.Exists(path))
{
    Console.Error.WriteLine($"vector store not found: {path}");
    return 1;
}

Console.WriteLine($"avx2 supported: {Avx2.IsSupported}");
Console.WriteLine($"vector store  : {path}");

using var store = VectorStore.Open(path);
Console.WriteLine($"vectors       : {store.Count:N0}");
Console.WriteLine($"warmup iters  : {Warmup:N0}");
Console.WriteLine($"measure iters : {Iters:N0}");

// Touch every page of the mmap'd region so latency isn't dominated by page faults
{
    int sum = 0;
    var vecs = store.Vectors;
    for (var i = 0; i < vecs.Length; i += 4096) sum += vecs[i];
    GC.KeepAlive(sum);
}

var search = new IvfVectorSearch(store);

var rng = new Random(42);
var queries = new float[(Warmup + Iters) * Dims];
for (var i = 0; i < Warmup + Iters; i++)
{
    var slice = queries.AsSpan(i * Dims, Dims);
    for (var j = 0; j < Dims; j++) slice[j] = (float)rng.NextDouble();
    // 30% chance of last_transaction == null → sentinel -1 in dims 5, 6
    if (rng.NextDouble() < 0.3)
    {
        slice[5] = -1f;
        slice[6] = -1f;
    }
    // dims 9, 10, 11 are 0/1 in reality
    slice[9] = rng.Next(2);
    slice[10] = rng.Next(2);
    slice[11] = rng.Next(2);
}

// Warmup
long warmupFrauds = 0;
for (var i = 0; i < Warmup; i++)
    warmupFrauds += search.CountFraudInTop5(queries.AsSpan(i * Dims, Dims));
GC.KeepAlive(warmupFrauds);

// Measurement
var samples = new long[Iters];
long totalFrauds = 0;
var freq = Stopwatch.Frequency;

var benchStart = Stopwatch.GetTimestamp();
for (var i = 0; i < Iters; i++)
{
    var q = queries.AsSpan((Warmup + i) * Dims, Dims);
    var t0 = Stopwatch.GetTimestamp();
    totalFrauds += search.CountFraudInTop5(q);
    samples[i] = Stopwatch.GetTimestamp() - t0;
}
var benchElapsed = Stopwatch.GetTimestamp() - benchStart;
GC.KeepAlive(totalFrauds);

Array.Sort(samples);

double ToMs(long ticks) => (ticks * 1000.0) / freq;
double ToUs(long ticks) => (ticks * 1_000_000.0) / freq;

Console.WriteLine();
Console.WriteLine($"total time    : {ToMs(benchElapsed):F1} ms");
Console.WriteLine($"throughput    : {Iters * 1000.0 / ToMs(benchElapsed):F0} req/s (single thread)");
Console.WriteLine($"avg frauds    : {totalFrauds / (double)Iters:F2} / 5");
Console.WriteLine();
Console.WriteLine("latency:");
Console.WriteLine($"  min   : {ToUs(samples[0]):F1} us");
Console.WriteLine($"  p50   : {ToMs(samples[Iters / 2]):F3} ms");
Console.WriteLine($"  p90   : {ToMs(samples[Iters * 90 / 100]):F3} ms");
Console.WriteLine($"  p95   : {ToMs(samples[Iters * 95 / 100]):F3} ms");
Console.WriteLine($"  p99   : {ToMs(samples[Iters * 99 / 100]):F3} ms");
Console.WriteLine($"  p99.9 : {ToMs(samples[Iters * 999 / 1000]):F3} ms");
Console.WriteLine($"  max   : {ToMs(samples[Iters - 1]):F3} ms");

return 0;
