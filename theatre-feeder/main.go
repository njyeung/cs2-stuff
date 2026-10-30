// theatre-feeder turns a video URL into the raw streams the TheatreScreen plugin plays.
// It runs on the csfleet VM itself, outside the game containers, because it needs
// yt-dlp and ffmpeg; containers reach it on the Docker bridge gateway.
//
//	GET /stream?url=<video url>&w=96&h=54&fps=15
//
// Video in the plugin's TSV1 format: a little-endian header (magic "TSV1", width,
// height, fps, frame count — 0 here, since a stream's length isn't known up front)
// followed by back-to-back rgb24 frames.
//
//	GET /audio?url=<video url>                (mode=file, the default)
//
// The whole audio track as one Ogg Opus file (48 kHz mono), sent only once it has
// fully downloaded, with a Content-Length. This is the shape the Audio plugin's
// PlayFromBuffer takes: it only plays complete buffers ffmpeg can decode.
//
//	GET /audio?url=<video url>&mode=pcm
//
// The audio track as a live stream in TSA1 format: a little-endian header (magic
// "TSA1", sample rate, channels, bits per sample) followed by interleaved s16le
// samples, for a player that can take audio incrementally.
//
// Streams hold back the status line until their first frame or chunk decodes, so a
// bad URL comes back as a 502 with yt-dlp's error rather than an empty 200. Pacing is
// left to the client: it reads at playback speed, and TCP back pressure stalls ffmpeg
// (and yt-dlp behind it) whenever the client's buffer is full.
package main

import (
	"bytes"
	"encoding/binary"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
)

const (
	tsv1Magic = 0x31565354 // "TSV1"
	tsa1Magic = 0x31415354 // "TSA1"

	audioRate     = 48000
	audioChannels = 1
	audioBits     = 16
	// 20 ms of PCM per write, the same granularity as a voice packet.
	audioChunk = audioRate / 50 * audioChannels * audioBits / 8
)

var (
	listen      = flag.String("listen", "172.30.0.1:8787", "address to serve on; the default is csfleet's bridge gateway, reachable only from the game containers and the VM")
	maxStreams  = flag.Int("max-streams", 8, "concurrent requests across all servers; /av takes one slot")
	allowHosts  = flag.String("allow-hosts", "youtube.com,youtu.be,twitch.tv", "comma-separated hosts (and their subdomains) a URL may point at; empty allows any site yt-dlp supports")
	videoFormat = flag.String("format", "bv*[height<=360]/wv*/b", "yt-dlp format selector for /stream")
	avFormat    = flag.String("av-format", "bv*[height<=360]+ba/b", "yt-dlp format selector for /av")
	audioFormat = flag.String("audio-format", "ba/b", "yt-dlp format selector for /audio")
	maxAudioMB  = flag.Int("max-audio-mb", 32, "largest /audio file to buffer (64 kbps Opus is about 0.5 MB a minute); longer videos and live streams are refused")
	ytdlpPath   = flag.String("yt-dlp", "yt-dlp", "yt-dlp binary")
	ffmpegPath  = flag.String("ffmpeg", "ffmpeg", "ffmpeg binary")
)

func main() {
	flag.Parse()

	slots := make(chan struct{}, *maxStreams)
	limited := func(h http.HandlerFunc) http.HandlerFunc {
		return func(w http.ResponseWriter, r *http.Request) {
			select {
			case slots <- struct{}{}:
				defer func() { <-slots }()
			default:
				http.Error(w, "too many streams", http.StatusServiceUnavailable)
				return
			}
			h(w, r)
		}
	}

	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, _ *http.Request) { io.WriteString(w, "ok\n") })
	mux.HandleFunc("GET /stream", limited(serveVideo))
	mux.HandleFunc("GET /audio", limited(serveAudio))
	mux.HandleFunc("GET /av", limited(serveAV))

	ln := listenWhenReady(*listen)
	log.Printf("serving on %s", ln.Addr())
	log.Fatal(http.Serve(ln, mux))
}

// listenWhenReady retries until the address can be bound. The default address lives
// on csfleet's Docker bridge, which only exists once csfleet has started, so at boot
// the feeder may come up first.
func listenWhenReady(addr string) net.Listener {
	for {
		ln, err := net.Listen("tcp", addr)
		if err == nil {
			return ln
		}
		log.Printf("listen %s: %v (retrying in 5s)", addr, err)
		time.Sleep(5 * time.Second)
	}
}

func parseSource(q url.Values) (string, error) {
	src := q.Get("url")
	u, err := url.Parse(src)
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") {
		return "", fmt.Errorf("url must be an http(s) link")
	}
	if !hostAllowed(u.Hostname()) {
		return "", fmt.Errorf("host %q is not allowed", u.Hostname())
	}
	return src, nil
}

func hostAllowed(host string) bool {
	if *allowHosts == "" {
		return true
	}
	host = strings.ToLower(host)
	for allowed := range strings.SplitSeq(*allowHosts, ",") {
		allowed = strings.ToLower(strings.TrimSpace(allowed))
		if host == allowed || strings.HasSuffix(host, "."+allowed) {
			return true
		}
	}
	return false
}

func serveVideo(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	src, err := parseSource(q)
	var width, height, fps int
	for _, f := range []struct {
		name     string
		dst      *int
		min, max int
	}{
		{"w", &width, 1, 256},
		{"h", &height, 1, 256},
		{"fps", &fps, 1, 30},
	} {
		if err != nil {
			break
		}
		v, convErr := strconv.Atoi(q.Get(f.name))
		if convErr != nil || v < f.min || v > f.max {
			err = fmt.Errorf("%s must be %d-%d", f.name, f.min, f.max)
		}
		*f.dst = v
	}
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	log.Printf("%s: video %s at %dx%d@%d", r.RemoteAddr, src, width, height, fps)

	// Letterbox to the exact grid and resample to the target frame rate.
	vf := fmt.Sprintf("fps=%d,scale=%d:%d:force_original_aspect_ratio=decrease:flags=area,pad=%d:%d:(ow-iw)/2:(oh-ih)/2,setsar=1",
		fps, width, height, width, height)
	p, err := startPipeline(src, *videoFormat,
		"-an", "-sn", "-vf", vf, "-pix_fmt", "rgb24", "-f", "rawvideo")
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	defer p.stop()
	relay(w, r, p, header(tsv1Magic, uint32(width), uint32(height), uint32(fps), 0), width*height*3, "frames")
}

func serveAudio(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	src, err := parseSource(q)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	mode := q.Get("mode")
	if mode == "" {
		mode = "file"
	}
	if mode != "file" && mode != "pcm" {
		http.Error(w, "mode must be file or pcm", http.StatusBadRequest)
		return
	}
	log.Printf("%s: audio (%s) %s", r.RemoteAddr, mode, src)

	resample := []string{"-vn", "-sn", "-ac", strconv.Itoa(audioChannels), "-ar", strconv.Itoa(audioRate)}
	if mode == "pcm" {
		p, err := startPipeline(src, *audioFormat, append(resample, "-f", "s16le")...)
		if err != nil {
			http.Error(w, err.Error(), http.StatusInternalServerError)
			return
		}
		defer p.stop()
		relay(w, r, p, header(tsa1Magic, audioRate, audioChannels, audioBits), audioChunk, "chunks")
		return
	}

	p, err := startPipeline(src, *audioFormat, append(resample, "-c:a", "libopus", "-b:a", "64k", "-f", "ogg")...)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	defer p.stop()

	limit := int64(*maxAudioMB) << 20
	body, readErr := io.ReadAll(io.LimitReader(p.out, limit+1))
	if int64(len(body)) > limit {
		log.Printf("%s: audio over %d MB, refused", r.RemoteAddr, *maxAudioMB)
		http.Error(w, fmt.Sprintf("audio is longer than the %d MB limit (or a live stream)", *maxAudioMB), http.StatusBadGateway)
		return
	}
	// Unlike a stream, a file can be checked before anything is sent: if yt-dlp got
	// cut off partway (a 403 mid-download), ffmpeg still exits cleanly with a valid
	// but truncated file, so its exit status alone isn't enough.
	if err := errors.Join(readErr, p.finish()); err != nil || len(body) == 0 {
		msg := p.stderr()
		if msg == "" {
			msg = fmt.Sprint(err)
		}
		log.Printf("%s: audio failed: %s", r.RemoteAddr, msg)
		http.Error(w, msg, http.StatusBadGateway)
		return
	}

	w.Header().Set("Content-Type", "audio/ogg")
	w.Header().Set("Content-Length", strconv.Itoa(len(body)))
	if _, err := w.Write(body); err != nil {
		log.Printf("%s: client gone during audio send", r.RemoteAddr)
		return
	}
	log.Printf("%s: sent %d KB of audio", r.RemoteAddr, len(body)>>10)
}

// relay streams the pipeline's output to the client in fixed-size units (a video
// frame, a PCM chunk), flushing each one. The status line and header wait for the
// first full unit, so a pipeline that fails up front becomes a 502 with its stderr.
func relay(w http.ResponseWriter, r *http.Request, p *pipeline, hdr []byte, unitSize int, unitName string) {
	unit := make([]byte, unitSize)
	if _, err := io.ReadFull(p.out, unit); err != nil {
		p.finish()
		msg := p.stderr()
		if msg == "" {
			msg = err.Error()
		}
		log.Printf("%s: failed before the first of the %s: %s", r.RemoteAddr, unitName, msg)
		http.Error(w, msg, http.StatusBadGateway)
		return
	}

	w.Header().Set("Content-Type", "application/octet-stream")
	rc := http.NewResponseController(w)
	_, err := w.Write(hdr)
	sent := 0
	for {
		if err == nil {
			_, err = w.Write(unit)
		}
		if err == nil {
			err = rc.Flush()
		}
		if err != nil {
			log.Printf("%s: client gone after %d %s", r.RemoteAddr, sent, unitName)
			return
		}
		sent++
		if _, err := io.ReadFull(p.out, unit); err != nil {
			p.finish()
			if msg := p.stderr(); msg != "" {
				log.Printf("%s: stream ended after %d %s: %s", r.RemoteAddr, sent, unitName, msg)
			} else {
				log.Printf("%s: finished after %d %s", r.RemoteAddr, sent, unitName)
			}
			return
		}
	}
}

func header(fields ...uint32) []byte {
	b := make([]byte, 4*len(fields))
	for i, v := range fields {
		binary.LittleEndian.PutUint32(b[i*4:], v)
	}
	return b
}

// pipeline is yt-dlp downloading src to stdout, piped into ffmpeg, which decodes it
// with outputArgs and writes the result to out.
type pipeline struct {
	ytdlp, ffmpeg       *exec.Cmd
	ytdlpErr, ffmpegErr tail
	out                 io.ReadCloser
	audio               *os.File

	once    sync.Once
	waitErr error
}

func startPipeline(src, format string, outputArgs ...string) (*pipeline, error) {
	return startPipelineOutputs(src, format, nil, append(outputArgs, "pipe:1")...)
}

func startPipelineOutputs(src, format string, audio *os.File, outputArgs ...string) (*pipeline, error) {
	p := &pipeline{
		// "--" keeps a URL from being read as a flag. Warnings stay on: "n challenge
		// solving failed" (no JS runtime, stale yt-dlp) is the usual reason YouTube
		// cuts a stream off after a few seconds.
		ytdlp: exec.Command(*ytdlpPath,
			"--quiet", "--no-progress", "--no-playlist",
			// yt-dlp quiet also silences its ffmpeg downloader. Override that
			// child only: otherwise failures become an unexplained exit code.
			"--downloader-args", "ffmpeg:-loglevel warning -nostats",
			"-f", format, "-o", "-", "--", src),
		ffmpeg: exec.Command(*ffmpegPath,
			append([]string{"-hide_banner", "-loglevel", "error", "-i", "pipe:0"},
				outputArgs...)...),
	}
	if audio != nil {
		p.ffmpeg.ExtraFiles = []*os.File{audio}
	}
	p.ytdlp.Stderr = &p.ytdlpErr
	p.ffmpeg.Stderr = &p.ffmpegErr

	pr, pw, err := os.Pipe()
	if err != nil {
		return nil, err
	}
	// The children hold their own copies of the pipe ends.
	defer pr.Close()
	defer pw.Close()
	p.ytdlp.Stdout = pw
	p.ffmpeg.Stdin = pr
	if p.out, err = p.ffmpeg.StdoutPipe(); err != nil {
		return nil, err
	}

	for _, cmd := range []*exec.Cmd{p.ytdlp, p.ffmpeg} {
		// Own process group, so stopping also takes down anything yt-dlp spawns
		// (it hands HLS/live streams to its own ffmpeg).
		cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true}
		if err := cmd.Start(); err != nil {
			p.stop()
			return nil, err
		}
	}
	return p, nil
}

// finish waits for both processes and returns the first failure. Call it once out
// has hit EOF (ffmpeg is done, and yt-dlp has finished or failed writing to it); the
// wait is also what lets their stderr finish copying in.
func (p *pipeline) finish() error {
	p.once.Do(func() {
		for _, cmd := range []*exec.Cmd{p.ytdlp, p.ffmpeg} {
			if cmd.Process != nil {
				if err := cmd.Wait(); err != nil {
					name := "yt-dlp downloader"
					if cmd == p.ffmpeg {
						name = "ffmpeg decoder"
					}
					p.waitErr = errors.Join(p.waitErr, fmt.Errorf("%s: %w", name, err))
				}
			}
		}
	})
	return p.waitErr
}

// stop kills both processes, however the request ended (client gone, video over,
// error), and reaps them.
func (p *pipeline) stop() {
	for _, cmd := range []*exec.Cmd{p.ytdlp, p.ffmpeg} {
		if cmd.Process != nil {
			syscall.Kill(-cmd.Process.Pid, syscall.SIGKILL)
		}
	}
	p.finish()
	if p.out != nil {
		p.out.Close()
	}
	if p.audio != nil {
		p.audio.Close()
	}
}

func (p *pipeline) stderr() string {
	var messages []string
	if msg := p.ytdlpErr.String(); msg != "" {
		messages = append(messages, "yt-dlp downloader: "+msg)
	}
	if msg := p.ffmpegErr.String(); msg != "" {
		messages = append(messages, "ffmpeg decoder: "+msg)
	}
	return strings.Join(messages, "\n")
}

// tail keeps the last few KB written to it — enough of a child's stderr to say why
// it failed without holding a whole run's worth.
type tail struct {
	mu  sync.Mutex
	buf []byte
}

const tailSize = 4096

func (t *tail) Write(p []byte) (int, error) {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.buf = append(t.buf, p...)
	if len(t.buf) > tailSize {
		t.buf = t.buf[len(t.buf)-tailSize:]
	}
	return len(p), nil
}

func (t *tail) String() string {
	t.mu.Lock()
	defer t.mu.Unlock()
	return string(bytes.TrimSpace(t.buf))
}
