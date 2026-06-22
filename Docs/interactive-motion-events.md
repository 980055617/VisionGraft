# Interactive Motion Events

Interactive motion events are optional behaviors inserted during stereo video playback to make displayed person and animal tracks feel responsive. They are enabled by default and can be disabled at runtime from the settings panel with the `Motion` toggle. See [docs/adr/0004-interactive-motion-exclusive-handoff.md](adr/0004-interactive-motion-exclusive-handoff.md) for the architectural decision behind this design.

## Core model

- **Exclusive authority**: while an interactive motion event is active for a track, normal pose tracking (the `meta.bin`-driven root placement and FK/IK pipeline) is fully suspended for that track. The event owns the model's root and pose until it ends. There is no per-frame blending with live tracking while an event runs.
- **Animation handoff blend**: when an event ends, a fixed-duration blend (`interactiveHandoffBlendSeconds`, independent of how far the live tracked anchor has drifted during the event) transitions the model's root from the event's final pose back to the live tracked pose.
- Two kinds of event:
  - **Static animation**: freezes the model's root in place and plays an in-place gesture.
  - **Dynamic animation**: a three-phase sequence built on a shared walking-motion primitive — walk-in, a static-animation gesture, walk-back to the position the event started from.
- Two trigger sources:
  - **Random**: scheduled per track at random intervals (`interactiveMotionMinIntervalSeconds` / `interactiveMotionMaxIntervalSeconds`). Picks static or dynamic with a fixed in-code probability (`DynamicEventProbability`).
  - **System (frame-out)**: triggered automatically when a track disappears from the current frame's metadata. Modeled as a dynamic animation with no gesture phase: the model walks away from its last visible position (preserving the height it had at the moment of disappearance) and, when the track's reappearance frame can be predicted from upcoming metadata, walks toward that predicted position so it rejoins tracking smoothly when the subject reappears. If no reappearance can be predicted, it just walks out and waits.
  - The "last visible position" is deliberately not just the literal last frame's data. Inspecting `meta.bin` directly around a real frame-out (an animal track exiting the right edge) showed the detector's bbox collapsing hard for the last several frames before disappearing — the detector is only catching a shrinking sliver at the edge in those frames. `ObserveInteractiveMotionLiveTrackedSample`/`ObserveInteractiveMotionDisplayedRoot` reject a frame from updating the frame-out origin once its bbox area drops below `BBoxQualityMinAreaRatio` (50%) of the last accepted bbox area, so the origin freezes at the last reliable frame instead of one of the corrupted tail frames.
  - The same collapse happens symmetrically on reappearance: the first few frames after a track comes back also have a small/partial bbox before it recovers to full size, so the normal tracked pipeline's bbox-fit placement is briefly wrong right when visibility resumes. `TryStopSystemTriggerOnVisibleFrame` does not hand off the instant the track becomes visible; it keeps running the synthetic frame-out walk until a frame passes the same bbox-quality check (or `MaxFrameInQualityWaitSeconds` elapses, as a safety cap for a track whose bbox never fully recovers), so the handoff blends toward a reliable tracked frame instead of a still-corrupted one.
  - A track can also float *before* it ever frame-outs, while it is still genuinely visible in the metadata (this is not an Interactive Motion code path at all — it happens in the normal tracked pipeline). Checking `meta.bin` showed the bbox's right edge pinned exactly at the frame width for several frames before the track disappeared: the subject was being clipped by the screen edge, so the detector's bbox only covered a shrinking visible sliver, and the bbox's bottom edge (used for `AlignModelToBBoxBottom`) drifted away from the subject's true feet position as that sliver shrank. `ResolveReliableBBoxBottomVEye` in `StreamingStereoVideoPlayer.Playback.partial.cs` freezes the bottom-alignment target at the last reliable (large enough, frame-edge-touching) bbox instead of chasing a collapsed one. This is a core tracked-pipeline fix, independent of `enableInteractiveMotion`.

## Human tracks

- Two separate clip pools, both Humanoid-compatible `AnimationClip` assets:
  - `Human Static Gesture Clips`: played in place during a static animation or the gesture phase of a dynamic animation. Falls back to a simple right-arm wave if none are assigned.
  - `Human Walk Clips`: looped while walking in/back during a dynamic animation.
- Clips are sampled via Playables on the track's child `Animator`, independent of the tracked SMPL pipeline (which never runs while the event is Owned).
- If no clip is assigned for a phase (no static gesture clip, or no walk clip), a small procedural fallback runs instead (`ApplyFallbackHumanWave` / `ApplyFallbackHumanWalk`) so the model doesn't just slide around frozen in whatever pose it was last left in. Both fallbacks rotate from a base local rotation captured once per phase (`InteractiveMotionState.fallbackBoneBaseLocalRotations`) rather than the bone's current rotation, since multiplying an offset onto the current rotation every frame compounds indefinitely.
- `PlayableGraph.Evaluate` can write its own root motion onto the Animator's transform even with `applyRootMotion = false`, especially when the Animator component sits on the same GameObject as the track root rather than a child. The frozen gesture/walk position and the viewer-facing rotation are therefore re-applied via `TrackPlacementWriter` *after* every `ApplyHumanClipPlayback` call, not before — applying it only before evaluating let the clip's own root keyframes silently override the position (visible as a sudden height pop) and orientation (model not facing the viewer).
- An event's origin/freeze position must come from `ObserveInteractiveMotionDisplayedRoot` (the model's actual displayed transform, captured after anchor placement + bbox fit + skeleton placement), not the raw pinhole anchor world position. The raw anchor is frequently well above the model's grounded feet (it is a tracked keypoint, not a floor position), so freezing or originating a walk from it pops the model upward.
- `Interactive Handoff Blend Seconds`: fixed duration for the end-of-event blend back to tracking.
- `Human Approach Stop Distance Meters` / `Human Walk Speed Meters Per Second`: how close to the viewer a dynamic walk-in stops, and how fast the model walks.

- The handoff blend covers the humanoid bone pose too, not just the root: every bone's local rotation is captured once when the handoff starts and Slerped toward whatever the tracked pipeline writes that frame, in lockstep with the root position/rotation Lerp/Slerp. Blending only the root left the body pose snapping instantly into the tracked pose the moment the event ended, while only the outer root transform caught up smoothly — visually two disconnected transitions instead of one.

## Animal tracks

- No dedicated walk gait yet: the root translates along the walk path while the previously captured real pose (joints / animal control targets, or SMAL pose) is rigidly carried along and re-solved through the same `AnimalPoseApplier` IK every frame. This gives a coherent silhouette while sliding, but legs do not cycle. This is a known v1 limitation — see the ADR.
- The rigid remap (`RemapAnimalPoseRigid`) only rotates the cached pose by the *yaw* difference between the captured base rotation and the new root rotation, never the full 3D delta. The captured base rotation routinely carries pitch/roll from the animal's natural body tilt (e.g. head down while walking); rotating the whole cached point cloud by a delta that also removes that tilt shifts every point's height around the pivot, which showed up as the model rising during a walk/frame-out.
- `AnimalPoseApplier.Apply` re-aligns and low-pass-filters the root from the solved bone placement as part of its normal tracked-pipeline behavior, which drifts away from the intended walk/freeze height when fed a synthetic remapped pose instead of fresh tracking data. `ApplyAnimalPoseRequest` re-pins the root to the intended position/rotation immediately after calling `Apply`, every frame — this only rigidly carries the already-solved limb/head/tail pose along with the corrected root, it does not undo their solving.
- Static-animation gestures reuse the existing presets, now run with the root frozen:
  - `FaceViewer`: turns the cached pose's root yaw toward the viewer. Used whenever the cached pose came from SMAL data (no control-target gesture is available in that case).
  - `BodyTurnViewer`, `TailWag`, `PawWave`: available when the cached pose has animal control targets.
- `Animal Approach Stop Distance Meters` / `Animal Walk Speed Meters Per Second`: same role as the Human fields.

## Implementation map

- `StreamingStereoVideoPlayer.InteractiveMotion.partial.cs`: state machine (stage/kind/trigger/phase), scheduling, walk-phase resolution, Human clip playback, Animal pose caching + rigid remap + gesture presets, handoff blend.
- `InteractiveMotionSchedule.cs`: pure random-trigger decision logic (unit tested in `Assets/Editor/Tests/InteractiveMotionScheduleTests.cs`).
- `AnimalFrameOutMotion.cs`: shared walking-motion math — a one-way eased segment (used by walk-in/walk-back) and a there-and-back loop with a predicted endpoint (used by the system frame-out trigger).
- `StreamingStereoVideoPlayer.Playback.partial.cs`: `ApplyMetaTarget` gates the normal tracked pipeline behind `TryApplyOwnedInteractiveMotion`, and applies `ApplyInteractiveHandoffBlendIfActive` after it; `TryApplyInteractiveSystemTriggerTrack` handles tracks missing from the current frame.
- `StreamingStereoVideoPlayer.PosePipeline.partial.cs`: unchanged tracked FK/IK pipelines, plus a single hook (`CacheLiveAnimalPoseForInteractiveMotion`) that snapshots the real Animal pose whenever tracking actually runs, for later reuse by Owned-stage events.
- `StreamingStereoVideoPlayer.UI.*`: the runtime `Motion` ON/OFF toggle (unchanged).
- `InteractiveMotionDebugTools.cs` (Editor-only): force-trigger menu items, see "Testing" above.

## Testing

Random-triggered events fire on an unpredictable interval (`interactiveMotionMinIntervalSeconds`/`MaxIntervalSeconds`) with a random static/dynamic split (`DynamicEventProbability`), which makes visual verification during Play Mode slow. Two Editor-only menu items bypass the random schedule and force a specific kind immediately:

- `VisionGraft > Interactive Motion > Force Static (All Active Tracks)`
- `VisionGraft > Interactive Motion > Force Dynamic (All Active Tracks)`

Both call `StreamingStereoVideoPlayer.DebugForceInteractiveMotion(bool dynamicKind)` (`StreamingStereoVideoPlayer.InteractiveMotion.partial.cs`) for every `StreamingStereoVideoPlayer` in the scene. Behavior:

- Only Play Mode; only the random-trigger code path (`InteractiveTriggerSource.Random`). The system frame-out trigger is a separate path and is not covered by this tool.
- Applies to every currently active Human/Animal track at once (no per-track picker).
- Respects the same rules a real random trigger would: a track already mid-event (`InteractiveEventStage != Inactive`) is skipped rather than interrupted, and nothing fires while the runtime `Motion` toggle (`enableInteractiveMotion`) is off — so forced triggers stay consistent with what could happen in production, they just remove the wait.

## Asset guidance

For Human clips, prefer Humanoid-compatible assets. AI-generated or downloaded FBX animations should be imported as Humanoid and checked against the model avatar before assigning them. Keep walk clips and static gesture clips in separate folders/arrays — see `Assets/Animations/InteractiveMotion/README.md`.

For Animal animation, the existing presets are the only gesture option until a real walk-gait or model-specific clip system is built.
