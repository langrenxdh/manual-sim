namespace Sim.Core;

/// <summary>Small deterministic PRNG (xorshift64*), so results never depend on the runtime's Random.</summary>
internal struct Xorshift
{
    private ulong _state;

    public Xorshift(ulong seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    /// <summary>Uniform in [-1, 1).</summary>
    public double NextSigned()
    {
        _state ^= _state >> 12;
        _state ^= _state << 25;
        _state ^= _state >> 27;
        ulong r = _state * 0x2545F4914F6CDD1DUL;
        return (r >> 11) * (2.0 / (1UL << 53)) - 1.0;
    }
}
