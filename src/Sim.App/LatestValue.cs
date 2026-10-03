namespace Sim.App;

/// <summary>
/// Lock-free triple buffer for one writer and one reader. The writer never waits and the reader
/// always gets the newest complete value, never a half-written one. Each consumer thread
/// (render, audio, ...) gets its own instance.
/// </summary>
public sealed class LatestValue<T> where T : struct
{
    private const int IndexMask = 3;
    private const int FreshBit = 4;

    private readonly T[] _slots = new T[3];
    private int _back;          // writer-owned
    private int _middle = 1;    // shared: slot index, plus FreshBit when unread
    private int _front = 2;     // reader-owned

    /// <summary>Writer side.</summary>
    public void Publish(in T value)
    {
        _slots[_back] = value;
        // Interlocked.Exchange is a full fence: the slot write is visible before the index is.
        _back = Interlocked.Exchange(ref _middle, _back | FreshBit) & IndexMask;
    }

    /// <summary>Reader side: the newest published value (default until the first publish).</summary>
    public T Read()
    {
        if ((Volatile.Read(ref _middle) & FreshBit) != 0)
            _front = Interlocked.Exchange(ref _middle, _front) & IndexMask;
        return _slots[_front];
    }
}
