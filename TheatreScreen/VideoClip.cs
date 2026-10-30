using System.Drawing;
using System.IO.Compression;

namespace TheatreScreen;

/// <summary>Predecoded RGB frames embedded in the DLL; no runtime decoder or files.</summary>
public sealed class VideoClip
{
    public int Width { get; }
    public int Height { get; }
    public int FramesPerSecond { get; }
    public int FrameCount { get; }
    private readonly Color[] _pixels;

    public VideoClip()
    {
        using var resource = typeof(VideoClip).Assembly.GetManifestResourceStream("TheatreScreen.Rickroll")
            ?? throw new InvalidOperationException("Embedded video is missing.");
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip);
        if (reader.ReadUInt32() != 0x31565354) // TSV1
            throw new InvalidDataException("Invalid embedded video header.");
        Width = reader.ReadInt32();
        Height = reader.ReadInt32();
        FramesPerSecond = reader.ReadInt32();
        FrameCount = reader.ReadInt32();
        if (Width != 96 || Height != 54 || FramesPerSecond != 15 || FrameCount <= 0)
            throw new InvalidDataException("Invalid embedded video dimensions or timing.");
        _pixels = new Color[checked(Width * Height * FrameCount)];
        for (var i = 0; i < _pixels.Length; i++)
            _pixels[i] = Color.FromArgb(reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
    }

    public Color Pixel(int frame, int col, int row, int columns, int rows) =>
        _pixels[frame * Width * Height + (row * Height / rows) * Width + col * Width / columns];
}
