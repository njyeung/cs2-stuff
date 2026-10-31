# cs2-stuff

## TheatreScreen

A CounterStrikeSharp plugin with a black screen on startup. Anyone can type
`!play <YouTube/Twitch URL>` in chat to load a video through theatre-feeder. The server
console equivalent is `css_play <YouTube/Twitch URL>`. Each request replaces the previous
video and plays once; completion or failure returns the screen to black. Audio
plays through our TheatreVoice Metamod plugin using the voice channel.
A bot must be present as the speaker. `!mute` toggles audio for the player
who runs it; everyone else continues hearing it. Videos never start automatically
from configuration. Anyone can use `!stop` to stop playback for everyone and return
the screen to black. The console equivalents are `css_mute` and `css_stop`.

GitHub Actions publishes `TheatreScreen.zip`. Use `theatrescreen.toml` with csfleet
to install the latest release. CounterStrikeSharp must already be installed.

### Feeder playback

Configure these keys in the plugin JSON or csfleet TOML template, then restart:

```json
"FeederUrl": "http://172.30.0.1:8787",
"FramesPerSecond": 15,
"Backdrop": true
```

`FeederUrl` is a base address, without `/av` or a video URL. The command builds
`/av?url=<escaped URL>&w=<Columns>&h=<Rows>&fps=<FramesPerSecond>`.
The bridge gateway is reachable from game containers; `localhost` would point at
the game container instead of the VM. `VideoStreamUrl` from build-4 is no longer
used. Quote YouTube URLs containing `&` when invoking `css_play` in the console.
Unquoted URLs pasted in chat are accepted without relying on the engine's argument
count. For long links, use the console command: CS2 chat can truncate the message
before the plugin receives it. A short `https://youtu.be/VIDEO_ID` link also works.

The plugin reads combined audio/video HTTP records on a background task. It
buffers up to five seconds per track and starts after 300 ms of audio and the first
video frame (or a shorter complete clip). On each server tick it sends due Opus
packets through TheatreVoice, normally three 10 ms packets per voice message.
Running byte and sample offsets match the confirmed voice test setup, with the
speaker entity set to the bot slot + 1. The audio packet count is the video clock.
If audio runs dry, video waits; resumed audio does not cause a catch-up burst.
Mute removes the player's recipient bit while playback keeps its clock.

EOF drains queued sound before finishing. HTTP, protocol and voice errors end the
request and return to black; type `!play` again to retry. Network reads time out
after 10 seconds; startup URL resolution allows 60 seconds for response headers.
A separate audio-stall timeout prevents a blocked track from waiting forever.
Feeder errors are logged and summarized in chat. Map changes cancel playback;
round starts rebuild the screen and retain the currently shown frame.
If the speaker bot disconnects, playback stops with a message.
When a full video buffer blocks reading needed audio or EOF, the reader retains
its nearest queued frames and discards excess future frames so the audio clock
can resume or finish. Buffer sizes stay bounded.

Run `css_screenstatus` in the game console if audio disappears. It reports the
audio clock, submitted voice messages, speaker, recipient/mute masks, queue sizes
and whether the HTTP input ended. The plugin logs this state every 10 seconds
while playing, plus the first successful TheatreVoice submission and the final
state. A successful submission does not confirm that a game client played it.

Install the release's `theatrevoice` and `metamod` directories into the server's
`addons` directory and restart so Metamod loads TheatreVoice. No ffmpeg or Opus
encoder is needed inside the game container. Rebuild and restart the feeder on
the VM (`cd theatre-feeder && go build .`); it needs yt-dlp and ffmpeg and must
support `/av`. The plugin reports an error if TheatreVoice is missing.

The plugin queues a black screen on load and map start and rebuilds after round
cleanup. Keep `Backdrop` enabled because black beam pixels alone are transparent.
The cube model is precached during map loading: after installing a new plugin,
restart the server or change the map to ensure the black backdrop is available.
Look for `[TheatreScreen] v0.7.1 loaded` in the server console and `Spawned black`
in the plugin log. Invalid JSON (including an extra quote on a key) prevents the
plugin from loading at all; validate the JSON inside the TOML template too.

Wire format: a 16-byte `TSM1` header with little-endian uint32 width, height and
FPS, then tagged records. `V` records carry a uint32 frame index and RGB24 frame;
`A` records carry a uint32 packet index, uint16 length and a raw 10 ms Opus packet.
See [the feeder protocol](theatre-feeder/README.md) for the complete contract.
The older TSV1 video-only `/stream` and `/audio` endpoints remain available for
other clients; `!play` now uses `/av`.

### Build

```sh
dotnet publish TheatreScreen/TheatreScreen.csproj -c Release -o out/TheatreScreen
```

Run the stream protocol, HTTP lifecycle and voice message checks with:

```sh
dotnet run --project tests/VideoStreamChecks -c Release
dotnet run --project tests/VoiceDataChecks -c Release
dotnet run --project tests/AvStreamChecks -c Release
(cd theatre-feeder && go test -race ./... && go vet ./...)
```

TheatreVoice builds in the Steam Runtime sniper SDK against hl2sdk (`cs2` branch) and
Metamod:Source checkouts; see `theatre-voice/build.sh` and the release workflow for
the pinned commits.
