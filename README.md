# cs2-stuff

## TheatreScreen

A CounterStrikeSharp plugin with a black screen on startup. Anyone can type
`!play <YouTube URL>` in chat to load a video through theatre-feeder. The server
console equivalent is `css_play <YouTube URL>`. Each request replaces the previous
video and plays once; completion or failure returns the screen to black. Audio is
not implemented. Videos never start automatically from configuration.

GitHub Actions publishes `TheatreScreen.zip`. Use `theatrescreen.toml` with csfleet
to install the latest release. CounterStrikeSharp must already be installed.
The original converted Rickroll asset remains embedded but is not played.

### Feeder playback

Configure these keys in the plugin JSON or csfleet TOML template, then restart:

```json
"FeederUrl": "http://172.30.0.1:8787",
"FramesPerSecond": 15,
"Backdrop": true
```

`FeederUrl` is a base address, without `/stream` or a video URL. The command builds
`/stream?url=<escaped YouTube URL>&w=<Columns>&h=<Rows>&fps=<FramesPerSecond>`.
The bridge gateway is reachable from game containers; `localhost` would point at
the game container instead of the VM. `VideoStreamUrl` from build-4 is no longer
used. Quote YouTube URLs containing `&` when invoking `css_play` in the console.

The plugin reads HTTP on a background task, paced at the advertised FPS because
the feeder relies on TCP backpressure. Only the latest complete frame is retained;
entity updates happen on the server tick. After 3 seconds without a complete frame,
the screen goes black. HTTP, decoding, and timeout failures end that playback request;
type `!play` again to retry. Network reads time out after 10 seconds; startup URL
resolution allows 60 seconds for response headers. Feeder errors (including the
plain-text HTTP error body) are written to the plugin log and summarized in chat.
Map changes cancel playback; round starts rebuild the screen and continue a video
that is still playing.

The plugin queues a black screen on load and map start and rebuilds after round
cleanup. Keep `Backdrop` enabled because black beam pixels alone are transparent.
The cube model is precached during map loading: after installing a new plugin,
restart the server or change the map to ensure the black backdrop is available.
Look for `[TheatreScreen] v0.5.0 loaded` in the server console and `Spawned black`
in the plugin log. Invalid JSON (including an extra quote on a key) prevents the
plugin from loading at all; validate the JSON inside the TOML template too.

Wire format: one 20-byte header (`TSV1`, then four little-endian signed int32 values:
width, height, FPS, frame count), followed by tightly packed RGB24 frames in row-major
order from top left. Frame count `0` means unknown/live; positive counts describe a
finite response. Width and height must each be 1–256, FPS 1–30. The HTTP body is
uncompressed (the embedded asset's gzip wrapper is not used on the network).
Frames have no timestamps or per-frame headers. Each request starts at a fresh
header; EOF closes that session without replaying it. Audio is not part of this protocol.

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
