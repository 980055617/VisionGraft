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
    // includeNeckChain: 首の中間の骨（smalDriveNeckChain で FK が書く）も控える（2026-10-07、既定の姿勢から戻すイベントだけ。立ち姿と追従の差が大きく、
    // 役の骨 20 本だけを混ぜると首の鎖がハンドオフの最初の tick で跳ぶため）。
    public void CaptureBoneLocalRotations(Transform instanceRoot, Dictionary<Transform, Quaternion> destination, bool includeNeckChain = false)
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
        if (includeNeckChain)
        {
            foreach (Transform chainBone in cache.neckChainBindLocal.Keys)
            {
                AddBoneLocalRotation(destination, chainBone);
            }
        }

        // 尾の鎖の骨（smalTailFullChain で FK が書く中間の骨を含む、2026-10-07）。混ぜないと中間の骨だけハンドオフの最初の tick で跳ぶ。
        AddSmalTailChainBoneLocalRotations(cache, destination);
        // 前足（smalDriveFeet、2026-10-08）: FK が書いた前足（役の骨ではない front_x_paw など）も控える。書いていなければ何もしない。
        AddSmalFrontFootBoneLocalRotations(cache, destination);
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
    // 頭の左右の軸は、bind のときの体の右（cache.bodyRightBindWorld）を頭ローカルにしたもの。取れないモデル（F2 の対象外。2026-10-09 に 16_Deer1 を外して今は無い）と、
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
            smalDefaultPoseWeight = request.defaultPoseWeight;
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
    // 非四足モード（smalNonQuadrupedRig、2026-10-08、2026-10-09 に既定 ON）: 前肢の上の役が左右とも無いリグ（鳥、cache.smalNoFrontLimbs）は spine と後肢の上 2 本で通す。
    // 前肢の関節 7〜14 は骨が無いので主ループの「骨なし」の分岐（計算だけ、何も書かない）に入る。smalNoFrontLimbs が false なら (A && B) || false で今と同じ。
    private static bool IsAnimalRigReadyForSmalFk(AnimalRigCache cache)
    {
        return cache != null
            && cache.spine != null
            && ((cache.leftFrontUpper != null && cache.rightFrontUpper != null) || cache.smalNoFrontLimbs)
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

    // 既定 ON（2026-10-08、全関節の監査 J-18 animalBindWithoutManualRotation。2026-10-09 に採用）。プレイヤーの TryApplyAnimalPosePipeline が、キャッシュがまだ無く
    // 手動の回転があるフレームに Apply の直前で呼ぶ。インスタンスの root を手動の回転を除いた配置の回転に置いてリグのキャッシュ（bind）を作り、
    // root を元の局所回転へ戻す。今は配置が root に「手動の回転 × 基底 × prefab」（ManualRotationMath.Apply は手動の回転を左から掛ける）を書いた後に bind を採るので、手動の回転 θ が bindRotWorld・
    // spineToNeckBindDirWorld・bodyRightBindWorld に入り、SMAL の FK（rawWorldFk0 = ExtractYawOnly(root) × …）が root の yaw をもう一度掛ける
    // （queue_syn1 の an_yawswap: bind が全 20 骨とも +y まわり 60.000°、体が回さない走り＋129.5°（p50）。rootYawFix の判定の dot0 も 0.992 → 0.511）。
    // 回すのは Animator の Transform ではなくインスタンスの root（Animator が子にあるリグは CaptureGestureBindLocals がその局所回転を控えて
    // 毎 tick 書き戻すので、そちらを回すと仮の回転が残る）。キャッシュを作る間に骨へ書くのは CaptureBindHeadYaw の首の鎖だけで、
    // その軸（体の up）も root から作るので、書く局所回転は root の回転に依らない。
    // リグのキャッシュがもうあるか（PrebuildRigCacheWithoutManualRotation と同じ鍵: Animator があればその Transform、無ければ root）。
    public bool HasRigCache(Transform instanceRoot, Animator animator)
    {
        Transform rigRoot = animator != null ? animator.transform : instanceRoot;
        return rigRoot != null && animalRigCaches.ContainsKey(rigRoot);
    }

    public void PrebuildRigCacheWithoutManualRotation(Transform instanceRoot, Animator animator, Quaternion rootRotationWithoutManual, AnimalPoseSettings settings)
    {
        Transform rigRoot = animator != null ? animator.transform : instanceRoot;
        if (instanceRoot == null || rigRoot == null || animalRigCaches.ContainsKey(rigRoot))
        {
            return;
        }

        Quaternion savedLocalRotation = instanceRoot.localRotation;
        float removedDegrees = Quaternion.Angle(instanceRoot.rotation, rootRotationWithoutManual);
        TransformWriter.ApplyWorldRotation(instanceRoot, rootRotationWithoutManual);
        try
        {
            GetOrBuildAnimalRigCache(rigRoot, instanceRoot, settings);
        }
        finally
        {
            TransformWriter.ApplyLocalRotation(instanceRoot, savedLocalRotation);
        }

        Debug.Log($"[ANIMAL-BIND] model={instanceRoot.name} bind を手動の回転を除いた配置の回転で採った（外した回転 {removedDegrees:F2}°）");
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
        // 既定 ON（2026-10-08、非四足モード smalNonQuadrupedRig。2026-10-09 に採用）: 名簿のモデルだけ、役を AnimalNonQuadrupedRig.Table の行で付け替え、cache.smalNonQuadruped・
        // smalNoFrontLimbs を決める。役を決めた直後なので、この後に控える bind（modelForwardLocal・bindRotWorld/Local・bindDirLocal・胴の鎖など）は新しい役で採る。
        ApplyNonQuadrupedRoles(cache, bones);
        // 既定 OFF（2026-10-08、J-03 smalLegReferenceSkinPose）: 名簿のモデルだけ、脚の鎖の局所回転を skin 姿勢へ一度だけ書く。役の骨を決めた直後で、
        // ResolveAnimalModelBasis・CaptureGestureBindLocals・PrimeAnimalBinds・EnsureGestureAnatomy より前なので、この後に控える bind
        // （modelForwardLocal・bindRotWorld/Local・bindDirLocal・gestureBindLocal・肩甲骨の bind）はすべて skin の脚になる。OFF なら何もしない。
        ApplyLegReferenceSkinPose(cache, bones);
        // 既定 ON（2026-10-08、smalNonQuadrupedReferencePose。2026-10-09 に採用）: 非四足モードのモデルだけ、基準姿勢（Resources/animal_reference_pose.json）を回転で一度だけ書く（bind を控える前）。
        ApplyNonQuadrupedReferencePose(cache, bones);
        ResolveAnimalModelBasis(root, cache, settings);
        // 既定 ON（2026-10-08、非四足モード。2026-10-09 に採用）: 体の前を「頭 → 顔の骨」の水平の向きにする（ResolveAnimalModelBasis の結果を上書き。体の右を採る前）。
        ApplyNonQuadrupedForward(root, cache);
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

        // PrimeAnimalBinds の後: 頭の軸（鼻 = Z）は bindRotWorld の頭を、四肢の right の符号は bindDirLocal（次の役の骨への向き）を使う。
        // 前に呼んでいたときは頭の軸が作られず、anatomicalAxes の資産の頭が骨の局所軸のまま回った（鼻まわりのつもりの首振りが左右の首振りに、
        // 2026-10-05 の R3 で実測）。PrimeAnimalBinds は読むだけなので、ここでもまだ誰も骨を書いていない bind。
        EnsureGestureAnatomy(cache);
        // 尾の鎖（smalTailFullChain、2026-10-07）の bind（局所回転・局所位置・弧長・長さの比）を控える。ここもまだ誰も骨を書いていない bind。
        CaptureSmalTailChainBind(cache);

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

    // ---- 脚の基準姿勢を skin 姿勢に（2026-10-08、全関節の監査 J-03 / AL-3、新しい振る舞い・既定 OFF。プレイヤーの同名フィールドが毎フレーム代入する） ----
    // smalLegReferenceSkinPose: リグのキャッシュを作るとき（GetOrBuildAnimalRigCache で役の骨を決めた直後、bind を控える前）に、
    //   smalLegReferenceSkinPoseModels に載ったモデルだけ、脚の鎖（肩甲骨 → 脚の役 → 指。後脚は脚の役 → 指）の局所回転を
    //   skin 姿勢（SkinnedMeshRenderer の bindposes = メッシュが歪まない姿勢）の局所回転へ一度だけ書く。FK ループの外で、親から順に
    //   「この時点の親の world × skin の局所」を ApplyWorldRotation で書く。位置は書かない。鎖の根の親は胴の骨（肩甲帯・spine）なので、
    //   書いた後の脚は胴の骨に対して skin 姿勢になる。その後に控える bind（modelForwardLocal・bindRotWorld/Local・bindDirLocal・gestureBindLocal・
    //   肩甲骨の bind）は自動で skin の脚になり、相対の転写・イベントの既定の姿勢（animalEventFromDefaultPose）・ジェスチャの肩甲骨の土台もこの立ち姿に乗る。
    //   FK が書かない鎖の骨（肩甲骨・指など）は以後も書いた局所のまま（肩甲骨はジェスチャの骨として毎 tick gestureBindLocal = skin の局所へ戻される）。
    //   理由: 39_Lynx の prefab の既定姿勢（今の bind。10/08 のダンプの bindRotLocal と 0.0002° で一致）は歩きの途中の形で左右非対称
    //   （矢状面の傾き L/R: 肩甲骨 −7.2/+14.3°、上腕 −49.9/−37.0°、中手 +20.4/+34.7°）。skin 姿勢は左右同じ。相対の転写がこの非対称を毎フレーム運び、
    //   左前足が常に右より後ろにあった（前の球節の前後差 体長比 モデル −0.220 対データ +0.073、像面 p50 79〜91 px）。各モデル自身のメッシュが
    //   歪まない姿勢なので、09-05/09-06 に否定された「共通の基準へ揃える」とは違う（10/04 の AM が採らなかった案の、左右非対称という根拠による再提案）。
    //   値: Resources/animal_leg_skin_pose.json（2026-10-04 の Unity の棚卸し inv2/unity/animal_rest_rotations.json の bindposes から焼いたもの。
    //   runtime で Mesh.bindposes は読まない: IL2CPP で読めるか確かめていない）。キーは prefab 名から先頭の「数字_」を外したもの。
    //   回転だけ書くので skin 姿勢そのものにはならない（胴 Spine〜Spine3 の局所回転 2〜6°、肩甲骨・Spine4 の局所位置 5〜6% の差が残り、肩の低さも変わらない）。
    //   Lynx の静的な予測（棚卸しの FK、impl_joints/I4/verify_I4.py）: 前の球節の前後差 −0.264 → −0.013、肩の支点 −0.097 → −0.010、
    //   区間の矢状面の角の L−R 最大 21.5° → 0.74°、体長 +3.6%、bind 姿勢で前の球節が約 2.5 cm 下がる（倍率 1 の root 座標。指の骨が既定姿勢の床
    //   （Reference）より最大 2.4 cm 下に出て、骨の高さの幅 +3.1%。再生中の倍率と ⑦ は shot によって上下どちらにも動く、目安 ×0.92〜×1.10）、
    //   後脚の下腿が約 17° 起きる、modelForwardLocal の yaw −0.16°・pitch +0.58°。フレームごと（impl_joints/I4/predict_I4_frames.py、
    //   今の FK の移植で bind だけ替えて回し直したもの）: 前の球節の前後差 全編 −0.220 → +0.011（データ +0.073）、shot 23 −0.118 → +0.115（+0.118）。
    //   書かない条件（モデルごとに 1 行ログを出して既定姿勢のまま。一部の脚だけ書くと左右の非対称が別の形で残るので、全部書くか何も書かないか）:
    //   名簿に無い / 表に無い / 表の骨が見つからない・親の名前が違う / 今の局所回転が表の def と 0.5° より違う（モデルが棚卸しの後に変わった）/
    //   役の骨（上腕〜手根・上腿〜つま先）が表の鎖に入っていない。フラグ OFF・名簿が空なら表も読まずログも出さない。
    //   効くのはキャッシュを作るときだけ（途中で切り替えても、モデルを作り直すまで変わらない。バッチの -setFields は再生前に入るので効く）。
    // smalLegReferenceSkinPoseModels: prefab 名のカンマ区切り（先頭の「数字_」は無視。"Lynx" と "39_Lynx" は同じ）。既定は空 = どのモデルにも掛けない。
    //   05_Horse・20_Donkey は skin 姿勢自体が左右非対称なので表に焼いていない（載せても何もしない）。
    public bool smalLegReferenceSkinPose;
    public string smalLegReferenceSkinPoseModels = "";

    private const string LegSkinPoseResource = "animal_leg_skin_pose";
    // 今の局所回転と表の def（棚卸しの既定姿勢）の許容差。runtime の bind と棚卸しの def の差は 0.0002°（AL_verify/v06）なので、
    // これを超えるのはモデルが棚卸しの後に変わったとき。
    private const float LegSkinPoseDefToleranceDegrees = 0.5f;

    private sealed class LegSkinPoseBone
    {
        public string name;
        public string parent;
        public Quaternion skinLocal;
        public Quaternion defLocal;
    }

    // 表（キー → 親が先に並んだ骨の列）。最初に要ったときに 1 回だけ読む。
    private static Dictionary<string, List<LegSkinPoseBone>> legSkinPoseTable;
    // 名簿の解釈は尾の鎖の名簿と同じ（カンマ区切り、先頭の「数字_」は AnimalLegMappingFix.Key で外す）。
    private readonly SmalTailModelList legSkinPoseModelList = new SmalTailModelList();

    private void ApplyLegReferenceSkinPose(AnimalRigCache cache, Transform[] bones)
    {
        if (!smalLegReferenceSkinPose || string.IsNullOrEmpty(smalLegReferenceSkinPoseModels) || cache == null || bones == null)
        {
            return;
        }

        string modelKey = AnimalLegMappingFix.Key(KeyFor(cache));
        if (!legSkinPoseModelList.Contains(smalLegReferenceSkinPoseModels, modelKey))
        {
            Debug.Log($"[LEGSKIN] model={modelKey} は smalLegReferenceSkinPoseModels='{smalLegReferenceSkinPoseModels}' に無い → 既定姿勢のまま");
            return;
        }

        if (!TryGetLegSkinPose(modelKey, out List<LegSkinPoseBone> entries))
        {
            Debug.Log($"[LEGSKIN] model={modelKey} は Resources/{LegSkinPoseResource}.json に無い → 既定姿勢のまま");
            return;
        }

        // 先に全部確かめる（1 本でも合わなければ何も書かない）。
        var targets = new Transform[entries.Count];
        var inChain = new HashSet<Transform>();
        float maxDefDiff = 0f;
        for (int i = 0; i < entries.Count; i++)
        {
            LegSkinPoseBone e = entries[i];
            Transform t = FindLegSkinPoseBone(bones, e.name, e.parent);
            if (t == null)
            {
                Debug.Log($"[LEGSKIN] model={modelKey} 骨 {e.name}（親 {e.parent}）が見つからない → 既定姿勢のまま");
                return;
            }

            float defDiff = Quaternion.Angle(t.localRotation, e.defLocal);
            if (defDiff > LegSkinPoseDefToleranceDegrees)
            {
                Debug.Log($"[LEGSKIN] model={modelKey} 骨 {e.name} の今の局所回転が表の既定姿勢と {defDiff:F2}° 違う（モデルが棚卸しの後に変わった?）→ 既定姿勢のまま");
                return;
            }

            maxDefDiff = Mathf.Max(maxDefDiff, defDiff);
            targets[i] = t;
            inChain.Add(t);
        }

        Transform[] roleBones =
        {
            cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw, cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw,
            cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw, cache.leftRearToe,
            cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw, cache.rightRearToe,
        };
        foreach (Transform role in roleBones)
        {
            if (role != null && !inChain.Contains(role))
            {
                Debug.Log($"[LEGSKIN] model={modelKey} 役の骨 {role.name} が表の鎖に無い（役の割り当てが表を焼いた後に変わった?）→ 既定姿勢のまま");
                return;
            }
        }

        // 親から順に書く（表は親が先に並ぶ）。world = この時点の親の world × skin の局所。
        float maxChange = 0f;
        float maxResidual = 0f;
        for (int i = 0; i < targets.Length; i++)
        {
            Transform t = targets[i];
            Quaternion parentWorld = t.parent != null ? t.parent.rotation : Quaternion.identity;
            maxChange = Mathf.Max(maxChange, Quaternion.Angle(t.localRotation, entries[i].skinLocal));
            TransformWriter.ApplyWorldRotation(t, parentWorld * entries[i].skinLocal);
            maxResidual = Mathf.Max(maxResidual, Quaternion.Angle(t.localRotation, entries[i].skinLocal));
        }

        Debug.Log($"[LEGSKIN] applied model={modelKey} bones={targets.Length} maxChange={maxChange:F1}° residual={maxResidual:F3}° defCheck={maxDefDiff:F4}°");
    }

    private static Transform FindLegSkinPoseBone(Transform[] bones, string name, string parentName)
    {
        for (int i = 0; i < bones.Length; i++)
        {
            Transform t = bones[i];
            if (t != null && t.parent != null && t.name == name && t.parent.name == parentName)
            {
                return t;
            }
        }

        return null;
    }

    // Resources/animal_leg_skin_pose.json を MiniJson で読む（反射を使わない。IL2CPP で黙って効かなくなる経路を避ける）。
    // 形: { "models": { "<キー>": { "chains": [ { "bones": [ { "n": 骨, "p": 親, "skin": [x,y,z,w], "def": [x,y,z,w] }, ... ] }, ... ] } } }
    // 1 本でも読めない骨があるモデルは表に入れない（そのモデルは「表に無い」で何もしない）。
    private static bool TryGetLegSkinPose(string modelKey, out List<LegSkinPoseBone> entries)
    {
        entries = null;
        if (legSkinPoseTable == null)
        {
            legSkinPoseTable = new Dictionary<string, List<LegSkinPoseBone>>(System.StringComparer.Ordinal);
            TextAsset asset = Resources.Load<TextAsset>(LegSkinPoseResource);
            if (asset == null)
            {
                Debug.LogWarning($"[LEGSKIN] Resources/{LegSkinPoseResource}.json が無い");
            }
            else if (MiniJson.Parse(asset.text) is Dictionary<string, object> rootObj &&
                     rootObj.TryGetValue("models", out object modelsObj) && modelsObj is Dictionary<string, object> models)
            {
                foreach (KeyValuePair<string, object> kv in models)
                {
                    if (TryParseLegSkinPoseModel(kv.Value, out List<LegSkinPoseBone> list))
                    {
                        legSkinPoseTable[kv.Key] = list;
                    }
                }

                Debug.Log($"[LEGSKIN] Resources/{LegSkinPoseResource}.json を読んだ: {legSkinPoseTable.Count} モデル");
            }
            else
            {
                Debug.LogWarning($"[LEGSKIN] Resources/{LegSkinPoseResource}.json の形が違う");
            }
        }

        return legSkinPoseTable.TryGetValue(modelKey, out entries) && entries != null && entries.Count > 0;
    }

    private static bool TryParseLegSkinPoseModel(object node, out List<LegSkinPoseBone> list)
    {
        list = new List<LegSkinPoseBone>();
        if (!(node is Dictionary<string, object> model) || !model.TryGetValue("chains", out object chainsObj) || !(chainsObj is List<object> chains))
        {
            return false;
        }

        foreach (object chainObj in chains)
        {
            if (!(chainObj is Dictionary<string, object> chain) || !chain.TryGetValue("bones", out object bonesObj) || !(bonesObj is List<object> boneList))
            {
                return false;
            }

            foreach (object boneObj in boneList)
            {
                if (!(boneObj is Dictionary<string, object> bone) ||
                    !bone.TryGetValue("n", out object nameObj) || !(nameObj is string boneName) ||
                    !bone.TryGetValue("p", out object parentObj) || !(parentObj is string parentName) ||
                    !TryReadLegSkinPoseQuaternion(bone, "skin", out Quaternion skinLocal) ||
                    !TryReadLegSkinPoseQuaternion(bone, "def", out Quaternion defLocal))
                {
                    return false;
                }

                list.Add(new LegSkinPoseBone { name = boneName, parent = parentName, skinLocal = skinLocal, defLocal = defLocal });
            }
        }

        return list.Count > 0;
    }

    private static bool TryReadLegSkinPoseQuaternion(Dictionary<string, object> bone, string key, out Quaternion q)
    {
        q = Quaternion.identity;
        if (!bone.TryGetValue(key, out object value) || !(value is List<object> a) || a.Count != 4)
        {
            return false;
        }

        var c = new float[4];
        for (int i = 0; i < 4; i++)
        {
            if (a[i] is double d)
            {
                c[i] = (float)d;
            }
            else if (a[i] is long l)
            {
                c[i] = l;
            }
            else
            {
                return false;
            }
        }

        float n = Mathf.Sqrt(c[0] * c[0] + c[1] * c[1] + c[2] * c[2] + c[3] * c[3]);
        if (!(n > 0.5f && n < 1.5f))
        {
            return false;
        }

        q = new Quaternion(c[0] / n, c[1] / n, c[2] / n, c[3] / n);
        return true;
    }

    // ---- 非四足モード（2026-10-08、鳥 3 体とカンガルーを SMAL の FK で動かす。新しい振る舞い。2026-10-09 にユーザーが採用し、プレイヤーの同名フィールドは既定 ON。プレイヤーが毎フレーム代入する） ----
    // smalNonQuadrupedRig: 主スイッチ。リグのキャッシュを作るとき（GetOrBuildAnimalRigCache、モデルを置いた最初の姿勢適用）に、名簿のモデルだけ
    //   役を AnimalNonQuadrupedRig.Table の行で付け替え（ApplyNonQuadrupedRoles）、体の前を「頭 → 顔の骨（鼻・顎）」の水平の向きにする（ApplyNonQuadrupedForward）。
    //   前肢の上の役が左右とも無いリグ（鳥、cache.smalNoFrontLimbs）は spine と後肢の上 2 本で SMAL FK の入口を通す（IsAnimalRigReadyForSmalFk）。
    //   尾の付け根のあるモデル（カンガルーの Tail01）は関節 25/26 を bind に保つ（AnimalSmalFkApplier の主ループ）。
    //   モードはキャッシュを作るとき 1 回だけ決めて cache.smalNonQuadruped に持つ（途中で切り替えても、Change Model でモデルを作り直すまで変わらない）。
    //   ON でも名簿・表に無いモデル（実験の Labrador・Lynx を含む）は何も変わらない（ログも出さない）。
    // smalNonQuadrupedRigModels: 名簿。prefab 名のカンマ区切り（先頭の「数字_」は無視。解釈は尾の鎖の名簿と同じ）。効くのは表に行のあるモデルだけ。
    // smalNonQuadrupedHock: 鳥の脛（LegL3/LegR3 = 関節 19/23）を手根・飛節と同じ写し方で（AnimalSmalFkApplier.IsSmalCarpusHockDriven）。前肢の無いリグ（鳥）だけ。
    //   鳥の脛はこの切り替えだけで決まり、全体の smalDriveCarpusHock（J-08、判断待ち）を ON にしても動かない。鳥の前足・指は smalDriveFeet が ON でも受け身のまま。
    //   前肢のあるリグ（カンガルー・四足）には効かない（今のまま全体の切り替えに従う）。毎 tick 読む。
    // smalNonQuadrupedReferencePose: 基準姿勢（Resources/animal_reference_pose.json。形は animal_leg_skin_pose.json と同じで 'skin' が目標の局所回転）を、
    //   キャッシュを作るとき 1 回だけ bind を控える前に回転だけ書く（ApplyNonQuadrupedReferencePose）。表が無い・行が無い・合わないなら何もせず、ログは
    //   ファイルが無いことは再生ごとに 1 回、行の理由はモデルごとに 1 回だけ（2026-10-08 の時点で表はまだ無い。焼く Editor の道具は別の作業）。
    // smalNonQuadrupedUprightRoot: 体の根を上下まわり（yaw）だけにする（既存の ResolveDefaultPoseRoot、イベントの既定の姿勢と同じ式）。毎 tick 読む。
    // 脛・基準姿勢・直立は、主スイッチ ON で作ったキャッシュ（cache.smalNonQuadruped）にだけ効く。
    public bool smalNonQuadrupedRig;
    public string smalNonQuadrupedRigModels = "Goose,Guineafowl,Pheasant,Kangaroo";
    public bool smalNonQuadrupedHock;
    public bool smalNonQuadrupedReferencePose;
    public bool smalNonQuadrupedUprightRoot;

    private const string NonQuadrupedReferencePoseResource = "animal_reference_pose";
    // 基準姿勢の表（キー → 骨の列）。最初に要ったときに 1 回だけ読む。ファイルが無ければ空の表のまま（読み直さない）。
    private static Dictionary<string, List<LegSkinPoseBone>> nonQuadrupedReferencePoseTable;
    private static bool nonQuadrupedReferencePoseFileFound;
    // (a) を書かなかったログは 1 回だけ（キャッシュを作り直すたび = Change Model のたびには出さない）。ファイルが無いことは 1 回、行の理由はモデルごとに 1 回。
    // 表と同じく static（再生ごとに初めから。EditorSettings の Enter Play Mode Options はドメインを読み直す設定）。
    private static bool nonQuadrupedReferencePoseMissingFileLogged;
    private static HashSet<string> nonQuadrupedReferencePoseSkipLogged;
    // 名簿の解釈は尾の鎖の名簿と同じ（カンマ区切り、先頭の「数字_」は AnimalLegMappingFix.Key で外す）。
    private readonly SmalTailModelList nonQuadrupedModelList = new SmalTailModelList();

    // 役を AnimalNonQuadrupedRig.Table の行で付け替える（キャッシュを作るとき 1 回だけ。役の骨を決めた直後、bind を控える前）。
    // 全部か何もしないか: 空でない骨名を全部見つけてから代入する。1 つでも無ければ 1 行ログを出して今のまま（cache.smalNonQuadruped は false のまま）。
    // 骨名の探し方は prefab の上書きと同じ（FindBoneByExactNames: 完全一致、メッシュの節なら親）。空の値はその役を null にする（名前は探さない）。
    private void ApplyNonQuadrupedRoles(AnimalRigCache cache, Transform[] bones)
    {
        if (!smalNonQuadrupedRig || string.IsNullOrEmpty(smalNonQuadrupedRigModels) || cache == null || bones == null)
        {
            return;
        }

        string modelKey = AnimalLegMappingFix.Key(KeyFor(cache));
        if (!nonQuadrupedModelList.Contains(smalNonQuadrupedRigModels, modelKey))
        {
            return;
        }

        if (!AnimalNonQuadrupedRig.Table.TryGetValue(modelKey, out string spec))
        {
            Debug.Log($"[NONQUAD] model={modelKey} は smalNonQuadrupedRigModels にあるが AnimalNonQuadrupedRig.Table に行が無い → 今のまま");
            return;
        }

        var pairs = new List<KeyValuePair<string, string>>();
        if (!AnimalNonQuadrupedRig.TryParse(spec, pairs, out string error))
        {
            Debug.Log($"[NONQUAD] model={modelKey} の行 '{spec}' が読めない（{error}）→ 今のまま");
            return;
        }

        var resolved = new Transform[pairs.Count];
        for (int i = 0; i < pairs.Count; i++)
        {
            if (pairs[i].Value.Length == 0)
            {
                continue;
            }

            resolved[i] = FindBoneByExactNames(bones, pairs[i].Value);
            if (resolved[i] == null)
            {
                Debug.Log($"[NONQUAD] model={modelKey} 骨 {pairs[i].Value}（{pairs[i].Key}）が見つからない → 今のまま（行 '{spec}'）");
                return;
            }
        }

        var roles = new System.Text.StringBuilder();
        for (int i = 0; i < pairs.Count; i++)
        {
            Transform previous = SetNonQuadrupedRole(cache, pairs[i].Key, resolved[i]);
            roles.Append(i > 0 ? "," : string.Empty).Append(pairs[i].Key).Append('=')
                 .Append(previous != null ? previous.name : "null").Append("->").Append(resolved[i] != null ? resolved[i].name : "null");
        }

        cache.smalNonQuadruped = true;
        cache.smalNoFrontLimbs = cache.leftFrontUpper == null && cache.rightFrontUpper == null;
        Debug.Log($"[NONQUAD] applied model={modelKey} roles={roles} noFront={cache.smalNoFrontLimbs}");
    }

    // 役のフィールドへの代入（AnimalLegMappingFix.TrySetOverrideField と同じ並びの switch。反射は使わない: IL2CPP で黙って効かなくなる経路を避ける）。前の骨を返す。
    private static Transform SetNonQuadrupedRole(AnimalRigCache cache, string key, Transform bone)
    {
        Transform previous;
        switch (key)
        {
            case "spine": previous = cache.spine; cache.spine = bone; break;
            case "neck": previous = cache.neck; cache.neck = bone; break;
            case "head": previous = cache.head; cache.head = bone; break;
            case "tailBase": previous = cache.tailBase; cache.tailBase = bone; break;
            case "tailMid": previous = cache.tailMid; cache.tailMid = bone; break;
            case "tailTip": previous = cache.tailTip; cache.tailTip = bone; break;
            case "frontLUpper": previous = cache.leftFrontUpper; cache.leftFrontUpper = bone; break;
            case "frontLLower": previous = cache.leftFrontLower; cache.leftFrontLower = bone; break;
            case "frontLPaw": previous = cache.leftFrontPaw; cache.leftFrontPaw = bone; break;
            case "frontRUpper": previous = cache.rightFrontUpper; cache.rightFrontUpper = bone; break;
            case "frontRLower": previous = cache.rightFrontLower; cache.rightFrontLower = bone; break;
            case "frontRPaw": previous = cache.rightFrontPaw; cache.rightFrontPaw = bone; break;
            case "rearLUpper": previous = cache.leftRearUpper; cache.leftRearUpper = bone; break;
            case "rearLLower": previous = cache.leftRearLower; cache.leftRearLower = bone; break;
            case "rearLPaw": previous = cache.leftRearPaw; cache.leftRearPaw = bone; break;
            case "rearLToe": previous = cache.leftRearToe; cache.leftRearToe = bone; break;
            case "rearRUpper": previous = cache.rightRearUpper; cache.rightRearUpper = bone; break;
            case "rearRLower": previous = cache.rightRearLower; cache.rightRearLower = bone; break;
            case "rearRPaw": previous = cache.rightRearPaw; cache.rightRearPaw = bone; break;
            case "rearRToe": previous = cache.rightRearToe; cache.rightRearToe = bone; break;
            default: return null;
        }

        return previous;
    }

    // 基準姿勢を回転だけ一度書く（キャッシュを作るとき、bind を控える前。ApplyLegReferenceSkinPose と同じ書き方と同じ 0.5° の def の確認）。
    // 全部か何もしないか: 骨と親の名前・今の局所回転と表の 'def' の差を全部確かめてから、親から順に「この時点の親の world × 目標の局所（'skin'）」を書く。位置は書かない。
    // 役の骨が表の鎖に入っているかは確かめない（Goose・Pheasant の行は右脚の鎖だけを持つ）。
    private void ApplyNonQuadrupedReferencePose(AnimalRigCache cache, Transform[] bones)
    {
        if (!smalNonQuadrupedReferencePose || cache == null || !cache.smalNonQuadruped || bones == null)
        {
            return;
        }

        string modelKey = AnimalLegMappingFix.Key(KeyFor(cache));
        if (!TryGetNonQuadrupedReferencePose(modelKey, out List<LegSkinPoseBone> entries))
        {
            if (!nonQuadrupedReferencePoseFileFound)
            {
                if (!nonQuadrupedReferencePoseMissingFileLogged)
                {
                    nonQuadrupedReferencePoseMissingFileLogged = true;
                    // 既定 ON の smalNonQuadrupedReferencePose が読む表。無いと鳥が黙って bind のままになるので Warning で出す（2026-10-10）。
                    Debug.LogWarning($"[NONQUAD] reference model={modelKey}: Resources/{NonQuadrupedReferencePoseResource}.json が無い → bind のまま（この行は再生ごとに 1 回だけ）");
                }
            }
            else
            {
                LogNonQuadrupedReferencePoseSkipOnce(modelKey, $"[NONQUAD] reference model={modelKey} は Resources/{NonQuadrupedReferencePoseResource}.json に無い → bind のまま");
            }

            return;
        }

        // 先に全部確かめる（1 本でも合わなければ何も書かない）。
        var targets = new Transform[entries.Count];
        var depth = new int[entries.Count];
        float maxDefDiff = 0f;
        for (int i = 0; i < entries.Count; i++)
        {
            LegSkinPoseBone e = entries[i];
            Transform t = FindLegSkinPoseBone(bones, e.name, e.parent);
            if (t == null)
            {
                LogNonQuadrupedReferencePoseSkipOnce(modelKey, $"[NONQUAD] reference model={modelKey} 骨 {e.name}（親 {e.parent}）が見つからない → bind のまま");
                return;
            }

            float defDiff = Quaternion.Angle(t.localRotation, e.defLocal);
            if (defDiff > LegSkinPoseDefToleranceDegrees)
            {
                LogNonQuadrupedReferencePoseSkipOnce(modelKey,
                    $"[NONQUAD] reference model={modelKey} 骨 {e.name} の今の局所回転が表の def と {defDiff:F2}° 違う（モデルが焼いた後に変わった?）→ bind のまま");
                return;
            }

            maxDefDiff = Mathf.Max(maxDefDiff, defDiff);
            targets[i] = t;
            for (Transform p = t.parent; p != null; p = p.parent)
            {
                depth[i]++;
            }
        }

        // 親から順（階層の浅い順、同じ深さは表の順）。world = この時点の親の world × 目標の局所。
        var order = new int[targets.Length];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        System.Array.Sort(order, (a, b) => depth[a] != depth[b] ? depth[a].CompareTo(depth[b]) : a.CompareTo(b));
        float maxChange = 0f;
        float maxResidual = 0f;
        foreach (int i in order)
        {
            Transform t = targets[i];
            Quaternion parentWorld = t.parent != null ? t.parent.rotation : Quaternion.identity;
            maxChange = Mathf.Max(maxChange, Quaternion.Angle(t.localRotation, entries[i].skinLocal));
            TransformWriter.ApplyWorldRotation(t, parentWorld * entries[i].skinLocal);
            maxResidual = Mathf.Max(maxResidual, Quaternion.Angle(t.localRotation, entries[i].skinLocal));
        }

        Debug.Log($"[NONQUAD] reference applied model={modelKey} bones={targets.Length} maxChange={maxChange:F1}° residual={maxResidual:F3}° defCheck={maxDefDiff:F4}°");
    }

    // Resources/animal_reference_pose.json を MiniJson で読む（形は animal_leg_skin_pose.json と同じ。モデルの読み方は TryParseLegSkinPoseModel をそのまま使う）。
    private static bool TryGetNonQuadrupedReferencePose(string modelKey, out List<LegSkinPoseBone> entries)
    {
        entries = null;
        if (nonQuadrupedReferencePoseTable == null)
        {
            nonQuadrupedReferencePoseTable = new Dictionary<string, List<LegSkinPoseBone>>(System.StringComparer.Ordinal);
            TextAsset asset = Resources.Load<TextAsset>(NonQuadrupedReferencePoseResource);
            nonQuadrupedReferencePoseFileFound = asset != null;
            if (asset != null)
            {
                if (MiniJson.Parse(asset.text) is Dictionary<string, object> rootObj &&
                    rootObj.TryGetValue("models", out object modelsObj) && modelsObj is Dictionary<string, object> models)
                {
                    foreach (KeyValuePair<string, object> kv in models)
                    {
                        if (TryParseLegSkinPoseModel(kv.Value, out List<LegSkinPoseBone> list))
                        {
                            nonQuadrupedReferencePoseTable[kv.Key] = list;
                        }
                    }

                    Debug.Log($"[NONQUAD] Resources/{NonQuadrupedReferencePoseResource}.json を読んだ: {nonQuadrupedReferencePoseTable.Count} モデル");
                }
                else
                {
                    Debug.LogWarning($"[NONQUAD] Resources/{NonQuadrupedReferencePoseResource}.json の形が違う");
                }
            }
        }

        return nonQuadrupedReferencePoseTable.TryGetValue(modelKey, out entries) && entries != null && entries.Count > 0;
    }

    // (a) を書かなかった理由（行が無い・骨が無い・def が違う）のログをモデルごとに 1 回だけ出す（そのモデルで最初の理由だけ。2 回目からは黙って何もしない）。
    private static void LogNonQuadrupedReferencePoseSkipOnce(string modelKey, string message)
    {
        if (nonQuadrupedReferencePoseSkipLogged == null)
        {
            nonQuadrupedReferencePoseSkipLogged = new HashSet<string>(System.StringComparer.Ordinal);
        }

        if (nonQuadrupedReferencePoseSkipLogged.Add(modelKey ?? string.Empty))
        {
            Debug.Log(message);
        }
    }

    // 体の前（cache.modelForwardLocal、root ローカル）を「頭 → 顔の骨（FindHeadFacingChild の nose・jaw・mouth・muzzle）」の水平の向きにする。
    // ResolveAnimalModelBasis の後・CaptureBodyRightBindWorld の前（体の右・modelOrientFix・rootYawFix・体の前を読むイベントの経路がすべてこれを使う）。
    // 鳥は前肢の役が無く ResolveAnimalModelBasis が全モデル共通の animalModelForwardLocal (0,0,-1) に落ちていた（実際の前は root ローカル −X）。
    // カンガルーは「肩の中点 − 股の中点」が直立の体で 79° 上を向いていた（FK の中は水平にして使うが、TryGetBodyForwardSpineLocal・ResolveDefaultPoseRoot は
    // 水平にせずに使う）。水平にするのは TryApplyAnimalSmalFk の modelOrientFix と同じ（y を 0）。顔の骨が無い・ほぼ真上か真下なら今のまま。
    private static void ApplyNonQuadrupedForward(Transform root, AnimalRigCache cache)
    {
        if (cache == null || !cache.smalNonQuadruped || root == null || cache.head == null)
        {
            return;
        }

        string modelKey = AnimalLegMappingFix.Key(KeyFor(cache));
        Transform tip = FindHeadFacingChild(cache.head);
        if (tip == null)
        {
            Debug.Log($"[NONQUAD] forward model={modelKey}: 頭 {cache.head.name} の子孫に顔の骨（nose/jaw/mouth/muzzle）が無い → 前は今のまま {cache.modelForwardLocal:F3}");
            return;
        }

        Vector3 raw = root.InverseTransformDirection(tip.position - cache.head.position);
        Vector3 flat = new Vector3(raw.x, 0f, raw.z);
        if (raw.sqrMagnitude < 1e-12f || flat.sqrMagnitude < 0.01f * raw.sqrMagnitude)
        {
            Debug.Log($"[NONQUAD] forward model={modelKey}: 頭 → {tip.name} がほぼ真上か真下（{raw:F3}）→ 前は今のまま {cache.modelForwardLocal:F3}");
            return;
        }

        Vector3 before = cache.modelForwardLocal;
        cache.modelForwardLocal = flat.normalized;
        Debug.Log($"[NONQUAD] forward={cache.modelForwardLocal:F3} model={modelKey} from={cache.head.name}->{tip.name} was={before:F3}");
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

    // 動物の動きの作り直し（2026-10-05）: 新しいジェスチャの点の骨と、資産の anatomicalAxes で使う骨ごとの体の軸を、キャッシュを作るとき（bind）に 1 回だけ求める。
    // - 胴の鎖: spine の子孫で、首（neck）と前脚の役の上腕の共通の祖先（肩甲帯）まで（spine は含めず肩甲帯は含める）。共通の祖先が spine 自身（00_Dog）なら空。
    //   SMAL の仮想の背骨（関節 1〜6）は骨を持たないので、これらは誰も書かない骨。gestureBindLocal に入れて毎 tick bind に戻し、その上にジェスチャを足す
    //   （今の回転に足すと積み重なって暴走する。メモリ animal_bones_not_reset_each_tick）。首の鎖（名前に neck、ApplySmalNeckChain）とは重ならない
    // - 肩甲骨: 役の上腕の親。spine・胴の鎖・root・役の骨なら無し（00_Dog は上腕の親が spine）
    // - 耳: 頭の子孫で名前に "ear" を含み、親が耳でない骨。左右は bind の頭からの位置と体の右の内積
    // - 体の軸: X = 体の右（bodyRightBindWorld）、Y = 上（world の上を体の右に直交化）、Z = 前（X × Y）を骨ローカルにした回転（頭は Z = 鼻）。
    //   軸は骨に付いたもの（骨が曲がると一緒に回る = 関節の蝶番の軸）。right の符号は「+5° で先（子の向き、頭は鼻）が四肢・肩甲骨なら体の前へ、ほかは上へ動く」側
    //   体の右が取れないモデル（F2 の対象外）は軸を作らない（資産の anatomicalAxes でも骨の局所軸のまま）
    private static void EnsureGestureAnatomy(AnimalRigCache cache)
    {
        if (cache.gestureAnatomyResolved)
        {
            return;
        }

        cache.gestureAnatomyResolved = true;
        cache.trunkChain.Clear();
        cache.gestureAxesFrame.Clear();
        cache.gestureSwingSign.Clear();

        var roles = new HashSet<Transform>
        {
            cache.root, cache.spine, cache.neck, cache.head, cache.tailBase, cache.tailMid, cache.tailTip,
            cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw, cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw,
            cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw, cache.leftRearToe,
            cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw, cache.rightRearToe,
        };

        // 胴の鎖
        Transform girdle = null;
        if (cache.spine != null && cache.neck != null && cache.leftFrontUpper != null)
        {
            var frontAncestors = new HashSet<Transform>();
            for (Transform t = cache.leftFrontUpper.parent; t != null; t = t.parent)
            {
                frontAncestors.Add(t);
            }

            for (Transform t = cache.neck.parent; t != null; t = t.parent)
            {
                if (frontAncestors.Contains(t))
                {
                    girdle = t;
                    break;
                }
            }

            if (girdle != null && girdle != cache.spine && girdle.IsChildOf(cache.spine))
            {
                for (Transform t = girdle; t != null && t != cache.spine; t = t.parent)
                {
                    if (roles.Contains(t))
                    {
                        cache.trunkChain.Clear();
                        break;
                    }

                    cache.trunkChain.Add(t);
                }

                cache.trunkChain.Reverse();
            }

            // 後脚の上腿・尾の付け根が胴の鎖の骨の下にぶら下がる形なら胴は曲げない（曲げると後脚・尾も振れる。47 体の移植では該当なし、motion_prep/check_rule.py）
            foreach (Transform hang in new[] { cache.leftRearUpper, cache.rightRearUpper, cache.tailBase })
            {
                if (hang != null && cache.trunkChain.Exists(b => hang.IsChildOf(b)))
                {
                    cache.trunkChain.Clear();
                    break;
                }
            }
        }

        // 肩甲骨
        cache.gestureScapulaLeft = ResolveGestureScapula(cache, cache.leftFrontUpper, roles, girdle);
        cache.gestureScapulaRight = ResolveGestureScapula(cache, cache.rightFrontUpper, roles, girdle);
        if (cache.gestureScapulaLeft == null || cache.gestureScapulaRight == null || cache.gestureScapulaLeft == cache.gestureScapulaRight)
        {
            // 片側だけ・同じ骨なら両方使わない
            cache.gestureScapulaLeft = null;
            cache.gestureScapulaRight = null;
        }

        // 耳: 頭の子孫で名前に "ear" という語（CamelCase・数字・記号で区切った語）を持ち、親が耳でない骨（耳の根）。
        // 素朴な部分一致は Beard（ヤギ）・Bear（熊の骨名）を拾う（motion_prep/ear_audit.py）。
        // 左右は耳の根どうしの体の右の座標を比べて決める（頭の位置を基準にすると、bind で頭が横を向いた 34_Hyena・38_LionessV2 で両耳とも左になった）
        cache.gestureEarLeft = null;
        cache.gestureEarRight = null;
        Vector3 bodyRight = cache.bodyRightBindWorld;
        if (cache.head != null && bodyRight.sqrMagnitude > 0.5f)
        {
            Transform leftmost = null;
            Transform rightmost = null;
            float minSide = float.MaxValue;
            float maxSide = float.MinValue;
            foreach (Transform t in cache.head.GetComponentsInChildren<Transform>(true))
            {
                if (t == cache.head || !NameHasWord(t.name, "ear") || (t.parent != null && NameHasWord(t.parent.name, "ear")))
                {
                    continue;
                }

                float side = Vector3.Dot(t.position, bodyRight);
                if (side < minSide)
                {
                    minSide = side;
                    leftmost = t;
                }

                if (side > maxSide)
                {
                    maxSide = side;
                    rightmost = t;
                }
            }

            if (leftmost != null && rightmost != null && leftmost != rightmost)
            {
                cache.gestureEarLeft = leftmost;
                cache.gestureEarRight = rightmost;
            }
            else if (leftmost != null)
            {
                // 耳の根が 1 本だけ: 頭に対する横の位置で片側に置く
                if (Vector3.Dot(leftmost.position - cache.head.position, bodyRight) >= 0f)
                {
                    cache.gestureEarRight = leftmost;
                }
                else
                {
                    cache.gestureEarLeft = leftmost;
                }
            }
        }

        // FK が書かない新しい点の骨は bind の局所を控える
        foreach (Transform t in cache.trunkChain)
        {
            cache.gestureBindLocal[t] = t.localRotation;
        }

        foreach (Transform t in new[] { cache.gestureScapulaLeft, cache.gestureScapulaRight, cache.gestureEarLeft, cache.gestureEarRight })
        {
            if (t != null)
            {
                cache.gestureBindLocal[t] = t.localRotation;
            }
        }

        // 体の軸
        if (bodyRight.sqrMagnitude > 0.5f)
        {
            Vector3 r = bodyRight.normalized;
            Vector3 u = Vector3.ProjectOnPlane(Vector3.up, r).normalized;
            Vector3 f = Vector3.Cross(r, u);
            var limbs = new HashSet<Transform>
            {
                cache.leftFrontUpper, cache.leftFrontLower, cache.leftFrontPaw, cache.rightFrontUpper, cache.rightFrontLower, cache.rightFrontPaw,
                cache.leftRearUpper, cache.leftRearLower, cache.leftRearPaw, cache.leftRearToe,
                cache.rightRearUpper, cache.rightRearLower, cache.rightRearPaw, cache.rightRearToe,
                cache.gestureScapulaLeft, cache.gestureScapulaRight,
            };
            foreach (Transform canonical in cache.gestureCanonicalLimbs.Values)
            {
                limbs.Add(canonical);
            }

            var axial = new HashSet<Transform> { cache.spine, cache.neck, cache.tailBase, cache.tailMid, cache.tailTip, cache.gestureEarLeft, cache.gestureEarRight };
            foreach (Transform t in cache.trunkChain)
            {
                axial.Add(t);
            }

            foreach (Transform bone in limbs)
            {
                AddGestureAxesFrame(cache, bone, r, u, f, true);
            }

            foreach (Transform bone in axial)
            {
                AddGestureAxesFrame(cache, bone, r, u, f, false);
            }

            // 頭は Z = 鼻（顔の骨から。取れなければ体の前）
            if (cache.head != null && cache.bindRotWorld.TryGetValue(cache.head, out Quaternion headBind))
            {
                Vector3 nose = ResolveHeadNoseLocal(cache) ? cache.headNoseLocal : Quaternion.Inverse(headBind) * f;
                Vector3 rl = Vector3.ProjectOnPlane(Quaternion.Inverse(headBind) * r, nose);
                if (rl.sqrMagnitude > 1e-6f)
                {
                    rl.Normalize();
                    Vector3 ul = Vector3.Cross(nose.normalized, rl);
                    cache.gestureAxesFrame[cache.head] = Quaternion.LookRotation(nose.normalized, ul);
                    cache.gestureSwingSign[cache.head] = ResolveGestureSwingSign(headBind, rl, nose, u);
                }
            }
        }

        string Name(Transform t) => t != null ? t.name : "-";
        Debug.Log($"[GESTURE-ANATOMY] {Name(cache.root)} trunk=[{string.Join(",", cache.trunkChain.ConvertAll(t => t.name))}] girdle={Name(girdle)} " +
                  $"scapula={Name(cache.gestureScapulaLeft)}/{Name(cache.gestureScapulaRight)} ears={Name(cache.gestureEarLeft)}/{Name(cache.gestureEarRight)} " +
                  $"axesFrames={cache.gestureAxesFrame.Count} bodyRight={(bodyRight.sqrMagnitude > 0.5f ? "yes" : "none")}");
    }

    // 肩甲骨 = 役の上腕の親。肩甲帯（girdle）の直下に限る: 表が当たらず上腕の役が 1 本下の正規名の骨のままだと、親は本当の上腕で肩甲帯との間に 2 本以上入る
    // （LionStyleFull・Labrador は 2 本、Fox・Beaver は 3 本。motion_prep/bone_tree_map.json の S3）。
    private static Transform ResolveGestureScapula(AnimalRigCache cache, Transform upper, HashSet<Transform> roles, Transform girdle)
    {
        Transform parent = upper != null ? upper.parent : null;
        if (parent == null || roles.Contains(parent) || cache.trunkChain.Contains(parent) || girdle == null || parent == girdle || parent.parent != girdle)
        {
            return null;
        }

        return parent;
    }

    // 名前を CamelCase・数字・記号で語に区切り、word と同じ語（大文字小文字を問わない）があるか。"fLeftEar" → f / Left / Ear、"rear_l_upper" → rear / l / upper。
    private static bool NameHasWord(string name, string word)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        int start = -1;
        for (int i = 0; i <= name.Length; i++)
        {
            bool boundary = i == name.Length || !char.IsLetter(name[i]) ||
                            (i > 0 && char.IsUpper(name[i]) && (char.IsLower(name[i - 1]) ||
                             (char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]))));
            if (boundary && start >= 0)
            {
                if (i - start == word.Length && string.Compare(name, start, word, 0, word.Length, System.StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return true;
                }

                start = -1;
            }

            if (i < name.Length && char.IsLetter(name[i]) && start < 0)
            {
                start = i;
            }
        }

        return false;
    }

    // 骨ローカルの体の軸（X = 右、Y = 上、Z = 前）と、right の符号（四肢: +5° で先が前へ、ほか: 先が上へ）。先の向きは bind の子の向き。
    private static void AddGestureAxesFrame(AnimalRigCache cache, Transform bone, Vector3 r, Vector3 u, Vector3 f, bool limb)
    {
        if (bone == null || cache.gestureAxesFrame.ContainsKey(bone))
        {
            return;
        }

        // bind の world 回転。役の骨は PrimeAnimalBind が控えている。胴の鎖・肩甲骨・耳は控えていないが、この関数はキャッシュを作るとき
        // （まだ誰も書いていない bind）に呼ぶので、今の回転がそのまま bind
        Quaternion bw = cache.bindRotWorld.TryGetValue(bone, out Quaternion primed) ? primed : bone.rotation;
        Quaternion inv = Quaternion.Inverse(bw);
        Vector3 fl = inv * f;
        Vector3 ul = inv * u;
        cache.gestureAxesFrame[bone] = Quaternion.LookRotation(fl, ul);
        Vector3 distal = cache.bindDirLocal.TryGetValue(bone, out Vector3 d) && d.sqrMagnitude > 1e-8f ? d : ResolveFirstChildDirLocal(bone);
        cache.gestureSwingSign[bone] = ResolveGestureSwingSign(bw, inv * r, distal, limb ? f : u);
    }

    private static Vector3 ResolveFirstChildDirLocal(Transform bone)
    {
        for (int i = 0; i < bone.childCount; i++)
        {
            Vector3 p = bone.GetChild(i).localPosition;
            if (p.sqrMagnitude > 1e-10f)
            {
                return p.normalized;
            }
        }

        return Vector3.zero;
    }

    // +5° を骨ローカルの軸 axisLocal まわりに回したとき、先（distalLocal）の world の動きが want の側なら +1、逆なら −1（判定できなければ +1）。
    private static float ResolveGestureSwingSign(Quaternion boneBindWorld, Vector3 axisLocal, Vector3 distalLocal, Vector3 want)
    {
        if (distalLocal.sqrMagnitude < 1e-8f || axisLocal.sqrMagnitude < 1e-8f)
        {
            return 1f;
        }

        Vector3 before = boneBindWorld * distalLocal.normalized;
        Vector3 after = boneBindWorld * (Quaternion.AngleAxis(5f, axisLocal.normalized) * distalLocal.normalized);
        float d = Vector3.Dot(after - before, want);
        return d < 0f ? -1f : 1f;
    }

    private static void CaptureBodyRightBindWorld(Transform root, AnimalRigCache cache)
    {
        cache.bodyRightBindWorld = Vector3.zero;
        if (root == null)
        {
            return;
        }

        // F2 を使わないモデル（AnimalLegMappingFix.FrontLimbBodyLateralExcluded。2026-10-09 から空）は体の右を採らない（零のままなら F2 の分岐に入らず、首の副軸のまま）。
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
