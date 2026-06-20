# Normal mode plays source/pre_removal_stereo_video.mp4 directly

The project rule has been "`source/*` is debug/verification only, never used for runtime placement." Normal mode is a new playback mode that plays `source/pre_removal_stereo_video.mp4` as the actual on-screen video during runtime, which looks like it breaks that rule.

It doesn't: the rule is about placement/pose data (subjects must be positioned only from `meta.bin` + `manifest.json`). `pre_removal_stereo_video.mp4` is consumed purely as a video stream — normal mode shows no replaceable models, proxy boxes, or pose-driven content at all — so no placement decision is ever derived from a `source/*` file. We picked direct playback over re-deriving an equivalent video from `meta.bin` because there is no such equivalent: `meta.bin` carries tracking metadata, not pixels.

`source/pre_removal_stereo_video.mp4` is treated as Optional (older bundles may omit it); the mode-toggle UI disables itself when the file is absent.
