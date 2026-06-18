# Context

This file records the domain language and project-specific concepts for VisionGraft.

## Glossary

- **Spatial video bundle**: A playback package that carries the video, frame metadata, and optional sidecars needed to place tracked subjects in Unity space.
- **Playback video**: The visual media stream shown during playback. It may be monocular, stereo, or spatial-video-derived, but it is distinct from the metadata used to place subjects.
- **Frame metadata**: Per-frame tracking data for visible objects, including category, track identity, image-space bounds, anchor depth, and optional skeleton joints.
- **Sidecar**: Optional per-frame data that enriches frame metadata without changing the playback video, such as animal control targets, other-object proxies, or Human SMPL motion.
- **Track**: A stable identity for one observed subject across playback frames. One track may disappear and later reappear while keeping the same identity.
- **Playback subject**: A tracked thing in the video that may be represented in Unity as a model or proxy. Current subject categories include Human, Animal, and Other.
- **Track instance**: The Unity representation of a track during playback. A track instance may use a replaceable model, a proxy, or a rigged character depending on the subject category and available data.
- **Replaceable model**: A Unity model assigned to stand in for a playback subject. Its placement follows the track while its size is locked according to the model scale lock policy.
- **Display screen**: A Unity surface that shows one eye or view of the playback video and provides the reference plane for image-space placement.
- **Stereo screen pair**: The pair of display screens used to present left and right eye views of stereo or spatial-video-derived playback.
- **Pinhole placement space**: The camera-like coordinate space used to convert image pixels and depth into Unity world positions for screens, subjects, and poses.
- **Playback anchor**: The image-space point plus depth that locates a playback subject in pinhole placement space.
- **Animal pose**: The per-frame animal skeleton state after camera/screen coordinates have been converted into world coordinates.
- **Animal pose applier**: The module that applies an animal pose to a Unity model by handling root placement, root orientation, limb solving, smoothing, rig cache state, and bone rotation.
- **Animal control targets**: Bundle-provided rig-friendly animal targets for root, head, tail, paws, and body hints. They are preferred over direct animal keypoint-to-bone mapping when present.
- **Animal pose preset**: A reusable animal interaction behavior that modifies tracked animal pose targets, such as head, tail, paw, or root motion, instead of playing a model-specific animation clip.
- **Human pose reconstruction**: The per-frame process of turning tracked Human keypoints into a model pose, including root placement, body orientation, limb positions, and local bone orientation. Keypoint-only reconstruction can match joint positions while still losing palm direction, limb twist, and other orientation details.
- **Human SMPL motion**: Per-frame Human motion data that carries SMPL root orientation and local joint rotations in addition to shape and translation parameters. It is the preferred source for Human local bone orientation when available.
- **Human SMPL orientation overlay**: When Human SMPL motion is available, its local joint rotations may add orientation detail that tracked keypoints cannot express, such as hand rotation and limb twist. Keypoint IK remains the gross pose authority for matching visible joint positions until SMPL-to-Humanoid retargeting is calibrated well enough to drive the whole body directly.
- **Human SMPL root placement**: Human root placement preserves the playback scene's tracked root, tracked orientation, and display-depth policy. SMPL translation and root orientation are not treated as authoritative unless their camera space is calibrated to the bundle placement space.
- **Human root orientation**: Human root orientation remains the tracked orientation resolved from playback metadata rather than SMPL `globalOrient`, while SMPL local joint rotations may still drive the child bone pose.
- **Animal SMAL motion**: Per-frame animal motion data carrying SMAL root orientation, 34 joint pose rotations, shape parameters, and translation. When present in a bundle frame (FLAG_SMAL), it is the preferred source for animal bone orientation over joint-position IK. The 35-joint SMAL topology is species-agnostic: dog, cat, and horse subjects all produce the same joint layout, so the rig-mapping problem is about supporting many different Unity models/rigs (bone names, proportions, rest poses), not many different joint topologies.
- **Animal SMAL FK**: The forward-kinematics reconstruction applied to the animal rig using SMAL rotation matrices. Uses the same standard FK formula as Human SMPL FK: `tw[j] = parentTW * bindRotLocal[j] * pose[j]`. When SMAL FK is active, TwoBone IK is skipped entirely.
- **Animal SMAL root**: SMAL joint 0 (root) drives the spine bone world orientation. Joints 1–6 (pelvis0…spine3) are virtual spine joints with no Unity bone; they accumulate the FK chain so front-leg and neck branches inherit the correct spine-top rotation. Rear-leg and tail branches inherit from joint 0 directly, matching the SMAL parent array.
- **Interactive motion event**: A user-toggleable, time-bounded behavior inserted during playback to make a tracked model feel responsive. It can either overlay the current tracked pose for local gestures or replace tracking temporarily for authored actions such as approaching the viewer.
- **Runtime controls**: In-playback controls that let a viewer adjust playback, screen placement, or selected track orientation without leaving the Unity scene.
- **XR interaction surface**: The input-facing surface aligned with a display screen so XR rays or tracked-device pointers can interact with playback UI and screen-adjacent controls.
- **Other-object proxy**: A simple spatial proxy for a tracked subject that is not treated as a Human or Animal.
- **Model scale lock**: Human and animal model size is chosen once when a track's model is first placed, then reused for that track while later frames update position, depth, rotation, and pose without changing size.

## Notes

Use `docs/adr/` for architectural decisions that should be preserved over time.
