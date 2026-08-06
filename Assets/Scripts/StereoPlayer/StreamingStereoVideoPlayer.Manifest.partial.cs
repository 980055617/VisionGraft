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
    // (bundle.svb / bundle_old.svb / bundle_train.svb) は depth_policy を持たず、
    // anchor_z は larger=nearer のままなので、両方を同じ runtime で再生できるよう
    // ここで分岐する。値そのものから向きを推定しない（bundle によっては深度が
    // 中央付近に固まっていて推定が成立しない）。
    private bool IsAnchorDepthLargerMeansFarther()
    {
        return manifest != null &&
               manifest.depth_policy != null &&
               !string.IsNullOrEmpty(manifest.depth_policy.convention);
    }

    private float DecodeAnchorDepthMetersFromBundle(float zRaw01)
    {
        if (float.IsNaN(zRaw01) || float.IsInfinity(zRaw01))
        {
            zRaw01 = 0f;
        }

        float z01 = Mathf.Clamp01(zRaw01);
        // popout は「スクリーンからどれだけ手前に出すか」なので近さが要る。
        // larger=farther の bundle で z01 をそのまま使うと前後関係が反転し、
        // 例えば bundle_human.svb ではボールが人物の奥に回り込む。
        float nearness = IsAnchorDepthLargerMeansFarther() ? (1f - z01) : z01;
        float screenDist = Mathf.Max(0.001f, screenDistanceMeters);
        float eps = Mathf.Max(0f, EpsilonMeters);
        float popout = Mathf.Max(0f, PopoutRangeMeters) * nearness;

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

