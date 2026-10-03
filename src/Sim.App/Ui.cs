using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>Shared text drawing. Uses a Windows TrueType font when available, raylib's default otherwise.</summary>
public static class Ui
{
    private const int FontBaseSize = 96;
    private static readonly string[] FontCandidates =
    [
        @"C:\Windows\Fonts\bahnschrift.ttf", // DIN-style, close to VW instrument lettering
        @"C:\Windows\Fonts\segoeui.ttf",
    ];

    private static Font _font;
    private static bool _loaded;

    /// <summary>Call once after the window exists.</summary>
    public static void Load()
    {
        foreach (string path in FontCandidates.Where(File.Exists))
        {
            var font = LoadFontEx(path, FontBaseSize, null!, 0);
            if (!IsFontValid(font)) continue;
            SetTextureFilter(font.Texture, TextureFilter.Bilinear);
            _font = font;
            _loaded = true;
            return;
        }
        _font = GetFontDefault();
    }

    public static void Unload()
    {
        if (_loaded) UnloadFont(_font);
        _loaded = false;
    }

    public static void Text(string text, float x, float y, float size, Color colour) =>
        DrawTextEx(_font, text, new Vector2(x, y), size, 0, colour);

    public static float Width(string text, float size) => MeasureTextEx(_font, text, size, 0).X;

    public static void Centred(string text, float cx, float y, float size, Color colour) =>
        Text(text, cx - Width(text, size) / 2, y, size, colour);
}
