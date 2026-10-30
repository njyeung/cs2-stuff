#!/usr/bin/env python3
"""Convert the source clip to the embedded 96x54, 15 FPS RGB frame stream."""
from pathlib import Path
import gzip
import struct
import subprocess

root = Path(__file__).resolve().parents[1]
width, height, fps = 96, 54, 15
frames = subprocess.check_output([
    'ffmpeg', '-v', 'error', '-i', str(root / 'rickroll.webm'),
    '-an', '-vf', f'fps={fps},scale={width}:{height}:flags=area',
    '-f', 'rawvideo', '-pix_fmt', 'rgb24', 'pipe:1',
])
frame_size = width * height * 3
assert frames and len(frames) % frame_size == 0
count = len(frames) // frame_size
output = root / 'TheatreScreen/Media/rickroll.rgb.gz'
output.write_bytes(gzip.compress(struct.pack('<4s4i', b'TSV1', width, height, fps, count) + frames, mtime=0))
print(f'{count} frames, {count / fps:.1f} seconds, {output.stat().st_size:,} bytes embedded')
