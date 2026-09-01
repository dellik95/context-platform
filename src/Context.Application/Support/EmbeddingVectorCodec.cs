using System.Runtime.InteropServices;

namespace Context.Application.Support;

/// <summary>
/// Packs/unpacks the <c>float[]</c> embeddings produced by <c>IEmbeddingGenerator</c> into the
/// raw <c>byte[]</c> blob format stored in <c>Embedding.Vector</c> / <c>MemoryEmbedding.Vector</c>
/// (CONTRACTS.md §1: "packed float32[]"). Little-endian, matching the host architecture this
/// platform actually ships on (x86/x64/ARM64 are all little-endian) — see remarks for the
/// portability follow-up.
/// </summary>
/// <remarks>
/// Not endianness-safe across a big-endian host. Acceptable for this local-first, single-machine
/// MVP (the database is never expected to move between machines of different endianness); a
/// production version should either fix the wire format explicitly (e.g. always little-endian,
/// converting on big-endian hosts) or store dimension-tagged, self-describing vectors.
/// </remarks>
public static class EmbeddingVectorCodec
{
    public static byte[] Encode(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        MemoryMarshal.Cast<float, byte>(vector).CopyTo(bytes);
        return bytes;
    }

    public static float[] Decode(byte[] bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new ArgumentException(
                $"Embedding blob length {bytes.Length} is not a multiple of {sizeof(float)} bytes.", nameof(bytes));
        }

        var vector = new float[bytes.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(bytes).CopyTo(vector);
        return vector;
    }
}
