# Canonical animal rig bone names

All animal model prefabs use a single fixed set of bone names (`spine`, `neck`, `head`, `tail_base`, `tail_mid`, `tail_tip`, `front_l_upper`, `front_l_lower`, `front_l_paw`, `front_r_upper`, `front_r_lower`, `front_r_paw`, `rear_l_upper`, `rear_l_lower`, `rear_l_paw`, `rear_l_toe`, `rear_r_upper`, `rear_r_lower`, `rear_r_paw`, `rear_r_toe`). Bones are renamed in each prefab's Transform hierarchy to match these names before the prefab enters Resources/Models/Animal/. `AnimalRigDefinition` uses these as exact-match names and needs no per-model entries.

The alternative was to maintain a growing list of per-model exact names and token fallbacks in `AnimalRigDefinition` for each rig style (Blender Japanese `ボーン.00x`, Rigify `DEF-*`, Tiger `HiRes*`, WildBoar `Front.Lx` / `Back.Lx`, etc.). That approach compounds with every new model and requires code changes whenever a new asset pack is added. Renaming at the prefab layer is safe because `AnimalGesturePose` stores curves against `AnimalGesturePoint` enum values (resolved through `AnimalRigCache` at runtime), not against bone path strings, so no authored animation data breaks when bones are renamed.

## Considered Options

- **Per-model entries in AnimalRigDefinition** — rejected: requires code changes per new model, naming divergence accumulates
- **Token-based fallback matching** — retained only as a last-resort safety net, not as the primary resolution path
- **Runtime bone-name map component per prefab** — rejected: adds a component to every prefab with no benefit once canonical naming is enforced at import time
