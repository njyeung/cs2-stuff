using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace TheatreScreen;

/// <summary>
/// A wall of black-tinted dev cubes just behind the pixels. Beams blend additively, so
/// over the open map they read as tinted glass; over black, additive gives back exactly
/// the beam's colour. Tinting multiplies the material's albedo, so black stays black
/// whatever the lighting. The wall overhangs the picture by a bezel on every side.
/// </summary>
public sealed class Backdrop
{
    public const string Model = "models/dev/dev_cube.vmdl";

    // dev_cube's approximate size, used only if its bounds come back empty.
    private const float FallbackSize = 32f;

    private readonly List<CDynamicProp> _blocks = [];

    /// <summary>The cube's local bounds the layout was built from, for the log.</summary>
    public (Vector3 Mins, Vector3 Maxs) BlockBounds { get; }
    public int BlockCount => _blocks.Count;

    /// <param name="bottomLeft">The picture's bottom-left corner (audience's view).</param>
    /// <param name="across">Bottom edge, left to right.</param>
    /// <param name="height">Picture height.</param>
    /// <param name="facing">Unit vector the screen faces; the wall goes behind it.</param>
    /// <param name="gap">How far behind the pixel plane the wall's front face sits.
    /// Must clear half a beam's thickness, since beams twist to face the camera.</param>
    public Backdrop(Vector3 bottomLeft, Vector3 across, float height, Vector3 facing, float bezel, float gap)
    {
        // Spawn one block first to learn the cube's real size and pivot.
        var probe = SpawnBlock();
        var mins = ToVector3(probe.Collision.Mins);
        var maxs = ToVector3(probe.Collision.Maxs);
        var size = maxs - mins;
        if (size.X <= 0 || size.Z <= 0)
        {
            size = new Vector3(FallbackSize);
            mins = -size / 2;
            maxs = size / 2;
        }
        BlockBounds = (mins, maxs);
        var pivotToCenter = (mins + maxs) / 2;
        var side = MathF.Min(size.X, size.Z);

        var right = Vector3.Normalize(across);
        var wallWidth = across.Length() + 2 * bezel;
        var wallHeight = height + 2 * bezel;
        var origin = bottomLeft - right * bezel - Vector3.UnitZ * bezel;
        var depth = facing * -(gap + side / 2);

        var cols = (int)MathF.Ceiling(wallWidth / side);
        var rows = (int)MathF.Ceiling(wallHeight / side);
        for (var row = 0; row < rows; row++)
        for (var col = 0; col < cols; col++)
        {
            // Clamp the last row/column back inside so the wall ends flush, overlapping
            // its neighbour rather than overhanging.
            var u = MathF.Min(col * side, wallWidth - side) + side / 2;
            var v = MathF.Min(row * side, wallHeight - side) + side / 2;
            var center = origin + right * u + Vector3.UnitZ * v + depth;

            // Blocks stay axis-aligned, so the wall is only tidy for screens on 90°
            // boundaries — fine for the walls we've picked so far.
            var block = _blocks.Count == 0 ? probe : SpawnBlock();
            block.Teleport(BeamPixel.ToVector(center - pivotToCenter), new QAngle(), new Vector());
            _blocks.Add(block);
        }
    }

    public void Remove()
    {
        foreach (var block in _blocks)
            if (block.IsValid)
                block.Remove();
    }

    private static CDynamicProp SpawnBlock()
    {
        var block = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic")
            ?? throw new InvalidOperationException("Couldn't create prop_dynamic entity");
        block.Render = Color.Black;
        block.DispatchSpawn();
        block.SetModel(Model);
        return block;
    }

    private static Vector3 ToVector3(Vector v) => new(v.X, v.Y, v.Z);
}
