using UnityEngine;

public static class PinholePlacementSpace
{
    public static bool TryResolveProjectionIntrinsics(
        ManifestData manifest,
        bool hasFovxDeg,
        float fovxDeg,
        out float fx,
        out float fy,
        out float cxPixels,
        out float cyPixels)
    {
        fx = 0f;
        fy = 0f;
        cxPixels = 0f;
        cyPixels = 0f;
        if (manifest == null || manifest.eye_w <= 0 || manifest.eye_h <= 0)
        {
            return false;
        }

        float w = manifest.eye_w;
        float h = manifest.eye_h;
        float cxNorm = ResolvePrincipalPointNorm(manifest.cx, w);
        float cyNorm = ResolvePrincipalPointNorm(manifest.cy, h);
        cxPixels = Mathf.Clamp01(cxNorm) * w;
        cyPixels = Mathf.Clamp01(cyNorm) * h;

        if (manifest.fx_norm > 0f && manifest.fy_norm > 0f)
        {
            fx = manifest.fx_norm;
            fy = manifest.fy_norm;
            return true;
        }

        if (!hasFovxDeg)
        {
            return false;
        }

        float fovxRad = fovxDeg * Mathf.Deg2Rad;
        fx = 1f / Mathf.Tan(fovxRad * 0.5f);
        if (manifest.fovy_deg > 0f || manifest.fovy > 0f)
        {
            float fovyDeg = manifest.fovy_deg > 0f ? manifest.fovy_deg : manifest.fovy;
            float fovyRad = fovyDeg * Mathf.Deg2Rad;
            fy = 1f / Mathf.Tan(fovyRad * 0.5f);
        }
        else
        {
            fy = fx * (manifest.eye_w / (float)manifest.eye_h);
        }
        return fx > 0f && fy > 0f;
    }

    public static Vector3 ReconstructCamLocalFromEyePixel(
        ManifestData manifest,
        float uEye,
        float vEye,
        float zMeters,
        float fx,
        float fy,
        int eyeW,
        int eyeH)
    {
        float cxNorm = 0.5f;
        float cyNorm = 0.5f;
        if (manifest != null && manifest.eye_w > 0 && manifest.eye_h > 0)
        {
            cxNorm = ResolvePrincipalPointNorm(manifest.cx, manifest.eye_w);
            cyNorm = ResolvePrincipalPointNorm(manifest.cy, manifest.eye_h);
        }

        float xNdc = ((uEye / (float)eyeW) - cxNorm) * 2f;
        float yNdc = (cyNorm - (vEye / (float)eyeH)) * 2f;
        return new Vector3(xNdc * zMeters / fx, yNdc * zMeters / fy, zMeters);
    }

    public static Vector3 EyePixelDepthToWorld(
        Vector3 camOrigin,
        Quaternion camRotation,
        ManifestData manifest,
        float uEye,
        float vEye,
        float zMeters,
        float fx,
        float fy)
    {
        Vector3 camLocal = ReconstructCamLocalFromEyePixel(
            manifest,
            uEye,
            vEye,
            zMeters,
            fx,
            fy,
            manifest.eye_w,
            manifest.eye_h);
        return camOrigin + (camRotation * camLocal);
    }

    public static bool TryProjectCamLocalToEyePixel(
        ManifestData manifest,
        Vector3 camLocal,
        float fx,
        float fy,
        out Vector2 eyePixel)
    {
        eyePixel = Vector2.zero;
        if (manifest == null ||
            manifest.eye_w <= 0 ||
            manifest.eye_h <= 0 ||
            fx <= 0f ||
            fy <= 0f ||
            camLocal.z <= 0.001f)
        {
            return false;
        }

        float cxNorm = ResolvePrincipalPointNorm(manifest.cx, manifest.eye_w);
        float cyNorm = ResolvePrincipalPointNorm(manifest.cy, manifest.eye_h);
        eyePixel = new Vector2(
            (cxNorm + (camLocal.x * fx / camLocal.z) * 0.5f) * manifest.eye_w,
            (cyNorm - (camLocal.y * fy / camLocal.z) * 0.5f) * manifest.eye_h);
        return true;
    }

    // 点（カメラ空間の深さ pointZ、像 pointU / pointV）を目標の列・行へ動かすカメラの面内の移動（m）。z は変えないので 1 点について厳密
    // （TryProjectCamLocalToEyePixel の逆。主点に依らない）。AlignProjectedModelBottomToBBox と同じ換算だが、そちらは書き換えない（2026-10-14、animalBottomClippedHeadPin）。
    public static float CameraShiftXToPutPointOnColumn(int eyeW, float fx, float pointZ, float pointU, float targetU)
    {
        return (targetU - pointU) * 2f / eyeW * (pointZ / fx);
    }

    public static float CameraShiftYToPutPointOnRow(int eyeH, float fy, float pointZ, float pointV, float targetV)
    {
        return -((targetV - pointV) * 2f / eyeH) * (pointZ / fy);
    }

    // ---- ⑧ の厳密解（StreamingStereoVideoPlayer.depthRefineExactSolve、既定 OFF、2026-10-13。Docs/tmp/depthcap_20261013/README.md の U2）----
    // ⑧（RefineDepthFromProjectedBones）はモデルを基準点の光線に沿って剛体で平行移動し、そのあと 2 回目の ⑦（FitDisplayedModelToBBox →
    // AlignProjectedModelBottomToBBox）がカメラの上下だけ合わせ直す。その 2 段を変換を書かずに写し、骨の投影の縦の広がりが目標に一致する
    // 奥行きの倍率を解く。プレイヤーの状態に触れない純粋な計算なので EditMode で確かめられる（PinholePlacementSpaceTests）。

    // 2 回目の ⑦ が何に合わせるか（FitDisplayedModelToBBox の分岐と同じ）。
    public enum SecondFitMode
    {
        // ⑦ が何もしない（レンダラーが無い・AABB の中心が目の後ろ・⑦ を掛け直さない経路）
        None,
        // 下端が画面の下で切れて上端が切れていない: メッシュの AABB の上端（AABB の中心の深さで投影）を bbox の上端へ
        Top,
        // それ以外: 一番下の投影点を bbox の下端へ
        Bottom,
        // animalBottomClippedHeadPin（2026-10-14）: 頭の骨の点を目標の行へ（その点の深さで換算）
        HeadPoint,
        // bottomFitBorderEdgesAsBounds（2026-10-25）: 上端も下端も画面の縁で切れた tick。⑧ の基準点のカメラ空間の高さを heldReferenceY へ戻す
        HoldReferenceY
    }

    // 2 回目の ⑦ を写すための値。どれも ⑧ が動かす前（anchor から作った配置）の値。AABB は平行移動でそのまま動き、縦の半幅は変わらない
    // （dci の記録で、2 回目の ⑦ の AABB の中心の深さは ⑧ の移動量どおりに 1 mm 以内で動いていた。2026-10-13）。
    public struct SecondFitModel
    {
        public SecondFitMode mode;
        // Top: bbox の上端（bboxY）、Bottom: ResolveReliableBBoxBottomVEye（eye px）
        public float targetV;
        // レンダラーの world AABB の中心（カメラ空間 y・z）と、カメラの上方向の半幅（TryProjectRendererBoundsToEyeHeight と同じ式）
        public float aabbCenterY;
        public float aabbCenterZ;
        public float aabbVerticalExtent;
        // AABB の投影に使う主点（TryGetProjectionIntrinsics の cyPixels。骨の投影は TryProjectCamLocalToEyePixel の主点）
        public float cyPixels;
        // Bottom の px → m を一番下の点自身の深さで換算する（FitDisplayedModelToBBox の alignDepth の分岐と同じ条件で決める）
        public bool bottomUsesPointDepth;
        // HeadPoint: 頭の骨の点（カメラ空間 y・z、⑧ が動かす前）。targetV はその点を置く行（eye px）
        public float pointY;
        public float pointZ;
        // HoldReferenceY: 保つ基準点（⑧ の基準点 = referenceY と同じ点）のカメラ空間の高さ（m）
        public float heldReferenceY;
        // bottomFitBorderEdgesAsBounds: Top・HoldReferenceY の移動を「一番下の点を edgeTargetV（画面の下の縁、eye px）へ置く移動」以下にする。
        // その移動の換算は Bottom と同じ（bottomUsesPointDepth）。
        public bool lowestNotAboveEdge;
        public float edgeTargetV;
    }

    // 数値上の設定（新しい係数ではない。解はこれらに依らず、評価回数と打ち切りの精度だけが変わる）。
    // 最初の一歩は ln(一手の比) の大きさ、ただし ln s で 1% 以上（比がほぼ 1 のときにも囲い込みを始めるため）。
    private const float DepthSolveMinLogStep = 0.01f;
    // ln s の幅がこれ以下で打ち切る（s を 0.001% まで: z_ref 3 m で 0.03 mm、広がり 720 px で 0.01 px。bbox の揺れ 1〜3 px より十分小さい）。
    private const float DepthSolveLogTolerance = 1e-5f;
    // 囲い込みの倍々の上限（1% から e^10 倍まで。ガードを通る比（3 以下）では 2 回以内で囲える）。
    private const int DepthSolveMaxExpansions = 10;
    // 二分の上限（ふつう 12〜20 回で上の幅に届く。最悪の手数を決めておくため）。
    private const int DepthSolveMaxBisections = 40;
    // 手前への探索は、いま目の前にある点が z = 0.002 m を越えない所まで（投影が有効な z > 0.001 m のすぐ内側。その手前の広がりは事実上無限大）。
    private const float DepthSolveNearEyeMeters = 0.002f;
    // 倍率の下限（基準点が目の前に残るように。⑧ の下限 0.25 m がこれより先に効く）。
    private const float DepthSolveMinMultiplier = 0.001f;

    // 深度倍率 s（基準点を光線に沿って z_ref → s × z_ref へ動かす剛体の平行移動。どの点も y が (s − 1) × y_ref、z が (s − 1) × z_ref だけ動く）と、
    // そのあとの 2 回目の ⑦ の縦の移動を済ませたときの、投影点の縦の広がり（eye px）。目の前に点が無い・広がりが 0 のときは −1。
    // 投影は TryProjectCamLocalToEyePixel と同じ式・同じ除外（z ≤ 0.001 の点は数えない）。横（x）は縦の広がりに効かないので受け取らない。
    public static float EvaluateExtentAfterDepthMove(
        ManifestData manifest,
        float fy,
        float[] pointY,
        float[] pointZ,
        bool[] pointIsBone,
        int count,
        float referenceY,
        float referenceZ,
        float multiplier,
        SecondFitModel fit)
    {
        float eyeH = manifest.eye_h;
        float cyNorm = ResolvePrincipalPointNorm(manifest.cy, manifest.eye_h);
        float dy = (multiplier - 1f) * referenceY;
        float dz = (multiplier - 1f) * referenceZ;

        // ⑦ が合わせる一番下の点（TryProjectBonesToEyeHeight と同じく、最初に見つけた最大の v）。
        float bottomV = float.MinValue;
        int bottomIndex = -1;
        for (int i = 0; i < count; i++)
        {
            float z = pointZ[i] + dz;
            if (z <= 0.001f)
            {
                continue;
            }

            float v = (cyNorm - ((pointY[i] + dy) * fy / z) * 0.5f) * eyeH;
            if (v > bottomV)
            {
                bottomV = v;
                bottomIndex = i;
            }
        }

        if (bottomIndex < 0)
        {
            return -1f;
        }

        // 2 回目の ⑦ の縦の移動（AlignProjectedModelBottomToBBox: −(deltaV × 2 / eye_h) × (深さ / fy)）。
        float shiftY = 0f;
        float centerZ = fit.aabbCenterZ + dz;
        if (fit.mode == SecondFitMode.HeadPoint)
        {
            // 2 回目の ⑦ の頭合わせ: ⑧ の剛体の平行移動の後の頭の点を目標の行へ。移動後の頭の行は固定されるので、広がりは dz だけで決まる（基準の光線に依らない）。
            float hz = fit.pointZ + dz;
            if (hz > 0.001f)
            {
                float hv = (cyNorm - ((fit.pointY + dy) * fy / hz) * 0.5f) * eyeH;
                shiftY = CameraShiftYToPutPointOnRow(manifest.eye_h, fy, hz, hv, fit.targetV);
            }
        }
        else if (fit.mode != SecondFitMode.None && centerZ > 0.001f)
        {
            float depth = Mathf.Max(0.001f, centerZ);
            // 一番下の点の移動の換算の深さ（Bottom と縁の判定で共用。今の Bottom と同じ条件）。
            float lowestDepth = depth;
            if (fit.bottomUsesPointDepth && pointIsBone[bottomIndex])
            {
                float pointDepth = pointZ[bottomIndex] + dz;
                if (pointDepth > 0.001f)
                {
                    lowestDepth = pointDepth;
                }
            }

            if (fit.mode == SecondFitMode.Top)
            {
                float topY = fit.aabbCenterY + dy + fit.aabbVerticalExtent;
                float topV = ((fit.cyPixels / eyeH) - (topY * fy / centerZ) * 0.5f) * eyeH;
                shiftY = -((fit.targetV - topV) * 2f / eyeH) * (depth / fy);
            }
            else if (fit.mode == SecondFitMode.HoldReferenceY)
            {
                // ⑧ の平行移動の後の基準点の高さは referenceY + dy。2 回目の ⑦ はそれを heldReferenceY へ戻す。
                shiftY = fit.heldReferenceY - (referenceY + dy);
            }
            else
            {
                shiftY = -((fit.targetV - bottomV) * 2f / eyeH) * (lowestDepth / fy);
            }

            // bottomFitBorderEdgesAsBounds: 一番下の点を画面の下の縁より上へ持ち上げない（FitDisplayedModelToBBox の Mathf.Min と同じ）。
            if (fit.lowestNotAboveEdge && (fit.mode == SecondFitMode.Top || fit.mode == SecondFitMode.HoldReferenceY))
            {
                shiftY = Mathf.Min(shiftY, -((fit.edgeTargetV - bottomV) * 2f / eyeH) * (lowestDepth / fy));
            }
        }

        float minV = float.MaxValue;
        float maxV = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            float z = pointZ[i] + dz;
            if (z <= 0.001f)
            {
                continue;
            }

            float v = (cyNorm - ((pointY[i] + dy + shiftY) * fy / z) * 0.5f) * eyeH;
            if (v < minV)
            {
                minV = v;
            }

            if (v > maxV)
            {
                maxV = v;
            }
        }

        return maxV > minV ? maxV - minV : -1f;
    }

    // 2 回目の ⑦ のあとの投影の縦の広がりが targetPixels になる深度倍率 s を求める。
    // 広がりは s について減少する（奥へ動かすほど小さく写る）として、s = 1（今の配置）での符号から向きを決め、ln s で倍々に囲い込んでから二分する。
    // 一手の比（initialGuess）は最初の一歩の大きさにだけ使う（解は一手の比に依らない）。目の前に点が無い等で広がりを測れないときは false。
    // 囲い込めなかったとき（奥へ倍々しても大きい・目の直前まで寄せても小さい）はその端の倍率を返して saturated = true。
    // residualPixels は返す倍率での（広がり − 目標）。
    // 実データ（dci の記録の走り、動物 1,456 tick・人 588 tick、2026-10-13）では全 tick で広がり − 目標の符号の変化がちょうど 1 回（解は一意）、
    // 評価は 14〜23 回だった（scratchpad の w1013/fix/D2/d2_checks_out.txt）。
    public static bool TrySolveDepthMultiplierForExtent(
        ManifestData manifest,
        float fy,
        float[] pointY,
        float[] pointZ,
        bool[] pointIsBone,
        int count,
        float referenceY,
        float referenceZ,
        SecondFitModel fit,
        float targetPixels,
        float initialGuess,
        out float multiplier,
        out int evaluations,
        out float residualPixels,
        out bool saturated)
    {
        multiplier = 1f;
        evaluations = 0;
        residualPixels = 0f;
        saturated = false;
        if (manifest == null || manifest.eye_h <= 0 || fy <= 0f ||
            pointY == null || pointZ == null || pointIsBone == null || count <= 0 ||
            count > pointY.Length || count > pointZ.Length || count > pointIsBone.Length ||
            referenceZ <= 0.0001f || targetPixels <= 0.0001f)
        {
            return false;
        }

        // 手前への探索の下限: いま目の前にある点のうち一番近い点が DepthSolveNearEyeMeters に来る倍率。
        float nearest = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            if (pointZ[i] > DepthSolveNearEyeMeters && pointZ[i] < nearest)
            {
                nearest = pointZ[i];
            }
        }

        if (nearest == float.MaxValue)
        {
            return false;
        }

        float logMin = Mathf.Log(Mathf.Max(DepthSolveMinMultiplier, 1f - (nearest - DepthSolveNearEyeMeters) / referenceZ));

        float residual0;
        if (!TryDepthSolveResidual(manifest, fy, pointY, pointZ, pointIsBone, count, referenceY, referenceZ, fit, targetPixels, 0f,
                ref evaluations, out residual0))
        {
            return false;
        }

        if (residual0 == 0f)
        {
            return true;
        }

        float step = Mathf.Max(Mathf.Abs(Mathf.Log(Mathf.Max(DepthSolveMinMultiplier, initialGuess))), DepthSolveMinLogStep);
        float logLow;
        float logHigh;
        if (residual0 > 0f)
        {
            // 大きく写っている → 奥へ（s > 1）。広がりが目標を下回る所まで倍々で伸ばす。
            logLow = 0f;
            logHigh = step;
            float residualHigh;
            if (!TryDepthSolveResidual(manifest, fy, pointY, pointZ, pointIsBone, count, referenceY, referenceZ, fit, targetPixels, logHigh,
                    ref evaluations, out residualHigh))
            {
                return false;
            }

            for (int k = 0; k < DepthSolveMaxExpansions && residualHigh > 0f; k++)
            {
                logLow = logHigh;
                step *= 2f;
                logHigh = logLow + step;
                if (!TryDepthSolveResidual(manifest, fy, pointY, pointZ, pointIsBone, count, referenceY, referenceZ, fit, targetPixels, logHigh,
                        ref evaluations, out residualHigh))
                {
                    return false;
                }
            }

            if (residualHigh > 0f)
            {
                multiplier = Mathf.Exp(logHigh);
                residualPixels = residualHigh;
                saturated = true;
                return true;
            }
        }
        else
        {
            // 小さく写っている → 手前へ（s < 1）。目の直前の下限 logMin で止める。
            logHigh = 0f;
            logLow = Mathf.Max(-step, logMin);
            float residualLow;
            if (!TryDepthSolveResidual(manifest, fy, pointY, pointZ, pointIsBone, count, referenceY, referenceZ, fit, targetPixels, logLow,
                    ref evaluations, out residualLow))
            {
                return false;
            }

            for (int k = 0; k < DepthSolveMaxExpansions && residualLow < 0f && logLow > logMin; k++)
            {
                logHigh = logLow;
                step *= 2f;
                logLow = Mathf.Max(logHigh - step, logMin);
                if (!TryDepthSolveResidual(manifest, fy, pointY, pointZ, pointIsBone, count, referenceY, referenceZ, fit, targetPixels, logLow,
                        ref evaluations, out residualLow))
                {
                    return false;
                }
            }

            if (residualLow < 0f)
            {
                multiplier = Mathf.Exp(logLow);
                residualPixels = residualLow;
                saturated = true;
                return true;
            }
        }

        // ここで 残差(logLow) ≥ 0 ≥ 残差(logHigh)。ln s で二分する。
        for (int i = 0; i < DepthSolveMaxBisections && logHigh - logLow > DepthSolveLogTolerance; i++)
        {
            float logMid = 0.5f * (logLow + logHigh);
            float residualMid;
            if (!TryDepthSolveResidual(manifest, fy, pointY, pointZ, pointIsBone, count, referenceY, referenceZ, fit, targetPixels, logMid,
                    ref evaluations, out residualMid))
            {
                return false;
            }

            if (residualMid > 0f)
            {
                logLow = logMid;
            }
            else
            {
                logHigh = logMid;
            }
        }

        float logSolution = 0.5f * (logLow + logHigh);
        multiplier = Mathf.Exp(logSolution);
        TryDepthSolveResidual(manifest, fy, pointY, pointZ, pointIsBone, count, referenceY, referenceZ, fit, targetPixels, logSolution,
            ref evaluations, out residualPixels);
        return true;
    }

    private static bool TryDepthSolveResidual(
        ManifestData manifest,
        float fy,
        float[] pointY,
        float[] pointZ,
        bool[] pointIsBone,
        int count,
        float referenceY,
        float referenceZ,
        SecondFitModel fit,
        float targetPixels,
        float logMultiplier,
        ref int evaluations,
        out float residual)
    {
        evaluations++;
        float extent = EvaluateExtentAfterDepthMove(
            manifest, fy, pointY, pointZ, pointIsBone, count, referenceY, referenceZ, Mathf.Exp(logMultiplier), fit);
        residual = extent - targetPixels;
        return extent > 0f;
    }

    private static float ResolvePrincipalPointNorm(float value, float dimension)
    {
        if (value <= 0f)
        {
            return 0.5f;
        }

        return value > 1f ? value / dimension : value;
    }
}
