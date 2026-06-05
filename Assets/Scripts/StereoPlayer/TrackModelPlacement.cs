using UnityEngine;

public static class TrackModelPlacement
{
    public readonly struct ScaleRequest
    {
        public readonly Vector3 baseLocalScale;
        public readonly Vector2 baseBoundsSize;
        public readonly Vector3 otherProxySize;
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
        public readonly bool isOther;
        public readonly bool hasOtherProxySize;
        public readonly bool isAnimal;
        public readonly bool hasFocalLengths;

        public ScaleRequest(
            Vector3 baseLocalScale,
            Vector2 baseBoundsSize,
            Vector3 otherProxySize,
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
            bool isOther,
            bool hasOtherProxySize,
            bool isAnimal,
            bool hasFocalLengths)
        {
            this.baseLocalScale = baseLocalScale;
            this.baseBoundsSize = baseBoundsSize;
            this.otherProxySize = otherProxySize;
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
            this.isOther = isOther;
            this.hasOtherProxySize = hasOtherProxySize;
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

        if (request.isOther && request.hasOtherProxySize && request.otherProxySize.sqrMagnitude > 0.000001f)
        {
            Vector3 proxySize = AbsVector(request.otherProxySize);
            float scaleW = request.baseBoundsSize.x > 0.000001f ? proxySize.x / request.baseBoundsSize.x : targetUniform;
            float scaleH = request.baseBoundsSize.y > 0.000001f ? proxySize.y / request.baseBoundsSize.y : targetUniform;
            float proxyUniform = Mathf.Max(scaleW, scaleH) * request.userScale;
            if (proxyUniform <= 0.000001f)
            {
                proxyUniform = targetUniform;
            }

            return request.baseLocalScale * proxyUniform;
        }

        if (request.hasFocalLengths && request.eyeWidthPixels > 0 && request.eyeHeightPixels > 0 && request.fx > 0f && request.fy > 0f)
        {
            float bboxWorldW = (2f * request.bboxWidthPixels / request.eyeWidthPixels) * (request.anchorDepthMeters / request.fx);
            float bboxWorldH = (2f * request.bboxHeightPixels / request.eyeHeightPixels) * (request.anchorDepthMeters / request.fy);
            float scaleW = request.baseBoundsSize.x > 0f ? bboxWorldW / request.baseBoundsSize.x : targetUniform;
            float scaleH = request.baseBoundsSize.y > 0f ? bboxWorldH / request.baseBoundsSize.y : targetUniform;
            float uniformScale = request.isAnimal ? Mathf.Min(scaleW, scaleH) : scaleH;
            return request.baseLocalScale * uniformScale;
        }

        return request.baseLocalScale * targetUniform;
    }

    private static Vector3 AbsVector(Vector3 v)
    {
        return new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }
}
