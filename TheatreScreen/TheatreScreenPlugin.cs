using System.Drawing;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace TheatreScreen;

/// <summary>
/// Server-side "theatre screen": a wall of stock entities used as pixels, so clients
/// need no downloads. The screen is always up — rebuilt every round start, since the
/// round's map cleanup wipes plugin-spawned entities. For now it only shows a placement
/// test card; video comes later by feeding frames through <see cref="Screen.Draw"/>.
/// </summary>
public sealed class TheatreScreenPlugin : BasePlugin, IPluginConfig<TheatreScreenConfig>
{
    public override string ModuleName => "TheatreScreen";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleAuthor => "cs2-server";
    public override string ModuleDescription => "Entity-pixel screen rendered entirely server-side.";

    public TheatreScreenConfig Config { get; set; } = new();
    private Screen? _screen;

    public void OnConfigParsed(TheatreScreenConfig config) => Config = config;

    public override void Load(bool hotReload)
    {
        // The map change already destroyed the entities; just forget them.
        RegisterListener<Listeners.OnMapStart>(_ => _screen = null);
        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            SpawnScreen();
            return HookResult.Continue;
        });

        // Switch pixel primitive in place to compare how they look.
        AddCommand("css_screen", "Rebuild the theatre screen: !screen [beam|text]", OnScreenCommand);

        if (hotReload)
            SpawnScreen();
    }

    public override void Unload(bool hotReload) => ClearScreen();

    private void OnScreenCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (info.ArgCount > 1)
        {
            var type = info.GetArg(1).Trim().ToLowerInvariant();
            if (type is not ("beam" or "text"))
            {
                info.ReplyToCommand(" \x02[Screen]\x01 Usage: !screen [beam|text]");
                return;
            }
            Config.PixelType = type;
        }

        SpawnScreen();
        info.ReplyToCommand($" \x04[Screen]\x01 Rebuilt as {Config.PixelType}.");
    }

    private void SpawnScreen()
    {
        ClearScreen();
        _screen = new Screen(Config, TestCard);
        Logger.LogInformation("Spawned {cols}x{rows} {type} screen", _screen.Columns, _screen.Rows, Config.PixelType);
    }

    private void ClearScreen()
    {
        _screen?.Remove();
        _screen = null;
    }

    /// <summary>Placement check: blue on the audience's left, red on the right, a white
    /// strip along the top. Mirrored means we're facing the wrong way; white at the
    /// bottom means rows are flipped.</summary>
    private Color TestCard(int col, int row)
    {
        if (row < 2)
            return Color.White;
        return col < Config.Columns / 2 ? Color.Blue : Color.Red;
    }
}
