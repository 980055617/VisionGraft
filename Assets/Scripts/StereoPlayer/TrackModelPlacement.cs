using UnityEngine;

public static class TrackModelPlacement
{
    public readonly struct ScaleRequest
    {
        public readonly Vector3 baseLocalScale;
        public readonly float baseHeightMeters;
        public readonly float userScale;
        public readonly float modelHeightMeters;
        public readonly float targetHeightMeters;
        public readonly float bboxHeightPixels;
        public readonly float anchorDepthMeters;
        public readonly float fy;
        public readonly int eyeHeightPixels;
        public readonly bool hasFocalLengths;

        public ScaleRequest(
            Vector3 baseLocalScale,
            float baseHeightMeters,
            float userScale,
            float modelHeightMeters,
            float targetHeightMeters,
            float bboxHeightPixels,
            float anchorDepthMeters,
            float fy,
            int eyeHeightPixels,
            bool hasFocalLengths)
        {
            this.baseLocalScale = baseLocalScale;
            this.baseHeightMeters = baseHeightMeters;
            this.userScale = userScale;
            this.modelHeightMeters = modelHeightMeters;
            this.targetHeightMeters = targetHeightMeters;
            this.bboxHeightPixels = bboxHeightPixels;
            this.anchorDepthMeters = anchorDepthMeters;
            this.fy = fy;
            this.eyeHeightPixels = eyeHeightPixels;
            this.hasFocalLengths = hasFocalLengths;
        }
    }

    public static float ResolveTargetHeightMeters(float bboxHeightPixels, int eyeHeightPixels, float depthMeters, float fy)
    {
        if (eyeHeightPixels <= 0 || bboxHeightPixels == 0f || fy <= 0f)
        {
            return 0f;
        }

        return (2f * bboxHeightPixels / eyeHeightPixels) * (depthMeters / fy);
    }

    // Human / Animal / Else すべて bbox の「高さ」だけを基準にした uniform scale で合わせる。
    //
    // Animal だけは以前 Min(scaleW, scaleH) で幅も bbox に収めていたが、これは誤りだった
    // （2026-08-07 に bundle_animal.svb 全 2120 フレームで検証）。scaleW が比べていた
    // 「bbox の幅」と「モデルの bind pose の X 幅」は同じものを測っていない:
    //
    //   - bbox 幅は動物の yaw で体長〜体幅の間を動く。実測で同一 track の W/H が
    //     0.46〜3.79（median 1.10）、shot 先頭フレームだけでも 0.63〜2.16 と 3 倍以上変動する。
    //   - モデルの X 幅は bind pose 固定で、しかも prefab によって X 軸が体長だったり
    //     体幅だったりする（AABB の W/H が 0.33〜1.84 とモデル間で 5.5 倍の開き）。
    //
    // Min は必ず小さい側を採るので、このミスマッチは常に「縮む」方向にしか働かない。
    // 実測では 22_Elk1.0 / 17_Deer1.0 が 15 shot 中 13 shot で縮み、最悪で bbox 高さの
    // 34%（1/3 の大きさ）まで潰れていた。スケールは shot 先頭でロックされるため、
    // 一度縮むとその shot の間ずっと小さいままになる。
    //
    // 高さ軸は bbox 高さと意味が対応しているので、こちらだけで合わせる。
    public static Vector3 ResolveDesiredLocalScale(ScaleRequest request)
    {
        float targetUniform = request.modelHeightMeters > 0f && request.targetHeightMeters > 0f
            ? (request.targetHeightMeters / request.modelHeightMeters) * request.userScale
            : request.userScale;

        // Else も含めて bbox + anchorZ からスケールを出す。source/other_object_proxies.json の
        // proxy3d.size は units="same_as_depth_npz" でメートルではないため使わない。
        if (request.hasFocalLengths && request.eyeHeightPixels > 0 && request.fy > 0f)
        {
            float bboxWorldH = (2f * request.bboxHeightPixels / request.eyeHeightPixels) * (request.anchorDepthMeters / request.fy);

            // 高さの基準は modelHeightMeters を使う。Humanoid では ReplaceableModel が
            // 骨格から推定した身長を返すので、bbox に合わせる対象が「メッシュの外形」ではなく
            // 「骨格」になる。AABB（baseHeightMeters）を基準にすると髪・靴・広げた腕のぶん
            // 骨格が縮み、実測で bbox の 73% まで小さくなっていた（頭上のボールが浮く原因）。
            // Humanoid でないモデルでは modelHeightMeters は AABB 高さと同じ値になるため、
            // Animal / Else の挙動は変わらない。
            float heightBasis = request.modelHeightMeters > 0f
                ? request.modelHeightMeters
                : request.baseHeightMeters;
            float uniformScale = heightBasis > 0f ? bboxWorldH / heightBasis : targetUniform;
            return request.baseLocalScale * uniformScale;
        }

        return request.baseLocalScale * targetUniform;
    }
}
