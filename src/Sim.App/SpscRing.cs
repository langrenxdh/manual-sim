namespace Sim.App;

/// <summary>
/// Lock-free bounded queue for exactly one producer thread and one consumer thread. Unlike
/// <see cref="LatestValue{T}"/> it keeps every item (telemetry needs every physics step). When full,
/// the producer drops the item and counts it instead of waiting (docs/engineering-rules.en.md hard rule 6).
/// </summary>
public sealed class SpscRing<T> where T : struct
{
    private readonly T[] _items;
    private readonly long _mask;
    private long _head; // items written (producer-owned, read by consumer)
    private long _tail; // items read (consumer-owned, read by producer)
    private long _dropped;

    /// <param name="capacityPowerOfTwo">Capacity; must be a power of two.</param>
    public SpscRing(int capacityPowerOfTwo)
    {
        if (capacityPowerOfTwo <= 0 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
            throw new ArgumentException("Capacity must be a power of two.", nameof(capacityPowerOfTwo));
        _items = new T[capacityPowerOfTwo];
        _mask = capacityPowerOfTwo - 1;
    }

    public long Dropped => Volatile.Read(ref _dropped);

    /// <summary>Producer side. Returns false (and counts a drop) when the queue is full.</summary>
    public bool TryWrite(in T item)
    {
        long head = _head;
        if (head - Volatile.Read(ref _tail) >= _items.Length)
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }
        _items[head & _mask] = item;
        Volatile.Write(ref _head, head + 1); // publishes the item
        return true;
    }

    /// <summary>Consumer side.</summary>
    public bool TryRead(out T item)
    {
        long tail = _tail;
        if (tail >= Volatile.Read(ref _head))
        {
            item = default;
            return false;
        }
        item = _items[tail & _mask];
        Volatile.Write(ref _tail, tail + 1); // frees the slot
        return true;
    }
}
