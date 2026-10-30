package main

import (
	"context"
	"encoding/binary"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"strconv"
	"sync"
)

// TSM1: magic, uint32 LE width, height, fps. Records use a one-byte tag
// ('V' or 'A'), uint32 LE index, then RGB24 or uint16 LE length + Opus.
func serveAV(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	src, err := parseSource(q)
	if err != nil {
		http.Error(w, err.Error(), 400)
		return
	}
	dims := [3]int{}
	for i, key := range []string{"w", "h", "fps"} {
		n, e := strconv.Atoi(q.Get(key))
		max := 256
		if i == 2 {
			max = 30
		}
		if e != nil || n < 1 || n > max {
			http.Error(w, fmt.Sprintf("%s must be 1-%d", key, max), 400)
			return
		}
		dims[i] = n
	}
	width, height, fps := dims[0], dims[1], dims[2]
	ar, aw, err := os.Pipe()
	if err != nil {
		http.Error(w, err.Error(), 500)
		return
	}
	defer ar.Close()
	vf := fmt.Sprintf("fps=%d,scale=%d:%d:force_original_aspect_ratio=decrease:flags=area,pad=%d:%d:(ow-iw)/2:(oh-ih)/2,setsar=1", fps, width, height, width, height)
	p, err := startPipelineOutputs(src, *avFormat, aw,
		"-map", "0:v:0", "-an", "-sn", "-vf", vf, "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1",
		"-map", "0:a:0", "-vn", "-sn", "-c:a", "libopus", "-frame_duration", "10", "-application", "audio", "-ac", "1", "-ar", "48000", "-b:a", "64k", "-f", "ogg", "-page_duration", "10000", "-flush_packets", "1", "pipe:3")
	aw.Close()
	if err != nil {
		http.Error(w, err.Error(), 500)
		return
	}
	p.audio = ar
	ctx, cancel := context.WithCancel(r.Context())
	var readers sync.WaitGroup
	defer func() { cancel(); p.stop(); readers.Wait() }()
	// A disconnected client must also interrupt reads before the first record.
	stopCancel := context.AfterFunc(ctx, func() { p.stop() })
	defer stopCancel()
	records := make(chan []byte)
	failures := make(chan error, 2)
	send := func(tag byte, index uint32, payload []byte) error {
		size := 5
		if tag == 'A' {
			size += 2
		}
		b := make([]byte, size+len(payload))
		b[0] = tag
		binary.LittleEndian.PutUint32(b[1:], index)
		if tag == 'A' {
			binary.LittleEndian.PutUint16(b[5:], uint16(len(payload)))
		}
		copy(b[size:], payload)
		select {
		case records <- b:
			return nil
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	readers.Add(2)
	go func() {
		defer readers.Done()
		frame := make([]byte, width*height*3)
		for i := uint32(0); ; i++ {
			_, e := io.ReadFull(p.out, frame)
			if e != nil {
				if e != io.EOF {
					failures <- e
				}
				return
			}
			if send('V', i, frame) != nil {
				return
			}
		}
	}()
	go func() {
		defer readers.Done()
		var index uint32
		e := readOpusPackets(ar, func(packet []byte) error { e := send('A', index, packet); index++; return e })
		if e != nil {
			failures <- e
		}
	}()
	go func() { readers.Wait(); close(records) }()
	started := false
	rc := http.NewResponseController(w)
	for {
		select {
		case <-ctx.Done():
			return
		case e := <-failures:
			cancel()
			p.stop()
			if !started {
				http.Error(w, fmt.Sprintf("decode: %v\n%s", e, p.stderr()), 502)
			} else {
				log.Printf("AV decode: %v", e)
			}
			return
		case record, ok := <-records:
			if !ok {
				e := p.finish()
				select {
				case failure := <-failures:
					e = failure
				default:
				}
				if !started {
					http.Error(w, fmt.Sprintf("no AV records: %v\n%s", e, p.stderr()), 502)
				} else if e != nil {
					log.Printf("AV pipeline: %v: %s", e, p.stderr())
				}
				return
			}
			if !started {
				w.Header().Set("Content-Type", "application/octet-stream")
				if _, e := w.Write(header(0x314d5354, uint32(width), uint32(height), uint32(fps))); e != nil {
					return
				}
				started = true
			}
			if _, e := w.Write(record); e != nil {
				return
			}
			if e := rc.Flush(); e != nil {
				return
			}
		}
	}
}

// Packets can span Ogg pages; a lacing value below 255 ends a packet.
func readOpusPackets(r io.Reader, emit func([]byte) error) error {
	var packet []byte
	count := 0
	for {
		h := make([]byte, 27)
		_, err := io.ReadFull(r, h)
		if err == io.EOF && len(packet) == 0 {
			if count < 2 {
				return fmt.Errorf("missing Opus headers")
			}
			return nil
		}
		if err != nil {
			return err
		}
		if string(h[:4]) != "OggS" || h[4] != 0 {
			return fmt.Errorf("invalid Ogg page")
		}
		if (h[5]&1 != 0) != (len(packet) > 0) {
			return fmt.Errorf("invalid Ogg continuation")
		}
		lacing := make([]byte, int(h[26]))
		if _, err = io.ReadFull(r, lacing); err != nil {
			return err
		}
		for _, n := range lacing {
			if len(packet)+int(n) > 65535 {
				return fmt.Errorf("Opus packet exceeds uint16 length")
			}
			start := len(packet)
			packet = append(packet, make([]byte, int(n))...)
			if _, err = io.ReadFull(r, packet[start:]); err != nil {
				return err
			}
			if n == 255 {
				continue
			}
			switch count {
			case 0:
				if len(packet) < 19 || string(packet[:8]) != "OpusHead" {
					return fmt.Errorf("missing OpusHead")
				}
			case 1:
				if len(packet) < 8 || string(packet[:8]) != "OpusTags" {
					return fmt.Errorf("missing OpusTags")
				}
			default:
				if len(packet) == 0 {
					return fmt.Errorf("empty Opus packet")
				}
				if err = emit(packet); err != nil {
					return err
				}
			}
			count++
			packet = nil
		}
	}
}
