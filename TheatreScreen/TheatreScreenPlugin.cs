using System.Drawing;
using System.Diagnostics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace TheatreScreen;

/// <summary>A black screen until a player requests a video with !play.</summary>
public sealed class TheatreScreenPlugin : BasePlugin, IPluginConfig<TheatreScreenConfig>
{
    public override string ModuleName => "TheatreScreen";
    public override string ModuleVersion => "0.7.0";
    public override string ModuleAuthor => "cs2-server";
    public override string ModuleDescription => "Entity-pixel video screen rendered entirely server-side.";

    public TheatreScreenConfig Config { get; set; } = new();
    private Screen? _screen;
    private AvStream? _stream;
    private AvPlayback? _playback;
    private long _lastTick;
    private long _lastAudioProgress;
    private int _speaker;
    private uint _voiceSection;
    private ulong _muted;
    private VideoFrame? _shownStreamFrame;
    private TheatreVoice? _theatreVoice;
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
        AddCommand("css_play", "Play a YouTube or Twitch video on the screen: !play <url>", OnPlay);
        AddCommand("css_screenmute", "Toggle screen audio for yourself", (player, info) =>
        {
            if (player is not { IsValid: true, IsBot: false } || player.Slot is < 0 or >= 64) return;
            _muted ^= 1UL << player.Slot;
            info.ReplyToCommand((_muted & (1UL << player.Slot)) != 0
                ? " [Screen] Screen audio muted." : " [Screen] Screen audio unmuted.");
        });
        RegisterListener<Listeners.OnClientDisconnect>(slot =>
        {
            if (slot is >= 0 and < 64) _muted &= ~(1UL << slot);
        });
        Logger.LogInformation("TheatreScreen {version} loaded; starting black, use !play <YouTube/Twitch URL>. Feeder: {feeder}",
            ModuleVersion, Config.FeederUrl);
        Server.PrintToConsole($"[TheatreScreen] v{ModuleVersion} loaded. Use !play <YouTube/Twitch URL>; feeder {Config.FeederUrl}");
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
        _theatreVoice?.Dispose();
        _theatreVoice = null;
        ClearScreen();
    }

    private void OnPlay(CCSPlayerController? player, CommandInfo info)
    {
        // Source's command tokenizer may split punctuation in an unquoted URL.
        // ArgString retains the original text after the command, including its query.
        if (!PlaybackRequest.TryReadArgument(info.ArgString, out var sourceUrl))
        {
            info.ReplyToCommand(" [Screen] Usage: !play <YouTube/Twitch URL>. For long links, use console: css_play \"URL\"");
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
        var bot = Utilities.GetPlayers().FirstOrDefault(p => p is { IsValid: true, IsBot: true } && p.Slot is >= 0 and < 64);
        if (bot == null)
        {
            info.ReplyToCommand(" [Screen] Audio needs a bot as its voice speaker. Add a bot, then try !play again.");
            return;
        }
        if (_theatreVoice == null)
        {
            var path = Path.Combine(ModuleDirectory, "..", "..", "..", "theatrevoice", "bin", "theatrevoice.so");
            if (!TheatreVoice.TryAttach(path, out _theatreVoice, out var attachError))
            {
                info.ReplyToCommand($" [Screen] {attachError}");
                return;
            }
        }
        StopPlayback();
        ShowBlack();
        _speaker = bot.Slot;
        _voiceSection++;
        _lastTick = _lastAudioProgress = Stopwatch.GetTimestamp();
        _playback = new AvPlayback();
        Logger.LogInformation("Playback requested by {player}: {url}; voice speaker {speaker}", player?.PlayerName ?? "server console", sourceUrl, _speaker);
        _stream = new AvStream(endpoint!, message => Logger.LogInformation("Theatre feeder: {message}", message));
        info.ReplyToCommand(" [Screen] Loading video...");
    }

    private void DrawVideoFrame()
    {
        if (_stream is not { } stream || _playback is not { } playback) return;
        if (stream.Error != null || stream.Finished)
        {
            FinishPlayback(stream.Error);
            return;
        }
        var players = Utilities.GetPlayers();
        if (!players.Any(p => p is { IsValid: true, IsBot: true } && p.Slot == _speaker))
        {
            FinishPlayback("The voice speaker bot disconnected; add a bot and play again.");
            return;
        }
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds;
        _lastTick = now;
        var recipients = players.Where(p => p is { IsValid: true, IsBot: false } && p.Slot is >= 0 and < 64)
            .Aggregate(0UL, (mask, human) => mask | (1UL << human.Slot)) & ~_muted;
        try
        {
            var before = playback.SentPackets;
            playback.Tick(elapsed, stream.Ready, stream.InputEnded, () => stream.BufferedAudio, stream.TakeAudio,
                (packets, sequenceBytes, samples) =>
                {
                    // Even when everyone is muted, consume on schedule and retain the stream clock.
                    if (recipients == 0) return;
                    var data = VoiceData.Build(_speaker, _speaker + 1, _voiceSection, sequenceBytes, samples, packets);
                    _theatreVoice!.Post(VoiceData.MessageId, data, recipients);
                });
            if (playback.SentPackets != before) _lastAudioProgress = now;
            else if (Stopwatch.GetElapsedTime(_lastAudioProgress, now).TotalSeconds > (before == 0 ? 75 : 10))
            {
                FinishPlayback("Feeder audio stalled.");
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Screen voice playback failed");
            FinishPlayback(ex.Message);
            return;
        }
        if (stream.VideoAt(playback.Seconds) is { } received && _screen is { } screen)
        {
            screen.Draw((col, row) => received.Pixel(col, row, screen.Columns, screen.Rows));
            _shownStreamFrame = received;
        }
    }

    private void FinishPlayback(string? error)
    {
        StopPlayback();
        ShowBlack();
        var summary = error is { Length: > 180 } ? error[..180] + "... (see server log)" : error;
        Server.PrintToChatAll(error == null ? " [Screen] Video finished." : $" [Screen] Playback failed: {summary}");
    }

    private bool SpawnScreen()
    {
        var previous = _shownStreamFrame;
        ClearScreen();
        try
        {
            _screen = new Screen(Config, (_, _) => Color.Black);
            if (previous != null)
            {
                _screen.Draw((col, row) => previous.Pixel(col, row, _screen.Columns, _screen.Rows));
                _shownStreamFrame = previous;
            }
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
        _playback = null;
        _shownStreamFrame = null;
    }

    private void ClearScreen()
    {
        _screen?.Remove();
        _screen = null;
        _shownStreamFrame = null;
    }
}
