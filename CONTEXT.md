# Context

This file records the domain language and project-specific concepts for VisionGraft.

## Glossary

- **Animal pose**: The per-frame animal skeleton state after camera/screen coordinates have been converted into world coordinates.
- **Animal pose applier**: The module that applies an animal pose to a Unity model by handling root placement, root orientation, limb solving, smoothing, rig cache state, and bone rotation.
- **Human pose reconstruction**: The per-frame process of turning tracked Human keypoints into a model pose, including root placement, body orientation, limb positions, and local bone orientation. Keypoint-only reconstruction can match joint positions while still losing palm direction, limb twist, and other orientation details.
- **Human SMPL motion**: Per-frame Human motion data that carries SMPL root orientation and local joint rotations in addition to shape and translation parameters. It is the preferred source for Human local bone orientation when available.
- **Human SMPL root placement**: Human root placement preserves the playback scene's tracked root, tracked orientation, and display-depth policy. SMPL translation and root orientation are not treated as authoritative unless their camera space is calibrated to the bundle placement space.
- **Interactive motion event**: A user-toggleable, time-bounded behavior inserted during playback to make a tracked model feel responsive. It can either overlay the current tracked pose for local gestures or replace tracking temporarily for authored actions such as approaching the viewer.
- **Model scale lock**: Human and animal model size is chosen once when a track's model is first placed, then reused for that track while later frames update position, depth, rotation, and pose without changing size.

## Notes

Use `docs/adr/` for architectural decisions that should be preserved over time.
