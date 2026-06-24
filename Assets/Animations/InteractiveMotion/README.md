# Interactive Motion Animation Assets

Put imported Human animation FBX or AnimationClip assets here.

Recommended layout:

- `Human/Static/`: Humanoid-compatible FBX or `.anim` clips played in place during a static animation or the gesture phase of a dynamic animation (wave, idle gesture, appeal pose).
- `Human/Walk/`: Humanoid-compatible walk/run cycle clips played while the model is walking in or back during a dynamic animation.
- `Animal/Static/` and `Animal/Walk/`: `AnimalGesturePose` data assets (not AnimationClips - there is no Humanoid-style retargeting for animal rigs, so a real mocap clip only plays back correctly on the exact model it was authored for). Each asset defines per-point rotation curves (root/head tip/tail tip/each paw) applied by the shared `AnimalGesturePosePlayer` as an additive overlay *after* `AnimalPoseApplier` has placed the bone for this frame - this runs the same way regardless of whether the frame's pose came from SMAL or animal control targets, so one asset works on any model `AnimalRigDefinition` can resolve and on any bundle. Create via `Assets > Create > VisionGraft > Animal Gesture Pose`.
  - `Static/`: played once during a static animation or the gesture phase of a dynamic animation (curve time runs 0→1 once over `duration`).
  - `Walk/`: looped while walking in/back during a dynamic animation (curve time runs 0→1 repeatedly, wrapping every `duration` seconds) - this is what turns the otherwise rigid walk/walk-back translation into a leg cycle.

After creating an `AnimalGesturePose`:

1. Add one `AnimalGesturePointCurve` entry per point you want to move. For legs, prefer the `...Upper` (thigh) point over `...Paw`: rotating the upper leg swings the whole leg rigidly and reads as an actual stride, where rotating just the paw (a leaf bone with little mesh hanging below it on some models) barely moves anything visible. Add the matching `...Lower` point with a clamped-positive curve to bend the knee during the forward-swing half of the stride, so the foot visibly lifts instead of dragging.
2. Author `right`/`up`/`forward` curves over `[0, 1]` gesture time; values are **degrees of additive local rotation** around that bone's own right/up/forward axes, applied on top of whatever pose it already has this frame. Bone-local axis conventions are not guaranteed consistent across models (see "Current Risk Notes" in `Docs/DogMetaBoneMapping.md`) - on the current dog rig, `right` is confirmed to swing a leg fore-aft and `forward` is confirmed for head/tail motion; re-check with `VisionGraft > Interactive Motion > Create Animal Leg Axis Calibration Asset` (`AnimalGestureSampleTools.cs`) before assuming the same axes hold on a different model. For a `Walk/` clip, make sure the curve's value at `t=0` matches its value at `t=1` so the loop doesn't pop.
3. Set `duration` to how long the gesture (or one walk cycle) should run.
4. Leave `animalStaticGestureClips` / `animalWalkClips` empty on `StreamingStereoVideoPlayer` to auto-assign everything found in the matching folder, or assign specific assets manually.

This applies on top of the bone placement `AnimalPoseApplier` already did this frame (SMAL FK or keypoint/control-target IK), so unlike the existing `TailWag`/`PawWave`/`BodyTurnViewer` presets (which still only apply pre-FK, when the cached pose has animal control targets), `AnimalGesturePose` gestures and walk cycles work on every animal track regardless of pose source. See "Animal tracks" in `Docs/interactive-motion-events.md`.

After importing a Human FBX:

1. Select the FBX in Unity.
2. In `Rig`, set `Animation Type` to `Humanoid`.
3. Check that Unity can configure the Avatar.
4. In `Animation`, split or rename clips if needed.
5. Assign the resulting clips to `StreamingStereoVideoPlayer.humanStaticGestureClips` or `humanWalkClips` depending on which folder they belong in.

Good first Human clips:

- `Static/`: wave, idle gesture, appeal pose
- `Walk/`: walk forward, walk backward
