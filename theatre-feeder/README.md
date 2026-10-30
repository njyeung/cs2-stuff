# Theatre feeder

`GET /av?url=<escaped URL>&w=96&h=54&fps=15` delivers video and audio
in a single uncompressed HTTP response. Both tracks are required. `/stream`
and `/audio` retain their existing protocols. The default host allowlist includes
YouTube and Twitch; use `-allow-hosts` to override it.

The 16-byte header is `TSM1`, then little-endian uint32 width, height and FPS.
Dimensions are 1–256 and FPS is 1–30. Records follow in arrival order:

| Tag (one byte) | Remaining fields |
| --- | --- |
| ASCII `V` | uint32 LE frame index, then width × height × 3 RGB24 bytes |
| ASCII `A` | uint32 LE packet index, uint16 LE packet length, then raw Opus packet |

Indices start at zero independently per track. Video time is index / FPS;
audio time is index × 10 ms. Opus is 48 kHz mono, 64 kbps, 10 ms frames.
Audio is attenuated to 50% amplitude (about −6 dB) before Opus encoding.
OpusHead and OpusTags are removed. Clients should buffer and pace both tracks
by these times. Opus encoder pre-skip and final padding are not represented;
this protocol has packet-level timing rather than sample-exact trimming.
EOF and partial records terminate playback. HTTP errors before the first record
return 502 with decoder diagnostics; failures after it terminate the response.

The feeder starts one yt-dlp invocation and one ffmpeg decoder with two outputs.
Independent readers drain raw video on stdout and Ogg on fd 3; a single HTTP
writer serializes complete records with bounded buffering. Client backpressure
propagates upstream. Cancellation kills process groups and closes both readers.

The default `-av-format` is `bv*[height<=360]+ba/b`. yt-dlp merges separate
formats directly to stdout using an internal ffmpeg downloader, so YouTube may
have an additional ffmpeg muxer process. This does not wait for a complete file.
Twitch HLS already carries both tracks. Installed yt-dlp downloader selection
and its upstream documentation support this streaming path; live YouTube/Twitch
network playback has not been validated by the local checks.

Run `go test -race ./...` here. Tests exercise cross-page Ogg packets, malformed
input, cancellation, and an actual ffmpeg audio/video fixture (when installed).
Build with `go build .`; the service uses the existing deployment path.

If playback stalls, inspect `journalctl -u theatre-feeder`: AV requests log their
URL and dimensions, and termination logs include video/audio counts, media times
and elapsed wall time. Errors are labeled `yt-dlp downloader` or `ffmpeg decoder`.
The downloader's ffmpeg warnings/errors remain visible even with yt-dlp quiet,
so a download failure includes its underlying message rather than only an exit
code. An Opus parser message at EOF alone does not establish why downloading
stopped. Increasing the plugin's stall timeout cannot repair a failed download.
