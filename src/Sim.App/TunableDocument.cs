using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sim.App;

/// <summary>
/// One editable parameter set (vehicle, input, sound, scenario) held as a JSON tree. Every edit is
/// re-parsed through the real loader, so validation is the loader's own; an invalid edit is undone.
/// </summary>
public sealed class TunableDocument
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Func<string, object> _parse;
    private readonly Action<object> _apply;

    public string Title { get; }
    /// <summary>File under config/ that Ctrl+S writes, or null for settings that are not saved.</summary>
    public string? FileName { get; }
    /// <summary>The C# record the JSON maps to; used to tell integers from reals.</summary>
    public Type RootType { get; }
    public JsonObject Root { get; }
    public bool Dirty { get; set; }
    public string? Error { get; private set; }

    public TunableDocument(string title, string? fileName, Type rootType, string json,
        Func<string, object> parse, Action<object> apply)
    {
        Title = title;
        FileName = fileName;
        RootType = rootType;
        Root = JsonNode.Parse(json, documentOptions: ReadOptions)?.AsObject()
            ?? throw new JsonException($"{title}: empty JSON");
        _parse = parse;
        _apply = apply;
    }

    /// <summary>Replaces one value. Returns false (and keeps the old value) if the result is invalid.</summary>
    public bool TrySet(JsonNode container, string? key, int index, JsonNode newValue)
    {
        JsonNode? old = Get(container, key, index)?.DeepClone();
        Put(container, key, index, newValue);
        try
        {
            _apply(_parse(Root.ToJsonString()));
            Error = null;
            Dirty = true;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException)
        {
            Put(container, key, index, old);
            Error = ex.Message;
            return false;
        }
    }

    public static JsonNode? Get(JsonNode container, string? key, int index) =>
        key != null ? container[key] : container[index];

    private static void Put(JsonNode container, string? key, int index, JsonNode? value)
    {
        if (key != null) container[key] = value;
        else container[index] = value;
    }
}
