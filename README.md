# cs2-stuff

## TheatreScreen

A CounterStrikeSharp plugin that loops the bundled Rickroll video on a screen
made from server-side pixel entities. Playback starts on round start and on hot
reload, at 96×54 and 15 FPS with the default configuration. No commands are needed.
The clip is silent; audio playback is not implemented.

All 114 frames are compressed and embedded in `TheatreScreen.dll`. The server needs
neither FFmpeg nor the source WebM. The screen's position, pixel primitive, and
backdrop are configured through the usual CounterStrikeSharp plugin config.

GitHub Actions publishes `TheatreScreen.zip`. Use `theatrescreen.toml` with csfleet
to install the latest release. CounterStrikeSharp must already be installed.

### Build

```sh
dotnet publish TheatreScreen/TheatreScreen.csproj -c Release -o out/TheatreScreen
```

The converted frames are committed. To replace or regenerate them, put the source
clip at `rickroll.webm` and run the following with Python 3 and FFmpeg installed:

```sh
python3 tools/encode-video.py
```

Changing the encoded dimensions or frame rate also requires updating `VideoClip`.
