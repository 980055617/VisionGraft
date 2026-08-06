using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: manifest/metaHeader fields in Core.cs and Meta partial
    // Provides: manifest-driven crop helpers and intrinsics/fov accessors

    private bool IsManifestJointsSpaceRootRelative()
    {
        return manifest != null && manifest.joints_space == "camera_xyz_root_relative";
    }


    private int GetFullWidth()
    {
        if (manifest != null && manifest.width > 0)
        {
            return manifest.width;
        }

        return metaHeader.width;
    }


    private float GetManifestFovxDeg()
    {
        return manifest != null && manifest.fovx_deg > 0f
            ? manifest.fovx_deg
            : 0f;
    }


    private float GetManifestQuantPosScale()
    {
        return manifest != null && manifest.quant_pos_scale > 0f
            ? manifest.quant_pos_scale
            : 0f;
    }

    // meta.bin の anchor_z がどちら向きかを manifest から判定する。
    //
    // 2026-08-06 の生成側修正で anchor_z は larger=farther に統一され、その目印として
    // depth_policy ブロックが manifest に入るようになった。それ以前の bundle
    // （手元に残っているのは bundle.svb）は depth_policy を持たず、anchor_z は
    // larger=nearer のままなので、両方を同じ runtime で再生できるようここで分岐する。
    // 値そのものから向きを推定しない（bundle によっては深度が中央付近に固まっていて
    // 推定が成立しない）。bundle_human / bundle_animal / bundle_train は 2026-08-07 の
    // 再生成で depth_policy 付きになった。
    private bool IsAnchorDepthLargerMeansFarther()
    {
        return manifest != null &&
               manifest.depth_policy != null &&
               !string.IsNullOrEmpty(manifest.depth_policy.convention);
    }

    // z01 を「この bundle で実際に使われている範囲」で 0..1 に張り直す。
    // 較正できていない bundle や、レンジが極端に狭い場合は素通しする。
    private float NormalizeAnchorZ01(float z01)
    {
        if (!enableAnchorDepthRangeNormalization || !hasAnchorZ01Range)
        {
            return z01;
        }

        float span = anchorZ01RangeMax - anchorZ01RangeMin;
        if (span < 0.0001f)
        {
            return z01;
        }

        return Mathf.Clamp01((z01 - anchorZ01RangeMin) / span);
    }


    // disparity（nearness、大きいほど近い）から popout の割合を求める。
    // 1 を返すと最も手前、0 でスクリーン面。
    //
    // bundle の anchor_z は 1/Z に比例する量（disparity 系）である。生成側で確認済み:
    // depth.npz から anchor_z を作る disparity_to_camera_z() は `1.0 - disparity` という
    // アフィン変換だけを行い、逆数を取る操作をどの経路（person / other / animal）でも
    // 一切行っていない。DepthCrafter 自体が disp_raw ≈ a/Z + b を出すので、
    // 実距離へ直すには Unity 側で逆数を取る必要がある。
    //     Z ∝ 1 / d       （d = 1 - anchor_z）
    // 以前はここで nearness をそのまま返しており（disparity をそのまま距離として使用）、
    // 実距離の変化幅を再現できていなかった。bundle_human.svb 実測では、実距離が 1.517 倍
    // 変化する場面で配置深度が 1.21 倍しか動いていなかった（反比例にすると 1.45 倍で一致）。
    //
    // 逆数は分母が 0 に近づくと発散するため、次の 3 段で保護している:
    //   1. d の範囲は CalibrateAnchorDepthRange が 2〜98 パーセンタイルで求める（外れ値除去）
    //   2. d をその範囲へ Clamp してから逆数を取る（分母が 0 に近づかない）
    //   3. 結果を Clamp01（数値誤差で範囲外に出ても配置は壊れない）
    // 範囲が取れなかった bundle では逆数変換を諦めて素の disparity を返す。これは
    // 「線形が正しい」からではなく、較正できないときの退避である。
    private float ResolvePopoutFraction(float nearness)
    {
        if (!hasAnchorZ01Range)
        {
            return Mathf.Clamp01(nearness);
        }

        float dMin = 1f - anchorZ01RangeMax;   // 最も遠い側の disparity
        float dMax = 1f - anchorZ01RangeMin;   // 最も近い側の disparity
        if (dMin <= AnchorDisparityMinimum || dMax - dMin < 0.0001f)
        {
            return Mathf.Clamp01(nearness);
        }

        float d = Mathf.Clamp(nearness, dMin, dMax);
        float invNear = 1f / dMax;             // 近い = Z 小
        float invFar = 1f / dMin;              // 遠い = Z 大
        float farness = (1f / d - invNear) / (invFar - invNear);

        if (logInverseDepthRange && !loggedInverseDepthRange)
        {
            loggedInverseDepthRange = true;
            Debug.Log(
                $"[INVDEPTH] disparity {dMin:F4}〜{dMax:F4} (比 {dMax / dMin:F3}) " +
                $"→ 1/d {invNear:F4}〜{invFar:F4}");
        }

        return 1f - Mathf.Clamp01(farness);
    }


    private float DecodeAnchorDepthMetersFromBundle(float zRaw01)
    {
        if (float.IsNaN(zRaw01) || float.IsInfinity(zRaw01))
        {
            zRaw01 = 0f;
        }

        // この bundle で実際に使われている範囲へ引き伸ばしてから popout に渡す。
        // 単調変換なので前後関係は変わらず、奥行きの解像度だけが上がる。
        float z01 = NormalizeAnchorZ01(Mathf.Clamp01(zRaw01));
        // popout は「スクリーンからどれだけ手前に出すか」なので近さが要る。
        // larger=farther の bundle で z01 をそのまま使うと前後関係が反転し、
        // 例えば bundle_human.svb ではボールが人物の奥に回り込む。
        float nearness = IsAnchorDepthLargerMeansFarther() ? (1f - z01) : z01;
        float screenDist = Mathf.Max(0.001f, screenDistanceMeters);
        float eps = Mathf.Max(0f, EpsilonMeters);
        float popout = Mathf.Max(0f, popoutRangeMeters) * ResolvePopoutFraction(nearness);

        float zPlacement = screenDist - eps - popout;
        zPlacement = Mathf.Max(zPlacement, Mathf.Max(0.001f, MinDistanceFromHeadMeters));
        zPlacement = Mathf.Min(zPlacement, screenDist - 0.0001f);
        return Mathf.Max(0.001f, zPlacement);
    }

    private Vector3 DecodeJointCamFromBundle(Vector3 bundleCam)
    {
        if (float.IsNaN(bundleCam.x) || float.IsInfinity(bundleCam.x) ||
            float.IsNaN(bundleCam.y) || float.IsInfinity(bundleCam.y) ||
            float.IsNaN(bundleCam.z) || float.IsInfinity(bundleCam.z))
        {
            return Vector3.zero;
        }

        return bundleCam;
    }

}

