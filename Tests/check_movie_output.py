"""Verify -avaProductionSmoke / -avaMovieEdgeSmoke output with independent MP4 decoders."""
import argparse
from array import array
import json
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument("directory", type=Path)
parser.add_argument("--ffmpeg", default="ffmpeg")
parser.add_argument("--ffprobe", default="ffprobe")
args = parser.parse_args()


def decode(path, *options):
    return subprocess.check_output([args.ffmpeg, "-v", "error", "-i", str(path), *options, "-"])


def streams(path):
    data = subprocess.check_output([args.ffprobe, "-v", "error", "-show_streams",
                                    "-of", "json", str(path)])
    return json.loads(data)["streams"]


movie = args.directory / "dance.mp4"
video, audio = streams(movie)
assert (video["codec_name"], video["nb_frames"], video["avg_frame_rate"]) == ("h264", "60", "30/1")
assert (audio["codec_name"], audio["channels"], audio["sample_rate"]) == ("aac", 2, "48000")

segment, = streams(args.directory / "segment.mp4")
assert (segment["width"], segment["height"], segment["nb_frames"], segment["avg_frame_rate"]) == (320, 240, "10", "60/1")

def pixels(path, first=False):
    limit = ["-frames:v", "1"] if first else []
    return decode(path, *limit, "-map", "0:v", "-pix_fmt", "rgb24", "-f", "rawvideo")


original = pixels(movie)
reopened = pixels(args.directory / "Cold" / "reopened.mp4")
size = video["width"] * video["height"] * 3
assert len(original) == 60 * size and original == reopened, "reopened project changed the rendered animation"
assert original[:size] != original[-size:], "no visible animation"
reference = pixels(args.directory / "dance.mp4.first.png", first=True)
assert sum(abs(x-y) for x,y in zip(original[:size], reference)) / size < 4, "wrong colors or orientation"
assert pixels(args.directory / "ground-on.png", True) != pixels(args.directory / "ground-off.png", True)

pcm = array("h")
pcm.frombytes(decode(movie, "-map", "0:a", "-f", "s16le"))
for channel, hz in enumerate((440, 660)):
    window = pcm[48000 + channel:96000:2]
    crossings = sum(a < 0 <= b for a, b in zip(window, window[1:]))
    assert abs(crossings * 2 - hz) <= 4 and max(window) > 1000, "wrong or silent audio channel"
print("PASS: 60 matching re-opened frames, moving picture, ground shadow, oriented RGB, stereo AAC, silent 60 fps range")
