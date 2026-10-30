using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace TheatreScreen;

/// <summary>
/// One on-screen pixel backed by a stock entity. A server can't ship clients a texture,
/// so every pixel is its own networked entity and "drawing" is just changing its colour —
/// the engine delta-compresses, so only pixels that actually change cost bandwidth.
/// </summary>
public interface IScreenPixel
{
    void SetColor(Color color);
    void Remove();
}

/// <summary>A short, thick <c>beam</c> spanning the pixel left-to-right, its width
/// covering the pixel's height. Beams are unlit and camera-facing, the same primitive
/// zone plugins use to draw box outlines.</summary>
public sealed class BeamPixel : IScreenPixel
{
    private readonly CBeam _beam;

    public BeamPixel(Vector3 start, Vector3 end, float thickness, Color color)
    {
        _beam = Utilities.CreateEntityByName<CBeam>("beam")
            ?? throw new InvalidOperationException("Couldn't create beam entity");
        _beam.Render = color;
        _beam.Width = thickness;
        _beam.EndWidth = thickness;
        _beam.Teleport(ToVector(start), new QAngle(), new Vector());
        _beam.EndPos.X = end.X;
        _beam.EndPos.Y = end.Y;
        _beam.EndPos.Z = end.Z;
        _beam.DispatchSpawn();
    }

    public void SetColor(Color color)
    {
        if (!_beam.IsValid)
            return;
        _beam.Render = color;
        Utilities.SetStateChanged(_beam, "CBaseModelEntity", "m_clrRender");
    }

    public void Remove()
    {
        if (_beam.IsValid)
            _beam.Remove();
    }

    internal static Vector ToVector(Vector3 v) => new(v.X, v.Y, v.Z);
}

/// <summary>A fullbright <c>point_worldtext</c> showing a single block glyph, centred on
/// the pixel. Crisp and unlit, but how well neighbouring glyphs tile depends on the font.</summary>
public sealed class TextPixel : IScreenPixel
{
    // Glyphs are rasterised at this pixel size, then scaled into world units.
    private const float FontSize = 64f;

    private readonly CPointWorldText _text;

    public TextPixel(Vector3 center, float size, float facingYaw, string glyph, float scale, Color color)
    {
        _text = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext")
            ?? throw new InvalidOperationException("Couldn't create point_worldtext entity");
        _text.MessageText = glyph;
        _text.Enabled = true;
        _text.Fullbright = true;
        _text.FontSize = FontSize;
        _text.WorldUnitsPerPx = size / FontSize * scale;
        _text.Color = color;
        _text.JustifyHorizontal = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_CENTER;
        _text.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_CENTER;
        _text.ReorientMode = PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;
        _text.DispatchSpawn();
        // Text lies in the entity's local plane; yaw +90 / roll 90 stands it upright
        // with its readable side toward facingYaw.
        _text.Teleport(BeamPixel.ToVector(center), new QAngle(0, facingYaw + 90, 90), new Vector());
    }

    public void SetColor(Color color)
    {
        if (!_text.IsValid)
            return;
        _text.Color = color;
        Utilities.SetStateChanged(_text, "CPointWorldText", "m_Color");
    }

    public void Remove()
    {
        if (_text.IsValid)
            _text.Remove();
    }
}
