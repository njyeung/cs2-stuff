package main

import (
	"bytes"
	"context"
	"encoding/binary"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func page(flags byte, lace, body []byte) []byte {
	h := make([]byte, 27)
	copy(h, "OggS")
	h[5] = flags
	h[26] = byte(len(lace))
	return append(append(h, lace...), body...)
}
func TestOpusContinuation(t *testing.T) {
	head := append([]byte("OpusHead"), make([]byte, 11)...)
	stream := page(2, []byte{19, 8}, append(head, []byte("OpusTags")...))
	stream = append(stream, page(0, []byte{255}, bytes.Repeat([]byte{1}, 255))...)
	stream = append(stream, page(1, []byte{3, 1}, []byte{2, 2, 2, 3})...)
	var packets [][]byte
	if err := readOpusPackets(bytes.NewReader(stream), func(b []byte) error { packets = append(packets, bytes.Clone(b)); return nil }); err != nil {
		t.Fatal(err)
	}
	if len(packets) != 2 || len(packets[0]) != 258 || len(packets[1]) != 1 {
		t.Fatalf("unexpected packets: %v", packets)
	}
	for _, bad := range [][]byte{stream[:len(stream)-1], page(1, []byte{1}, []byte{1}), []byte("invalid")} {
		if err := readOpusPackets(bytes.NewReader(bad), func([]byte) error { return nil }); err == nil {
			t.Fatal("accepted malformed Ogg")
		}
	}
}
func TestAVRealFFmpeg(t *testing.T) {
	if _, e := exec.LookPath("ffmpeg"); e != nil {
		t.Skip("ffmpeg unavailable")
	}
	dir := t.TempDir()
	media := filepath.Join(dir, "input.mkv")
	cmd := exec.Command("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=32x24:rate=10:duration=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:v", "ffv1", "-c:a", "pcm_s16le", media)
	if b, e := cmd.CombinedOutput(); e != nil {
		t.Fatalf("fixture: %v %s", e, b)
	}
	fake := filepath.Join(dir, "yt-dlp")
	if e := os.WriteFile(fake, []byte("#!/bin/sh\ncat '"+media+"'\n"), 0700); e != nil {
		t.Fatal(e)
	}
	old := *ytdlpPath
	*ytdlpPath = fake
	defer func() { *ytdlpPath = old }()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	r := httptest.NewRequest("GET", "/av?url=https://youtu.be/test&w=16&h=12&fps=10", nil).WithContext(ctx)
	w := httptest.NewRecorder()
	serveAV(w, r)
	if w.Code != 200 {
		t.Fatalf("%d %s", w.Code, w.Body.String())
	}
	data := bytes.NewReader(w.Body.Bytes())
	var hdr [4]uint32
	if e := binary.Read(data, binary.LittleEndian, &hdr); e != nil {
		t.Fatal(e)
	}
	if hdr != [4]uint32{0x314d5354, 16, 12, 10} {
		t.Fatal(hdr)
	}
	counts := map[byte]uint32{}
	for data.Len() > 0 {
		tag, e := data.ReadByte()
		if e != nil {
			t.Fatal(e)
		}
		var index uint32
		if e = binary.Read(data, binary.LittleEndian, &index); e != nil {
			t.Fatal(e)
		}
		if index != counts[tag] {
			t.Fatalf("tag %c index %d expected %d", tag, index, counts[tag])
		}
		counts[tag]++
		size := 16 * 12 * 3
		switch tag {
		case 'V':
		case 'A':
			var n uint16
			if e = binary.Read(data, binary.LittleEndian, &n); e != nil {
				t.Fatal(e)
			}
			size = int(n)
		default:
			t.Fatalf("tag %q", tag)
		}
		if _, e = io.CopyN(io.Discard, data, int64(size)); e != nil {
			t.Fatal(e)
		}
	}
	if counts['V'] != 20 || counts['A'] < 200 || counts['A'] > 202 {
		t.Fatal(counts)
	}
}

func TestAVCancellationBeforeOutput(t *testing.T) {
	if _, e := exec.LookPath("ffmpeg"); e != nil {
		t.Skip("ffmpeg unavailable")
	}
	fake := filepath.Join(t.TempDir(), "yt-dlp")
	if e := os.WriteFile(fake, []byte("#!/bin/sh\nsleep 30\n"), 0700); e != nil {
		t.Fatal(e)
	}
	old := *ytdlpPath
	*ytdlpPath = fake
	defer func() { *ytdlpPath = old }()
	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
	defer cancel()
	done := make(chan struct{})
	go func() {
		defer close(done)
		serveAV(httptest.NewRecorder(), httptest.NewRequest("GET", "/av?url=https://youtu.be/test&w=16&h=12&fps=10", nil).WithContext(ctx))
	}()
	select {
	case <-done:
	case <-time.After(3 * time.Second):
		t.Fatal("cancellation did not stop pipeline")
	}
}

// Exercise the real yt-dlp -> internal ffmpeg failure path: quiet mode must not
// turn an invalid HTTP media response into only "ffmpeg exited with code 183".
func TestDownloaderFailureIncludesFFmpegDiagnostic(t *testing.T) {
	realYTDLP, err := exec.LookPath("yt-dlp")
	if err != nil {
		t.Skip("yt-dlp unavailable")
	}
	if _, err := exec.LookPath("ffmpeg"); err != nil {
		t.Skip("ffmpeg unavailable")
	}
	badMedia := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/html")
		io.WriteString(w, "<html>This upstream response is not a media stream.</html>")
	}))
	defer badMedia.Close()
	dir := t.TempDir()
	info, err := json.Marshal(map[string]any{
		"id": "broken", "title": "broken media", "extractor": "generic", "webpage_url": badMedia.URL,
		"formats": []map[string]any{
			{"format_id": "v", "url": badMedia.URL + "/v.mp4", "ext": "mp4", "height": 360, "vcodec": "h264", "acodec": "none", "protocol": "http"},
			{"format_id": "a", "url": badMedia.URL + "/a.m4a", "ext": "m4a", "vcodec": "none", "acodec": "aac", "protocol": "http"},
		},
	})
	if err != nil {
		t.Fatal(err)
	}
	infoPath := filepath.Join(dir, "input.json")
	if err := os.WriteFile(infoPath, info, 0600); err != nil {
		t.Fatal(err)
	}
	wrapper := filepath.Join(dir, "yt-dlp")
	quote := func(s string) string { return "'" + strings.ReplaceAll(s, "'", "'\"'\"'") + "'" }
	script := "#!/bin/sh\nexec " + quote(realYTDLP) + " --ignore-config --load-info-json " + quote(infoPath) + " \"$@\"\n"
	if err := os.WriteFile(wrapper, []byte(script), 0700); err != nil {
		t.Fatal(err)
	}
	old := *ytdlpPath
	*ytdlpPath = wrapper
	defer func() { *ytdlpPath = old }()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	response := httptest.NewRecorder()
	serveAV(response, httptest.NewRequest("GET", "/av?url=https://youtu.be/test&w=16&h=12&fps=10", nil).WithContext(ctx))
	body := response.Body.String()
	if response.Code != 502 || !strings.Contains(body, "yt-dlp downloader:") || !strings.Contains(body, "ffmpeg decoder:") || !strings.Contains(body, "Error opening input") {
		t.Fatalf("Missing underlying ffmpeg diagnostic: status=%d body=%s", response.Code, body)
	}
}
