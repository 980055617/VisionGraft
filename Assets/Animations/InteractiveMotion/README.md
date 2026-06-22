# Interactive Motion Animation Assets

Put imported Human animation FBX or AnimationClip assets here.

Recommended layout:

- `Human/Static/`: Humanoid-compatible FBX or `.anim` clips played in place during a static animation or the gesture phase of a dynamic animation (wave, idle gesture, appeal pose).
- `Human/Walk/`: Humanoid-compatible walk/run cycle clips played while the model is walking in or back during a dynamic animation.
- `Animal/`: Reserved for future model-specific animal clips. Current animal behavior is code preset based (root motion only, no dedicated walk gait yet).

After importing a Human FBX:

1. Select the FBX in Unity.
2. In `Rig`, set `Animation Type` to `Humanoid`.
3. Check that Unity can configure the Avatar.
4. In `Animation`, split or rename clips if needed.
5. Assign the resulting clips to `StreamingStereoVideoPlayer.humanStaticGestureClips` or `humanWalkClips` depending on which folder they belong in.

Good first Human clips:

- `Static/`: wave, idle gesture, appeal pose
- `Walk/`: walk forward, walk backward
