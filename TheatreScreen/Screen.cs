using System.Drawing;
using System.Numerics;

namespace TheatreScreen;

/// <summary>
/// A Columns×Rows grid of <see cref="IScreenPixel"/>s standing on the configured bottom
/// edge. Row 0 is the top and column 0 the audience's left, like an image buffer.
/// <see cref="Draw"/> only touches pixels whose colour changed.
/// </summary>
public sealed class Screen
{
    public int Columns { get; }
    public int Rows { get; }

    private readonly IScreenPixel[] _pixels;
    private readonly Color[] _shown;

    public Screen(TheatreScreenConfig config, Func<int, int, Color> image)
    {
        Columns = config.Columns;
        Rows = config.Rows;

        var left = ToVector3(config.BottomLeft);
        var right = ToVector3(config.BottomRight);
        var across = right - left;
        var pixelSize = across.Length() / Columns;
        var step = across / Columns;
        var up = Vector3.UnitZ * pixelSize;
        var bottom = left + Vector3.UnitZ * config.Elevation;

        _pixels = new IScreenPixel[Columns * Rows];
        _shown = new Color[Columns * Rows];

        for (var row = 0; row < Rows; row++)
        for (var col = 0; col < Columns; col++)
        {
            var color = image(col, row);
            var center = bottom + step * (col + 0.5f) + up * (Rows - row - 0.5f);
            _pixels[Index(col, row)] = config.PixelType == "text"
                ? new TextPixel(center, pixelSize, config.FacingYaw, config.TextGlyph, config.TextScale, color)
                : new BeamPixel(center - step / 2, center + step / 2, pixelSize, color);
            _shown[Index(col, row)] = color;
        }
    }

    /// <summary>Repaints from <paramref name="image"/>; returns how many pixels changed.</summary>
    public int Draw(Func<int, int, Color> image)
    {
        var changed = 0;
        for (var row = 0; row < Rows; row++)
        for (var col = 0; col < Columns; col++)
        {
            var i = Index(col, row);
            var color = image(col, row);
            if (color.ToArgb() == _shown[i].ToArgb())
                continue;
            _pixels[i].SetColor(color);
            _shown[i] = color;
            changed++;
        }
        return changed;
    }

    public void Remove()
    {
        foreach (var pixel in _pixels)
            pixel.Remove();
    }

    private int Index(int col, int row) => row * Columns + col;

    private static Vector3 ToVector3(float[] v) => new(v[0], v[1], v[2]);
}
