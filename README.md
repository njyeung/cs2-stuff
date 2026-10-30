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

### Feeder playback

Set `VideoStreamUrl` in the plugin JSON (or the csfleet TOML template) to the full
feeder endpoint reachable from the game container, then restart the plugin/server:

```json
"VideoStreamUrl": "http://172.30.0.1:8787/stream?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DdQw4w9WgXcQ&w=96&h=54&fps=15"
```

The address is the csfleet Docker bridge gateway, not `localhost` inside the
container. Percent-encode the complete source URL in the `url` query parameter,
especially any `&` in playlist links. Set `Columns`/`Rows` to match `w`/`h` for a
pixel-for-pixel image. If dimensions differ, the plugin samples the incoming frame
onto the existing grid; remote dimensions never cause it to spawn extra entities.

The plugin reads HTTP on a background task, paced at the advertised FPS because
the feeder relies on TCP backpressure. Only the latest complete frame is retained;
entity updates happen on the server tick. After 3 seconds without a complete frame,
the embedded clip takes over. Failed connections/EOF retry after 5 seconds; a stream
that stops sending data is closed after a 10-second read timeout. Startup URL
resolution has a 60-second response-header timeout. Empty `VideoStreamUrl` keeps
the embedded loop without connecting to a feeder. No playback commands are added.

Wire format: one 20-byte header (`TSV1`, then four little-endian signed int32 values:
width, height, FPS, frame count), followed by tightly packed RGB24 frames in row-major
order from top left. Frame count `0` means unknown/live; positive counts describe a
finite response. Width and height must each be 1–256, FPS 1–30. The HTTP body is
uncompressed (the embedded asset's gzip wrapper is not used on the network).
Frames have no timestamps or per-frame headers. Each request starts at a fresh
header; EOF closes that session. Audio is not part of this protocol.

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

Run the stream protocol and HTTP lifecycle checks with:

```sh
dotnet run --project tests/VideoStreamChecks -c Release
```
