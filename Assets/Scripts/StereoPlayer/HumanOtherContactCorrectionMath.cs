using UnityEngine;

public static class HumanOtherContactCorrectionMath
{
    public static float ResolveContactWeight(
        float sourceCenterToHumanBoundaryPixels,
        float otherRadiusPixels,
        float fullContactRadiusMultiplier,
        float releaseRadiusMultiplier)
    {
        if (otherRadiusPixels <= 0f)
        {
            return 0f;
        }

        float fullDistance = otherRadiusPixels * Mathf.Max(0f, fullContactRadiusMultiplier);
        float releaseDistance =
            otherRadiusPixels *
            Mathf.Max(fullContactRadiusMultiplier, releaseRadiusMultiplier);
        if (sourceCenterToHumanBoundaryPixels <= fullDistance)
        {
            return 1f;
        }
        if (sourceCenterToHumanBoundaryPixels >= releaseDistance ||
            releaseDistance <= fullDistance)
        {
            return 0f;
        }

        float t = Mathf.Clamp01(
            (sourceCenterToHumanBoundaryPixels - fullDistance) /
            (releaseDistance - fullDistance));
        float smoothT = t * t * (3f - 2f * t);
        return 1f - smoothT;
    }

    public static bool TryResolveMappedSegmentContact(
        Vector2 sourceOtherPixel,
        Vector2 sourceSegmentStart,
        Vector2 sourceSegmentEnd,
        Vector2 displayedSegmentStart,
        Vector2 displayedSegmentEnd,
        float minimumCenterToSegmentPixels,
        out Vector2 targetPixel,
        out float segmentParameter)
    {
        targetPixel = sourceOtherPixel;
        if (!TryResolveSourceSegmentContact(
                sourceOtherPixel,
                sourceSegmentStart,
                sourceSegmentEnd,
                out segmentParameter,
                out Vector2 localDirection))
        {
            return false;
        }

        return TryResolveDisplayedSegmentContact(
            displayedSegmentStart,
            displayedSegmentEnd,
            segmentParameter,
            localDirection,
            minimumCenterToSegmentPixels,
            out targetPixel);
    }

    public static bool TryResolveSourceSegmentContact(
        Vector2 sourceOtherPixel,
        Vector2 sourceSegmentStart,
        Vector2 sourceSegmentEnd,
        out float segmentParameter,
        out Vector2 localDirection)
    {
        segmentParameter = 0f;
        localDirection = Vector2.zero;
        Vector2 sourceSegment =
            sourceSegmentEnd - sourceSegmentStart;
        if (sourceSegment.sqrMagnitude <= 0.0001f)
        {
            return false;
        }

        segmentParameter = ClosestPointParameter(
            sourceOtherPixel,
            sourceSegmentStart,
            sourceSegmentEnd);
        Vector2 sourceClosest =
            Vector2.Lerp(sourceSegmentStart, sourceSegmentEnd, segmentParameter);
        Vector2 sourceOffset = sourceOtherPixel - sourceClosest;
        if (sourceOffset.sqrMagnitude <= 0.0001f)
        {
            return false;
        }

        Vector2 sourceTangent = sourceSegment.normalized;
        Vector2 sourceNormal =
            new Vector2(-sourceTangent.y, sourceTangent.x);
        localDirection = new Vector2(
            Vector2.Dot(sourceOffset, sourceTangent),
            Vector2.Dot(sourceOffset, sourceNormal)).normalized;
        return IsFinite(localDirection.x) && IsFinite(localDirection.y);
    }

    public static bool TryResolveDisplayedSegmentContact(
        Vector2 displayedSegmentStart,
        Vector2 displayedSegmentEnd,
        float segmentParameter,
        Vector2 localDirection,
        float minimumCenterToSegmentPixels,
        out Vector2 targetPixel)
    {
        targetPixel = Vector2.zero;
        Vector2 displayedSegment =
            displayedSegmentEnd - displayedSegmentStart;
        if (displayedSegment.sqrMagnitude <= 0.0001f ||
            localDirection.sqrMagnitude <= 0.0001f ||
            minimumCenterToSegmentPixels <= 0f)
        {
            return false;
        }

        Vector2 displayedTangent = displayedSegment.normalized;
        Vector2 displayedNormal =
            new Vector2(-displayedTangent.y, displayedTangent.x);
        localDirection.Normalize();
        Vector2 displayedDirection =
            displayedTangent * localDirection.x +
            displayedNormal * localDirection.y;
        Vector2 displayedClosest = Vector2.Lerp(
            displayedSegmentStart,
            displayedSegmentEnd,
            segmentParameter);
        targetPixel =
            displayedClosest +
            displayedDirection.normalized * minimumCenterToSegmentPixels;
        return IsFinite(targetPixel.x) && IsFinite(targetPixel.y);
    }

    public static float DistanceToSegment(
        Vector2 point,
        Vector2 start,
        Vector2 end)
    {
        Vector2 segment = end - start;
        float lengthSquared = segment.sqrMagnitude;
        if (lengthSquared <= 0.0001f)
        {
            return Vector2.Distance(point, start);
        }

        float t = Mathf.Clamp01(
            Vector2.Dot(point - start, segment) / lengthSquared);
        return Vector2.Distance(point, start + segment * t);
    }

    public static float ClosestPointParameter(
        Vector2 point,
        Vector2 start,
        Vector2 end)
    {
        Vector2 segment = end - start;
        float lengthSquared = segment.sqrMagnitude;
        if (lengthSquared <= 0.0001f)
        {
            return 0f;
        }

        return Mathf.Clamp01(
            Vector2.Dot(point - start, segment) / lengthSquared);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
