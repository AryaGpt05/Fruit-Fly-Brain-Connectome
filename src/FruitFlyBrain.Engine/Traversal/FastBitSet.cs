using System.Numerics;
using System.Runtime.CompilerServices;

namespace FruitFlyBrain.Engine.Traversal;

/// <summary>
/// Ultra-compact bitset designed for high-throughput graph traversal.
/// 50 million nodes consume less than 6 MB of RAM.
/// Supports both full reset and selective reset of touched indices.
/// </summary>
public sealed class FastBitSet
{
    private readonly ulong[] _words;
    public int Capacity { get; }

    public FastBitSet(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        int wordCount = (capacity + 63) >> 6;
        _words = new ulong[wordCount];
    }

    /// <summary>
    /// Atomically/efficiently tests if bit is set; if not, sets it and returns true.
    /// Returns false if bit was already set.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TestAndSet(int index)
    {
        int wordIdx = index >> 6;
        ulong mask = 1UL << (index & 63);
        ulong current = _words[wordIdx];

        if ((current & mask) != 0)
        {
            return false; // Already visited
        }

        _words[wordIdx] = current | mask;
        return true; // Newly visited
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSet(int index)
    {
        return (_words[index >> 6] & (1UL << (index & 63))) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int index)
    {
        _words[index >> 6] |= (1UL << (index & 63));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Unset(int index)
    {
        _words[index >> 6] &= ~(1UL << (index & 63));
    }

    /// <summary>
    /// Selectively unsets only the indices visited in the last traversal.
    /// This takes O(k) microseconds where k is the number of visited nodes,
    /// avoiding clearing the entire 50M bitset.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearSelective(ReadOnlySpan<int> indices)
    {
        foreach (int idx in indices)
        {
            _words[idx >> 6] &= ~(1UL << (idx & 63));
        }
    }

    /// <summary>
    /// Full reset of all bits across the entire capacity using vectorized SIMD.
    /// </summary>
    public void ClearAll()
    {
        Array.Clear(_words);
    }
}
