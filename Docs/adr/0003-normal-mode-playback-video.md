# Normal mode plays source/pre_removal_stereo_video.mp4 directly

The project rule has been "`source/*` is debug/verification only, never used for runtime placement." Normal mode is a new playback mode that plays `source/pre_removal_stereo_video.mp4` as the actual on-screen video during runtime, which looks like it breaks that rule.

It doesn't: the rule is about placement/pose data (subjects must be positioned only from `meta.bin` + `manifest.json`). `pre_removal_stereo_video.mp4` is consumed purely as a video stream — normal mode shows no replaceable models, proxy boxes, or pose-driven content at all — so no placement decision is ever derived from a `source/*` file. We picked direct playback over re-deriving an equivalent video from `meta.bin` because there is no such equivalent: `meta.bin` carries tracking metadata, not pixels.

`source/pre_removal_stereo_video.mp4` is treated as Optional (older bundles may omit it); the mode-toggle UI disables itself when the file is absent.

## Codec requirement

Normal mode swaps `VideoPlayer.url` to this file at runtime, so it has to decode through the same
Android/Quest MediaCodec path as `video.mp4`: **H.264** (H.265/VP8/VP9 are the other supported
options). A bundle whose pre-removal video is `mpeg4` / `mp4v` (MPEG-4 Part 2 — what OpenCV
`VideoWriter_fourcc(*'mp4v')` and ffmpeg's mpeg4 encoder produce) shows a **black screen** on the
headset: prepare never yields a decodable frame, `frameReady` stops firing, and the
`renderMode = APIOnly` screen material keeps its now-destroyed texture.

Every bundle in `Assets/StreamingAssets` as of 2026-08-07 (`bundle`, `bundle_human`,
`bundle_animal`, `bundle_train`) had `video.mp4` as h264/libx264 but
`source/pre_removal_stereo_video.mp4` as mpeg4/mp4v — same 2560x640 / 30fps / 2167 frames / AAC
audio, only the video codec differed. Both videos must come out of the same encoder settings on the
bundle-generation side.

Check a bundle before shipping it:

    ffprobe -v error -select_streams v:0 -show_entries stream=codec_name,codec_tag_string -of csv=p=0 pre_removal_stereo_video.mp4
    # expected: h264,avc1     NG: mpeg4,mp4v

`VideoPlayer.errorReceived` is now subscribed and logs `[Video] error: ...`, and mode switches log
`[Mode] switch ...` / `[Mode] prepared ...`, so this class of failure is visible in logcat instead
of being silent.
