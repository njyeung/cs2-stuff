using System.Diagnostics;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace TheatreScreen;

/// <summary>
/// Server-side "theatre screen": a wall of stock entities used as pixels, so clients
/// need no downloads. The screen is always up — rebuilt every round start, since the
/// round's map cleanup wipes plugin-spawned entities. Embedded video loops at 15 FPS.
/// </summary>
public sealed class TheatreScreenPlugin : BasePlugin, IPluginConfig<TheatreScreenConfig>
{
    public override string ModuleName => "TheatreScreen";
    public override string ModuleVersion => "0.4.0";
    public override string ModuleAuthor => "cs2-server";
    public override string ModuleDescription => "Entity-pixel screen rendered entirely server-side.";

    public TheatreScreenConfig Config { get; set; } = new();
    private Screen? _screen;
    private readonly VideoClip _video = new();
    private readonly Stopwatch _playback = new();
    private int _shownFrame = -1;
    private VideoStream? _stream;
    private VideoFrame? _shownStreamFrame;

    public void OnConfigParsed(TheatreScreenConfig config) => Config = config;

    public override void Load(bool hotReload)
    {
        if (!string.IsNullOrWhiteSpace(Config.VideoStreamUrl))
        {
            if (!Uri.TryCreate(Config.VideoStreamUrl, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("VideoStreamUrl must be an absolute HTTP(S) URL.");
            _stream = new VideoStream(endpoint, message => Logger.LogInformation("{message}", message));
        }
        // Precache only runs on map load — after the first hot-load, change map before
        // the backdrop's cube model will show.
        RegisterListener<Listeners.OnServerPrecacheResources>(manifest => manifest.AddResource(Backdrop.Model));
        // The map change already destroyed the entities; just forget them.
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _screen = null;
            _playback.Reset();
            _shownFrame = -1;
        });
        RegisterListener<Listeners.OnTick>(DrawVideoFrame);
        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            SpawnScreen();
            return HookResult.Continue;
        });

        if (hotReload)
            SpawnScreen();
    }

    public override void Unload(bool hotReload)
    {
        _stream?.Dispose();
        _stream = null;
        ClearScreen();
    }

    private void DrawVideoFrame()
    {
        if (_screen is not { } screen)
            return;
        if (_stream?.Latest is { IsFresh: true } received)
        {
            if (!ReferenceEquals(received, _shownStreamFrame))
            {
                screen.Draw((col, row) => received.Pixel(col, row, screen.Columns, screen.Rows));
                _shownStreamFrame = received;
                _shownFrame = -1;
            }
            return;
        }
        _shownStreamFrame = null;
        // Skip missed frames instead of queuing work when a server tick runs late.
        var frame = (int)((long)(_playback.Elapsed.TotalSeconds * _video.FramesPerSecond) % _video.FrameCount);
        if (frame == _shownFrame)
            return;
        screen.Draw((col, row) => _video.Pixel(frame, col, row, screen.Columns, screen.Rows));
        _shownFrame = frame;
    }

    private void SpawnScreen()
    {
        ClearScreen();
        _screen = new Screen(Config, (col, row) => _video.Pixel(0, col, row, Config.Columns, Config.Rows));
        _shownFrame = 0;
        _playback.Restart();
        Logger.LogInformation("Spawned {cols}x{rows} {type} screen", _screen.Columns, _screen.Rows, Config.PixelType);
        if (_screen.Backdrop is { } backdrop)
            Logger.LogInformation("Backdrop: {count} blocks, cube bounds {mins} .. {maxs}",
                backdrop.BlockCount, backdrop.BlockBounds.Mins, backdrop.BlockBounds.Maxs);
    }

    private void ClearScreen()
    {
        _screen?.Remove();
        _screen = null;
        _playback.Reset();
        _shownFrame = -1;
        _shownStreamFrame = null;
    }

}
