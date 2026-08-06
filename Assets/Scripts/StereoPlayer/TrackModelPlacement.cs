using UnityEngine;

public static class TrackModelPlacement
{
    public readonly struct ScaleRequest
    {
        public readonly Vector3 baseLocalScale;
        public readonly Vector2 baseBoundsSize;
        public readonly float userScale;
        public readonly float modelHeightMeters;
        public readonly float targetHeightMeters;
        public readonly float bboxWidthPixels;
        public readonly float bboxHeightPixels;
        public readonly float anchorDepthMeters;
        public readonly float fx;
        public readonly float fy;
        public readonly int eyeWidthPixels;
        public readonly int eyeHeightPixels;
        public readonly bool isAnimal;
        public readonly bool hasFocalLengths;

        public ScaleRequest(
            Vector3 baseLocalScale,
            Vector2 baseBoundsSize,
            float userScale,
            float modelHeightMeters,
            float targetHeightMeters,
            float bboxWidthPixels,
            float bboxHeightPixels,
            float anchorDepthMeters,
            float fx,
            float fy,
            int eyeWidthPixels,
            int eyeHeightPixels,
            bool isAnimal,
            bool hasFocalLengths)
        {
            this.baseLocalScale = baseLocalScale;
            this.baseBoundsSize = baseBoundsSize;
            this.userScale = userScale;
            this.modelHeightMeters = modelHeightMeters;
            this.targetHeightMeters = targetHeightMeters;
            this.bboxWidthPixels = bboxWidthPixels;
            this.bboxHeightPixels = bboxHeightPixels;
            this.anchorDepthMeters = anchorDepthMeters;
            this.fx = fx;
            this.fy = fy;
            this.eyeWidthPixels = eyeWidthPixels;
            this.eyeHeightPixels = eyeHeightPixels;
            this.isAnimal = isAnimal;
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

    public static Vector3 ResolveDesiredLocalScale(ScaleRequest request)
    {
        float targetUniform = request.modelHeightMeters > 0f && request.targetHeightMeters > 0f
            ? (request.targetHeightMeters / request.modelHeightMeters) * request.userScale
            : request.userScale;

        // Else も含めて bbox + anchorZ からスケールを出す。source/other_object_proxies.json の
        // proxy3d.size は units="same_as_depth_npz" でメートルではないため使わない。
        if (request.hasFocalLengths && request.eyeWidthPixels > 0 && request.eyeHeightPixels > 0 && request.fx > 0f && request.fy > 0f)
        {
            float bboxWorldW = (2f * request.bboxWidthPixels / request.eyeWidthPixels) * (request.anchorDepthMeters / request.fx);
            float bboxWorldH = (2f * request.bboxHeightPixels / request.eyeHeightPixels) * (request.anchorDepthMeters / request.fy);

            // 高さの基準は modelHeightMeters を使う。Humanoid では ReplaceableModel が
            // 骨格から推定した身長を返すので、bbox に合わせる対象が「メッシュの外形」ではなく
            // 「骨格」になる。AABB（baseBoundsSize.y）を基準にすると髪・靴・広げた腕のぶん
            // 骨格が縮み、実測で bbox の 73% まで小さくなっていた（頭上のボールが浮く原因）。
            // Humanoid でないモデルでは modelHeightMeters は AABB 高さと同じ値になるため、
            // Animal / Else の挙動は変わらない。
            float heightBasis = request.modelHeightMeters > 0f
                ? request.modelHeightMeters
                : request.baseBoundsSize.y;
            float scaleW = request.baseBoundsSize.x > 0f ? bboxWorldW / request.baseBoundsSize.x : targetUniform;
            float scaleH = heightBasis > 0f ? bboxWorldH / heightBasis : targetUniform;
            float uniformScale = request.isAnimal ? Mathf.Min(scaleW, scaleH) : scaleH;
            return request.baseLocalScale * uniformScale;
        }

        return request.baseLocalScale * targetUniform;
    }
}
