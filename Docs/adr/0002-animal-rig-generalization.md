# Animal SMAL FK rig generalization across multiple models

**Status:** accepted

Animal SMAL FK was validated end-to-end on `DogRoot` (2026-06-16〜18, see ADR-0001), but applying the
same pipeline to a second model (`P_GermanShepherd`, RSG_DogsPack asset, Blender Rigify-style `DEF-*`
bone names) produced a front-back-flipped orientation. The SMAL pose feed is species-agnostic — dog, cat,
and horse subjects all produce the same 35-joint topology — so the real scope is "support many different
Unity rigs/models," not "support many different animal species' joint layouts."

Root cause: per-joint bend corrections (front/rear leg upper+lower, neck) already auto-derive per model,
by comparing the SMAL rest skeleton's bone direction against this exact Unity bone's own bind-time
direction (`Quaternion.FromToRotation`, see ADR-0001's 2026-06-18 entries). Only the **root/global_orient**
correction was still a single hand-tuned constant (`animalSmalCanonicalCorrectionEuler`), calibrated by
eye against `DogRoot`'s own bind pose — it does not generalize to a model with a different rest
orientation.

## Decisions

1. **Root orientation correction becomes per-model and auto-derived**, using the same SMAL-rest-vs-Unity-bind
   geometric comparison already used for per-joint bends (spine's registered aim child is `neck`, so the
   data needed already exists). Falls back to the existing keypoint-derived `modelForwardLocal` heuristic
   if the rest-skeleton comparison can't be computed for a given model.
2. **Bone identification stays name-based first** (`AnimalRigDefinition` exact names/tokens), with a
   position/topology-based fallback to be added only for rigs where name matching fails outright (e.g. a
   horse rig with fully arbitrary bone names). Not implemented yet — see "Next" below.
3. **Minimum confidence bar for enabling SMAL FK on a model:** spine plus all four leg-upper bones
   (front-left/right, rear-left/right) must be identified with confident front/rear and left/right
   classification. Below that bar, SMAL FK is disabled for that model and the existing keypoint-IK
   pipeline is used instead — a partially-wrong FK (e.g. front/rear legs swapped) is worse than the
   existing IK fallback.
4. **Sequencing:** before building the position/topology fallback, diagnose how far name-based matching
   alone gets on `P_GermanShepherd` (it already mostly uses the `DEF-*` convention `AnimalRigDefinition`
   partially anticipates) via the same one-time bone-resolution diagnostic log used for `DogRoot`
   (`[SMAL-SKIN-CHECK]`-equivalent). Only build the positional fallback for whatever name matching can't
   cover, to avoid building unneeded infrastructure.

## Outcome (2026-06-18)

Ran the `[SMAL-SKIN-CHECK]` bone-resolution diagnostic against `P_GermanShepherd`: name-based matching
alone resolved spine, head, neck, and all four leg-upper/lower/paw bones correctly (only `tailBase`/
`tailMid`/`tailTip` came back null - this rig has no tail bones at all, not a matching failure). This
clears decision 3's confidence bar with name matching alone, so **decision 2's positional fallback was
not needed for this model** and was not implemented - confirms the sequencing in decision 4 was the right
call (it would have been wasted work).

Implemented decision 1 in `AnimalSmalFkApplier.cs`: `tw[0]`'s correction is now derived per model by
comparing `SmalRootRestDir` (SMAL rest skeleton's root->Neck direction) against this model's actual
Unity bind direction (`bindRotWorld[spine] * bindDirLocal[spine]`, where `bindDirLocal[spine]` already
points at the registered aim child `neck`), the same `FromToRotation` conjugation pattern as the
per-joint bend corrections. Falls back to the manual `canonicalCorrectionEuler` constant only when a
model has no spine bind data to compare against.
