using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>
/// Shared text drawing. Uses a Windows TrueType font when available, raylib's default otherwise.
/// Every string drawn or measured goes through <see cref="Tr.T"/>; in Chinese a CJK font is loaded
/// with just the glyphs the translation table uses (plus ASCII).
/// </summary>
public static class Ui
{
    private const int FontBaseSize = 96;
    private static readonly string[] FontCandidates =
    [
        @"C:\Windows\Fonts\bahnschrift.ttf", // DIN-style, close to VW instrument lettering
        @"C:\Windows\Fonts\segoeui.ttf",
    ];

    private static readonly string[] ChineseFontCandidates =
    [
        @"C:\Windows\Fonts\Deng.ttf",   // DengXian, the Windows 10+ Chinese UI font
        @"C:\Windows\Fonts\simhei.ttf",
    ];
    private const int ChineseFontBaseSize = 64; // a few hundred CJK glyphs must fit one texture

    private static Font _font;
    private static bool _loaded;

    /// <summary>Call once after the window exists.</summary>
    public static void Load()
    {
        if (Tr.Chinese && LoadChinese()) return;
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

    private static bool LoadChinese()
    {
        // ASCII, a few symbols the UI uses, and every character of the Chinese table.
        var codepoints = Enumerable.Range(32, 95)
            .Concat("°±·–—…→←↑↓".Select(c => (int)c))
            .Concat(Tr.ChineseTexts.SelectMany(t => t.EnumerateRunes()).Select(r => r.Value))
            .Distinct().ToArray();
        foreach (string path in ChineseFontCandidates.Where(File.Exists))
        {
            var font = LoadFontEx(path, ChineseFontBaseSize, codepoints, codepoints.Length);
            if (!IsFontValid(font)) continue;
            SetTextureFilter(font.Texture, TextureFilter.Bilinear);
            _font = font;
            _loaded = true;
            return true;
        }
        return false;
    }

    /// <summary>Reloads the font after the language changed.</summary>
    public static void Reload()
    {
        Unload();
        Load();
    }

    public static void Unload()
    {
        if (_loaded) UnloadFont(_font);
        _loaded = false;
    }

    public static void Text(string text, float x, float y, float size, Color colour) =>
        DrawTextEx(_font, Tr.T(text), new Vector2(x, y), size, 0, colour);

    public static float Width(string text, float size) => MeasureTextEx(_font, Tr.T(text), size, 0).X;

    public static void Centred(string text, float cx, float y, float size, Color colour) =>
        Text(text, cx - Width(text, size) / 2, y, size, colour);
}
