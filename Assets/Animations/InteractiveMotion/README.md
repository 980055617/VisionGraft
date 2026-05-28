# Interactive Motion Animation Assets

Put imported Human animation FBX or AnimationClip assets here.

Recommended layout:

- `Human/`: Humanoid-compatible FBX or `.anim` clips for person tracks.
- `Animal/`: Reserved for future model-specific animal clips. Current animal behavior is code preset based.

After importing a Human FBX:

1. Select the FBX in Unity.
2. In `Rig`, set `Animation Type` to `Humanoid`.
3. Check that Unity can configure the Avatar.
4. In `Animation`, split or rename clips if needed.
5. Assign the resulting clips to `StreamingStereoVideoPlayer.humanInteractiveClips`.

Good first Human clips:

- wave
- walk forward
- step forward and wave
- idle gesture
