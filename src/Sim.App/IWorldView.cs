using System.Numerics;
using Raylib_cs;
using Sim.Core;

namespace Sim.App;

/// <summary>
/// A 3D world the driver aids (mirrors, overhead view) can draw again from their own cameras: the hill
/// road (<see cref="SceneView"/>) or the town map (<see cref="TownView"/>). Presentation only.
/// </summary>
public interface IWorldView
{
    /// <summary>
    /// Renders the world seen from <paramref name="camera"/> into <paramref name="target"/>.
    /// <paramref name="lookingBack"/> draws further behind the car (mirrors); <paramref name="extra3D"/> is
    /// drawn in the same 3D pass after the world.
    /// </summary>
    void Render(RenderTexture2D target, Camera3D camera, in SimState s, bool lookingBack, Action? extra3D);

    /// <summary>The ground point under the car's reference point and its unit forward direction, in raylib space.</summary>
    (Vector3 Ground, Vector3 Forward) CarPose(in SimState s);
}
