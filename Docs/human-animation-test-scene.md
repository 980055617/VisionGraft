# Human Animation Test Scene

Use this scene to verify Human animation clips before connecting them to stereo video playback.

## Why This Exists

The playback scene combines video tracking, SMPL24 pose application, model placement, and interactive motion events. If an animation does not move there, it is hard to know whether the issue is the clip import, the model Avatar, or the playback override.

This test scene isolates the animation clip and the Human model.

## Create The Scene

In Unity:

1. Open the top menu: `VisionGraft > Interactive Motion > Reimport Human Clips As Humanoid`.
2. Then run: `VisionGraft > Interactive Motion > Create Animation Test Scene`.
3. Unity creates `Assets/Scenes/InteractiveMotionAnimationTest.unity`.
4. Press Play.

Controls:

- `Right Arrow`: next clip.
- `Left Arrow`: previous clip.
- `Space`: replay current clip.

The scene uses:

- `Assets/Models/character.fbx`
- clips under `Assets/Animations/InteractiveMotion/Human`
- `HumanAnimationClipTester`

## What To Check

If the model moves in this scene:

- The downloaded animation is usable.
- The issue is in the stereo playback integration layer.

If the model does not move:

- Select each FBX under `Assets/Animations/InteractiveMotion/Human`.
- In `Rig`, confirm `Animation Type` is `Humanoid`.
- Confirm Unity does not show Avatar mapping errors.
- In `Animation`, confirm there is a non-empty clip with a visible length.

## Current Known Risk

The first imported FBX files were initially Generic (`animationType: 2`) while `character.fbx` is Humanoid (`animationType: 3`). Generic clips do not retarget cleanly onto the Humanoid character, so the editor tools force Human interactive FBX files to import as Humanoid.
