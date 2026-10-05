using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

public sealed partial class AnimalPoseApplier
{
    private readonly AnimalMotionFilter motionFilter;
    private readonly Dictionary<Transform, AnimalRigCache> animalRigCaches = new Dictionary<Transform, AnimalRigCache>();

    public AnimalPoseApplier(AnimalFilterConfig filterConfig = default)
    {
        AnimalFilterConfig resolved = filterConfig.minCutoffHz <= 0f ? AnimalFilterConfig.Default : filterConfig;
        motionFilter = new AnimalMotionFilter(resolved);
    }

    // shot 境界で持ち越してはいけないのは平滑化状態だけ。animalRigCaches はモデルの
    // ボーン解決結果でカットとは無関係なので保持する（作り直すと bind pose の再取得が走る）。
    public void ResetMotionState()
    {
        motionFilter.Reset();
        ResetSmalSmoothing();
    }

    // Snapshot every resolved canonical bone's local rotation, so a caller can blend the body
    // pose back toward whatever the tracked pipeline writes next (mirroring
    // CaptureHumanoidBoneLocalRotations/BlendHumanoidBoneLocalRotations for Human) - without
    // this, only the root smoothly catches up at the end of an interactive motion event while
    // the legs/spine/head/tail snap instantly into the live tracked pose the moment the
    // tracked pipeline writes it.
    public void CaptureBoneLocalRotations(Transform instanceRoot, Dictionary<Transform, Quaternion> destination)
    {
        Transform rigRoot = instanceRoot != null ? (instanceRoot.GetComponentInChildren<Animator>()?.transform ?? instanceRoot) : null;
        if (rigRoot == null || !animalRigCaches.TryGetValue(rigRoot, out AnimalRigCache cache) || cache == null)
        {
            return;
        }

        AddBoneLocalRotation(destination, cache.neck);
        AddBoneLocalRotation(destination, cache.head);
        AddBoneLocalRotation(destination, cache.spine);
        AddBoneLocalRotation(destination, cache.tailBase);
        AddBoneLocalRotation(destination, cache.tailMid);
        AddBoneLocalRotation(destination, cache.tailTip);
        AddBoneLocalRotation(destination, cache.leftFrontUpper);
        AddBoneLocalRotation(destination, cache.leftFrontLower);
        AddBoneLocalRotation(destination, cache.leftFrontPaw);
        AddBoneLocalRotation(destination, cache.rightFrontUpper);
        AddBoneLocalRotation(destination, cache.rightFrontLower);
        AddBoneLocalRotation(destination, cache.rightFrontPaw);
        AddBoneLocalRotation(destination, cache.leftRearUpper);
        AddBoneLocalRotation(destination, cache.leftRearLower);
        AddBoneLocalRotation(destination, cache.leftRearPaw);
        AddBoneLocalRotation(destination, cache.leftRearToe);
        AddBoneLocalRotation(destination, cache.rightRearUpper);
        AddBoneLocalRotation(destination, cache.rightRearLower);
        AddBoneLocalRotation(destination, cache.rightRearPaw);
        AddBoneLocalRotation(destination, cache.rightRearToe);
    }

    private static void AddBoneLocalRotation(Dictionary<Transform, Quaternion> destination, Transform bone)
    {
        if (bone != null)
        {
            destination[bone] = bone.localRotation;
        }
    }

    public static void BlendBoneLocalRotations(Dictionary<Transform, Quaternion> fromLocalRotations, float weight)
    {
        float clampedWeight = Mathf.Clamp01(weight);
        foreach (KeyValuePair<Transform, Quaternion> kv in fromLocalRotations)
        {
            if (kv.Key == null)
            {
                continue;
            }

            kv.Key.localRotation = Quaternion.Slerp(kv.Value, kv.Key.localRotation, clampedWeight);
        }
    }

    // 頭を視聴者へ向ける設定（StreamingStereoVideoPlayer の animalLookAtViewer* をそのまま渡す）。
    public struct LookAtViewerSettings
    {
        public bool bodyRelative;          // 体の向き基準（既定）。false なら頭の向き基準（2026-10-04 の方式）
        public float bodyMaxYawDegrees;    // 体の向き基準: 体の前からの視聴者の方位の上限（超えたら 40° かけて重み 0）、頭の目標の方位の上限
        public float maxPitchDegrees;      // 体の向き基準: 頭の目標の仰角の上限（±）
        public float maxDegrees;           // 頭の向き基準: 回す角度の上限
        public float maxYawDegrees;        // 頭の向き基準: 鼻と視聴者の水平の角度の上限
        public float neckShare;
    }

    // インタラクティブモーション: 頭の鼻先を視聴者へ向ける（2026-10-04、StreamingStereoVideoPlayer.animalGestureLookAtViewer）。
    // **root を最終の位置・向きへ置き直した後に呼ぶ**（向ける回転は world で計算するので、その後に root を動かすとずれる）。
    // SMAL の FK がこの frame に首・頭を書き直したときだけ掛ける（cache.smalFkWrittenFrame。書き直されない tick に掛けると積み重なる）。
    // 鼻先は頭の子孫の顔の骨から（ResolveHeadNoseLocal）。回転は「横（world の上まわり）→ 縦」に分けて作る（最小回転だと頭が傾く）。
    // 体の向き基準（既定、ApplyLookAtViewerBodyRelative）と、頭の向き基準（以下: 回すのは最大 maxDegrees まで、鼻と視聴者の水平の角度が
    // maxYawDegrees を超えたら 40° かけて重みを 0）。首に neckShare、残りを頭に配り、首を回した後に頭の位置から目標を計算し直す。
    // world の回転は TransformWriter.ApplyWorldRotation で書く。
    public void ApplyLookAtViewer(Transform instanceRoot, Vector3 viewerWorld, float weight, LookAtViewerSettings settings)
    {
        if (weight <= 0.001f || !TryGetRigCacheForInstance(instanceRoot, out AnimalRigCache cache) || cache.head == null ||
            cache.smalFkWrittenFrame != Time.frameCount || !ResolveHeadNoseLocal(cache))
        {
            return;
        }

        Vector3 nose0 = (cache.head.rotation * cache.headNoseLocal).normalized;
        Transform neck = cache.neck != null && cache.neck != cache.head && cache.head.IsChildOf(cache.neck) ? cache.neck : null;
        float neckShare = settings.neckShare;
        if (settings.bodyRelative && TryGetBodyForward(cache, out Vector3 bodyForward))
        {
            // 体がほぼ垂直（前の水平が定まらない）なら頭の向き基準に戻す
            Vector3 bodyFlat = Vector3.ProjectOnPlane(bodyForward, Vector3.up);
            if (bodyFlat.magnitude >= LookAtMinFlat * bodyForward.magnitude)
            {
                ApplyLookAtViewerBodyRelative(cache, neck, viewerWorld, weight, bodyFlat.normalized, nose0, settings);
                return;
            }
        }

        float maxDegrees = settings.maxDegrees;
        float maxYawDegrees = settings.maxYawDegrees;
        Vector3 want = ResolveLookAtDirection(nose0, viewerWorld - cache.head.position, weight, maxDegrees, maxYawDegrees);
        if (want.sqrMagnitude < 0.5f)
        {
            return;
        }

        if (neck != null && neckShare > 0f)
        {
            // 首の分も「横 → 縦」で作る（合成した回転を Slerp で割ると傾きが入り、頭の YawThenPitch はそれを消さずに残した: 最大 4〜7°。2 回目の査読）
            Quaternion neckDelta = YawThenPitch(nose0, Vector3.Slerp(nose0, want, Mathf.Clamp01(neckShare)).normalized);
            TransformWriter.ApplyWorldRotation(neck, neckDelta * neck.rotation);
            // 首を回すと頭の位置も動くので、目標を計算し直す（基準の鼻先は向ける前のまま）
            want = ResolveLookAtDirection(nose0, viewerWorld - cache.head.position, weight, maxDegrees, maxYawDegrees);
            if (want.sqrMagnitude < 0.5f)
            {
                return;
            }
        }

        Vector3 nose1 = (cache.head.rotation * cache.headNoseLocal).normalized;
        TransformWriter.ApplyWorldRotation(cache.head, YawThenPitch(nose1, want) * cache.head.rotation);
        LevelHeadRoll(cache, want, weight);
    }

    // 視聴者へ向けた頭の傾き（鼻の軸まわり）を、weight の分だけ水平に戻す（2026-10-05、判定役: Labrador が 10〜15° 首を傾けたまま見ていた）。
    // 傾きは凍結した姿勢（データ）から引き継いだもので、視聴者を見るときは真っすぐのほうが自然（首をかしげるのはジェスチャ HeadTilt が上から足す）。
    // 頭の左右の軸は、bind のときの体の右（cache.bodyRightBindWorld）を頭ローカルにしたもの。取れないモデル（F2 の対象外の 16_Deer1 など）と、
    // 鼻がほぼ真上・真下のとき（水平の左右が定まらない）は何もしない。
    // weight: 体の向き基準では方位で落とした後の重み w を渡す（元のイベントの重みを渡すと、w が 0 になる境目で傾きの戻しが 1 tick で消え、
    // 凍結した傾きの分だけ頭が跳んだ。査読、2026-10-05。頭の向き基準は切り替えの再現のため元の重みのまま）。
    // upright: 揃える水平を「近い側」ではなく「頭の上（鼻 × 右）が world の上を向く側」にする（体の向き基準）。近い側だと、凍結した傾きが 90° を超えると
    // 上下逆さで水平に揃った（89° → 0°、91° → 180°。査読）。どちらが上かは bind の姿勢（頭ローカルの鼻・体の右・world の上）で決める。決められないモデルは近い側。
    private static void LevelHeadRoll(AnimalRigCache cache, Vector3 nose, float weight, bool upright = false)
    {
        if (cache.bodyRightBindWorld.sqrMagnitude < 0.5f || !cache.bindRotWorld.TryGetValue(cache.head, out Quaternion headBind))
        {
            return;
        }

        Vector3 level = Vector3.Cross(Vector3.up, nose);
        if (level.sqrMagnitude < 0.07f)
        {
            return;
        }

        Vector3 right = cache.head.rotation * (Quaternion.Inverse(headBind) * cache.bodyRightBindWorld);
        Vector3 rightOnPlane = Vector3.ProjectOnPlane(right, nose);
        if (rightOnPlane.sqrMagnitude < 1e-6f)
        {
            return;
        }

        level.Normalize();
        float side = upright ? UprightLevelSide(cache, headBind) : 0f;
        if (side != 0f ? side < 0f : Vector3.Dot(rightOnPlane, level) < 0f)
        {
            level = -level;
        }

        float roll = Vector3.SignedAngle(rightOnPlane, level, nose);
        TransformWriter.ApplyWorldRotation(cache.head, Quaternion.AngleAxis(roll * Mathf.Clamp01(weight), nose) * cache.head.rotation);
    }

    // 頭の右を +level（= cross(上, 鼻)）と -level のどちらに揃えると頭の上が world の上を向くか。1 なら +level、-1 なら -level、0 なら決められない。
    // cross(鼻, +level) は常に上を向く（y = 1 − 鼻の y²）。bind の姿勢で cross(鼻, 右) が頭の上（world の上を頭ローカルにしたもの）と同じ向きなら +level。
    private static float UprightLevelSide(AnimalRigCache cache, Quaternion headBind)
    {
        Quaternion inv = Quaternion.Inverse(headBind);
        Vector3 r = inv * cache.bodyRightBindWorld;
        Vector3 u = inv * Vector3.up;
        Vector3 c = Vector3.Cross(cache.headNoseLocal, r);
        if (c.sqrMagnitude < 1e-6f || u.sqrMagnitude < 1e-6f)
        {
            return 0f;
        }

        float d = Vector3.Dot(c.normalized, u.normalized);
        return Mathf.Abs(d) < 0.3f ? 0f : Mathf.Sign(d);
    }

    // 向ける前の鼻先 nose から、視聴者の向き toViewer へ weight だけ寄せた向き（回すのは最大 maxDegrees）。向けないなら zero。
    // 重みは水平の角度で落とす: 視聴者が体の後ろ側（鼻と視聴者の水平の向きの差が maxYawDegrees を超える）なら 40° かけて 0（歩いて戻る間に頭が反らないように）。
    // 縦の差（伏せで鼻が下・視聴者が上）は落とさない（3 次元の角度で落とすと、伏せで発火したとき向けがまったく効かなかった。判定役、2026-10-04）。
    private static Vector3 ResolveLookAtDirection(Vector3 nose, Vector3 toViewer, float weight, float maxDegrees, float maxYawDegrees)
    {
        if (toViewer.sqrMagnitude < 1e-8f)
        {
            return Vector3.zero;
        }

        toViewer.Normalize();
        float angle = Vector3.Angle(nose, toViewer);
        Vector3 noseFlat = Vector3.ProjectOnPlane(nose, Vector3.up);
        Vector3 toFlat = Vector3.ProjectOnPlane(toViewer, Vector3.up);
        float yawAngle = noseFlat.sqrMagnitude > 0.01f && toFlat.sqrMagnitude > 0.01f ? Vector3.Angle(noseFlat, toFlat) : 0f;
        float w = Mathf.Clamp01(weight) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(maxYawDegrees, maxYawDegrees + 40f, yawAngle)));
        if (w <= 0.001f)
        {
            return Vector3.zero;
        }

        Vector3 target = angle > maxDegrees && angle > 0.001f ? Vector3.Slerp(nose, toViewer, maxDegrees / angle) : toViewer;
        return Vector3.Slerp(nose, target, w).normalized;
    }

    // from を to へ向ける回転を「world の上まわりの横 → 横に寝た軸まわりの縦」で作る（最小回転の FromTo は鼻の軸まわりに頭を傾けることがある）。
    // ただし鼻がほぼ真上・真下（水平成分が cos 75° 未満）のときと、横の回転が 3 次元の角度より 60° 以上大きいとき（頭が垂直を越えて反っている・
    // 水平成分がほぼ逆向き）は、横の向きが定まらず逆さのまま向いたり 1 フレームで反転したりするので、最小回転（FromTo）に戻す（2 回目の査読）。
    // 頭の向き基準だけが使う。鼻がほぼ真上・真下で方位の差が 120° を超えると、この FromTo も頭を上下逆さにする（2026-10-05 の査読。体の向き基準は向けないことで避けた）。
    private static Quaternion YawThenPitch(Vector3 from, Vector3 to)
    {
        Vector3 fromFlat = Vector3.ProjectOnPlane(from, Vector3.up);
        Vector3 toFlat = Vector3.ProjectOnPlane(to, Vector3.up);
        const float minFlat = 0.26f;
        if (fromFlat.magnitude < minFlat * from.magnitude || toFlat.magnitude < minFlat * to.magnitude ||
            Vector3.Angle(fromFlat, toFlat) > Vector3.Angle(from, to) + 60f)
        {
            return Quaternion.FromToRotation(from, to);
        }

        Quaternion yaw = Quaternion.FromToRotation(fromFlat, toFlat);
        return Quaternion.FromToRotation(yaw * from, to) * yaw;
    }

    // 向きの水平成分がこれ未満（cos 75°）なら、水平の方位が定まらないとみなす。
    private const float LookAtMinFlat = 0.26f;

    // 体の向き基準で頭を視聴者へ向ける（2026-10-05、M8 の実測から）。
    // 伏せの犬は頭を体の真横へ向けたまま凍結していて、FaceViewer・歩きで体が視聴者を向くと頭が視聴者から 100〜180° それる。頭の向き基準では
    // それを「視聴者が後ろ」と判定して重みを落とし、回す量の上限 90° でも残った。ここでは:
    //   - 重み: 視聴者の方位を体の前から測り、bodyMaxYawDegrees を超えたら 40° かけて 0（視聴者が本当に体の後ろのときだけ抜く）
    //   - 目標: 視聴者の向き。体の前からの方位は ±bodyMaxYawDegrees、仰角は ±maxPitchDegrees に収める
    //   - 回し方: 鼻と目標を「体の前からの方位・仰角」で表し、その差を横（world の上まわり）→ 縦で回す。方位の差は ±180 を回り込まないので、
    //     頭は体の前を通って回る（重みが途中の tick も体の後ろを通らない）。凍結した姿勢の中では鼻の方位は変わらないので左右が tick ごとに入れ替わらない
    //   - 首に neckShare、首を回した後に頭の位置から目標を計算し直し、残りを頭で合わせる
    //   - 頭の傾きは、方位で落とした後の重み w の分だけ、上下が正しい側の水平へ戻す（LevelHeadRoll の upright）
    //   - **凍結した鼻がほぼ真上・真下（水平から 75° 超）なら向けない**（顎しか無いモデルと同じ扱い）。方位が定まらず、最小回転（FromTo）で向けると
    //     頭が体の横〜後ろを向いているときに上下逆さになり、重みの入り際にも最大 30° 跳んだ（査読、2026-10-05）。凍結した姿勢の中では鼻の仰角は root の yaw で変わらないので、
    //     イベントの途中で向ける / 向けないが切り替わることはない
    // 移植と検算: scratchpad/rev2/lookat_body_relative.py（鼻が真上・真下でないとき、重み 1 で鼻先の誤差 0.0000°、首なしの傾きの変化 0.000°、体の前を通る）。
    // 査読の厳密な移植: scratchpad/review_lookat/cs_port.py。
    private static void ApplyLookAtViewerBodyRelative(AnimalRigCache cache, Transform neck, Vector3 viewerWorld, float weight, Vector3 bodyFlat, Vector3 nose0, LookAtViewerSettings settings)
    {
        if (!TryGetBearing(bodyFlat, nose0, out float noseYaw) ||
            !TryResolveBodyRelativeAim(bodyFlat, viewerWorld - cache.head.position, weight, settings, out float w, out float aimYaw, out float aimPitch))
        {
            return;
        }

        float nosePitch = ElevationDegrees(nose0);
        float wantYaw = noseYaw + w * (aimYaw - noseYaw);
        float wantPitch = nosePitch + w * (aimPitch - nosePitch);
        float share = Mathf.Clamp01(settings.neckShare);
        if (neck != null && share > 0f)
        {
            Quaternion neckDelta = YawPitchDelta(nose0, share * (wantYaw - noseYaw), share * (wantPitch - nosePitch));
            TransformWriter.ApplyWorldRotation(neck, neckDelta * neck.rotation);
            if (!TryResolveBodyRelativeAim(bodyFlat, viewerWorld - cache.head.position, weight, settings, out w, out aimYaw, out aimPitch))
            {
                return;
            }

            wantYaw = noseYaw + w * (aimYaw - noseYaw);
            wantPitch = nosePitch + w * (aimPitch - nosePitch);
        }

        // 首を回した後の鼻は、元の鼻と目標の仰角の間に収まる（目標は ±maxPitchDegrees）ので、既定値では真上・真下にならない（査読で 22913 件中 0 件）。
        // maxPitchDegrees を 75° より大きくしたときだけ最小回転に落ちる。
        Vector3 nose1 = (cache.head.rotation * cache.headNoseLocal).normalized;
        Vector3 want = DirectionFromBearing(bodyFlat, wantYaw, wantPitch);
        Quaternion headDelta = TryGetBearing(bodyFlat, nose1, out float nose1Yaw)
            ? YawPitchDelta(nose1, wantYaw - nose1Yaw, wantPitch - ElevationDegrees(nose1))
            : Quaternion.FromToRotation(nose1, want);
        TransformWriter.ApplyWorldRotation(cache.head, headDelta * cache.head.rotation);
        LevelHeadRoll(cache, want, w, true);
    }

    // 体の向き基準の重みと目標（体の前からの方位 aimYaw・仰角 aimPitch、度）。重みが 0 なら false。
    private static bool TryResolveBodyRelativeAim(Vector3 bodyFlat, Vector3 toViewer, float weight, LookAtViewerSettings settings, out float w, out float aimYaw, out float aimPitch)
    {
        w = 0f;
        aimYaw = 0f;
        aimPitch = 0f;
        if (toViewer.sqrMagnitude < 1e-8f)
        {
            return false;
        }

        Vector3 flat = Vector3.ProjectOnPlane(toViewer, Vector3.up);
        float beta = flat.sqrMagnitude > 1e-10f ? Vector3.SignedAngle(bodyFlat, flat, Vector3.up) : 0f;
        float maxYaw = settings.bodyMaxYawDegrees;
        w = Mathf.Clamp01(weight) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(maxYaw, maxYaw + 40f, Mathf.Abs(beta))));
        aimYaw = Mathf.Clamp(beta, -maxYaw, maxYaw);
        aimPitch = Mathf.Clamp(ElevationDegrees(toViewer), -settings.maxPitchDegrees, settings.maxPitchDegrees);
        return w > 0.001f;
    }

    // v の水平の方位（bodyFlat からの符号付きの角度、world の上まわり）。水平成分が小さければ false。
    private static bool TryGetBearing(Vector3 bodyFlat, Vector3 v, out float yaw)
    {
        Vector3 flat = Vector3.ProjectOnPlane(v, Vector3.up);
        if (flat.magnitude < LookAtMinFlat * v.magnitude)
        {
            yaw = 0f;
            return false;
        }

        yaw = Vector3.SignedAngle(bodyFlat, flat, Vector3.up);
        return true;
    }

    private static float ElevationDegrees(Vector3 v)
    {
        float m = v.magnitude;
        return m > 1e-8f ? Mathf.Asin(Mathf.Clamp(v.y / m, -1f, 1f)) * Mathf.Rad2Deg : 0f;
    }

    private static Vector3 DirectionFromBearing(Vector3 bodyFlat, float yaw, float pitch)
    {
        Vector3 flat = Quaternion.AngleAxis(yaw, Vector3.up) * bodyFlat;
        float p = pitch * Mathf.Deg2Rad;
        return (flat * Mathf.Cos(p) + Vector3.up * Mathf.Sin(p)).normalized;
    }

    // nose を world の上まわりに dYaw、続けて横に寝た軸（水平の向きと上の外積）まわりに dPitch 回す回転。dPitch が正なら上へ。
    private static Quaternion YawPitchDelta(Vector3 nose, float dYaw, float dPitch)
    {
        Quaternion yaw = Quaternion.AngleAxis(dYaw, Vector3.up);
        Vector3 flat = Vector3.ProjectOnPlane(yaw * nose, Vector3.up);
        if (flat.sqrMagnitude < 1e-10f)
        {
            return yaw;
        }

        return Quaternion.AngleAxis(dPitch, Vector3.Cross(flat.normalized, Vector3.up)) * yaw;
    }

    // インタラクティブモーション: root を置き直して頭を視聴者へ向けた後に、ジェスチャを足す（ApplyGestureOverlay と同じもの）。
    // Apply がジェスチャを足す地点まで来た frame だけ（cache.gestureOverlayFrame。HEAD では Apply が早く返るとジェスチャも足さなかった）。
    public void ApplyGestureOverlayAfterPlacement(Transform instanceRoot, AnimalGesturePose clip, float normalizedTime, bool onCanonicalLimbs, bool anatomicalHeadAxes = false)
    {
        if (clip == null || !TryGetRigCacheForInstance(instanceRoot, out AnimalRigCache cache) || cache.gestureOverlayFrame != Time.frameCount)
        {
            return;
        }

        AnimalGesturePosePlayer.ApplyToRigCache(clip, normalizedTime, cache, onCanonicalLimbs, ResolveHeadGestureRemap(cache, anatomicalHeadAxes));
    }

    // 頭のジェスチャ（HeadShake・HeadTiltAndTailWag の頭の点）を、頭の骨の局所軸ではなくモデルの解剖学的な軸で回すための写像（2026-10-05）。
    // 資産は Labrador と同じ局所軸の意味で作ってある（局所 Z = 頭の上 → forward の曲線で横に首を振る、局所 Y ≈ 鼻）。モデルによって頭の骨の局所軸は違い、
    // Lynx は視聴者へ向けて傾きを水平に戻した後でも、鼻に垂直な面で局所 Z が上から 45〜48° 傾いていて、首振りが斜め（横と頷きの中間）になった（M10。Labrador は 8〜10°）。
    // 写像 = 頭ローカルで「+Z → 頭の上、+Y → 鼻」へ回す回転（LookRotation(上, 鼻)）。ジェスチャの回転 R は M × R × M⁻¹ にして掛ける。
    // 頭の上 = cross(鼻, 体の右)（体の右 = bind のときの体の右 cache.bodyRightBindWorld を頭ローカルにしたもの、LevelHeadRoll と同じ）を、bind で world の上を向く側に。
    // 鼻（顔の骨）か体の右が取れないモデル、上下が決められないモデルは null（従来どおり局所軸）。モデルごとに 1 回だけ求めてキャッシュする。
    private static Quaternion? ResolveHeadGestureRemap(AnimalRigCache cache, bool enabled)
    {
        if (!enabled || cache == null || cache.head == null)
        {
            return null;
        }

        if (!cache.headGestureRemapResolved)
        {
            cache.headGestureRemapResolved = true;
            cache.headGestureRemapValid = false;
            if (ResolveHeadNoseLocal(cache) && cache.bodyRightBindWorld.sqrMagnitude >= 0.5f &&
                cache.bindRotWorld.TryGetValue(cache.head, out Quaternion headBind))
            {
                float side = UprightLevelSide(cache, headBind);
                Vector3 r = Quaternion.Inverse(headBind) * cache.bodyRightBindWorld;
                Vector3 up = Vector3.Cross(cache.headNoseLocal, r) * side;
                if (side != 0f && up.sqrMagnitude > 1e-6f)
                {
                    cache.headGestureRemap = Quaternion.LookRotation(up.normalized, cache.headNoseLocal);
                    cache.headGestureRemapValid = true;
                }
            }

            Debug.Log($"[GESTURE-HEAD] {(cache.root != null ? cache.root.name : "?")} anatomicalHeadAxes=" +
                      (cache.headGestureRemapValid ? $"on remap={Quaternion.Angle(Quaternion.identity, cache.headGestureRemap):F1}deg up={cache.headGestureRemap * Vector3.forward:F3}" : "off（鼻か体の右が取れない）"));
        }

        return cache.headGestureRemapValid ? cache.headGestureRemap : (Quaternion?)null;
    }

    private bool TryGetRigCacheForInstance(Transform instanceRoot, out AnimalRigCache cache)
    {
        cache = null;
        Transform rigRoot = instanceRoot != null ? (instanceRoot.GetComponentInChildren<Animator>()?.transform ?? instanceRoot) : null;
        return rigRoot != null && animalRigCaches.TryGetValue(rigRoot, out cache) && cache != null;
    }

    // 頭ローカルの鼻先方向を、頭の子孫の顔の骨から一度だけ求める（2026-10-04）。優先: 鼻の骨（LeftNose / RightNose など、名前に nose）の中点
    // → 舌の骨（tongue）→ 唇・口（lip / mouth / muzzle / snout）→ 当てはめ済みの頭（animal_head_fit.json、00_Dog・Labrador）の FK の照準。どれも無ければ向けない。
    // 顔の骨は FK が動かさないので、いつ求めても頭に対して同じ。animal_head_aim.json は耳・角・唇の端を指していて使えない（Lynx で 85°、査読役）。
    // 顔の骨の調べ: 鼻 32 体、舌・唇 12 体、顎だけ 3 体（16_Deer1・21_Donkey1.0・29_Goat1、向けない）。scratchpad/inv3/size/face_bones_survey.py
    private static readonly string[][] HeadNoseTokenGroups =
    {
        new[] { "nose" },
        new[] { "tongue" },
        new[] { "lip", "mouth", "muzzle", "snout" },
    };

    private static bool ResolveHeadNoseLocal(AnimalRigCache cache)
    {
        if (cache.headNoseResolved)
        {
            return cache.headNoseValid;
        }

        cache.headNoseResolved = true;
        cache.headNoseValid = false;
        Transform head = cache.head;
        if (head == null)
        {
            return false;
        }

        Transform[] desc = head.GetComponentsInChildren<Transform>(true);
        foreach (string[] tokens in HeadNoseTokenGroups)
        {
            Vector3 sum = Vector3.zero;
            int count = 0;
            foreach (Transform t in desc)
            {
                if (t == head) { continue; }
                string n = t.name.ToLowerInvariant();
                foreach (string token in tokens)
                {
                    if (n.Contains(token)) { sum += t.position; count++; break; }
                }
            }

            if (count > 0)
            {
                Vector3 local = head.InverseTransformDirection(sum / count - head.position);
                if (local.sqrMagnitude > 1e-10f)
                {
                    cache.headNoseLocal = local.normalized;
                    cache.headNoseValid = true;
                    cache.headNoseSource = tokens[0] + "(" + count + ")";
                    Debug.Log($"[LOOKAT] {(cache.root != null ? cache.root.name : "?")} nose={cache.headNoseSource} local={cache.headNoseLocal:F3}{BodyForwardLogText(cache)}");
                    return true;
                }
            }
        }

        if (HasBakedHeadFit(cache) && cache.bindDirLocal.TryGetValue(head, out Vector3 fit) && fit.sqrMagnitude > 1e-8f)
        {
            cache.headNoseLocal = fit.normalized;
            cache.headNoseValid = true;
            cache.headNoseSource = "head_fit";
            Debug.Log($"[LOOKAT] {(cache.root != null ? cache.root.name : "?")} nose=head_fit local={cache.headNoseLocal:F3}{BodyForwardLogText(cache)}");
            return true;
        }

        cache.headNoseSource = "none";
        Debug.Log($"[LOOKAT] {(cache.root != null ? cache.root.name : "?")} nose=none（顔の骨が無いので頭を視聴者へ向けない）");
        return false;
    }

    // [LOOKAT] のログに体の前（spine ローカル）を足す。オフラインの移植で体の向き基準を再現するため（spine の world の回転に掛ける）。
    private static string BodyForwardLogText(AnimalRigCache cache)
    {
        return TryGetBodyForwardSpineLocal(cache, out Vector3 local)
            ? $" bodyFwdSpineLocal={local.x:F4},{local.y:F4},{local.z:F4} spine={cache.spine.name}"
            : " bodyFwdSpineLocal=none";
    }

    // There is no Humanoid-Avatar-style standard for which local axis an animal's nose points
    // along, so unlike Human (where local +Z reliably is the facing direction),
    // instanceRoot.rotation's own +Z cannot be assumed to be the nose. cache.spine.forward is
    // kept live and correct every frame by AnimalSmalFkApplier's FK write (with the project's
    // established "nose = -spine.forward" convention - see the gizmo in AnimalSmalFkApplier),
    // so reading it is the reliable way to measure which way the model is actually facing right
    // now, for callers that need to compute a relative turn (e.g. the FaceViewer interactive
    // motion preset) rather than assume an absolute local axis.
    public bool TryGetCurrentNoseWorldDirection(Transform instanceRoot, out Vector3 noseWorldDirection)
    {
        Transform rigRoot = instanceRoot != null ? (instanceRoot.GetComponentInChildren<Animator>()?.transform ?? instanceRoot) : null;
        if (rigRoot != null && animalRigCaches.TryGetValue(rigRoot, out AnimalRigCache cache) &&
            cache != null && TryGetBodyForward(cache, out noseWorldDirection))
        {
            return true;
        }

        noseWorldDirection = Vector3.forward;
        return false;
    }

    // 体の前の向き（world）。TryGetCurrentNoseWorldDirection と頭を視聴者へ向ける体の向き基準（ApplyLookAtViewerBodyRelative）が使う。
    private static bool TryGetBodyForward(AnimalRigCache cache, out Vector3 bodyForward)
    {
        if (!TryGetBodyForwardSpineLocal(cache, out Vector3 local))
        {
            bodyForward = Vector3.forward;
            return false;
        }

        bodyForward = cache.spine.rotation * local;
        return true;
    }

    // 体の前の向きを spine のローカルで。
    private static bool TryGetBodyForwardSpineLocal(AnimalRigCache cache, out Vector3 local)
    {
        if (cache == null || cache.spine == null)
        {
            local = Vector3.back;
            return false;
        }

        // Compute model-accurate nose direction: spine.rotation * Inv(spineBindWorld) * modelForwardLocal.
        // For Dog (bindSpineW=identity, modelForwardLocal=-Z) this equals -spine.forward (old hardcoded value).
        // For other models where the spine bone is not aligned with the nose at T-pose, this correctly
        // remaps modelForwardLocal through the spine's current rotation.
        local = cache.bindRotWorld.TryGetValue(cache.spine, out Quaternion spineBindWorld) && cache.modelForwardLocal.sqrMagnitude > 0.000001f
            ? Quaternion.Inverse(spineBindWorld) * cache.modelForwardLocal
            : Vector3.back;
        return true;
    }

    public void Apply(AnimalPoseRequest request)
    {
        Transform instanceRoot = request.instanceRoot;
        AnimalPoseWorldData pose = request.pose;
        if (instanceRoot == null)
            return;

        bool hasJoints = pose.jointsWorld != null && pose.jointVis != null && pose.jointCount >= 20;
        if (!hasJoints && !request.hasSmalPose)
            return;

        RuntimeClock.TickContext tick = request.tickContext;
        AnimalRigCache cache = ApplyAnimalSkeletonPlacement(
            instanceRoot, request.animator,
            pose.rootWorld, request.settings, tick);

        if (!request.enableBoneApply || cache == null || !cache.ready)
            return;

        if (request.hasSmalPose && IsAnimalRigReadyForSmalFk(cache))
        {
            AlignAnimalRootToSkeleton(instanceRoot, cache, pose.rootWorld, true, tick);
            TryApplyAnimalSmalFk(cache, request.smalPose, request.settings, pose.jointsWorld, pose.jointVis, instanceRoot);
            if (enableAnimalKeypointAimAt)
            {
                ApplyAnimalLimbAimAt(cache, pose.jointsWorld, pose.jointVis, Mathf.Clamp01(request.settings.boneApplyAlpha));
            }

            ApplyGestureOverlay(cache, request);
            return;
        }

        TryApplyAnimalRootOrientation(instanceRoot, cache, pose.jointsWorld, pose.jointVis, Mathf.Clamp01(request.settings.animalRootRotateAlpha), pose.hasAnimalControl, pose.animalControl, request.settings, tick);
        AlignAnimalRootToSkeleton(instanceRoot, cache, pose.rootWorld, true, tick);

        float alpha = Mathf.Clamp01(request.settings.boneApplyAlpha);
        ApplyAnimalHeadPose(cache, pose.jointsWorld, pose.jointVis, alpha, pose.hasAnimalControl, pose.animalControl, tick);
        ApplyAnimalTailPose(cache, alpha, pose.hasAnimalControl, pose.animalControl, tick);

        if (request.settings.enableAnimalLimbApply)
        {
            ApplyAnimalLimbPose(cache, pose.jointsWorld, pose.jointVis, alpha, request.freezeAnimalDistal, pose.hasAnimalControl, pose.animalControl, tick);
        }

        ApplyGestureOverlay(cache, request);
    }

    // Runs after bone placement regardless of pose source (SMAL FK or keypoint/control-target
    // IK above), so an AnimalGesturePose gesture/walk clip works on any track - unlike the
    // animal-control-target-only presets (TailWag/PawWave/BodyTurnViewer), which stay pre-FK
    // and so still only apply when pose.hasAnimalControl is true.
    private static void ApplyGestureOverlay(AnimalRigCache cache, AnimalPoseRequest request)
    {
        if (cache == null)
        {
            return;
        }

        // ジェスチャを足す地点まで来た印（ジェスチャを置き直しの後に足す経路は、これが今の frame のときだけ足す。2026-10-04）。
        cache.gestureOverlayFrame = Time.frameCount;
        if (request.gestureOverlayClip == null)
        {
            return;
        }

        AnimalGesturePosePlayer.ApplyToRigCache(request.gestureOverlayClip, request.gestureOverlayNormalizedTime, cache, request.gestureOnCanonicalLimbs,
            ResolveHeadGestureRemap(cache, request.gestureAnatomicalHeadAxes));
    }

    // SMAL FK needs confident front/rear and left/right identification of all four leg
    // roots (plus spine) to compute a meaningful root orientation and per-joint bends - a
    // partially-wrong FK (e.g. front/rear legs swapped) looks worse than just falling back
    // to the existing keypoint-IK pipeline (ADR-0002 decision 3, 2026-06-18). This is
    // deliberately stricter than the general cache.ready bone-apply gate, which also covers
    // the keypoint-IK path and stays lenient so partial rigs still get some bone apply there.
    private static bool IsAnimalRigReadyForSmalFk(AnimalRigCache cache)
    {
        return cache != null
            && cache.spine != null
            && cache.leftFrontUpper != null
            && cache.rightFrontUpper != null
            && cache.leftRearUpper != null
            && cache.rightRearUpper != null;
    }

    private void TryApplyAnimalRootOrientation(Transform instanceRoot, AnimalRigCache cache, Vector3[] jointsWorld, byte[] vis, float rotateAlpha, bool hasControl, AnimalControlWorldData control, AnimalPoseSettings settings, RuntimeClock.TickContext tick)
    {
        if (instanceRoot == null || jointsWorld == null || vis == null)
        {
            return;
        }

        if (!TryGetAnimalBodyBasis(jointsWorld, vis, instanceRoot, hasControl, control, out Vector3 bodyForward, out Vector3 bodyUp, out Vector3 facingHint))
        {
            return;
        }

        Vector3 worldUp = Vector3.up;
        Vector3 stabilizedForward = bodyForward;
        Vector3 stabilizedUp = bodyUp;

        if (settings.stabilizeAnimalRootYaw)
        {
            StabilizeAnimalRootBasis(instanceRoot, worldUp, bodyForward, bodyUp, facingHint, tick, out stabilizedForward, out stabilizedUp);
        }

        StabilizeAnimalRootPitchRoll(worldUp, stabilizedForward, stabilizedUp, settings.animalRootPitchRollBlend, out stabilizedForward, out stabilizedUp);

        Vector3 modelForwardLocal = cache != null && cache.modelForwardLocal.sqrMagnitude > 0.000001f
            ? cache.modelForwardLocal
            : settings.animalModelForwardLocal;
        Vector3 modelUpLocal = cache != null && cache.modelUpLocal.sqrMagnitude > 0.000001f
            ? cache.modelUpLocal
            : settings.animalModelUpLocal;

        Vector3 modelForward = modelForwardLocal.sqrMagnitude > 0.000001f ? modelForwardLocal.normalized : Vector3.right;
        Vector3 modelUp = modelUpLocal.sqrMagnitude > 0.000001f ? modelUpLocal.normalized : Vector3.up;

        Quaternion modelBasis = Quaternion.LookRotation(modelForward, modelUp);
        Quaternion targetBasis = Quaternion.LookRotation(stabilizedForward, stabilizedUp);
        Quaternion targetRootRot = targetBasis * Quaternion.Inverse(modelBasis);
        TrackPlacementWriter.Apply(
            instanceRoot,
            TrackPlacementCommand.RotationOnly(
                instanceRoot.position,
                Quaternion.Slerp(instanceRoot.rotation, targetRootRot, Mathf.Clamp01(rotateAlpha)),
                instanceRoot.localScale));
    }

    private static void StabilizeAnimalRootPitchRoll(Vector3 worldUp, Vector3 forward, Vector3 up, float pitchRollBlend, out Vector3 stabilizedForward, out Vector3 stabilizedUp)
    {
        AnimalRootBasisMath.StabilizePitchRoll(
            worldUp,
            forward,
            up,
            pitchRollBlend,
            out stabilizedForward,
            out stabilizedUp);
    }

    private void StabilizeAnimalRootBasis(Transform root, Vector3 worldUp, Vector3 forward, Vector3 bodyUp, Vector3 facingHint, RuntimeClock.TickContext tick, out Vector3 stabilizedForward, out Vector3 stabilizedUp)
    {
        stabilizedForward = forward;
        stabilizedUp = bodyUp;
        if (root == null)
        {
            return;
        }

        int currentFrame = tick.frameCount;
        bool seenRecently = motionFilter.WasSeenRecently(root, currentFrame);
        motionFilter.MarkRootSeen(root, currentFrame);

        Vector3 planarForward = Vector3.ProjectOnPlane(forward, worldUp);
        if (planarForward.sqrMagnitude <= 0.000001f)
        {
            return;
        }
        planarForward.Normalize();

        if (!seenRecently)
        {
            motionFilter.ResetRootBasis(root, planarForward, bodyUp, worldUp);
            stabilizedForward = forward;
            stabilizedUp = bodyUp;
            return;
        }

        if (!motionFilter.TryGetPrevRootForward(root, out Vector3 prevForward))
        {
            prevForward = Vector3.ProjectOnPlane(root.forward, worldUp);
        }
        if (prevForward.sqrMagnitude < 0.000001f)
        {
            motionFilter.ResetRootBasis(root, planarForward, bodyUp, worldUp);
            return;
        }
        prevForward.Normalize();

        Vector3 candA = planarForward;
        Vector3 candB = -candA;
        Vector3 planarHint = Vector3.ProjectOnPlane(facingHint, worldUp);
        Vector3 chosenForward;
        if (planarHint.sqrMagnitude > 0.000001f)
        {
            planarHint.Normalize();
            chosenForward = Vector3.Dot(candA, planarHint) >= Vector3.Dot(candB, planarHint) ? candA : candB;
        }
        else
        {
            chosenForward = Vector3.Dot(prevForward, candA) >= Vector3.Dot(prevForward, candB) ? candA : candB;
        }

        float dt = tick.deltaTime;
        Vector3 filteredForward = motionFilter.FilterRootForward(root, chosenForward, dt);
        filteredForward = Vector3.ProjectOnPlane(filteredForward, worldUp);
        if (filteredForward.sqrMagnitude <= 0.000001f)
        {
            filteredForward = chosenForward;
        }
        filteredForward.Normalize();
        motionFilter.SetRootForward(root, filteredForward);

        Vector3 projectedUp = Vector3.ProjectOnPlane(bodyUp, filteredForward);
        if (projectedUp.sqrMagnitude <= 0.000001f)
        {
            projectedUp = Vector3.ProjectOnPlane(root.up, filteredForward);
        }
        if (projectedUp.sqrMagnitude <= 0.000001f)
        {
            projectedUp = Vector3.ProjectOnPlane(worldUp, filteredForward);
        }
        if (projectedUp.sqrMagnitude <= 0.000001f)
        {
            stabilizedForward = filteredForward;
            stabilizedUp = bodyUp;
            return;
        }
        projectedUp.Normalize();

        Vector3 filteredUp = motionFilter.FilterRootUp(root, projectedUp, dt);
        filteredUp = Vector3.ProjectOnPlane(filteredUp, filteredForward);
        if (filteredUp.sqrMagnitude <= 0.000001f)
        {
            filteredUp = projectedUp;
        }
        filteredUp.Normalize();

        Vector3 right = Vector3.Cross(filteredForward, filteredUp);
        if (right.sqrMagnitude <= 0.000001f)
        {
            stabilizedForward = filteredForward;
            stabilizedUp = filteredUp;
            return;
        }
        right.Normalize();
        filteredUp = Vector3.Cross(right, filteredForward).normalized;

        stabilizedForward = filteredForward;
        stabilizedUp = filteredUp;
    }

    private static bool TryGetAnimalBodyBasis(Vector3[] jointsWorld, byte[] vis, Transform instanceRoot, bool hasControl, AnimalControlWorldData control, out Vector3 forward, out Vector3 up, out Vector3 facingHint)
    {
        forward = Vector3.zero;
        up = Vector3.zero;
        facingHint = Vector3.zero;
        if (hasControl && TryGetAnimalBodyBasisFromControl(control, out forward, out up, out facingHint))
        {
            return true;
        }

        Vector3 preferredUp = instanceRoot != null ? instanceRoot.up : Vector3.up;
        return AnimalBodyBasisResolver.TryResolveFromJoints(
            jointsWorld,
            vis,
            preferredUp,
            out forward,
            out up,
            out facingHint);
    }

    public static bool TryGetAnimalBodyBasisFromControl(AnimalControlWorldData control, out Vector3 forward, out Vector3 up, out Vector3 facingHint)
    {
        return AnimalBodyBasisResolver.TryResolveFromControl(control, out forward, out up, out facingHint);
    }

    private AnimalRigCache ApplyAnimalSkeletonPlacement(Transform instanceRoot, Animator animator, Vector3 skeletonRoot, AnimalPoseSettings settings, RuntimeClock.TickContext tick)
    {
        Transform rigRoot = animator != null ? animator.transform : instanceRoot;
        AnimalRigCache cache = GetOrBuildAnimalRigCache(rigRoot, instanceRoot, settings);
        // ジェスチャを bind の局所の上に足す骨（FK が書かない骨・Animator の子の root）を、配置と FK より前に bind へ戻す（2026-10-04、2 回目の査読）。
        // 戻さないと、FK が前の tick のジェスチャが残った root の下で胴の world 回転を書き、後で root を bind × G に置き直したときに
        // 1 tick 分の差しか残らず、00_Dog・27_GermanShepherd の BodyShake（±10°）が 1° 未満に消えた。追従中は何もしない（bind のまま）。
        if (cache != null)
        {
            foreach (KeyValuePair<Transform, Quaternion> kv in cache.gestureBindLocal)
            {
                if (kv.Key != null)
                {
                    kv.Key.localRotation = kv.Value;
                }
            }
        }

        if (cache != null && cache.ready)
        {
            AlignAnimalRootToSkeleton(instanceRoot, cache, skeletonRoot, false, tick);
        }
        else
        {
            TrackPlacementWriter.Apply(
                instanceRoot,
                TrackPlacementCommand.PositionOnly(
                    skeletonRoot,
                    instanceRoot.rotation,
                    instanceRoot.localScale));
        }

        return cache;
    }

    private void AlignAnimalRootToSkeleton(Transform instanceRoot, AnimalRigCache cache, Vector3 skeletonRoot, bool smooth, RuntimeClock.TickContext tick)
    {
        if (instanceRoot == null || cache == null)
        {
            return;
        }

        Vector3 targetPosition = TrackPlacementWriter.CalculateAnchorAlignedPosition(
            instanceRoot,
            ResolveAnimalPlacementBone(cache),
            skeletonRoot);
        if (smooth)
        {
            targetPosition = SmoothAnimalRootPosition(instanceRoot, targetPosition, tick.deltaTime);
        }

        TrackPlacementWriter.Apply(
            instanceRoot,
            TrackPlacementCommand.PositionOnly(
                targetPosition,
                instanceRoot.rotation,
                instanceRoot.localScale));
    }

    private Vector3 SmoothAnimalRootPosition(Transform root, Vector3 targetPosition, float deltaTime)
    {
        if (root == null)
        {
            return targetPosition;
        }

        return motionFilter.FilterRootPosition(root, targetPosition, deltaTime);
    }

    private static Transform ResolveAnimalPlacementBone(AnimalRigCache cache)
    {
        return AnimalPlacementBoneSelector.Select(cache.spine, cache.neck, cache.tailBase, cache.root);
    }

    // SMAL FK のあとに、四肢のボーンを keypoint の位置へ向ける（Human の AimAt に相当）。
    //
    // なぜ要るか（2026-08-28 の実測、docs/smpl-retargeting.md）:
    //   SMAL FK は「リグの bind pose を globalOrient で回したもの」に body_pose の曲げを
    //   足す形なので、**リグの T-pose が姿勢のベースラインとして焼き込まれている**。
    //   実測では body_pose を切っても誤差が 1 度しか変わらず（測定 B）、動物が自分の
    //   rest から離れている量も median 13〜15 度しかない。つまり誤差 30〜34 度の大半は
    //   「リグの T-pose が動物の T-pose と違う」ぶんで、曲げの transport をどう直しても
    //   届かない（2 軸版 jointFrameMap の A/B で確認済み）。
    //
    //   向きを直接与えれば rest 姿勢の対応そのものが要らなくなる。Human が
    //   enableKeypointAimAt で同じことをしており、実機で 2 回「外すと破綻する」と
    //   確認されている。
    //
    // **セグメントの向き（2 点の差）を使う。keypoint の絶対位置に向けてはいけない。**
    // 表示モデルは高さ 0.42m のミニチュアスケール、jointsWorld の keypoint 骨格は実寸で
    // 体長 0.87m と**約 2 倍違う**。絶対位置へ向けた第 1 版は遠位が破綻した（2026-08-28、
    // 目視で確認）。差はスケール不変なのでこの食い違いを受けない。
    //
    // 後肢 Upper（股関節→膝）は入れない。26 関節に股関節に相当する点が無く、
    // 代わりの kp7 は**尾の付け根**なので 22 度の系統誤差が乗る（実測済み）。
    // 現状の SMAL FK の 28〜40 度を残すほうがまし。
    //
    // 親から順に当てる。親を回すと子の位置が動くので順序が要る。
    // (a, b) = そのボーンが向くべきセグメント。null は AimAt しない。
    private static readonly (int a, int b)?[][] AnimalAimSegments =
    {
        // 前肢 左: 肩→肘 / 肘→手根 / 手根→前足
        new (int, int)?[] { (12, 8), (8, 14), (14, 3) },
        // 前肢 右
        new (int, int)?[] { (13, 9), (9, 15), (15, 4) },
        // 後肢 左: Upper は無し / 膝→飛節 / 飛節→後足
        new (int, int)?[] { null, (10, 16), (16, 5) },
        // 後肢 右
        new (int, int)?[] { null, (11, 17), (17, 6) },
    };

    public bool enableAnimalKeypointAimAt;

    private void ApplyAnimalLimbAimAt(AnimalRigCache cache, Vector3[] jointsWorld, byte[] vis, float alpha)
    {
        if (cache == null || jointsWorld == null || vis == null)
        {
            return;
        }

        (Transform upper, Transform lower, Transform paw)[] bones =
        {
            (cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw),
            (cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw),
            (cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw),
            (cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw),
        };

        for (int i = 0; i < bones.Length; i++)
        {
            (Transform upper, Transform lower, Transform paw) = bones[i];
            (int, int)?[] segments = AnimalAimSegments[i];
            AimAnimalBoneAlongSegment(cache, upper, jointsWorld, vis, segments[0], alpha);
            AimAnimalBoneAlongSegment(cache, lower, jointsWorld, vis, segments[1], alpha);
            AimAnimalBoneAlongSegment(cache, paw, jointsWorld, vis, segments[2], alpha);
        }
    }

    private void AimAnimalBoneAlongSegment(AnimalRigCache cache, Transform bone, Vector3[] jointsWorld, byte[] vis, (int a, int b)? segment, float alpha)
    {
        if (bone == null || !segment.HasValue)
        {
            return;
        }

        (int a, int b) = segment.Value;
        if (!TrackedJointPoints.TryGet(jointsWorld, vis, a, out Vector3 from) ||
            !TrackedJointPoints.TryGet(jointsWorld, vis, b, out Vector3 to))
        {
            return;
        }

        // ApplyAnimalBoneFromPoints は (to - from) の**向き**しか使わないので、
        // keypoint 骨格と表示モデルのスケールが違っても影響を受けない。
        ApplyAnimalBoneFromPoints(cache, bone, from, to, alpha);
    }

    private void ApplyAnimalHeadPose(AnimalRigCache cache, Vector3[] jointsWorld, byte[] vis, float alpha, bool hasControl, AnimalControlWorldData control, RuntimeClock.TickContext tick)
    {
        if (hasControl)
        {
            if (control.hasWithers && control.hasHeadRoot)
            {
                Vector3 headRoot = SmoothAnimalPoseTarget(cache.neck, control.headRootWorld, 2.0f, 0.08f, tick.deltaTime);
                ApplyAnimalBoneFromPoints(cache, cache.neck, control.withersWorld, headRoot, alpha * 0.42f);
            }

            if (control.hasHeadRoot && control.hasHeadTip)
            {
                Vector3 headRoot = SmoothAnimalPoseTarget(cache.neck, control.headRootWorld, 2.0f, 0.08f, tick.deltaTime);
                Vector3 headTip = SmoothAnimalPoseTarget(cache.head, control.headTipWorld, 2.0f, 0.08f, tick.deltaTime);
                ApplyAnimalBoneFromPoints(cache, cache.head, headRoot, headTip, alpha * 0.42f);
                return;
            }
        }

        // ここから下は **bundle が SMAL block を持たないときだけ** 走る。現行の animal
        // bundle は全フレームで SMAL block を持つので、ApplyAnimalPose 冒頭の
        // 「hasSmalPose && IsAnimalRigReadyForSmalFk」分岐が return し、この関数の以降は
        // 一度も実行されない。首の向きを直したいなら AnimalSmalFkApplier.cs を見ること
        // （2026-08-28、Docs/smpl-retargeting.md に経緯）。
        //
        // neck と head で別のセグメントを使う（2026-08-28、D-007 の対応表で訂正）。
        //
        // 以前は両方に 24→2 を渡していたが、**24 と 2 はどちらも顔の中の点**（鼻先端と
        // マズル中央）で、体長 0.756m に対し 8.5cm しか離れていない。首の向きにならない。
        //
        // 首は **両肩の中点 → 頭**。26 関節に首そのものの点は無いので、左右の肩
        // （kp12 / kp13）の中点を起点にする。SMAL 側の joint 15（Neck）の rest dir が
        // Neck→Head を向いているのと同じ意味づけ。
        //
        // head は rest pose のまま（登録済み aim-child が無く ADR-0002 の未実装）なので
        // 何を渡しても効かないが、意味としては顔の向き 18→24（頭→鼻先端）にしておく。
        if (TrackedJointPoints.TryGet(jointsWorld, vis, AnimalHeadKeypoints.LeftShoulder, out Vector3 leftShoulder) &&
            TrackedJointPoints.TryGet(jointsWorld, vis, AnimalHeadKeypoints.RightShoulder, out Vector3 rightShoulder) &&
            TrackedJointPoints.TryGet(jointsWorld, vis, AnimalHeadKeypoints.Head, out Vector3 headWorld))
        {
            ApplyAnimalBoneFromPoints(cache, cache.neck, (leftShoulder + rightShoulder) * 0.5f, headWorld, alpha * 0.35f);
        }

        ApplyAnimalBoneFromJoints(cache, cache.head, jointsWorld, vis, AnimalHeadKeypoints.Head, AnimalHeadKeypoints.Nose, alpha * 0.35f);
    }

    private void ApplyAnimalTailPose(AnimalRigCache cache, float alpha, bool hasControl, AnimalControlWorldData control, RuntimeClock.TickContext tick)
    {
        if (!hasControl || cache.tailBase == null)
        {
            return;
        }

        if (control.hasTailBase && control.hasTailTip)
        {
            Vector3 tailTip = SmoothAnimalPoseTarget(cache.tailBase, control.tailTipWorld, 1.5f, 0.04f, tick.deltaTime);
            ApplyAnimalBoneFromPoints(cache, cache.tailBase, control.tailBaseWorld, tailTip, alpha * 0.25f);
            return;
        }

        ApplyAnimalBoneFromChain(cache, cache.tailBase, null, null, control.tailWorld, alpha * 0.25f, false);
    }

    private void ApplyAnimalLimbPose(AnimalRigCache cache, Vector3[] jointsWorld, byte[] vis, float alpha, bool freezeAnimalDistal, bool hasControl, AnimalControlWorldData control, RuntimeClock.TickContext tick)
    {
        if (hasControl)
        {
            ApplyAnimalLimbIkFromControlChain(cache, cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw, control.frontLeftLegWorld, alpha, false, tick.deltaTime);
            ApplyAnimalLimbIkFromControlChain(cache, cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw, control.frontRightLegWorld, alpha, false, tick.deltaTime);
            ApplyAnimalLimbIkFromControlChain(cache, cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw, control.rearLeftLegWorld, alpha, !freezeAnimalDistal, tick.deltaTime);
            ApplyAnimalLimbIkFromControlChain(cache, cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw, control.rearRightLegWorld, alpha, !freezeAnimalDistal, tick.deltaTime);
            return;
        }

        ApplyAnimalLimbIkByChain(cache, cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw, jointsWorld, vis, AnimalPoseJointChains.LeftFront, alpha, false, tick.deltaTime);
        ApplyAnimalLimbIkByChain(cache, cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw, jointsWorld, vis, AnimalPoseJointChains.RightFront, alpha, false, tick.deltaTime);
        ApplyAnimalLimbIkByChain(cache, cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw, jointsWorld, vis, AnimalPoseJointChains.LeftRear, alpha, !freezeAnimalDistal, tick.deltaTime);
        ApplyAnimalLimbIkByChain(cache, cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw, jointsWorld, vis, AnimalPoseJointChains.RightRear, alpha, !freezeAnimalDistal, tick.deltaTime);
    }

    private void ApplyAnimalLimbByChain(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3[] jointsWorld, byte[] vis, int[] chain, float alpha, bool applyDistal)
    {
        if (chain == null || chain.Length < 4)
        {
            return;
        }

        ApplyAnimalBoneFromJoints(cache, upper, jointsWorld, vis, chain[0], chain[1], alpha * 0.9f);
        ApplyAnimalBoneFromJoints(cache, lower, jointsWorld, vis, chain[1], chain[2], alpha * 0.85f);
        if (paw != null && applyDistal)
        {
            ApplyAnimalBoneFromJoints(cache, paw, jointsWorld, vis, chain[2], chain[3], alpha * 0.7f);
        }
    }

    private void ApplyAnimalBoneFromChain(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3[] chainWorld, float alpha, bool applyDistal)
    {
        if (chainWorld == null || chainWorld.Length < 2)
        {
            return;
        }

        int upperStart = 0;
        int lowerStart = 1;
        int distalStart = 2;
        if (chainWorld.Length >= 5)
        {
            upperStart = 1;
            lowerStart = 2;
            distalStart = 3;
        }

        if (upper != null && upperStart + 1 < chainWorld.Length)
        {
            ApplyAnimalBoneFromPoints(cache, upper, chainWorld[upperStart], chainWorld[upperStart + 1], alpha * 0.9f);
        }

        if (lower != null && lowerStart + 1 < chainWorld.Length)
        {
            ApplyAnimalBoneFromPoints(cache, lower, chainWorld[lowerStart], chainWorld[lowerStart + 1], alpha * 0.85f);
        }

        if (paw != null && applyDistal && distalStart + 1 < chainWorld.Length)
        {
            ApplyAnimalBoneFromPoints(cache, paw, chainWorld[distalStart], chainWorld[distalStart + 1], alpha * 0.7f);
        }
    }

    private void ApplyAnimalLimbIkByChain(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3[] jointsWorld, byte[] vis, int[] chain, float alpha, bool applyDistal, float deltaTime)
    {
        if (chain == null || chain.Length < 4)
        {
            return;
        }

        if (!TrackedJointPoints.TryGet(jointsWorld, vis, chain[0], out Vector3 root) ||
            !TrackedJointPoints.TryGet(jointsWorld, vis, chain[1], out Vector3 mid) ||
            !TrackedJointPoints.TryGet(jointsWorld, vis, chain[2], out Vector3 end))
        {
            ApplyAnimalLimbByChain(cache, upper, lower, paw, jointsWorld, vis, chain, alpha, applyDistal);
            return;
        }

        Vector3 distal = end;
        if (applyDistal && TrackedJointPoints.TryGet(jointsWorld, vis, chain[3], out Vector3 toe))
        {
            distal = toe;
        }

        ApplyAnimalTwoBoneLimbIk(cache, upper, lower, paw, root, mid, end, distal, alpha, applyDistal, deltaTime);
    }

    private void ApplyAnimalLimbIkFromControlChain(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3[] chainWorld, float alpha, bool applyDistal, float deltaTime)
    {
        if (chainWorld == null || chainWorld.Length < 3)
        {
            ApplyAnimalBoneFromChain(cache, upper, lower, paw, chainWorld, alpha, applyDistal);
            return;
        }

        int rootIndex = 0;
        int midIndex = 1;
        int endIndex = 2;
        int distalIndex = 3;
        if (chainWorld.Length >= 5)
        {
            rootIndex = 1;
            midIndex = 2;
            endIndex = 3;
            distalIndex = 4;
        }

        if (endIndex >= chainWorld.Length)
        {
            ApplyAnimalBoneFromChain(cache, upper, lower, paw, chainWorld, alpha, applyDistal);
            return;
        }

        Vector3 distal = chainWorld[endIndex];
        if (applyDistal && distalIndex < chainWorld.Length)
        {
            distal = chainWorld[distalIndex];
        }

        ApplyAnimalTwoBoneLimbIk(cache, upper, lower, paw, chainWorld[rootIndex], chainWorld[midIndex], chainWorld[endIndex], distal, alpha, applyDistal, deltaTime);
    }

    private void ApplyAnimalTwoBoneLimbIk(AnimalRigCache cache, Transform upper, Transform lower, Transform paw, Vector3 observedRoot, Vector3 observedMid, Vector3 observedEnd, Vector3 observedDistal, float alpha, bool applyDistal, float deltaTime)
    {
        if (upper == null || lower == null)
        {
            return;
        }

        Vector3 root = upper.position;
        Vector3 targetEnd = SmoothAnimalPoseTarget(lower, observedEnd, 2.2f, 0.1f, deltaTime);
        Vector3 bendHint = observedMid - observedRoot;
        if (bendHint.sqrMagnitude <= 0.000001f)
        {
            bendHint = lower.position - upper.position;
        }

        if (!TrySolveAnimalTwoBoneMidpoint(root, targetEnd, bendHint, upper, lower, paw, out Vector3 solvedMid))
        {
            ApplyAnimalBoneFromPoints(cache, upper, observedRoot, observedMid, alpha * 0.85f);
            ApplyAnimalBoneFromPoints(cache, lower, observedMid, observedEnd, alpha * 0.8f);
        }
        else
        {
            ApplyAnimalBoneFromPoints(cache, upper, root, solvedMid, alpha * 0.9f);
            ApplyAnimalBoneFromPoints(cache, lower, lower.position, targetEnd, alpha * 0.85f);
        }

        if (paw != null && applyDistal)
        {
            Vector3 targetDistal = SmoothAnimalPoseTarget(paw, observedDistal, 2.2f, 0.1f, deltaTime);
            ApplyAnimalBoneFromPoints(cache, paw, targetEnd, targetDistal, alpha * 0.35f);
        }
    }

    private static bool TrySolveAnimalTwoBoneMidpoint(Vector3 root, Vector3 targetEnd, Vector3 bendHint, Transform upper, Transform lower, Transform paw, out Vector3 solvedMid)
    {
        solvedMid = Vector3.zero;
        if (upper == null || lower == null)
        {
            return false;
        }

        float upperLen = Vector3.Distance(upper.position, lower.position);
        float lowerLen = paw != null ? Vector3.Distance(lower.position, paw.position) : upperLen;
        if (upperLen <= 0.0001f || lowerLen <= 0.0001f)
        {
            return false;
        }

        Vector3 toTarget = targetEnd - root;
        float distance = toTarget.magnitude;
        if (distance <= 0.0001f)
        {
            return false;
        }

        Vector3 aim = toTarget / distance;
        float clampedDistance = Mathf.Clamp(distance, Mathf.Abs(upperLen - lowerLen) + 0.001f, upperLen + lowerLen - 0.001f);
        float along = (upperLen * upperLen - lowerLen * lowerLen + clampedDistance * clampedDistance) / (2f * clampedDistance);
        float heightSq = Mathf.Max(0f, upperLen * upperLen - along * along);
        float height = Mathf.Sqrt(heightSq);

        Vector3 pole = Vector3.ProjectOnPlane(bendHint, aim);
        if (pole.sqrMagnitude <= 0.000001f)
        {
            pole = Vector3.ProjectOnPlane(lower.position - upper.position, aim);
        }
        if (pole.sqrMagnitude <= 0.000001f)
        {
            pole = Vector3.ProjectOnPlane(Vector3.up, aim);
        }
        if (pole.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        pole.Normalize();
        solvedMid = root + aim * along + pole * height;
        return true;
    }

    private Vector3 SmoothAnimalPoseTarget(Transform key, Vector3 target, float minCutoffHz, float beta, float deltaTime)
    {
        if (key == null)
        {
            return target;
        }

        return motionFilter.FilterLimbTarget(key, target, minCutoffHz, beta, deltaTime);
    }

    private void RegisterAnimalAimChild(AnimalRigCache cache, Transform bone, Transform aimChild)
    {
        if (bone == null || aimChild == null)
        {
            return;
        }

        cache.aimChildByBone[bone] = aimChild;
    }

    private void RegisterAnimalAimPairs(AnimalRigCache cache, params Transform[] bones)
    {
        if (bones == null)
        {
            return;
        }

        for (int i = 0; i + 1 < bones.Length; i += 2)
        {
            RegisterAnimalAimChild(cache, bones[i], bones[i + 1]);
        }
    }

    // bind pose での「首→頭」の横振れを一度だけ測る。ここではまだ姿勢が当たっていない。
    private static void CaptureBindHeadYaw(AnimalRigCache cache)
    {
        cache.hasBindHeadYaw = false;
        if (cache.neck == null || cache.head == null || cache.root == null)
        {
            return;
        }

        // **軸は world の鉛直ではなく体の up を使う。**体が傾いているフレームで
        // 鉛直まわりに回すと「首を横に振る」動きにならず、頭がねじれる
        // （2026-09-06 に Vector3.up で実装して悪化させた）。
        Vector3 up = cache.root.TransformDirection(
            cache.modelUpLocal.sqrMagnitude > 0.001f
                ? cache.modelUpLocal.normalized
                : Vector3.up).normalized;
        Vector3 fwd = cache.root.TransformDirection(
            cache.modelForwardLocal.sqrMagnitude > 0.001f
                ? cache.modelForwardLocal.normalized
                : Vector3.back);
        fwd -= up * Vector3.Dot(fwd, up);
        // **狙うのは「顔がどこを向いているか」。**
        // 「首→頭の位置」で測ると、頭ボーン自身の向きのずれが残る。
        // 45_MountainGoat の実測: 首→頭 −41° に対し 頭→鼻 −50°（残り 9°）、
        // 38_LionessV2 は −38° に対し −52°（残り 14°）、34_Hyena は −25° に対し −49°。
        // 逆に 16_Deer1 は −12° に対し −4° で、位置基準だと 8° 行き過ぎる（2026-09-06）。
        //
        // 頭の子に鼻・顎・口のボーンがあればそれで測り、無ければ首→頭に落とす。
        Transform faceTip = FindHeadFacingChild(cache.head);
        Vector3 facing = faceTip != null
            ? faceTip.position - cache.head.position
            : cache.head.position - cache.neck.position;
        facing -= up * Vector3.Dot(facing, up);
        if (fwd.sqrMagnitude < 0.000001f || facing.sqrMagnitude < 0.000001f)
        {
            return;
        }

        cache.bindHeadYawDegrees = Vector3.SignedAngle(fwd.normalized, facing.normalized, up);
        cache.hasBindHeadYaw = true;

        // **補正は首の鎖に分散して bind 自体に焼き込む。**
        //
        // 頭ボーン 1 本で 50° 回すと、そこに折れが集中して首が「ぐにゅ」と潰れる
        // （2026-09-06 ユーザー指摘。ヤギの首は Neck1 / Neck2 / neck / head の 4 本ある）。
        // 実際の動物は首全体に分散して曲がるので、鎖の各ボーンへ等分する。
        //
        // ここは `PrimeAnimalBinds` より前に走るので、この回転を入れた状態が
        // そのまま bind として採取される。FK 側で毎フレーム補正する必要は無い。
        if (Mathf.Abs(cache.bindHeadYawDegrees) < HeadYawStraightenMinDegrees ||
            Mathf.Abs(cache.bindHeadYawDegrees) > HeadYawStraightenMaxDegrees)
        {
            return;
        }

        List<Transform> chain = new List<Transform>();
        for (Transform t = cache.head; t != null; t = t.parent)
        {
            chain.Add(t);
            if (t == cache.neck)
            {
                // 首の根まで遡る。canonical な neck の上にも中間ボーン
                // （Neck1 / Neck2 等）があることが多いので、spine / chest に当たるまで続ける。
                Transform up2 = t.parent;
                while (up2 != null && up2 != cache.spine && up2 != cache.root &&
                       up2.name.IndexOf("neck", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    chain.Add(up2);
                    up2 = up2.parent;
                }

                break;
            }
        }

        if (chain.Count == 0)
        {
            return;
        }

        // 親から順に等分して掛ける。鎖なので子には親のぶんも積み上がる。
        chain.Reverse();
        float share = -cache.bindHeadYawDegrees / chain.Count;
        Quaternion step = Quaternion.AngleAxis(share, up);
        for (int i = 0; i < chain.Count; i++)
        {
            TransformWriter.ApplyWorldRotation(chain[i], step * chain[i].rotation);
        }

        Debug.Log($"[SMAL-FK-DBG] HEADYAW model={cache.root?.name} " +
                  $"bind={cache.bindHeadYawDegrees:F1}° を首 {chain.Count} 本へ {share:F1}° ずつ分散");
    }

    // 補正を掛ける範囲。10° 未満は誤差、60° 超はリグの軸の取り方が特殊で
    // 測定が破綻するモデル（48_Puma が 179°）。
    private const float HeadYawStraightenMinDegrees = 10f;
    private const float HeadYawStraightenMaxDegrees = 60f;

    // 頭の向きを測る基準にする子ボーン。鼻・顎・口の順に探す。
    private static readonly string[] HeadFacingChildNames = { "nose", "jaw", "mouth", "muzzle" };

    private static Transform FindHeadFacingChild(Transform head)
    {
        if (head == null)
        {
            return null;
        }

        Transform[] children = head.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < HeadFacingChildNames.Length; i++)
        {
            for (int k = 0; k < children.Length; k++)
            {
                if (children[k] != null && children[k] != head &&
                    string.Equals(children[k].name, HeadFacingChildNames[i],
                                  System.StringComparison.OrdinalIgnoreCase))
                {
                    return children[k];
                }
            }
        }

        return null;
    }

    private void PrimeAnimalBind(AnimalRigCache cache, Transform bone)
    {
        if (bone == null || cache.bindRotLocal.ContainsKey(bone))
        {
            return;
        }

        cache.bindRotLocal[bone] = bone.localRotation;
        cache.bindRotWorld[bone] = bone.rotation;
        Vector3 bindDirLocal = Vector3.forward;
        if (TryGetBoneCenterDirectionWorld(cache, bone, out Vector3 bindDirWorld))
        {
            bindDirLocal = bone.InverseTransformDirection(bindDirWorld);
        }
        cache.bindDirLocal[bone] = bindDirLocal == Vector3.zero ? Vector3.forward : bindDirLocal.normalized;

        // **頭だけ照準を上書きする。**（2026-09-11）
        // 実測（[AIMBIND]）: neck / 四肢 / 尾は**すべてローカル +Y** を照準にしているのに、
        // 頭だけ aim child が登録されておらず（RegisterAnimalAimPairs に head → ? が無い）、
        // first child の head.001（頭のメッシュ）の bounds 中心へ向く経路に落ちて
        // (0.017, 0.681, -0.732) と +Y から 43 度ずれていた。
        // head.001 の bounds は extents=(0.0113, 0.0175, 0.0118) で +Y が最長なので鼻先も +Y 側。
        //
        // **キャッシュを直す**のが要点。FK の中だけで上書きすると、
        // 首の副軸が TryGetUnityRestDirWorld 経由でこのキャッシュを読むため、
        // 頭と首で別の向きを使う不整合が出る。
        if (headAimFromModelForward && bone == cache.head)
        {
            // AnimalHeadAimBaker がモデルの頭メッシュの頂点から実測した鼻先方向
            // （head ローカル）を Resources/animal_head_aim.json から引く。
            // **リグごとにローカル軸が違う**ので決め打ちにはできない。実測値:
            //   00_Dog (0.007, 0.470, -0.882) / 27_GermanShepherd (0.000, -0.219, +0.976)
            //   36_LabradorDog (0.279, 0.804, -0.526)
            if (TryGetBakedHeadAim(cache, out Vector3 baked))
            {
                cache.bindDirLocal[bone] = baked;
            }
        }

        // どの向きを「そのボーンの照準」として採ったのかを 1 度だけ出す（2026-09-11）。
        // 頭は aim child が登録されておらず、子ボーンも Renderer も無ければ
        // **既定の Vector3.forward（ローカル +Z）が黙って使われる**。
        // モデルの前方が -Z なら真逆を向く。
        {
            bool reg = cache.aimChildByBone.TryGetValue(bone, out Transform regChild) && regChild != null;
            Transform resolved = ResolveAnimalAimChild(cache, bone);
            bool got = TryGetBoneCenterDirectionWorld(cache, bone, out Vector3 dbgDir);
            string regName = reg ? regChild.name : "none";
            string resName = resolved != null ? resolved.name : "none";

            // 頭だけ照準の出所が違うので、子ボーンが何をどこに持っているかを出す。
            // 向きは **root ローカル**で出す（modelForwardLocal と同じ座標系で比べるため）。
            // このモデルはスキンではなく**剛体パーツの集合**（head.001 が頭のメッシュ、
            // er.L / er.R が耳、body が胴）。頭のメッシュ頂点から鼻先を実測する。
            // 口・鼻のボーンが無いので、頭ボーン原点から最も遠い頂点を鼻先とみなす。
            if (bone == cache.head && cache.root != null && bone.childCount > 0)
            {
                MeshFilter mf = bone.GetChild(0).GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    Transform mt = mf.transform;
                    // メッシュは Read/Write が無効で vertices が空になる。bounds を使う。
                    Vector3[] vs = mf.sharedMesh.vertices;
                    if (vs == null || vs.Length == 0)
                    {
                        Bounds bb = mf.sharedMesh.bounds;
                        float bestC = -1f; Vector3 bestCorner = Vector3.zero;
                        for (int sx = -1; sx <= 1; sx += 2)
                        for (int sy = -1; sy <= 1; sy += 2)
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            Vector3 c = mt.TransformPoint(bb.center + Vector3.Scale(bb.extents,
                                new Vector3(sx, sy, sz)));
                            float dd = (c - bone.position).sqrMagnitude;
                            if (dd > bestC) { bestC = dd; bestCorner = c; }
                        }
                        Vector3 farD = cache.root.InverseTransformDirection((bestCorner - bone.position).normalized);
                        Vector3 cenD = cache.root.InverseTransformDirection(
                            (mt.TransformPoint(bb.center) - bone.position).normalized);
                        Debug.Log("[HEADMESH] bounds center(rootLocal)=" + cenD.ToString("F3") +
                            " 最遠コーナー(rootLocal)=" + farD.ToString("F3") +
                            " extents=" + bb.extents.ToString("F4") +
                            " centerLocal=" + bb.center.ToString("F4"));
                    }
                    float best = -1f; Vector3 bestW = Vector3.zero; Vector3 sum = Vector3.zero;
                    for (int vi = 0; vi < vs.Length; vi++)
                    {
                        Vector3 w = mt.TransformPoint(vs[vi]);
                        sum += w;
                        float dd = (w - bone.position).sqrMagnitude;
                        if (dd > best) { best = dd; bestW = w; }
                    }
                    if (vs.Length > 0)
                    {
                        Vector3 farDir = cache.root.InverseTransformDirection((bestW - bone.position).normalized);
                        Vector3 cenDir = cache.root.InverseTransformDirection(((sum / vs.Length) - bone.position).normalized);
                        Debug.Log("[HEADMESH] mesh=" + mf.name + " 頂点数=" + vs.Length +
                            " 最遠点方向(rootLocal)=" + farDir.ToString("F3") +
                            " 頂点重心方向(rootLocal)=" + cenDir.ToString("F3") +
                            " 最遠距離=" + Mathf.Sqrt(best).ToString("F4"));
                    }
                }
                else { Debug.Log("[HEADMESH] head の子に MeshFilter が無い"); }
            }

            if (bone == cache.head && cache.root != null)
            {
                // cache.root は Animator の transform で、メッシュはその外にいることがある。
                // 階層の最上位から探し、頭ボーンを bones に持つものを選ぶ。
                SkinnedMeshRenderer smr = null;
                foreach (SkinnedMeshRenderer cand in bone.root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (cand == null || cand.bones == null) { continue; }
                    for (int bi = 0; bi < cand.bones.Length; bi++)
                    {
                        if (cand.bones[bi] == bone) { smr = cand; break; }
                    }
                    if (smr != null) { break; }
                }
                if (smr != null && smr.sharedMesh != null && smr.bones != null)
                {
                    int headIdx = -1;
                    for (int bi = 0; bi < smr.bones.Length; bi++)
                    {
                        if (smr.bones[bi] == bone) { headIdx = bi; break; }
                    }
                    if (headIdx >= 0)
                    {
                        Mesh mesh = smr.sharedMesh;
                        BoneWeight[] bw = mesh.boneWeights;
                        Vector3[] vs = mesh.vertices;
                        Transform mt = smr.transform;
                        float best = -1f; Vector3 bestW = Vector3.zero; int n = 0;
                        Vector3 sum = Vector3.zero;
                        for (int vi = 0; vi < vs.Length && vi < bw.Length; vi++)
                        {
                            bool onHead = (bw[vi].boneIndex0 == headIdx && bw[vi].weight0 > 0.5f);
                            if (!onHead) { continue; }
                            Vector3 w = mt.TransformPoint(vs[vi]);
                            n++; sum += w;
                            float dd = (w - bone.position).sqrMagnitude;
                            if (dd > best) { best = dd; bestW = w; }
                        }
                        if (n > 0)
                        {
                            Vector3 farDir = cache.root.InverseTransformDirection((bestW - bone.position).normalized);
                            Vector3 cenDir = cache.root.InverseTransformDirection(((sum / n) - bone.position).normalized);
                            Debug.Log("[HEADMESH] 頭の頂点数=" + n +
                                " 最遠点方向(rootLocal)=" + farDir.ToString("F3") +
                                " 重心方向(rootLocal)=" + cenDir.ToString("F3") +
                                " 距離=" + Mathf.Sqrt(best).ToString("F4"));
                        }
                        else { Debug.Log("[HEADMESH] 頭に skin された頂点が見つからない"); }
                    }
                    else { Debug.Log("[HEADMESH] SkinnedMeshRenderer の bones に頭が無い"); }
                }
                else
                {
                    Debug.Log("[HEADMESH] 頭を bones に持つ SkinnedMeshRenderer が無い。階層の中身を出す:");
                    foreach (SkinnedMeshRenderer cand in bone.root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        Debug.Log("[HEADMESH]   SMR name=" + cand.name + " bones=" +
                            (cand.bones != null ? cand.bones.Length : 0) +
                            " rootBone=" + (cand.rootBone != null ? cand.rootBone.name : "none"));
                    }
                    foreach (Renderer r in bone.root.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r is SkinnedMeshRenderer) { continue; }
                        Debug.Log("[HEADMESH]   Renderer name=" + r.name + " type=" + r.GetType().Name);
                    }
                    Transform h1 = bone.childCount > 0 ? bone.GetChild(0) : null;
                    if (h1 != null)
                    {
                        Debug.Log("[HEADMESH]   head.001 の Renderer=" +
                            (h1.GetComponent<Renderer>() != null ? h1.GetComponent<Renderer>().GetType().Name : "none") +
                            " MeshFilter=" + (h1.GetComponent<MeshFilter>() != null));
                    }
                }
            }

            if (bone == cache.head && cache.root != null)
            {
                for (int ci = 0; ci < bone.childCount; ci++)
                {
                    Transform ch = bone.GetChild(ci);
                    Vector3 dirRoot = cache.root.InverseTransformDirection(
                        (ch.position - bone.position).normalized);
                    Debug.Log("[HEADCHILD] " + ci + " name=" + ch.name +
                        " dirRootLocal=" + dirRoot.ToString("F3") +
                        " dist=" + (ch.position - bone.position).magnitude.ToString("F4"));
                }
                Debug.Log("[HEADCHILD] modelForwardLocal=" + cache.modelForwardLocal.ToString("F3") +
                    " modelUpLocal=" + cache.modelUpLocal.ToString("F3") +
                    " headBindDirRootLocal=" + cache.root.InverseTransformDirection(
                        bone.TransformDirection(cache.bindDirLocal[bone])).ToString("F3"));
            }
            Debug.Log("[AIMBIND] bone=" + bone.name + " registered=" + regName +
                " resolved=" + resName + " children=" + bone.childCount +
                " gotDir=" + got + " bindDirLocal=" + cache.bindDirLocal[bone].ToString("F3"));
        }
    }

    private void PrimeAnimalBinds(AnimalRigCache cache, params Transform[] bones)
    {
        if (bones == null)
        {
            return;
        }

        for (int i = 0; i < bones.Length; i++)
        {
            PrimeAnimalBind(cache, bones[i]);
        }
    }

    private bool ApplyAnimalBoneFromJoints(AnimalRigCache cache, Transform bone, Vector3[] jointsWorld, byte[] vis, int idxA, int idxB, float alpha)
    {
        if (bone == null)
        {
            return false;
        }

        if (!TrackedJointPoints.TryGet(jointsWorld, vis, idxA, out Vector3 a) || !TrackedJointPoints.TryGet(jointsWorld, vis, idxB, out Vector3 b))
        {
            return false;
        }

        return ApplyAnimalBoneFromPoints(cache, bone, a, b, alpha);
    }

    private bool ApplyAnimalBoneFromPoints(AnimalRigCache cache, Transform bone, Vector3 pointA, Vector3 pointB, float alpha)
    {
        if (bone == null)
        {
            return false;
        }

        Vector3 targetDir = (pointB - pointA).normalized;
        if (targetDir == Vector3.zero)
        {
            return false;
        }

        if (TryGetBoneCenterDirectionWorld(cache, bone, out Vector3 currentDir))
        {
            Quaternion deltaWorld = Quaternion.FromToRotation(currentDir, targetDir);
            Quaternion targetWorld = deltaWorld * bone.rotation;
            float dot = Vector3.Dot(currentDir, targetDir);
            if (dot > -0.98f)
            {
                TransformWriter.ApplyWorldRotation(
                    bone,
                    Quaternion.Slerp(bone.rotation, targetWorld, Mathf.Clamp01(alpha)));
                return true;
            }
        }

        Vector3 targetLocalDir = bone.parent != null ? bone.parent.InverseTransformDirection(targetDir) : targetDir;
        if (targetLocalDir == Vector3.zero)
        {
            return false;
        }
        targetLocalDir.Normalize();

        if (!cache.bindDirLocal.TryGetValue(bone, out Vector3 bindDirLocal) || bindDirLocal == Vector3.zero)
        {
            bindDirLocal = Vector3.forward;
        }

        if (!cache.bindRotLocal.TryGetValue(bone, out Quaternion bindRotLocal))
        {
            bindRotLocal = bone.localRotation;
        }

        Quaternion targetLocal = Quaternion.FromToRotation(bindDirLocal, targetLocalDir) * bindRotLocal;
        TransformWriter.ApplyLocalRotation(
            bone,
            Quaternion.Slerp(bone.localRotation, targetLocal, Mathf.Clamp01(alpha)));
        return true;
    }

    private Transform ResolveAnimalAimChild(AnimalRigCache cache, Transform bone)
    {
        if (bone == null)
        {
            return null;
        }

        Transform registeredAimChild = null;
        if (cache.aimChildByBone.TryGetValue(bone, out Transform mapped) && mapped != null)
        {
            registeredAimChild = mapped;
        }

        Transform fallbackFirstChild = bone.childCount > 0 ? bone.GetChild(0) : null;

        return AnimalAimChildSelector.Select(registeredAimChild, fallbackFirstChild);
    }

    public static bool ShouldUseAnimalAimChildPivotDirection(bool hasRegisteredAimChild, bool isLimbBone)
    {
        return AnimalAimDirectionPolicy.ShouldUseChildPivotDirection(hasRegisteredAimChild, isLimbBone);
    }

    private bool TryGetBoneCenterDirectionWorld(AnimalRigCache cache, Transform bone, out Vector3 dirWorld)
    {
        dirWorld = Vector3.zero;
        if (bone == null)
        {
            return false;
        }

        bool hasRegisteredAimChild = cache.aimChildByBone.TryGetValue(bone, out Transform registeredAimChild) &&
            registeredAimChild != null;
        Transform centerTarget = ResolveAnimalAimChild(cache, bone);
        if (centerTarget != null &&
            ShouldUseAnimalAimChildPivotDirection(hasRegisteredAimChild, IsAnimalLimbBone(cache, bone)))
        {
            Vector3 childPivotDir = centerTarget.position - bone.position;
            if (childPivotDir.sqrMagnitude > 0.000001f)
            {
                dirWorld = childPivotDir.normalized;
                return true;
            }
        }

        if (centerTarget == null)
        {
            centerTarget = bone;
        }

        if (!TryGetTransformCenterWorld(centerTarget, out Vector3 centerWorld))
        {
            return false;
        }

        Vector3 rawDir = centerWorld - bone.position;
        if (rawDir.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        dirWorld = rawDir.normalized;
        return true;
    }

    // Resources/animal_head_aim.json（AnimalHeadAimBaker が焼いたもの）。
    // キーは Animator の transform 名 = AnimalRigCache.root.name。
    private static Dictionary<string, Vector3> bakedHeadAim;
    private static Dictionary<string, float> bakedHeadRoll;

    // JSON の 1 エントリの本体から "名前": [x, y, z] を読む。
    private static bool TryReadVec3(string body, string key, out Vector3 v)
    {
        v = Vector3.zero;
        Match m = Regex.Match(body, "\"" + key +
            @"""\s*:\s*\[\s*(-?[\d.eE+-]+)\s*,\s*(-?[\d.eE+-]+)\s*,\s*(-?[\d.eE+-]+)\s*\]");
        if (!m.Success) { return false; }
        if (!float.TryParse(m.Groups[1].Value, out float x) ||
            !float.TryParse(m.Groups[2].Value, out float y) ||
            !float.TryParse(m.Groups[3].Value, out float z))
        {
            return false;
        }
        v = new Vector3(x, y, z);
        return true;
    }

    // 当てはめた頭の定数回転 C（2026-09-11）。表に "rot" があるモデルだけ。
    //
    // 頭は `tw[16] = worldFk0 * boneBindWorld * C^-1 * smalAccum[16] * C` で直接置く。
    // 従来の `bendUnity * restWorldRot` は写像 M が restWorldRot 経由で globalOrient を
    // 含むため、胴が回るたびに写像自体が揺れていた（FromToRotation は同変ではない）。
    // 実測（keypoints3d の 頭18 → 鼻24 との角度差・交差検証）:
    //   従来の式 23〜26 度 / この式 5.7〜7.5 度 / データの下限 5.1 度
    private static Dictionary<string, Quaternion> bakedHeadRot;

    internal static bool TryGetBakedHeadRot(AnimalRigCache cache, out Quaternion rot)
    {
        rot = Quaternion.identity;
        if (!TryGetBakedHeadAim(cache, out _)) { return false; }   // 表の読み込みを兼ねる
        if (bakedHeadRot == null) { return false; }
        return bakedHeadRot.TryGetValue(KeyFor(cache), out rot);
    }

    // 表を引くキー。**prefab 名**。インスタンスは Track_<id> にリネームされ、
    // Animator の transform 名もモデルによって違うので、名前からは引けない。
    private static string KeyFor(AnimalRigCache cache)
    {
        if (cache == null || cache.root == null) { return string.Empty; }
        ReplaceableModel rm = cache.root.GetComponentInParent<ReplaceableModel>();
        return rm != null && !string.IsNullOrEmpty(rm.sourcePrefabName)
            ? rm.sourcePrefabName : cache.root.name;
    }

    // このモデルが当てはめ表に載っているか。
    //
    // **載っていないモデルに頭の新経路を掛けてはいけない**（2026-09-11）。
    // 新経路は「頭も連鎖に入れる」＋「jointFrameMap を FromToRotation に固定」で、
    // どちらも**当てはめた照準とロールがあって初めて成立する**。
    // 表に無いモデルは照準が頭メッシュの重心のままなので、連鎖で入力が大きくなるぶん
    // 誤差も増え、ロールも 0 のままで拘束されない。従来経路（連鎖から外す＋首を副軸にした
    // 2 軸基底）に残す。
    internal static bool HasBakedHeadFit(AnimalRigCache cache)
    {
        return TryGetBakedHeadAim(cache, out _);
    }

    // 当てはめたロール（度）。表に無ければ 0。
    internal static float GetBakedHeadRoll(AnimalRigCache cache)
    {
        if (cache == null || cache.root == null || bakedHeadRoll == null) { return 0f; }
        ReplaceableModel rm = cache.root.GetComponentInParent<ReplaceableModel>();
        string key = rm != null && !string.IsNullOrEmpty(rm.sourcePrefabName)
            ? rm.sourcePrefabName : cache.root.name;
        return bakedHeadRoll.TryGetValue(key, out float v) ? v : 0f;
    }

    private static bool TryGetBakedHeadAim(AnimalRigCache cache, out Vector3 aimLocal)
    {
        aimLocal = Vector3.zero;
        if (cache == null || cache.root == null) { return false; }
        if (bakedHeadAim == null)
        {
            bakedHeadAim = new Dictionary<string, Vector3>();
            bakedHeadRoll = new Dictionary<string, float>();
            bakedHeadRot = new Dictionary<string, Quaternion>();
            TextAsset ta = Resources.Load<TextAsset>("animal_head_fit");
            if (ta != null)
            {
                // 形式: { "00_Dog": { "aim": [x,y,z], "rot": [ex,ey,ez] }, ... }
                // 旧形式の "rollDeg" も読む（rot が無いモデルは従来の経路に落ちる）。
                //
                // **rot は Unity の eulerAngles（度）で持つ。**当てはめは Python 側で
                // 3x3 行列として解いているが、行列 -> quaternion を右手系の公式で書くと
                // 符号の取り違えが起きる。ログの eulerAngles を再現する式をそのまま
                // 逆に解いた値を渡し、ここで Quaternion.Euler に食わせる。
                foreach (Match m in Regex.Matches(ta.text,
                    @"""([^""_][^""]*)""\s*:\s*\{([^}]*)\}"))
                {
                    // このメソッドの後半に別の `key` があるので名前を分ける（CS0136）。
                    string entryKey = m.Groups[1].Value;
                    string body = m.Groups[2].Value;
                    if (!TryReadVec3(body, "aim", out Vector3 aim)) { continue; }
                    bakedHeadAim[entryKey] = aim;
                    if (TryReadVec3(body, "rot", out Vector3 e))
                    {
                        bakedHeadRot[entryKey] = Quaternion.Euler(e.x, e.y, e.z);
                    }
                    Match r = Regex.Match(body, @"""rollDeg""\s*:\s*(-?[\d.eE+-]+)");
                    if (r.Success && float.TryParse(r.Groups[1].Value, out float roll))
                    {
                        bakedHeadRoll[entryKey] = roll;
                    }
                }
                Debug.Log("[HEADAIMBAKE] 読み込んだ " + bakedHeadAim.Count + " 件 (rot あり "
                    + bakedHeadRot.Count + " 件)");
            }
            else { Debug.Log("[HEADAIMBAKE] animal_head_fit.json が無い"); }
        }
        // **キーは prefab 名。**インスタンスは Track_<id> にリネームされ、
        // Animator の transform 名もモデルによって違う（00_Dog は "dog"、
        // 36_LabradorDog は root 自身）ので、名前からは引けない。
        ReplaceableModel rm = cache.root.GetComponentInParent<ReplaceableModel>();
        string key = rm != null && !string.IsNullOrEmpty(rm.sourcePrefabName)
            ? rm.sourcePrefabName : cache.root.name;
        if (!bakedHeadAim.TryGetValue(key, out Vector3 v) || v.sqrMagnitude < 0.000001f)
        {
            return false;
        }
        aimLocal = v.normalized;
        return true;
    }

    private static bool IsAnimalLimbBone(AnimalRigCache cache, Transform bone)
    {
        if (bone == null)
        {
            return false;
        }

        return
            bone == cache.leftFrontUpper ||
            bone == cache.leftFrontLower ||
            bone == cache.leftFrontPaw ||
            bone == cache.rightFrontUpper ||
            bone == cache.rightFrontLower ||
            bone == cache.rightFrontPaw ||
            bone == cache.leftRearUpper ||
            bone == cache.leftRearLower ||
            bone == cache.leftRearPaw ||
            bone == cache.rightRearUpper ||
            bone == cache.rightRearLower ||
            bone == cache.rightRearPaw;
    }

    private static bool TryGetTransformCenterWorld(Transform target, out Vector3 centerWorld)
    {
        centerWorld = Vector3.zero;
        if (target == null)
        {
            return false;
        }

        SkinnedMeshRenderer smr = target.GetComponent<SkinnedMeshRenderer>();
        if (smr != null)
        {
            centerWorld = target.TransformPoint(smr.localBounds.center);
            return true;
        }

        MeshFilter mf = target.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            centerWorld = target.TransformPoint(mf.sharedMesh.bounds.center);
            return true;
        }

        Renderer renderer = target.GetComponent<Renderer>();
        if (renderer != null)
        {
            centerWorld = renderer.bounds.center;
            return true;
        }

        return false;
    }

    // 診断用。既に構築済みのキャッシュを読むだけで、新規構築はしない。
    // [ANIMALKP]（実ボーンと keypoints3d の投影差）が使う。
    // 診断用。ボーンが現在向いている方向（適用側が使うのと同じ定義）を返す。
    // [ANIMALKP] が「keypoint が示す方向」との角度差を測るのに使う。
    internal bool TryGetBoneDirectionForDiag(AnimalRigCache cache, Transform bone, out Vector3 dirWorld)
    {
        dirWorld = Vector3.zero;
        return cache != null && bone != null && TryGetBoneCenterDirectionWorld(cache, bone, out dirWorld);
    }

    internal AnimalRigCache PeekAnimalRigCache(GameObject instance)
    {
        if (instance == null)
        {
            return null;
        }

        Animator animator = instance.GetComponentInChildren<Animator>(true);
        Transform rigRoot = animator != null ? animator.transform : instance.transform;
        return rigRoot != null && animalRigCaches.TryGetValue(rigRoot, out AnimalRigCache cache) ? cache : null;
    }

    private AnimalRigCache GetOrBuildAnimalRigCache(Transform root, Transform skinSearchRoot, AnimalPoseSettings settings)
    {
        if (root == null)
        {
            return null;
        }

        if (animalRigCaches.TryGetValue(root, out AnimalRigCache existing))
        {
            return existing;
        }

        AnimalRigCache cache = new AnimalRigCache();
        cache.root = root;
        Transform[] bones = root.GetComponentsInChildren<Transform>(true);
        AnimalBoneMappingOverride boneOverride = root.GetComponentInChildren<AnimalBoneMappingOverride>();

        cache.neck = ResolveBone(bones, boneOverride?.neck, AnimalRigDefinition.Neck);
        cache.head = ResolveBone(bones, boneOverride?.head, AnimalRigDefinition.Head);
        cache.spine = ResolveBone(bones, boneOverride?.spine, AnimalRigDefinition.Spine);
        cache.tailBase = ResolveBoneByTokens(bones, boneOverride?.tailBase, AnimalRigDefinition.TailBaseTokens);

        FillAnimalSpineFallbacks(cache, root, bones, settings);

        cache.leftFrontUpper = ResolveBone(bones, boneOverride?.frontLUpper, AnimalRigDefinition.LeftFrontUpper);
        cache.leftFrontLower = ResolveBone(bones, boneOverride?.frontLLower, AnimalRigDefinition.LeftFrontLower);
        cache.leftFrontPaw = ResolveBone(bones, boneOverride?.frontLPaw, AnimalRigDefinition.LeftFrontPaw);
        cache.rightFrontUpper = ResolveBone(bones, boneOverride?.frontRUpper, AnimalRigDefinition.RightFrontUpper);
        cache.rightFrontLower = ResolveBone(bones, boneOverride?.frontRLower, AnimalRigDefinition.RightFrontLower);
        cache.rightFrontPaw = ResolveBone(bones, boneOverride?.frontRPaw, AnimalRigDefinition.RightFrontPaw);
        cache.leftRearUpper = ResolveBone(bones, boneOverride?.rearLUpper, AnimalRigDefinition.LeftRearUpper);
        cache.leftRearLower = ResolveBone(bones, boneOverride?.rearLLower, AnimalRigDefinition.LeftRearLower);
        cache.leftRearPaw = ResolveBone(bones, boneOverride?.rearLPaw, AnimalRigDefinition.LeftRearPaw);
        cache.leftRearToe = ResolveBone(bones, boneOverride?.rearLToe, AnimalRigDefinition.LeftRearToe);
        cache.rightRearUpper = ResolveBone(bones, boneOverride?.rearRUpper, AnimalRigDefinition.RightRearUpper);
        cache.rightRearLower = ResolveBone(bones, boneOverride?.rearRLower, AnimalRigDefinition.RightRearLower);
        cache.rightRearPaw = ResolveBone(bones, boneOverride?.rearRPaw, AnimalRigDefinition.RightRearPaw);
        cache.rightRearToe = ResolveBone(bones, boneOverride?.rearRToe, AnimalRigDefinition.RightRearToe);
        cache.tailMid = ResolveBone(bones, boneOverride?.tailMid, AnimalRigDefinition.TailMid);
        cache.tailTip = ResolveBone(bones, boneOverride?.tailTip, AnimalRigDefinition.TailTip);
        ResolveAnimalModelBasis(root, cache, settings);
        CaptureBodyRightBindWorld(root, cache);
        if (boneOverride != null)
        {
            CaptureGestureCanonicalLimbs(bones, cache);
        }

        CaptureGestureBindLocals(cache);
        CaptureBindHeadYaw(cache);

        if (cache.spine != null && cache.neck != null)
        {
            Vector3 spineToNeck = cache.neck.position - cache.spine.position;
            if (spineToNeck.sqrMagnitude > 0.000001f)
            {
                cache.spineToNeckBindDirWorld = spineToNeck.normalized;
            }
        }

        // Registered before PrimeAnimalBinds (not after, as it read previously): PrimeAnimalBind
        // captures bindDirLocal via ResolveAnimalAimChild, which prefers the registered aim
        // child over a bone's natural first Unity child. For leg/neck bones the two happened
        // to already coincide (their real first child always is the next canonical joint), so
        // the old order never visibly mattered there - but tailMid's first real Unity child is
        // often an intermediate undriven bone before tailTip (e.g. Buffalo's Tail2 -> Tail3 ->
        // Tail4, where Tail3 has no canonical name), so tail needs the registration to actually
        // be in effect at prime time.
        RegisterAnimalAimPairs(
            cache,
            cache.leftFrontUpper, cache.leftFrontLower,
            cache.leftFrontLower, cache.leftFrontPaw,
            cache.rightFrontUpper, cache.rightFrontLower,
            cache.rightFrontLower, cache.rightFrontPaw,
            cache.leftRearUpper, cache.leftRearLower,
            cache.leftRearLower, cache.leftRearPaw,
            cache.rightRearUpper, cache.rightRearLower,
            cache.rightRearLower, cache.rightRearPaw,
            cache.neck, cache.head,
            cache.spine, cache.neck,
            cache.tailBase, cache.tailMid,
            cache.tailMid, cache.tailTip);

        PrimeAnimalBinds(
            cache,
            cache.neck, cache.head, cache.spine, cache.tailBase,
            cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw,
            cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw,
            cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw,
            cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw,
            cache.leftRearToe, cache.rightRearToe,
            cache.tailMid, cache.tailTip);

        // spineToNeckBindDirWorld is kept in world space (bind-time direction from spine to neck).
        // The rootYawFix detection formula uses: candidate * modelOrientFix * spineToNeckBindDirWorld,
        // which correctly predicts the neck world direction because:
        //   tw[0] = candidate * modelOrientFix * S  →  neck_world = tw[0] * Inv(S) * spineToNeck_world
        //                                            = candidate * modelOrientFix * spineToNeck_world
        // (see rawWorldFk0 formula in TryApplyAnimalSmalFk for the modelOrientFix definition).

        cache.ready =
            cache.head != null ||
            cache.leftFrontUpper != null ||
            cache.rightFrontUpper != null ||
            cache.leftRearUpper != null ||
            cache.rightRearUpper != null;
        animalRigCaches[root] = cache;

        // One-time diagnostic: confirm the bones we resolved are the same Transforms a
        // SkinnedMeshRenderer actually deforms with. If a resolved bone (e.g. leftFrontUpper)
        // is NOT in any renderer's bones[] array, rotating it will have zero visible effect on
        // the rendered mesh even though the FK math runs correctly every frame.
        // Search from skinSearchRoot (the model instance root), not the Armature/animator
        // subtree: SkinnedMeshRenderer is commonly a sibling of the Armature, not a descendant.
        LogAnimalBoneSkinningCheck(skinSearchRoot != null ? skinSearchRoot : root, cache);

        return cache;
    }

    private static void LogAnimalBoneSkinningCheck(Transform root, AnimalRigCache cache)
    {
        SkinnedMeshRenderer[] renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        HashSet<Transform> skinnedBones = new HashSet<Transform>();
        for (int i = 0; i < renderers.Length; i++)
        {
            Transform[] bones = renderers[i].bones;
            if (bones == null) continue;
            for (int j = 0; j < bones.Length; j++)
                if (bones[j] != null) skinnedBones.Add(bones[j]);
        }

        void Check(string label, Transform bone)
        {
            if (bone == null)
            {
                Debug.Log($"[SMAL-SKIN-CHECK] {label}=NULL (not found by name matching)");
                return;
            }
            bool isSkinned = skinnedBones.Contains(bone);
            Debug.Log($"[SMAL-SKIN-CHECK] {label}={bone.name} isSkinnedByAnyRenderer={isSkinned}");
        }

        Debug.Log($"[SMAL-SKIN-CHECK] root={root.name} skinnedMeshRendererCount={renderers.Length} totalSkinnedBoneRefs={skinnedBones.Count}");
        Check("head", cache.head);
        Check("neck", cache.neck);
        Check("spine", cache.spine);
        Check("leftFrontUpper", cache.leftFrontUpper);
        Check("leftFrontLower", cache.leftFrontLower);
        Check("leftFrontPaw", cache.leftFrontPaw);
        Check("rightFrontUpper", cache.rightFrontUpper);
        Check("rightFrontLower", cache.rightFrontLower);
        Check("rightFrontPaw", cache.rightFrontPaw);
        Check("leftRearUpper", cache.leftRearUpper);
        Check("leftRearLower", cache.leftRearLower);
        Check("leftRearPaw", cache.leftRearPaw);
        Check("rightRearUpper", cache.rightRearUpper);
        Check("rightRearLower", cache.rightRearLower);
        Check("rightRearPaw", cache.rightRearPaw);
        Check("tailBase", cache.tailBase);
        Check("tailMid", cache.tailMid);
        Check("tailTip", cache.tailTip);
    }

    // F2（animalFrontLimbBodyLateralSecondary、2026-10-04、MAP）の副軸の Unity 側 = 捕捉時の world での体の右。
    // TryApplyAnimalSmalFk の modelOrientFix と同じく前を水平にしてから Cross(up, 前)（Unity の左手系の数値で「右」）。
    // 脚の割り当ての表で役の骨が変わったモデルについて、四肢のジェスチャを今までどおり正規名の骨に乗せるための控え（2026-10-04）。
    // 正規名の骨が見つからない、または役の骨と同じなら控えない（表に無いモデルは何も変わらない）。
    private void CaptureGestureCanonicalLimbs(Transform[] bones, AnimalRigCache cache)
    {
        cache.gestureCanonicalLimbs.Clear();
        AddGestureCanonical(cache, AnimalGesturePoint.FrontLeftPaw, FindAnimalBone(bones, AnimalRigDefinition.LeftFrontPaw), cache.leftFrontPaw);
        AddGestureCanonical(cache, AnimalGesturePoint.FrontRightPaw, FindAnimalBone(bones, AnimalRigDefinition.RightFrontPaw), cache.rightFrontPaw);
        AddGestureCanonical(cache, AnimalGesturePoint.RearLeftPaw, FindAnimalBone(bones, AnimalRigDefinition.LeftRearPaw), cache.leftRearPaw);
        AddGestureCanonical(cache, AnimalGesturePoint.RearRightPaw, FindAnimalBone(bones, AnimalRigDefinition.RightRearPaw), cache.rightRearPaw);
        AddGestureCanonical(cache, AnimalGesturePoint.FrontLeftUpper, FindAnimalBone(bones, AnimalRigDefinition.LeftFrontUpper), cache.leftFrontUpper);
        AddGestureCanonical(cache, AnimalGesturePoint.FrontRightUpper, FindAnimalBone(bones, AnimalRigDefinition.RightFrontUpper), cache.rightFrontUpper);
        AddGestureCanonical(cache, AnimalGesturePoint.RearLeftUpper, FindAnimalBone(bones, AnimalRigDefinition.LeftRearUpper), cache.leftRearUpper);
        AddGestureCanonical(cache, AnimalGesturePoint.RearRightUpper, FindAnimalBone(bones, AnimalRigDefinition.RightRearUpper), cache.rightRearUpper);
        AddGestureCanonical(cache, AnimalGesturePoint.FrontLeftLower, FindAnimalBone(bones, AnimalRigDefinition.LeftFrontLower), cache.leftFrontLower);
        AddGestureCanonical(cache, AnimalGesturePoint.FrontRightLower, FindAnimalBone(bones, AnimalRigDefinition.RightFrontLower), cache.rightFrontLower);
        AddGestureCanonical(cache, AnimalGesturePoint.RearLeftLower, FindAnimalBone(bones, AnimalRigDefinition.LeftRearLower), cache.leftRearLower);
        AddGestureCanonical(cache, AnimalGesturePoint.RearRightLower, FindAnimalBone(bones, AnimalRigDefinition.RightRearLower), cache.rightRearLower);
    }

    private static void AddGestureCanonical(AnimalRigCache cache, AnimalGesturePoint point, Transform canonical, Transform role)
    {
        if (canonical != null && canonical != role)
        {
            cache.gestureCanonicalLimbs[point] = canonical;
        }
    }

    // ジェスチャを乗せる骨のうち FK が毎 tick 書き直さないものの bind の局所回転を控える（キャッシュを作る時点 = まだ誰も書いていない bind）。
    // - 正規名の骨で、どの役にも入っていないもの（表の RollStyle の行の Fox・Beaver の front_*_lower など。役の骨は SMAL の FK が毎 tick 書く）
    // - root が Animator の子のとき（00_Dog の "dog"）。インスタンスの root はインタラクティブモーション中に毎 tick 置き直されるので要らない
    private static void CaptureGestureBindLocals(AnimalRigCache cache)
    {
        cache.gestureBindLocal.Clear();
        var roles = new HashSet<Transform>
        {
            cache.spine, cache.neck, cache.head, cache.tailBase, cache.tailMid, cache.tailTip,
            cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw, cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw,
            cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw, cache.leftRearToe,
            cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw, cache.rightRearToe,
        };
        foreach (Transform canonical in cache.gestureCanonicalLimbs.Values)
        {
            if (canonical != null && !roles.Contains(canonical))
            {
                cache.gestureBindLocal[canonical] = canonical.localRotation;
            }
        }

        if (cache.root != null && cache.root.GetComponent<ReplaceableModel>() == null)
        {
            cache.gestureBindLocal[cache.root] = cache.root.localRotation;
        }
    }

    private static void CaptureBodyRightBindWorld(Transform root, AnimalRigCache cache)
    {
        cache.bodyRightBindWorld = Vector3.zero;
        if (root == null)
        {
            return;
        }

        // F2 を使わないモデル（AnimalLegMappingFix.FrontLimbBodyLateralExcluded）は体の右を採らない（零のままなら F2 の分岐に入らず、首の副軸のまま）。
        ReplaceableModel model = root.GetComponentInParent<ReplaceableModel>();
        if (model != null && AnimalLegMappingFix.FrontLimbBodyLateralExcluded.Contains(AnimalLegMappingFix.Key(model.sourcePrefabName)))
        {
            return;
        }

        Vector3 fRaw = cache.modelForwardLocal.sqrMagnitude > 0.001f ? cache.modelForwardLocal.normalized : Vector3.back;
        Vector3 fFlat = new Vector3(fRaw.x, 0f, fRaw.z);
        Vector3 fLoc = fFlat.sqrMagnitude > 0.001f ? fFlat.normalized : fRaw;
        Vector3 uLoc = cache.modelUpLocal.sqrMagnitude > 0.001f ? cache.modelUpLocal.normalized : Vector3.up;
        Vector3 rLoc = Vector3.Cross(uLoc, fLoc);
        if (rLoc.sqrMagnitude > 1e-6f)
        {
            cache.bodyRightBindWorld = root.TransformDirection(rLoc).normalized;
        }
    }

    private static void ResolveAnimalModelBasis(Transform root, AnimalRigCache cache, AnimalPoseSettings settings)
    {
        if (root == null || cache == null)
        {
            return;
        }

        if (TryAverageAnimalBonePosition(cache.leftFrontUpper, cache.rightFrontUpper, out Vector3 frontCenter) &&
            TryAverageAnimalBonePosition(cache.leftRearUpper, cache.rightRearUpper, out Vector3 rearCenter))
        {
            Debug.Log($"[SMAL-SKIN-CHECK] BASIS frontCenter={frontCenter:F3} rearCenter={rearCenter:F3} " +
                $"leftFrontUpper.pos={(cache.leftFrontUpper != null ? cache.leftFrontUpper.position : Vector3.zero):F3} " +
                $"leftRearUpper.pos={(cache.leftRearUpper != null ? cache.leftRearUpper.position : Vector3.zero):F3} " +
                $"head.pos={(cache.head != null ? cache.head.position : Vector3.zero):F3} " +
                $"tailBase.pos={(cache.tailBase != null ? cache.tailBase.position : Vector3.zero):F3}");

            Vector3 forwardWorld = frontCenter - rearCenter;
            if (forwardWorld.sqrMagnitude > 0.000001f)
            {
                cache.modelForwardLocal = root.InverseTransformDirection(forwardWorld).normalized;
            }
        }

        if (cache.modelForwardLocal.sqrMagnitude <= 0.000001f)
        {
            cache.modelForwardLocal = settings.animalModelForwardLocal.sqrMagnitude > 0.000001f
                ? settings.animalModelForwardLocal.normalized
                : Vector3.forward;
        }

        cache.modelUpLocal = settings.animalModelUpLocal.sqrMagnitude > 0.000001f
            ? settings.animalModelUpLocal.normalized
            : Vector3.up;
    }

    private static bool TryAverageAnimalBonePosition(Transform left, Transform right, out Vector3 center)
    {
        if (left != null && right != null)
        {
            center = (left.position + right.position) * 0.5f;
            return true;
        }

        Transform single = left != null ? left : right;
        if (single != null)
        {
            center = single.position;
            return true;
        }

        center = Vector3.zero;
        return false;
    }

    private void FillAnimalSpineFallbacks(AnimalRigCache cache, Transform root, Transform[] bones, AnimalPoseSettings settings)
    {
        if (cache == null || root == null || bones == null)
        {
            return;
        }

        List<Transform> spineBones = new List<Transform>();
        for (int i = 0; i < bones.Length; i++)
        {
            Transform bone = bones[i];
            if (bone == null)
            {
                continue;
            }

            string name = bone.name.ToLowerInvariant();
            if (name == "def-spine" || name.StartsWith("def-spine."))
            {
                spineBones.Add(ResolveLikelyRigBone(bone));
            }
        }

        if (spineBones.Count == 0)
        {
            return;
        }

        Vector3 forwardLocal = settings.animalModelForwardLocal.sqrMagnitude > 0.000001f
            ? settings.animalModelForwardLocal.normalized
            : Vector3.forward;
        Vector3 forwardWorld = root.TransformDirection(forwardLocal);
        if (forwardWorld.sqrMagnitude <= 0.000001f)
        {
            forwardWorld = root.forward;
        }
        forwardWorld.Normalize();

        spineBones.Sort((a, b) =>
            Vector3.Dot(a.position - root.position, forwardWorld)
                .CompareTo(Vector3.Dot(b.position - root.position, forwardWorld)));

        Transform back = spineBones[0];
        Transform front = spineBones[spineBones.Count - 1];
        Transform neck = spineBones[Mathf.Max(0, spineBones.Count - 2)];
        Transform body = spineBones[Mathf.Clamp(spineBones.Count / 2, 0, spineBones.Count - 1)];

        if (cache.spine == null ||
            AnimalDefSpineFallbackPolicy.ShouldReplaceCanonicalSpineWithDefSpineChain(
                cache.tailBase != null,
                spineBones.Count))
        {
            cache.spine = body;
        }
        if (cache.neck == null)
        {
            cache.neck = neck;
        }
        if (cache.head == null)
        {
            cache.head = front;
        }
        if (cache.tailBase == null && back != cache.head)
        {
            cache.tailBase = back;
        }
    }

    private Transform FindAnimalBone(Transform[] bones, string[] exactNames, params string[] tokens)
    {
        Transform exact = FindBoneByExactNames(bones, exactNames);
        return exact ?? FindBoneByTokens(bones, tokens);
    }

    private Transform FindAnimalBone(Transform[] bones, AnimalBoneRule rule)
    {
        return FindAnimalBone(bones, rule.exactNames, rule.tokens);
    }

    private Transform ResolveBone(Transform[] bones, string overrideName, AnimalBoneRule rule)
    {
        if (!string.IsNullOrEmpty(overrideName))
            return FindBoneByExactNames(bones, overrideName);
        return FindAnimalBone(bones, rule);
    }

    private Transform ResolveBoneByTokens(Transform[] bones, string overrideName, params string[] tokens)
    {
        if (!string.IsNullOrEmpty(overrideName))
            return FindBoneByExactNames(bones, overrideName);
        return FindBoneByTokens(bones, tokens);
    }

    private Transform FindBoneByTokens(Transform[] bones, params string[] tokens)
    {
        if (bones == null || tokens == null || tokens.Length == 0)
        {
            return null;
        }

        Transform best = null;
        int bestScore = int.MinValue;
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (string.IsNullOrEmpty(token))
            {
                continue;
            }

            string needle = token.ToLowerInvariant();
            for (int j = 0; j < bones.Length; j++)
            {
                Transform bone = bones[j];
                if (bone == null)
                {
                    continue;
                }

                string name = bone.name.ToLowerInvariant();
                if (name.Contains(needle))
                {
                    int score = 0;
                    if (bone.GetComponent<Renderer>() == null)
                    {
                        score += 2;
                    }
                    if (bone.childCount > 0)
                    {
                        score += 1;
                    }
                    if (name == needle)
                    {
                        score += 2;
                    }
                    else if (name.StartsWith(needle))
                    {
                        score += 1;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = bone;
                    }
                }
            }
        }

        return ResolveLikelyRigBone(best);
    }

    private Transform FindBoneByExactNames(Transform[] bones, params string[] exactNames)
    {
        if (bones == null || exactNames == null || exactNames.Length == 0)
        {
            return null;
        }

        for (int i = 0; i < exactNames.Length; i++)
        {
            string exact = exactNames[i];
            if (string.IsNullOrEmpty(exact))
            {
                continue;
            }

            for (int j = 0; j < bones.Length; j++)
            {
                Transform bone = bones[j];
                if (bone == null)
                {
                    continue;
                }

                if (bone.name == exact)
                {
                    return ResolveLikelyRigBone(bone);
                }
            }
        }

        return null;
    }

    private static Transform ResolveLikelyRigBone(Transform node)
    {
        if (node == null)
        {
            return null;
        }

        bool hasMesh =
            node.GetComponent<MeshRenderer>() != null ||
            node.GetComponent<MeshFilter>() != null ||
            node.GetComponent<SkinnedMeshRenderer>() != null;
        if (hasMesh && node.parent != null)
        {
            return node.parent;
        }

        return node;
    }

}
