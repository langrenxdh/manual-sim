using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>
/// Coloured triangles collected once and uploaded to the GPU, so a large static scene (the town map)
/// can be drawn several times a frame (driver's view, mirrors, overhead) without re-sending it.
/// Build it, call <see cref="Upload"/> once a window exists, then <see cref="Draw"/> inside a 3D mode.
/// </summary>
public sealed class StaticMesh : IDisposable
{
    /// <summary>Vertices per uploaded chunk, kept well under 16-bit index limits some drivers still assume.</summary>
    private const int ChunkVertices = 60_000;

    private readonly List<(Vector3 A, Vector3 B, Vector3 C, Color Colour)> _triangles = [];
    private readonly List<Model> _models = [];

    public int TriangleCount => _triangles.Count;

    public void Triangle(Vector3 a, Vector3 b, Vector3 c, Color colour) => _triangles.Add((a, b, c, colour));

    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color colour)
    {
        Triangle(a, b, c, colour);
        Triangle(a, c, d, colour);
    }

    /// <summary>An axis-aligned box (bottom face left out: it is never seen).</summary>
    public void Box(Vector3 centre, Vector3 size, Color colour)
    {
        var h = size / 2;
        Vector3 P(float x, float y, float z) => centre + new Vector3(x * h.X, y * h.Y, z * h.Z);
        Color side = Shade(colour, 0.85f), end = Shade(colour, 0.7f);
        Quad(P(-1, 1, -1), P(1, 1, -1), P(1, 1, 1), P(-1, 1, 1), colour);   // top
        Quad(P(-1, -1, -1), P(1, -1, -1), P(1, 1, -1), P(-1, 1, -1), side); // -Z
        Quad(P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), side);     // +Z
        Quad(P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), end);  // -X
        Quad(P(1, -1, -1), P(1, -1, 1), P(1, 1, 1), P(1, 1, -1), end);      // +X
    }

    /// <summary>Darkens a colour so the faces of a box read as separate surfaces without lighting.</summary>
    public static Color Shade(Color c, float f) => new((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f), c.A);

    public void Upload()
    {
        DisposeModels();
        for (int start = 0; start < _triangles.Count; start += ChunkVertices / 3)
        {
            int count = Math.Min(ChunkVertices / 3, _triangles.Count - start);
            var mesh = new Mesh(count * 3, count);
            mesh.AllocVertices();
            mesh.AllocColors();
            var vertices = mesh.VerticesAs<Vector3>();
            var colours = mesh.ColorsAs<Color>();
            for (int i = 0; i < count; i++)
            {
                var (a, b, c, colour) = _triangles[start + i];
                vertices[3 * i] = a;
                vertices[3 * i + 1] = b;
                vertices[3 * i + 2] = c;
                colours[3 * i] = colours[3 * i + 1] = colours[3 * i + 2] = colour;
            }
            UploadMesh(ref mesh, false);
            _models.Add(LoadModelFromMesh(mesh));
        }
    }

    /// <summary>Draws every chunk; call between BeginMode3D and EndMode3D, with back-face culling off.</summary>
    public void Draw()
    {
        foreach (var m in _models) DrawModel(m, Vector3.Zero, 1, Color.White);
    }

    private void DisposeModels()
    {
        foreach (var m in _models) UnloadModel(m);
        _models.Clear();
    }

    public void Dispose() => DisposeModels();
}
