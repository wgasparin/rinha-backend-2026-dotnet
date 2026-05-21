using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace FraudScoreApi.Storage;

/// <summary>
/// Read-only, memory-mapped view of references_v1.bin (format RVF2 / IVF index).
/// Singleton, constructed at startup, safe for concurrent reads. The OS page cache is the
/// storage layer; this class only exposes typed spans over the mapped region.
/// Layout:
///   [0..128)                        header
///   [128..128+K*14)                 centroids (sbyte)
///   [128+K*14..128+K*14+(K+1)*4)    bucket offsets (uint32 LE)
///   [..count*14)                    vectors (sbyte), bucket-sorted
///   [..count)                       labels (byte)
/// </summary>
public sealed unsafe class VectorStore : IDisposable
{
    public const int Dimensions = 14;
    public const byte LabelLegit = 0;
    public const byte LabelFraud = 1;

    private const int HeaderSize = 128;
    private const uint ExpectedVersion = 2;
    private const uint ExpectedQuantType = 1;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly sbyte* _centroidsPtr;
    private readonly uint* _bucketOffsetsPtr;
    private readonly sbyte* _vectorsPtr;
    private readonly byte* _labelsPtr;
    private bool _disposed;

    public int Count { get; }
    public int CentroidCount { get; }

    /// <summary>K × 14 sbytes, row-major.</summary>
    public ReadOnlySpan<sbyte> Centroids => new(_centroidsPtr, CentroidCount * Dimensions);

    /// <summary>K+1 uint32: vectors[offsets[k] .. offsets[k+1]) belong to centroid k.</summary>
    public ReadOnlySpan<uint> BucketOffsets => new(_bucketOffsetsPtr, CentroidCount + 1);

    /// <summary>All vectors, bucket-sorted. Length = Count × Dimensions.</summary>
    public ReadOnlySpan<sbyte> Vectors => new(_vectorsPtr, Count * Dimensions);

    /// <summary>One byte per vector, same order as Vectors. 0 = legit, 1 = fraud.</summary>
    public ReadOnlySpan<byte> Labels => new(_labelsPtr, Count);

    public ReadOnlySpan<sbyte> GetVector(int index)
        => new(_vectorsPtr + index * Dimensions, Dimensions);

    public byte GetLabel(int index) => _labelsPtr[index];

    private VectorStore(
        MemoryMappedFile mmf,
        MemoryMappedViewAccessor view,
        sbyte* centroidsPtr,
        uint* bucketOffsetsPtr,
        sbyte* vectorsPtr,
        byte* labelsPtr,
        int count,
        int centroidCount)
    {
        _mmf = mmf;
        _view = view;
        _centroidsPtr = centroidsPtr;
        _bucketOffsetsPtr = bucketOffsetsPtr;
        _vectorsPtr = vectorsPtr;
        _labelsPtr = labelsPtr;
        Count = count;
        CentroidCount = centroidCount;
    }

    public static VectorStore Open(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists)
            throw new FileNotFoundException($"vector store '{path}' not found", path);

        var mmf = MemoryMappedFile.CreateFromFile(
            path,
            FileMode.Open,
            mapName: null,
            capacity: 0,
            access: MemoryMappedFileAccess.Read);

        MemoryMappedViewAccessor? view = null;
        bool acquired = false;
        try
        {
            view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            byte* basePtr = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
            acquired = true;
            basePtr += view.PointerOffset;

            var hdr = new ReadOnlySpan<byte>(basePtr, HeaderSize);

            if (hdr[0] != (byte)'R' || hdr[1] != (byte)'V'
                || hdr[2] != (byte)'F' || hdr[3] != (byte)'2')
                throw new InvalidDataException(
                    $"vector store '{path}': bad magic (expected 'RVF2')");

            var version = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(4, 4));
            if (version != ExpectedVersion)
                throw new InvalidDataException(
                    $"vector store '{path}': unsupported version {version}");

            var rawCount = BinaryPrimitives.ReadUInt64LittleEndian(hdr.Slice(8, 8));
            if (rawCount == 0 || rawCount > int.MaxValue)
                throw new InvalidDataException(
                    $"vector store '{path}': invalid count {rawCount}");

            var dims = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(16, 4));
            if (dims != Dimensions)
                throw new InvalidDataException(
                    $"vector store '{path}': unexpected dims {dims} (expected {Dimensions})");

            var quant = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(20, 4));
            if (quant != ExpectedQuantType)
                throw new InvalidDataException(
                    $"vector store '{path}': unsupported quant type {quant}");

            var rawCentroids = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(24, 4));
            if (rawCentroids == 0 || rawCentroids > 65536)
                throw new InvalidDataException(
                    $"vector store '{path}': invalid n_centroids {rawCentroids}");

            var count = (int)rawCount;
            var centroidCount = (int)rawCentroids;

            var expectedSize =
                (long)HeaderSize
                + (long)centroidCount * Dimensions
                + (long)(centroidCount + 1) * 4
                + (long)count * Dimensions
                + (long)count;
            if (fi.Length != expectedSize)
                throw new InvalidDataException(
                    $"vector store '{path}': size mismatch " +
                    $"(file={fi.Length}, expected={expectedSize})");

            var centroidsPtr = (sbyte*)(basePtr + HeaderSize);
            var bucketOffsetsPtr =
                (uint*)(basePtr + HeaderSize + (long)centroidCount * Dimensions);
            var vectorsPtr =
                (sbyte*)(basePtr + HeaderSize + (long)centroidCount * Dimensions + (long)(centroidCount + 1) * 4);
            var labelsPtr = (byte*)vectorsPtr + (long)count * Dimensions;

            return new VectorStore(
                mmf, view, centroidsPtr, bucketOffsetsPtr, vectorsPtr, labelsPtr,
                count, centroidCount);
        }
        catch
        {
            if (acquired && view is not null)
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            view?.Dispose();
            mmf.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mmf.Dispose();
    }
}
