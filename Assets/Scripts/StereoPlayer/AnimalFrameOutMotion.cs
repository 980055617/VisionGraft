using UnityEngine;

public static class AnimalFrameOutMotion
{
    private const float EdgeMargin01 = 0.08f;

    public static float ResolveTravelDistance(float hiddenSeconds, float speedMetersPerSecond)
    {
        return Mathf.Max(0f, hiddenSeconds) * Mathf.Max(0f, speedMetersPerSecond);
    }

    public static Vector3 ResolveDirection(
        Vector3 previousPosition,
        Vector3 currentPosition,
        bool hasPreviousPosition,
        Quaternion fallbackRotation,
        Vector3 upAxis)
    {
        Vector3 safeUp = upAxis.sqrMagnitude > 0.000001f ? upAxis.normalized : Vector3.up;
        Vector3 direction = hasPreviousPosition ? currentPosition - previousPosition : Vector3.zero;
        direction = Vector3.ProjectOnPlane(direction, safeUp);
        if (direction.sqrMagnitude <= 0.000001f)
        {
            direction = Vector3.ProjectOnPlane(fallbackRotation * Vector3.forward, safeUp);
        }
        if (direction.sqrMagnitude <= 0.000001f)
        {
            direction = Vector3.ProjectOnPlane(Vector3.forward, safeUp);
        }
        if (direction.sqrMagnitude <= 0.000001f)
        {
            direction = Vector3.right;
        }

        return direction.normalized;
    }

    public static Vector3 ResolveDirectionFromScreenMotion(
        Vector2 previousEyePixel,
        Vector2 currentEyePixel,
        bool hasPreviousEyePixel,
        Vector3 screenRight,
        Vector3 screenUp,
        Vector3 fallbackDirection)
    {
        if (!hasPreviousEyePixel)
        {
            return fallbackDirection.sqrMagnitude > 0.000001f ? fallbackDirection.normalized : Vector3.forward;
        }

        Vector2 delta = currentEyePixel - previousEyePixel;
        if (delta.sqrMagnitude <= 0.000001f)
        {
            return fallbackDirection.sqrMagnitude > 0.000001f ? fallbackDirection.normalized : Vector3.forward;
        }

        Vector3 right = screenRight.sqrMagnitude > 0.000001f ? screenRight.normalized : Vector3.right;
        Vector3 up = screenUp.sqrMagnitude > 0.000001f ? screenUp.normalized : Vector3.up;
        Vector3 direction = right * delta.x - up * delta.y;
        if (direction.sqrMagnitude <= 0.000001f)
        {
            return fallbackDirection.sqrMagnitude > 0.000001f ? fallbackDirection.normalized : Vector3.forward;
        }

        return direction.normalized;
    }

    public static Vector3 ResolveDirectionFromScreenExit(
        Rect bboxEye,
        float eyeWidth,
        float eyeHeight,
        Vector3 screenRight,
        Vector3 screenUp,
        Vector3 fallbackDirection)
    {
        Vector3 fallback = fallbackDirection.sqrMagnitude > 0.000001f ? fallbackDirection.normalized : Vector3.forward;
        if (eyeWidth <= 0f || eyeHeight <= 0f || bboxEye.width <= 0f || bboxEye.height <= 0f)
        {
            return fallback;
        }

        Vector3 right = screenRight.sqrMagnitude > 0.000001f ? screenRight.normalized : Vector3.right;
        Vector3 up = screenUp.sqrMagnitude > 0.000001f ? screenUp.normalized : Vector3.up;
        float marginX = eyeWidth * EdgeMargin01;
        float marginY = eyeHeight * EdgeMargin01;

        Vector3 direction = Vector3.zero;
        if (bboxEye.xMax >= eyeWidth - marginX)
        {
            direction += right;
        }
        if (bboxEye.xMin <= marginX)
        {
            direction -= right;
        }
        if (bboxEye.yMin <= marginY)
        {
            direction += up;
        }
        if (bboxEye.yMax >= eyeHeight - marginY)
        {
            direction -= up;
        }

        return direction.sqrMagnitude > 0.000001f ? direction.normalized : fallback;
    }

    // Fraction of the *whole* walk's duration the turn itself takes, regardless of how long
    // that total duration is - "quickly change heading, then keep walking straight" should read
    // as a quick turn relative to the walk whether the walk is 5 seconds or 0.3 seconds, so this
    // is a fraction of phaseDurationSeconds rather than a fixed number of real seconds (a fixed
    // real-second window would, for a short walk, end up covering most or all of it - turning
    // continuously throughout instead of "straight, turn, straight").
    private const float TurnWindowFraction = 0.2f;

    public static StreamingStereoVideoPlayer.AnimalFrameOutLoopPose ResolveLoopPose(
        Vector3 startPosition,
        Quaternion startRotation,
        Vector3 frameOutDirection,
        Vector3 frameInPosition,
        bool hasFrameInPosition,
        float normalizedTime,
        float travelDistance,
        float weight,
        Vector3 upAxis)
    {
        Vector3 safeUp = upAxis.sqrMagnitude > 0.000001f ? upAxis.normalized : Vector3.up;
        Vector3 forward = Vector3.ProjectOnPlane(frameOutDirection, safeUp);
        if (forward.sqrMagnitude <= 0.000001f)
        {
            forward = Vector3.ProjectOnPlane(Vector3.forward, safeUp);
        }
        if (forward.sqrMagnitude <= 0.000001f)
        {
            forward = Vector3.right;
        }
        forward.Normalize();

        float t = Mathf.Clamp01(normalizedTime);
        float distance = Mathf.Max(0f, travelDistance) * Mathf.Clamp01(weight);
        Vector3 end = hasFrameInPosition
            ? PreserveStartHeight(frameInPosition, startPosition, safeUp)
            : startPosition;
        Vector3 control = ResolveControlPoint(startPosition, end, hasFrameInPosition, forward, distance, safeUp);

        // Straight out, straight back - two segments meeting at a corner, split by their actual
        // distance ratio so both legs average the same walking speed.
        float outboundDistance = Vector3.Distance(startPosition, control);
        float returnDistance = hasFrameInPosition ? Vector3.Distance(control, end) : outboundDistance;
        float totalPathDistance = outboundDistance + returnDistance;
        float turnPoint01 = hasFrameInPosition && totalPathDistance > 0.0001f
            ? Mathf.Clamp01(outboundDistance / totalPathDistance)
            : 0.5f;

        bool returning = hasFrameInPosition && t >= turnPoint01;
        float segmentT = returning
            ? Mathf.InverseLerp(turnPoint01, 1.0f, t)
            : Mathf.InverseLerp(0.0f, turnPoint01, t);
        Vector3 segmentStart = returning ? control : startPosition;
        Vector3 segmentEnd = returning ? end : control;
        Vector3 position = Vector3.Lerp(segmentStart, segmentEnd, SmoothMotion01(segmentT));

        // The turn itself is a short Slerp window centered on turnPoint01, independent of the
        // (likely much longer) total walk duration - position keeps moving along the straight
        // segments above the whole time, the heading just catches up to the new segment's
        // direction quickly instead of all at once or gradually over the full walk.
        Vector3 returnFacing = end - control;
        if (returnFacing.sqrMagnitude <= 0.000001f)
        {
            returnFacing = -forward;
        }
        Quaternion turnedRotation = ResolveRotation(startRotation, forward, returnFacing, safeUp);
        float halfWindow01 = TurnWindowFraction * 0.5f;
        // Re-center the window so it always fits inside [0, 1] regardless of where turnPoint01
        // falls.
        float windowCenter = Mathf.Clamp(turnPoint01, halfWindow01, 1f - halfWindow01);
        float turnBlend = hasFrameInPosition
            ? Mathf.Clamp01(Mathf.InverseLerp(windowCenter - halfWindow01, windowCenter + halfWindow01, t))
            : 0f;
        Quaternion resultRotation = Quaternion.Slerp(startRotation, turnedRotation, turnBlend);
        Debug.Log($"[DEBUG-loop] t={t:F2} turnPoint01={turnPoint01:F2} windowCenter={windowCenter:F2} " +
            $"halfWindow01={halfWindow01:F2} turnBlend={turnBlend:F2} startRotation.euler={startRotation.eulerAngles:F1} " +
            $"resultRotation.euler={resultRotation.eulerAngles:F1}");

        return new StreamingStereoVideoPlayer.AnimalFrameOutLoopPose
        {
            position = PreserveStartHeight(position, startPosition, safeUp),
            rotation = resultRotation
        };
    }

    public static Vector3 PreserveStartHeight(Vector3 position, Vector3 startPosition, Vector3 upAxis)
    {
        Vector3 safeUp = upAxis.sqrMagnitude > 0.000001f ? upAxis.normalized : Vector3.up;
        return position + safeUp * Vector3.Dot(startPosition - position, safeUp);
    }

    public static Vector3 ResolveControlPoint(
        Vector3 startPosition,
        Vector3 frameInPosition,
        bool hasFrameInPosition,
        Vector3 frameOutDirection,
        float travelDistance,
        Vector3 upAxis)
    {
        Vector3 safeUp = upAxis.sqrMagnitude > 0.000001f ? upAxis.normalized : Vector3.up;
        Vector3 forward = Vector3.ProjectOnPlane(frameOutDirection, safeUp);
        if (forward.sqrMagnitude <= 0.000001f)
        {
            forward = Vector3.ProjectOnPlane(Vector3.forward, safeUp);
        }
        if (forward.sqrMagnitude <= 0.000001f)
        {
            forward = Vector3.right;
        }
        forward.Normalize();

        float distance = Mathf.Max(0f, travelDistance);
        if (!hasFrameInPosition)
        {
            return startPosition + forward * distance;
        }

        Vector3 toFrameIn = Vector3.ProjectOnPlane(frameInPosition - startPosition, safeUp);
        float frameInProjection = Vector3.Dot(toFrameIn, forward);
        float controlDistance = frameInProjection > 0f ? frameInProjection + distance : distance;
        return startPosition + forward * Mathf.Max(distance, controlDistance);
    }

    public static Vector3 ResolveWalkPosition(Vector3 posePosition)
    {
        return posePosition;
    }

    public static StreamingStereoVideoPlayer.AnimalFrameOutLoopPose ResolveOneWaySegmentPose(
        Vector3 startPosition,
        Vector3 endPosition,
        Quaternion fallbackRotation,
        Vector3 upAxis,
        float normalizedTime)
    {
        Vector3 safeUp = upAxis.sqrMagnitude > 0.000001f ? upAxis.normalized : Vector3.up;
        float t = EaseInOutMotion01(Mathf.Clamp01(normalizedTime));
        Vector3 position = Vector3.Lerp(startPosition, endPosition, t);

        Vector3 travel = Vector3.ProjectOnPlane(endPosition - startPosition, safeUp);
        Quaternion rotation = travel.sqrMagnitude > 0.000001f
            ? Quaternion.LookRotation(travel.normalized, safeUp)
            : fallbackRotation;

        return new StreamingStereoVideoPlayer.AnimalFrameOutLoopPose
        {
            position = position,
            rotation = rotation
        };
    }

    private static float EaseInOutMotion01(float value)
    {
        value = Mathf.Clamp01(value);
        return value * value * (3f - 2f * value);
    }

    private static Quaternion ResolveRotation(
        Quaternion startRotation,
        Vector3 frameOutDirection,
        Vector3 facingDirection,
        Vector3 upAxis)
    {
        Vector3 safeUp = upAxis.sqrMagnitude > 0.000001f ? upAxis.normalized : Vector3.up;
        Vector3 from = Vector3.ProjectOnPlane(frameOutDirection, safeUp);
        Vector3 to = Vector3.ProjectOnPlane(facingDirection, safeUp);
        if (from.sqrMagnitude <= 0.000001f || to.sqrMagnitude <= 0.000001f)
        {
            return startRotation;
        }

        from.Normalize();
        to.Normalize();
        float signedAngle = Vector3.SignedAngle(from, to, safeUp);
        return Quaternion.AngleAxis(signedAngle, safeUp) * startRotation;
    }

    private static float SmoothMotion01(float value)
    {
        value = Mathf.Clamp01(value);
        return value * value * (3f - 2f * value);
    }
}
