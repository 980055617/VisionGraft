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

First implementation of decision 1 used the same `FromToRotation`-based "bend" pattern as the per-joint
limb corrections (compare `SmalRootRestDir` against `bindRotWorld[spine] * bindDirLocal[spine]`). On
`P_GermanShepherd` this produced an unstable, tumbling orientation (`spine.up` drifting through nearly
every direction frame to frame) - confirmed by re-test. Root cause: `FromToRotation` only constrains
where one direction vector ends up; it leaves rotation around that vector (roll) completely
unconstrained. That's an acceptable approximation for a limb segment (roll there is barely visible) but
not for the root's overall body orientation, where roll/up directly determines whether the model looks
upright.

**Fix:** build a full orthonormal basis (forward + up, not just forward) in both SMAL's native frame and
this model's Unity frame, then transport `globalOrient` between them via conjugation - this preserves all
3 rotational degrees of freedom instead of just the bend direction:

```
smalBasis  = LookRotation(SmalRootRestDir, SmalUpAxis)        // SMAL's own Z-up convention, fixed constant
unityBasis = LookRotation(modelForwardLocal, modelUpLocal)    // per-model, leg-position-derived
rootFrameMap     = unityBasis * Inverse(smalBasis)
globalOrientUnity = rootFrameMap * globalOrient * Inverse(rootFrameMap)
tw[0] = camRotation * globalOrientUnity * bindRotWorld[spine]
```

`modelForwardLocal`/`modelUpLocal` are the same leg-position-derived, per-model basis vectors the
existing keypoint-IK root orientation code (`TryApplyAnimalRootOrientation`) already uses - reused here
instead of any single bone's potentially arbitrary local-axis convention, since that's a known, already
load-bearing source of per-model geometry. **Bug found on first re-test (both DogRoot and
P_GermanShepherd now wrong, not just the new model):** initially built `unityBasis` from
`cache.root.TransformDirection(modelForwardLocal/modelUpLocal)`. `cache.root` is the rigRoot/animator
transform, whose WORLD rotation drifts continuously - it's a child of `instanceRoot`, which unrelated
camera/anchor placement code keeps re-orienting every frame. `TransformDirection` pulled that drift
straight into the root-orientation math, so `spine.up` tumbled on every model, not just the new one.
Fix: use `modelForwardLocal`/`modelUpLocal` directly, the same way `TryApplyAnimalRootOrientation`
already does (treat them as fixed, "world-equivalent at the model's own captured bind-time facing"
vectors, captured once in `ResolveAnimalModelBasis` - never re-transform them through a transform whose
rotation changes at runtime).

**Still wrong after that fix, on both models this time - reverted (2026-06-18).** Two from-scratch
re-derivations of joint 0 in a row each introduced a new bug, without first confirming the *simple*
DogRoot-validated formula (`camRotation * globalOrient * SmalDataAxisCorrection(0,90,90) * bindRotWorld[spine]`)
was actually broken for `P_GermanShepherd` rather than something else being wrong. Reverted joint 0 (and
the BONE-MISSING virtual spine joints 1-6, which had picked up the same unvalidated conjugation) to that
exact original form, renamed `canonicalCorrection`/`animalSmalCanonicalCorrectionEuler` to
`SmalDataAxisCorrection` (a `private static readonly` constant, not a per-model or Inspector value -
the working hypothesis is that this constant is a property of the SMAL data/decoding convention, not of
any specific rig, and that whatever is actually wrong on `P_GermanShepherd` lives elsewhere - most
likely `bindRotWorld[spine]` itself, since `DEF-spine.004` sits several bones deep in its own spine
chain, unlike DogRoot's spine bone which *is* the literal hierarchy root). Added a one-time-per-sample
`[SMAL-FK-DBG] MODEL ...` log (bone name, `bindSpineW` Euler angles, `modelForwardLocal`/`modelUpLocal`)
to compare the two models' actual captured bind data directly, instead of re-deriving formulas from
theory again.

**Confirmed via that log + a direct visual comparison:** DogRoot is back to correct (nose-left,
tail-right as originally validated). `P_GermanShepherd` is consistently and *exactly* 180deg yawed -
nose-right, tail-left where it should be the mirror of that - not a true left/right mirror (which a pure
rotation could never fix anyway; rotations preserve handedness). `bindSpineW.euler=(82.3, 180.0, 180.0)`
for `DEF-spine.004` vs DogRoot's near-identity confirms the hypothesis: this bone's own local-forward
axis happens to point toward the tail rather than the head, purely a property of how that specific bone
was rolled when the rig was authored - independent of the SMAL data or `SmalDataAxisCorrection`.

**Fix attempt, didn't move the needle:** detect the per-bone reversal by comparing the spine bone's own
bind-time forward axis (`bindRotWorld[spine] * Vector3.forward`) against `modelForwardLocal`. Added a
`[SMAL-SKIN-CHECK] BASIS` log (front/rear leg center positions, head/tailBase positions) to verify this
numerically before trusting it - found `Vector3.forward` (local Z) was the wrong axis to check at all:
`DEF-spine.004`'s local **Y** axis is the one that points toward the head (`dot ≈ 0.999` against
`modelForwardLocal`), not Z - Blender's convention of "bone length runs along local Y" showing through,
unlike DogRoot's spine bone where Z happens to be the meaningful axis. With the right axis, the bone
*isn't* reversed at all - so this per-bone-roll theory was wrong, and the 180deg symptom has a different
cause.

**Actual cause, found by checking the architecture instead of the math again:** `DEF-spine.004` is one
link in a long chain (`DEF-spine` through `DEF-spine.011`), unlike DogRoot's spine bone which *is* the
hierarchy root. `neck`/`head`/front-leg joints have SMAL-logical parent = virtual joint 6 (the
accumulated, boneless spine-pose chain), but their *real* Unity parent is some other real link further
down `DEF-spine.00X` - not `cache.spine` (`DEF-spine.004`) at all. The per-joint geometric correction was
composing `bindRotLocal[bone]` (relative to that real, untracked parent) on top of `parentTW` (`tw[6]`,
which has no relationship to that real parent's actual bind orientation) - silently wrong on any rig
where the spine isn't a single bone.

**Fix:** stopped relying on any specific real-parent chain at all. `worldFk0` (the same value used to
compute `tw[0]`), left-multiplied directly onto a bone's own `bindRotWorld[bone]`, gives "this bone,
carried rigidly as part of the whole body" - `bindRotWorld[bone]` already encodes that bone's complete
real ancestor chain via Unity's own `bone.rotation`, regardless of how many untracked intermediate bones
sit in between. Replaced `restWorldRot = parentTW * bindLoc` with `restWorldRot = worldFk0 * boneBindWorld`
in the per-joint geometric-correction branch (front/rear leg upper+lower, neck).

**Root (`tw[0]`) was still 180deg off after the parent-chain fix.** Confirmed via a temporary
hardcoded-for-all-models A/B test (extra `Quaternion.Euler(0,180,0)` after `SmalDataAxisCorrection`) that
it really is a simple 180deg yaw offset specific to `P_GermanShepherd`. The auto-detection needed
correcting too: checking `bindRotWorld[spine] * Vector3.forward` (or even `* Vector3.up`) **alone**
against `modelForwardLocal` gave the wrong axis and sign - `SmalDataAxisCorrection` is a real 90+90deg
axis permutation, not a minor decode tweak, so it changes which world axis ends up being the effective
"nose" direction once composed with `bindRotWorld[spine]`. Checking the *full* composed chain
(`SmalDataAxisCorrection * bindRotWorld[spine]) * Vector3.forward`) against `modelForwardLocal` gives
`dot = -0.974` for `P_GermanShepherd` (clearly reversed) vs. the expected positive dot for DogRoot
(already correct). **Fix:** derive a per-model `rootYawFix` from that dot-product sign and fold it into
`rawWorldFk0` right after `SmalDataAxisCorrection`. Logged `rootYawFixApplied`/`forwardDot` in the
`[SMAL-FK-DBG] MODEL` line to make this checkable on future models without re-deriving the formula again.

**That fix flipped DogRoot too (regression) while fixing P_GermanShepherd.** The logged `forwardDot` for
DogRoot came out as `0.000` - not a clean positive signal, just noise-level near-orthogonality between
`SmalDataAxisCorrection * bindRotWorld[spine] * Vector3.forward` and `modelForwardLocal`. A front/rear-leg
position AVERAGE apparently isn't reliably comparable to this specific axis on every rig - it happened to
work for `P_GermanShepherd` only because that case's signal was unusually strong (-0.974), not because
the reference vector choice was sound in general.

**Fix:** swap the reference vector to `cache.bindDirLocal[cache.spine]` (transformed to world via
`bindRotWorld[spine]`) - the SAME registered aim-child-toward-neck direction already validated for the
per-joint leg/neck corrections (`RegisterAnimalAimPairs`/`TryGetBoneCenterDirectionWorld`), instead of the
leg-position-averaged `modelForwardLocal`. This is a direct measurement of which way *this specific spine
bone's* neck-ward side faces, not an indirect proxy - reusing infrastructure already proven to behave
consistently rather than introducing a third geometric heuristic.

**That swap fixed DogRoot but un-fixed P_GermanShepherd** (`forwardDot=+0.133`, no longer negative - fix
no longer applied). `bindDirLocal[spine]`'s aim-child/pivot-center logic
(`TryGetBoneCenterDirectionWorld`) apparently doesn't reduce to a plain spine-to-neck position vector
either - it has its own heuristics (centering on a child's pivot, preferring non-renderer objects, etc.)
that can disagree with the raw geometry for some rigs.

**Fix attempt, same regression pattern again:** `AnimalRigCache.spineToNeckBindDirWorld`, a plain
`cache.neck.position - cache.spine.position` captured once at cache-build time (no aim-child/pivot/center
heuristics at all this time) - and DogRoot landed back on `forwardDot=0.000` regardless. Three different
bind-time geometric references in a row (leg-position average, aim-child-pivot direction, plain position
subtraction) all gave the same noise-level, unusable signal for DogRoot specifically. At this point the
pattern itself is the finding: comparing `SmalDataAxisCorrection * bindRotWorld[spine] * Vector3.forward`
against *any* bind-time-only geometric proxy is not a sound methodology - `globalOrient` (the actual
per-frame pose data) was never part of the check, so it was never actually testing "does the nose point
the right way," only an intermediate quantity with no validated meaning on its own.

**Real fix (2026-06-18):** stopped guessing from bind-time geometry entirely and switched to a direct,
per-frame, data-driven check. The bundle's meta.bin carries a `FLAG_SKELETON` keypoint payload alongside
the SMAL block (independent of it), already decoded into `pose.jointsWorld`/`jointVis` and already used to
compute a real-world body-forward direction for the existing keypoint-IK path
(`AnimalBodyBasisResolver.TryResolveFromJoints`, keyed off pelvis/withers/head-root keypoint indices).
Reused that exact function.

**First version fixed P_GermanShepherd but flipped DogRoot (the same swap, mirrored).** The candidates
compared were `camRotation*globalOrient*SmalDataAxisCorrection*Vector3.forward` (with/without an extra
180deg) against `kpForward` - that quantity has **no model-specific term at all**
(`bindRotWorld[spine]` never enters it), so it's identical for every model given the same frame. It can
only ever apply the same fix everywhere; it happened to match `P_GermanShepherd`'s need while breaking
DogRoot's, the same coincidental-match failure as the very first flat hardcoded A/B test.

**Real fix:** make the comparison genuinely model-specific. `cache.spineToNeckBindDirWorld` (plain
`neck.position - spine.position` at bind time, re-added) is a world vector at bind time. Since
`tw[0] = candidate * bindRotWorld[spine]`, the bone's local "neck axis" is by construction
`Inverse(bindRotWorld[spine]) * spineToNeckBindDirWorld` - so the predicted WORLD neck direction under
any candidate is `candidate * spineToNeckBindDirWorld` (`bindRotWorld[spine]` cancels out algebraically,
but the per-model `spineToNeckBindDirWorld` term doesn't). Compare *that* prediction, per candidate,
against `kpForward`. Decided once per `AnimalSmalRetargetState` and cached.

**`animalSmalCanonicalCorrectionEuler` removed entirely** (Inspector field, `AnimalPoseSettings`,
`AnimalPoseSettingsFactory`, and the corresponding fallback branch in `AnimalSmalFkApplier.cs`) rather
than kept as a fallback. Once `IsAnimalRigReadyForSmalFk` (decision 3) gates entry to `TryApplyAnimalSmalFk`,
spine + all four leg-uppers are always present whenever this code runs, so the fallback condition could
never actually be hit - keeping it would have left a second, never-exercised orientation code path next
to the real one, which is exactly the kind of dead-but-plausible-looking code that misleads the next
person debugging this. Joints without a registered aim-child to derive a real per-joint correction from
yet (paws, head, tail) fall back to carrying the rest pose through (`tw[j] = parentTW * bindLoc`, no
body_pose contribution) rather than reusing any constant - an honest "not yet implemented" rather than a
guess.
