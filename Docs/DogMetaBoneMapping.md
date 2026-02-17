# Dog Meta-Bone Mapping (categoryId=2)

This table is based on the current in-repo dog asset at `Assets/Prefabs/DogRoot.prefab` and the runtime mapping in `StreamingStereoVideoPlayer.Model.cs`.

Important:
- `Assets/Prefabs/DogRoot.prefab` is a split `MeshRenderer` rig and does not contain `SkinnedMeshRenderer` entries.
- `arm.xxx / foot.xxx / head.001 / neck / body / er.xxx` are mesh node names.
- Runtime should drive their parent rig bones.

## Parent Bone Resolution (from mesh node parent)

| Mesh node | Parent rig bone |
|---|---|
| `body` | `ボーン` |
| `neck` | `ボーン.007` |
| `head.001` | `ボーン.009` |
| `er.L` | `ボーン.009_L.001` |
| `er.R` | `ボーン.009_R.001` |
| `arm.001.L` | `ボーン_L.001` |
| `arm.002.L` | `ボーン_L.002` |
| `arm.003.L` | `ボーン_L.003` |
| `arm.001.R` | `ボーン_R.001` |
| `arm.002.R` | `ボーン_R.002` |
| `arm.003.R` | `ボーン_R.003` |
| `foot.001.L` | `ボーン.001_L.001` |
| `foot.002.L` | `ボーン.001_L.002` |
| `foot.003.L` | `ボーン.001_L.003` |
| `foot.001.R` | `ボーン.001_R.001` |
| `foot.002.R` | `ボーン.001_R.002` |
| `foot.003.R` | `ボーン.001_R.003` |

## Meta Joint (0-19) to Runtime Use

| Meta idx | Semantic (current code interpretation) | Driven parent bone(s) |
|---|---|---|
| 0 | Left eye | Head aim synthesis only (`head`, `neck`) |
| 1 | Right eye | Head aim synthesis only (`head`, `neck`) |
| 2 | (unused in current dog mapping) | - |
| 3 | (unused in current dog mapping) | - |
| 4 | Nose | `ボーン.007` + `ボーン.009` via `5 -> 4` |
| 5 | Throat | `ボーン.007` + `ボーン.009` via `5 -> 4` |
| 6 | Rear hub / tail base (hip side) | `ボーン` via `6 -> 7`; rear leg chains origin |
| 7 | Front hub / withers | `ボーン` via `6 -> 7`; front leg chains origin |
| 8 | Left front upper distal joint | `ボーン_L.001` via `7 -> 8` |
| 9 | Right front upper distal joint | `ボーン_R.001` via `7 -> 9` |
| 10 | Left rear upper distal joint | `ボーン.001_L.001` via `6 -> 10` |
| 11 | Right rear upper distal joint | `ボーン.001_R.001` via `6 -> 11` |
| 12 | Left front lower distal joint | `ボーン_L.002` via `8 -> 12` |
| 13 | Right front lower distal joint | `ボーン_R.002` via `9 -> 13` |
| 14 | Left rear lower distal joint | `ボーン.001_L.002` via `10 -> 14` |
| 15 | Right rear lower distal joint | `ボーン.001_R.002` via `11 -> 15` |
| 16 | Left front paw tip | `ボーン_L.003` via `12 -> 16` |
| 17 | Right front paw tip | `ボーン_R.003` via `13 -> 17` |
| 18 | Left rear paw tip | `ボーン.001_L.003` via `14 -> 18` |
| 19 | Right rear paw tip | `ボーン.001_R.003` via `15 -> 19` |

## Current Risk Notes

- This mapping aligns naming and chain assignment, but does not fix per-bone local axis mismatch by itself.
- If limb twist remains unstable, the next step is DCC-side rig normalization (consistent bone roll/forward axis and deformation-bone separation).
