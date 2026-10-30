using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace TheatreScreen;

/// <summary>A black screen until a player requests a video with !play.</summary>
public sealed class TheatreScreenPlugin : BasePlugin, IPluginConfig<TheatreScreenConfig>
{
    public override string ModuleName => "TheatreScreen";
    public override string ModuleVersion => "0.5.2";
    public override string ModuleAuthor => "cs2-server";
    public override string ModuleDescription => "Entity-pixel video screen rendered entirely server-side.";

    public TheatreScreenConfig Config { get; set; } = new();
    private Screen? _screen;
    private VideoStream? _stream;
    private VideoFrame? _shownStreamFrame;
    private bool _loaded;

    public void OnConfigParsed(TheatreScreenConfig config) => Config = config;

    public override void Load(bool hotReload)
    {
        _loaded = true;
        RegisterListener<Listeners.OnServerPrecacheResources>(manifest => manifest.AddResource(Backdrop.Model));
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            StopPlayback();
            _screen = null; // Map change already removed the old entities.
            QueueScreen();
        });
        RegisterListener<Listeners.OnTick>(DrawVideoFrame);
        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            SpawnScreen();
            return HookResult.Continue;
        });
        AddCommand("css_play", "Play a YouTube video on the screen: !play <url>", OnPlay);
        Logger.LogInformation("TheatreScreen {version} loaded; starting black, use !play <YouTube URL>. Feeder: {feeder}",
            ModuleVersion, Config.FeederUrl);
        Server.PrintToConsole($"[TheatreScreen] v{ModuleVersion} loaded. Use !play <YouTube URL>; feeder {Config.FeederUrl}");
        QueueScreen(); // Also spawn when loaded mid-round, without waiting for round_start.
    }

    private void QueueScreen() => Server.NextWorldUpdate(() =>
    {
        if (_loaded)
            SpawnScreen();
    });

    public override void Unload(bool hotReload)
    {
        _loaded = false;
        StopPlayback();
        ClearScreen();
    }

    private void OnPlay(CCSPlayerController? player, CommandInfo info)
    {
        // Source's command tokenizer may split punctuation in an unquoted URL.
        // ArgString retains the original text after the command, including its query.
        if (!PlaybackRequest.TryReadArgument(info.ArgString, out var sourceUrl))
        {
            info.ReplyToCommand(" [Screen] Usage: !play <YouTube URL>. For long links, use console: css_play \"URL\"");
            return;
        }
        if (!PlaybackRequest.TryCreate(Config.FeederUrl, sourceUrl, Config.Columns, Config.Rows,
                Config.FramesPerSecond, out var endpoint, out var error))
        {
            info.ReplyToCommand($" [Screen] {error}");
            return;
        }
        if (_screen == null && !SpawnScreen())
        {
            info.ReplyToCommand(" [Screen] Could not create the screen. Check the TheatreScreen server log.");
            return;
        }
        StopPlayback();
        ShowBlack();
        Logger.LogInformation("Playback requested by {player}: {url}", player?.PlayerName ?? "server console", sourceUrl);
        _stream = new VideoStream(endpoint!, message => Logger.LogInformation("Theatre feeder: {message}", message));
        info.ReplyToCommand(" [Screen] Loading video...");
    }

    private void DrawVideoFrame()
    {
        if (_stream is not { } stream)
            return;
        if (stream.Finished)
        {
            var error = stream.Error;
            StopPlayback();
            ShowBlack();
            var summary = error is { Length: > 180 } ? error[..180] + "... (see server log)" : error;
            Server.PrintToChatAll(error == null ? " [Screen] Video finished." : $" [Screen] Playback failed: {summary}");
            return;
        }
        if (_screen is not { } screen)
            return;
        if (stream.Latest is { IsFresh: true } received)
        {
            if (!ReferenceEquals(received, _shownStreamFrame))
            {
                screen.Draw((col, row) => received.Pixel(col, row, screen.Columns, screen.Rows));
                _shownStreamFrame = received;
            }
        }
        else if (_shownStreamFrame != null)
            ShowBlack();
    }

    private bool SpawnScreen()
    {
        ClearScreen();
        try
        {
            _screen = new Screen(Config, (_, _) => Color.Black);
            Logger.LogInformation("Spawned black {cols}x{rows} screen; backdrop blocks: {blocks}",
                _screen.Columns, _screen.Rows, _screen.Backdrop?.BlockCount ?? 0);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not spawn theatre screen");
            Server.PrintToConsole($"[TheatreScreen] Screen creation failed: {ex.Message}");
            return false;
        }
    }

    private void ShowBlack()
    {
        _screen?.Draw((_, _) => Color.Black);
        _shownStreamFrame = null;
    }

    private void StopPlayback()
    {
        _stream?.Dispose();
        _stream = null;
        _shownStreamFrame = null;
    }

    private void ClearScreen()
    {
        _screen?.Remove();
        _screen = null;
        _shownStreamFrame = null;
    }
}
