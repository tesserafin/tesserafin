# Playback chain proof

Drives a real server and real FFmpeg processes with synthetic media. Disposable state only.

```sh
export PCR_ROOT=/some/empty/dir PCR_REAL_FFMPEG=/path/to/tesserafin-ffmpeg PCR_FFMPEG=$PWD/ffwrap
# The server looks for ffprobe beside the ffmpeg it was given: link the real one next to ffwrap.
ln -s "$(dirname "$PCR_REAL_FFMPEG")/ffprobe" ffprobe
# Under $PCR_ROOT/media/movies/<Name (2023)>/<Name (2023)>.<ext>, generated with ffmpeg lavfi:
#   Direct Mp4 (H.264/AAC, mp4), Remux Mkv (same streams, mkv), Remux Avi (H.264/AAC, avi),
#   Transcode Mpeg2 and Second Mpeg2 (MPEG-2/MP2, mkv, 15 min)
./serve.sh <server bin dir> $PCR_ROOT/state 18197 &
PCR_BASE=http://127.0.0.1:18197 PCR_STATE=$PCR_ROOT/state python3 delivery_matrix.py delivery.json
PCR_BASE=http://127.0.0.1:18197 PCR_STATE=$PCR_ROOT/state python3 lifecycle_matrix.py lifecycle.json
```

`delivery_matrix.py` compares decision, descriptor, ffprobe of the served bytes and the encoder FFmpeg ran. `lifecycle_matrix.py` covers credential combinations, stops, isolation, old links, seek, retry, client loss and temporary files. `ffwrap` paces HLS jobs to real time, and fails one job start while `$PCR_ROOT/FAILNEXT` exists.

`hevc_main10.py` independently provisions a native disposable server and synthetic
SDR/HDR10/PGS fixtures. It checks Main10 and Main HLS output, the eight-bit ceiling,
HDR10-to-SDR tone mapping, PGS burn-in, distant-segment seek and live-job stop.
It uses only the supplied FFmpeg and its sibling ffprobe. See
[software HEVC Main10](../../docs/software-hevc-main10.md) for invocation and the
capability policy.
