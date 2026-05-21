using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FraudScoreLoader;

internal static class Program
{
    const int Dimensions = 14;
    const int HeaderSize = 128;
    const int K = 1024; // centroids
    const uint Version = 2;
    const uint QuantTypeInt8Symmetric = 1;
    const float QuantScale = 127f;
    const int ExpectedCount = 3_000_000;

    // 0xFF in 14 useful lanes, 0 in lanes 14..15 — for masking unaligned 16-byte sbyte loads.
    static readonly Vector128<sbyte> LaneMask = Vector128.Create(
        (sbyte)-1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, 0, 0);

    static async Task<int> Main(string[] args)
    {
        string? inputPath = null;
        string? outputPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--input" when i + 1 < args.Length:
                    inputPath = args[++i];
                    break;
                case "--output" when i + 1 < args.Length:
                    outputPath = args[++i];
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;
            }
        }

        inputPath ??= Environment.GetEnvironmentVariable("REFERENCES_PATH");
        outputPath ??= Environment.GetEnvironmentVariable("OUTPUT_PATH");

        if (string.IsNullOrWhiteSpace(inputPath) || string.IsNullOrWhiteSpace(outputPath))
        {
            PrintUsage();
            return 1;
        }

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"[loader] FATAL: input '{inputPath}' not found");
            return 2;
        }

        if (!Avx2.IsSupported)
        {
            Console.Error.WriteLine("[loader] FATAL: this loader requires AVX2 on the build host");
            return 2;
        }

        var outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);

        Console.WriteLine($"[loader] input:   {inputPath}");
        Console.WriteLine($"[loader] output:  {outputPath}");
        Console.WriteLine($"[loader] config:  K={K}, dims={Dimensions}");

        var sw = Stopwatch.StartNew();

        // -------- Phase 1: parse + quantize into RAM --------
        var vectors = new sbyte[ExpectedCount * Dimensions];
        var labels = new byte[ExpectedCount];
        var capacity = ExpectedCount;
        int count = 0;
        int skipped = 0;

        await using (var inFile = File.OpenRead(inputPath))
        await using (var gz = new GZipStream(inFile, CompressionMode.Decompress))
        await using (var inBuf = new BufferedStream(gz, 1 << 20))
        {
            await foreach (var rec in JsonSerializer.DeserializeAsyncEnumerable(
                inBuf, LoaderJsonContext.Default.ReferenceRecord))
            {
                if (rec is null
                    || rec.Vector is null
                    || rec.Vector.Length != Dimensions
                    || rec.Label is null)
                {
                    skipped++;
                    continue;
                }

                if (count >= capacity)
                {
                    capacity *= 2;
                    Array.Resize(ref vectors, capacity * Dimensions);
                    Array.Resize(ref labels, capacity);
                }

                var baseIdx = count * Dimensions;
                for (int i = 0; i < Dimensions; i++)
                    vectors[baseIdx + i] = Quantize(rec.Vector[i]);

                labels[count] = rec.Label switch
                {
                    "fraud" => 1,
                    "legit" => 0,
                    _ => throw new InvalidDataException(
                        $"[loader] unknown label '{rec.Label}' at index {count}"),
                };

                count++;
                if (count % 500_000 == 0)
                    Console.WriteLine($"[loader] parsed {count:N0} in {sw.Elapsed.TotalSeconds:F1}s");
            }
        }

        Array.Resize(ref vectors, count * Dimensions);
        Array.Resize(ref labels, count);
        Console.WriteLine($"[loader] parsed: {count:N0} vectors, {skipped} skipped ({sw.Elapsed.TotalSeconds:F1}s)");

        // -------- Phase 2: pick K centroids by random sampling --------
        var rng = new Random(42);
        var pickedSet = new HashSet<int>(K);
        while (pickedSet.Count < K) pickedSet.Add(rng.Next(count));

        var centroids = new sbyte[K * Dimensions];
        {
            int ci = 0;
            foreach (var idx in pickedSet)
            {
                Buffer.BlockCopy(vectors, idx * Dimensions, centroids, ci * Dimensions, Dimensions);
                ci++;
            }
        }
        Console.WriteLine($"[loader] picked {K} centroids ({sw.Elapsed.TotalSeconds:F1}s)");

        // -------- Phase 3: assign each vector to nearest centroid (SIMD) --------
        var assign = new int[count];
        AssignAll(vectors, count, centroids, assign);
        Console.WriteLine($"[loader] assigned vectors to centroids ({sw.Elapsed.TotalSeconds:F1}s)");

        // -------- Phase 4: bucket counts + prefix-sum offsets --------
        var bucketCount = new int[K];
        for (int n = 0; n < count; n++) bucketCount[assign[n]]++;
        var offsets = new uint[K + 1];
        for (int k = 0; k < K; k++) offsets[k + 1] = offsets[k] + (uint)bucketCount[k];

        int maxBucket = 0, minBucket = int.MaxValue, emptyBuckets = 0;
        for (int k = 0; k < K; k++)
        {
            if (bucketCount[k] > maxBucket) maxBucket = bucketCount[k];
            if (bucketCount[k] < minBucket) minBucket = bucketCount[k];
            if (bucketCount[k] == 0) emptyBuckets++;
        }
        Console.WriteLine($"[loader] bucket sizes: min={minBucket} max={maxBucket} mean={count / K} empty={emptyBuckets}");

        // -------- Phase 5: stable bucket sort of vectors+labels --------
        var sortedVectors = new sbyte[count * Dimensions];
        var sortedLabels = new byte[count];
        var cursor = new int[K];
        for (int n = 0; n < count; n++)
        {
            int b = assign[n];
            int pos = (int)offsets[b] + cursor[b]++;
            Buffer.BlockCopy(vectors, n * Dimensions, sortedVectors, pos * Dimensions, Dimensions);
            sortedLabels[pos] = labels[n];
        }
        Console.WriteLine($"[loader] sorted by bucket ({sw.Elapsed.TotalSeconds:F1}s)");

        // -------- Phase 6: write v2 binary --------
        await using (var outFile = new FileStream(
            outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20))
        {
            WriteHeader(outFile, (ulong)count);
            outFile.Write(MemoryMarshal.AsBytes<sbyte>(centroids));
            outFile.Write(MemoryMarshal.AsBytes<uint>(offsets));
            outFile.Write(MemoryMarshal.AsBytes<sbyte>(sortedVectors));
            outFile.Write(sortedLabels);
            outFile.Flush();

            var expectedSize =
                (long)HeaderSize
                + (long)K * Dimensions
                + (long)(K + 1) * 4
                + (long)count * Dimensions
                + (long)count;
            if (outFile.Length != expectedSize)
            {
                Console.Error.WriteLine(
                    $"[loader] FATAL: size mismatch (expected {expectedSize}, got {outFile.Length})");
                return 3;
            }
            Console.WriteLine($"[loader] wrote {outFile.Length / 1024.0 / 1024.0:F1} MiB");
        }

        sw.Stop();
        Console.WriteLine($"[loader] done in {sw.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    // SIMD nearest-centroid assignment for all vectors.
    static void AssignAll(sbyte[] vectors, int count, sbyte[] centroids, int[] assign)
    {
        // Pre-widen all K centroids once. K=1024 × 32 bytes (Vector256<short>) = 32 KB. Fits L1.
        var centWide = new Vector256<short>[K];
        ref sbyte centRef = ref MemoryMarshal.GetArrayDataReference(centroids);
        for (int k = 0; k < K; k++)
        {
            var cv = Vector128.LoadUnsafe(ref Unsafe.Add(ref centRef, k * Dimensions)) & LaneMask;
            centWide[k] = Avx2.ConvertToVector256Int16(cv);
        }

        ref sbyte vecRef = ref MemoryMarshal.GetArrayDataReference(vectors);
        var progressStep = Math.Max(1, count / 20);

        for (int n = 0; n < count; n++)
        {
            var qv = Vector128.LoadUnsafe(ref Unsafe.Add(ref vecRef, n * Dimensions)) & LaneMask;
            var qvs = Avx2.ConvertToVector256Int16(qv);

            int bestK = 0;
            int bestDist = int.MaxValue;
            for (int k = 0; k < K; k++)
            {
                var diff = Avx2.Subtract(qvs, centWide[k]);
                var sq = Avx2.MultiplyAddAdjacent(diff, diff);
                int dist = Vector256.Sum(sq);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestK = k;
                }
            }
            assign[n] = bestK;

            if (n % progressStep == 0 && n > 0)
                Console.Write($"\r[loader] assigning... {n * 100L / count}%   ");
        }
        Console.WriteLine();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static sbyte Quantize(float v)
    {
        if (float.IsNaN(v) || v < -1f) v = -1f;
        else if (v > 1f) v = 1f;
        return (sbyte)MathF.Round(v * QuantScale);
    }

    static void WriteHeader(Stream s, ulong count)
    {
        Span<byte> hdr = stackalloc byte[HeaderSize];
        hdr[0] = (byte)'R';
        hdr[1] = (byte)'V';
        hdr[2] = (byte)'F';
        hdr[3] = (byte)'2';
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4, 4), Version);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(8, 8), count);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(16, 4), (uint)Dimensions);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(20, 4), QuantTypeInt8Symmetric);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(24, 4), (uint)K);
        // bytes 28..127: zero padding
        s.Write(hdr);
    }

    static void PrintUsage()
    {
        Console.Error.WriteLine(
            "usage: loader --input <references.json.gz> --output <references_v1.bin>");
        Console.Error.WriteLine(
            "  alternatively set env vars REFERENCES_PATH and OUTPUT_PATH");
    }
}

public sealed class ReferenceRecord
{
    [JsonPropertyName("vector")]
    public float[]? Vector { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

[JsonSerializable(typeof(ReferenceRecord))]
internal partial class LoaderJsonContext : JsonSerializerContext { }
