using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sim.Core;

/// <summary>
/// Piecewise-linear lookup table y(x). Clamped to the end values outside the table.
/// Serialized as a JSON array of [x, y] pairs.
/// </summary>
[JsonConverter(typeof(CurveJsonConverter))]
public sealed class Curve
{
    private readonly double[] _xs;
    private readonly double[] _ys;

    public Curve(IEnumerable<(double X, double Y)> points)
    {
        var list = points.ToList();
        if (list.Count == 0)
            throw new ArgumentException("A curve needs at least one point.", nameof(points));
        for (int i = 1; i < list.Count; i++)
        {
            if (!(list[i].X > list[i - 1].X))
                throw new ArgumentException("Curve x values must be strictly increasing.", nameof(points));
        }
        _xs = list.Select(p => p.X).ToArray();
        _ys = list.Select(p => p.Y).ToArray();
    }

    public IReadOnlyList<(double X, double Y)> Points =>
        _xs.Select((x, i) => (x, _ys[i])).ToArray();

    public double Evaluate(double x)
    {
        if (x <= _xs[0]) return _ys[0];
        int last = _xs.Length - 1;
        if (x >= _xs[last]) return _ys[last];

        int hi = Array.BinarySearch(_xs, x);
        if (hi >= 0) return _ys[hi];
        hi = ~hi;
        int lo = hi - 1;
        double t = (x - _xs[lo]) / (_xs[hi] - _xs[lo]);
        return _ys[lo] + t * (_ys[hi] - _ys[lo]);
    }
}

internal sealed class CurveJsonConverter : JsonConverter<Curve>
{
    public override Curve Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var pairs = JsonSerializer.Deserialize<double[][]>(ref reader, options)
            ?? throw new JsonException("Curve must be an array of [x, y] pairs.");
        if (pairs.Any(p => p is null || p.Length != 2))
            throw new JsonException("Curve must be an array of [x, y] pairs.");
        return new Curve(pairs.Select(p => (p[0], p[1])));
    }

    public override void Write(Utf8JsonWriter writer, Curve value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var (x, y) in value.Points)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(x);
            writer.WriteNumberValue(y);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }
}
