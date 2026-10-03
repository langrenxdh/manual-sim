using System.Text;
using System.Text.Json.Nodes;

namespace Sim.App;

/// <summary>
/// Writes a JSON tree the way the hand-written config files look: objects one key per line,
/// arrays of numbers (and curves of [x, y] pairs) inline, wrapped at a readable width.
/// </summary>
public static class JsonLayout
{
    private const int IndentSize = 2;
    private const int MaxLineLength = 100;

    public static string Write(JsonNode node) => Write(node, 0);

    private static string Write(JsonNode? node, int indent) => node switch
    {
        JsonObject obj => WriteObject(obj, indent),
        JsonArray arr when IsFlat(arr) => WriteFlatArray(arr, indent),
        JsonArray arr => WriteArray(arr, indent),
        null => "null",
        _ => node.ToJsonString(),
    };

    private static string WriteObject(JsonObject obj, int indent)
    {
        if (obj.Count == 0) return "{}";
        string pad = new(' ', indent + IndentSize);
        var entries = obj.Select(kv => $"{pad}\"{kv.Key}\": {Write(kv.Value, indent + IndentSize)}");
        return "{\n" + string.Join(",\n", entries) + "\n" + new string(' ', indent) + "}";
    }

    private static string WriteArray(JsonArray arr, int indent)
    {
        string pad = new(' ', indent + IndentSize);
        var items = arr.Select(n => pad + Write(n, indent + IndentSize));
        return "[\n" + string.Join(",\n", items) + "\n" + new string(' ', indent) + "]";
    }

    /// <summary>Arrays of values or of value-only arrays (curves) go inline, wrapped.</summary>
    private static bool IsFlat(JsonArray arr) =>
        arr.All(n => n is JsonValue || n is JsonArray inner && inner.All(i => i is JsonValue));

    private static string WriteFlatArray(JsonArray arr, int indent)
    {
        var items = arr.Select(n => n is JsonArray inner
            ? "[" + string.Join(", ", inner.Select(i => i!.ToJsonString())) + "]"
            : n!.ToJsonString()).ToList();
        string oneLine = "[" + string.Join(", ", items) + "]";
        if (indent + oneLine.Length <= MaxLineLength) return oneLine;

        string pad = new(' ', indent + IndentSize);
        var sb = new StringBuilder("[\n");
        var line = new StringBuilder(pad);
        for (int i = 0; i < items.Count; i++)
        {
            string item = items[i] + (i < items.Count - 1 ? "," : "");
            if (line.Length > pad.Length && line.Length + 1 + item.Length > MaxLineLength)
            {
                sb.Append(line.ToString().TrimEnd()).Append('\n');
                line.Clear().Append(pad);
            }
            line.Append(item).Append(' ');
        }
        sb.Append(line.ToString().TrimEnd()).Append('\n').Append(' ', indent).Append(']');
        return sb.ToString();
    }
}
