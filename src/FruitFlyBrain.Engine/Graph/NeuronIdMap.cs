namespace FruitFlyBrain.Engine.Graph;

/// <summary>
/// Bidirectional mapper between sparse 64-bit FlyWire/Connectome IDs (e.g. 7205759406...)
/// and contiguous 32-bit dense indices (0 to N-1).
/// </summary>
public sealed class NeuronIdMap
{
    private readonly ulong[] _sortedIds;

    public int Count => _sortedIds.Length;

    public NeuronIdMap(ulong[] sortedIds)
    {
        _sortedIds = sortedIds ?? throw new ArgumentNullException(nameof(sortedIds));
    }

    /// <summary>
    /// Looks up dense 0..N-1 index for a 64-bit neuron root ID via binary search.
    /// Returns -1 if not found.
    /// </summary>
    public int TryGetIndex(ulong neuronId)
    {
        int index = Array.BinarySearch(_sortedIds, neuronId);
        return index >= 0 ? index : -1;
    }

    /// <summary>
    /// Returns 64-bit neuron root ID for a dense 0..N-1 index.
    /// </summary>
    public ulong GetId(int denseIndex)
    {
        if ((uint)denseIndex >= (uint)_sortedIds.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(denseIndex), $"Index {denseIndex} out of range [0, {_sortedIds.Length})");
        }
        return _sortedIds[denseIndex];
    }

    /// <summary>
    /// Read-only access to the underlying sorted ID buffer.
    /// </summary>
    public ReadOnlySpan<ulong> AsSpan() => _sortedIds.AsSpan();

    /// <summary>
    /// Returns the raw array.
    /// </summary>
    public ulong[] RawArray => _sortedIds;
}
