using System.Collections.Generic;
using UnityEngine;

// 人の自分から動く動作のつながり（2026-10-25、調査 O2。scratchpad validate/obvious_inv/O2）。4 つの切り替え:
//
// 1. humanTrackingRestoresHipsLocalPosition（既定 ON、既存の不具合の直し）
//    追従は Hips の回転しか書かず（ResetHumanoidLocalRotations・FK）、配置は「Hips が目標に来るよう root を動かす」
//    （AlignHumanoidHipsToSmplRoot → TrackPlacementWriter.ApplyAnchorPosition）。一方、人のクリップ（Animator は Hips の局所位置も書く）と、
//    終わりの受け渡しで Hips を world で書く混ぜ（humanMotionKeepsBodyInPlace、ApplyInteractiveHandoffBlendIfActive）は Hips の局所位置を
//    変えたまま残す。混ぜは毎 tick「前の tick の混ぜの残り」の上に重なるので、1 回の受け渡しで（受け渡しの起点の体 − 追従の体）の
//    約 15.7 倍（0.45 s・72 Hz、Σ(1−t)）だけ局所位置がずれ、追従はそれを root を動かして打ち消す。体の見た目は変わらないが root だけが体から
//    離れる（テレビのゾンビは 2 周目の f101 の動作の後に 1.19 m、視聴者の後ろへ）。次の動作は root から視聴者の向き・歩ける距離・行き先を
//    決めるので、背中を向けて廊下の奥へ歩き、walk_back の 180° の向き替えで体（root の向きの座標で持つ差）が 2.4 m 振れて消えた。
//    直し: クリップが初めて Hips に触る直前の追従の局所位置を控え（それまで追従は局所位置を書かないので、その値は一定。初めての動作の前の
//    追従はまったく変わらない）、追従の tick の頭で毎回その値へ戻す。追従で描く体は変わらず、root だけが動作の前の関係へ戻る。
//
// 2. humanClipSwitchBlend（既定 OFF、新しい振る舞い）
//    人のクリップへ切り替える所（動作の始め・walk_in → gesture・gesture → walk_back・その場の動作の途中のモデルの差し替え）で、切り替えた tick に
//    描いていた体（骨の局所の回転と Hips の world の位置・向き）から interactiveHandoffBlendSeconds（シーン値 0.45 s）かけて新しいクリップへ
//    混ぜる（終わりの受け渡しと同じ式の逆向き。動物の to_default と同じ長さ）。今は 1 tick でクリップの最初の姿勢へ飛ぶ（上げていた腕が下がる・
//    腕が 30° 開いて手のひらが前・体が 5〜6% 高くなる）。あわせて体の合わせ（humanMotionKeepsBodyInPlace）を切り替えのたびに今の体から
//    測り直す（新しいクリップの体を前のクリップの体の続きに置く。今は動作の最初の 1 回だけ測るので、身ぶりのクリップの Hips の位置の違いで
//    0.1〜0.2 m 跳び、root を 180° 回す walk_back では体が差の 2 倍振れる）。BlendHumanoidBoneLocalRotations は FK のループの外（終わりの
//    受け渡し・画面の外への歩きで使っている混ぜと同じ）。
//
// 2'. humanClipSwitchReanchorBody（既定 ON、既存の不具合の直し。検証役の指摘）
//    2 の「切り替えのたびに体の合わせを測り直す」だけを混ぜとは別に入れる。測り直さないと、walk_back で root を 180° 回したとき
//    root の向きの座標で持つ差が反転し、体が差の 2 倍（サッカー 約 0.2 m・テレビ 約 0.4 m）跳ぶ。今朝までは 1 の root のずれがたまたま
//    これを打ち消していたので、1 だけ入れると歩いて近づく動作の戻りで新しく跳ぶ。その場の動作は発火の tick の値をもう一度測るだけで変わらない。
//    体の向きが 1 tick で半回転するのは残る（それは 2 の混ぜ）。
//
// 3. randomMotionStartsAfterVideoPause（既定 OFF、新しい振る舞い）
//    今はランダムの動作が音のフェード（interactiveMotionAudioFadeSeconds = 0.5 s、実時間）の始めに動き出し、動画はフェードの終わりに止まる。
//    その間（実機で 0.5 s = 24 fps で 12 フレーム。バッチは撮影が実時間より遅いので 7〜10 フレーム）モデルは追従を離れて動画だけ進む。
//    ON なら発火した tick にフェードだけ始め（PendingPause）、モデルは追従のまま動画が止まるのを待ち、止まった tick の表示位置・姿勢から
//    動作を始める。人・動物だけ（車の走行は今のまま）。動画の止まる時間はフェードの分（0.5 s）長くなる。実験ログの random_ の行は今と同じ
//    発火の tick に出す（pending=1 を付ける）。
public partial class StreamingStereoVideoPlayer
{
    [Header("Human random motion: continuity (2026-10-25, O2)")]
    public bool humanTrackingRestoresHipsLocalPosition = true;
    // 2026-10-27 から既定 ON（見る用の版 l の中身を既定にした）。上の既定の説明はそれまでの話。
    public bool humanClipSwitchBlend = true;
    public bool humanClipSwitchReanchorBody = true;
    public bool randomMotionStartsAfterVideoPause = false;

    // --- 1. 追従の Hips の局所位置を戻す -----------------------------------------------------

    // 人のクリップが Hips に触る直前（StartInteractiveMotion・StartBodyFrameOut・system の frame-out・モデルの差し替えの結び直し）に呼ぶ。
    // インスタンス（Animator のキャッシュ）ごとに最初の 1 回だけ控える。
    private void NoteHumanHipsTrackLocalPosition(GameObject instance)
    {
        Animator animator = instance != null ? instance.GetComponentInChildren<Animator>() : null;
        if (animator == null || !humanoidCaches.TryGetValue(animator, out HumanoidRigCache cache) || cache == null ||
            cache.hasHipsTrackLocalPosition ||
            !cache.bones.TryGetValue(HumanBodyBones.Hips, out Transform hips) || hips == null)
        {
            return;
        }

        cache.hipsTrackLocalPosition = hips.localPosition;
        cache.hasHipsTrackLocalPosition = true;
    }

    // ApplyMetaTarget の追従の配置の前（人だけ。動作の Owned の tick・画面の外への歩きの tick はここへ来ない）。
    private void RestoreHumanHipsTrackLocalPositionIfEnabled(GameObject instance, MetaObj target)
    {
        if (!humanTrackingRestoresHipsLocalPosition || instance == null || !IsCategoryPerson(target.categoryId))
        {
            return;
        }

        Animator animator = instance.GetComponentInChildren<Animator>();
        if (animator == null || !humanoidCaches.TryGetValue(animator, out HumanoidRigCache cache) || cache == null ||
            !cache.hasHipsTrackLocalPosition ||
            !cache.bones.TryGetValue(HumanBodyBones.Hips, out Transform hips) || hips == null)
        {
            return;
        }

        TransformWriter.ApplyLocalPosition(hips, cache.hipsTrackLocalPosition);
    }

    // --- 2. 人のクリップの切り替えの混ぜ -----------------------------------------------------

    // BeginGesturePhase・BeginWalkInPhase・BeginWalkBackPhase（人、StartHumanClipPlayback の前）と、その場の動作のモデルの差し替えから。
    // 呼んだ tick の骨は、まだ前の姿勢（追従か前のクリップ）のまま（新しいクリップは次の tick の Evaluate で書く）。
    private void BeginHumanClipSwitch(InteractiveMotionState state, GameObject instance, float now)
    {
        state.humanSwitchBlendActive = false;
        state.humanSwitchFromBoneLocalRotations.Clear();
        if (instance == null || state.triggerSource != InteractiveTriggerSource.Random)
        {
            return;
        }

        // 体の合わせは今描いている体から測り直す（次の tick の KeepHumanBodyInPlace が、混ぜる前のクリップの姿勢で差を測る）。
        if (humanMotionKeepsBodyInPlace && (humanClipSwitchReanchorBody || humanClipSwitchBlend))
        {
            state.humanBodyAnchorValid = TryGetHumanBodyAnchor(instance, Vector3.up, out state.humanBodyStartHips, out state.humanBodyGroundHeight);
            state.humanBodyOffsetValid = false;
        }

        if (!humanClipSwitchBlend)
        {
            return;
        }

        Animator animator = instance.GetComponentInChildren<Animator>();
        Transform hips = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
        if (hips == null)
        {
            return;
        }

        CaptureHumanoidBoneLocalRotations(instance, state.humanSwitchFromBoneLocalRotations);
        state.humanSwitchFromBoneLocalRotations.Remove(HumanBodyBones.Hips);
        state.humanSwitchFromHipsPosition = hips.position;
        state.humanSwitchFromHipsRotation = hips.rotation;
        state.humanSwitchBlendStartTime = now;
        state.humanSwitchBlendActive = true;
    }

    // Owned の tick ごと（ApplyFrozenPoseAndGesture・歩きの段、KeepHumanBodyInPlace の後）。Hips は world で混ぜる
    // （root の向きは切り替えの tick に変わるので、局所だけを混ぜると体が半回転する。終わりの受け渡しの 10/25 の直しと同じ理由）。
    private void ApplyHumanClipSwitchBlend(GameObject instance, InteractiveMotionState state, float now)
    {
        if (!state.humanSwitchBlendActive || instance == null)
        {
            return;
        }

        float u = Mathf.Clamp01(RuntimeClock.ResolveElapsed(now, state.humanSwitchBlendStartTime) / Mathf.Max(0.05f, interactiveHandoffBlendSeconds));
        Animator animator = instance.GetComponentInChildren<Animator>();
        Transform hips = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
        if (u >= 1f || hips == null)
        {
            state.humanSwitchBlendActive = false;
            return;
        }

        float w = Mathf.SmoothStep(0f, 1f, u);
        Vector3 clipHipsPosition = hips.position;
        Quaternion clipHipsRotation = hips.rotation;
        BlendHumanoidBoneLocalRotations(instance, state.humanSwitchFromBoneLocalRotations, w);
        TransformWriter.ApplyPose(hips,
            Vector3.Lerp(state.humanSwitchFromHipsPosition, clipHipsPosition, w),
            Quaternion.Slerp(state.humanSwitchFromHipsRotation, clipHipsRotation, w));
    }

    // --- 3. 動画が止まってから動き始める -----------------------------------------------------

    // StartRandomInteractiveMotion（人・動物）と DebugForceInteractiveMotion（バッチの -forceMotionAt）から。
    private void StartOrDeferInteractiveMotion(uint trackId, bool isAnimal, InteractiveEventKind kind, float now)
    {
        if (randomMotionStartsAfterVideoPause && vp != null && vp.isPlaying && interactiveMotionAudioFadeSeconds > 0f)
        {
            BeginPendingRandomMotion(trackId, isAnimal, kind, now);
            return;
        }

        StartInteractiveMotion(trackId, isAnimal, kind, now);
    }

    private void BeginPendingRandomMotion(uint trackId, bool isAnimal, InteractiveEventKind kind, float now)
    {
        InteractiveMotionState state = GetOrCreateInteractiveMotionState(trackId);
        state.stage = InteractiveEventStage.PendingPause;
        state.triggerSource = InteractiveTriggerSource.Random;
        state.subject = isAnimal ? InteractiveMotionSubject.Animal : InteractiveMotionSubject.Person;
        state.pendingIsAnimal = isAnimal;
        state.pendingKind = kind;
        state.pendingSince = now;
        BeginRandomInteractiveMotionVideoPause(trackId);
        // 実験ログの random_ の行は今と同じ発火の tick に出す（止める・動き始める行は後から続く）。
        ExperimentLog.Interaction(trackId, $"random_{kind}", $"subject={(isAnimal ? "animal" : "human")} pending=1");
        Debug.Log($"[MOTION] pending track={trackId} kind={kind} (starts when the video pauses, fade {interactiveMotionAudioFadeSeconds:F2}s) " +
                  $"frame={GetCurrentPlaybackFrame()} tick={Time.frameCount}");
    }

    // ApplyMetaTarget の配置と表示位置の記録の後。動画が止まっていたら、この tick の表示位置・姿勢から動作を始める。
    // 安全弁: フェードの 2 倍 + 1 秒たっても止まらなければ、そのまま始める。
    private void TryStartPendingRandomMotion(uint trackId)
    {
        if (!interactiveMotionByTrack.TryGetValue(trackId, out InteractiveMotionState state) || state == null ||
            state.stage != InteractiveEventStage.PendingPause)
        {
            return;
        }

        float now = GetRuntimeTickContext().now;
        float waited = RuntimeClock.ResolveElapsed(now, state.pendingSince);
        bool paused = vp == null || !vp.isPlaying;
        bool timedOut = waited > 2f * Mathf.Max(0f, interactiveMotionAudioFadeSeconds) + 1f;
        if (!paused && !timedOut)
        {
            return;
        }

        state.stage = InteractiveEventStage.Inactive;
        Debug.Log($"[MOTION] pending start track={trackId} paused={paused} waited={waited:F2}s frame={GetCurrentPlaybackFrame()} tick={Time.frameCount}");
        StartInteractiveMotion(trackId, state.pendingIsAnimal, state.pendingKind, now, true);
        if (state.stage == InteractiveEventStage.Inactive)
        {
            // 始めなかった（歩けずに見送る設定のとき）: 預かっていた一時停止を解く（motion_end reason=stopped が random_ の行と対になる）。
            EndRandomInteractiveMotionVideoPause(trackId, "stopped");
        }
    }
}
