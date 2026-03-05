using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: joint smoothing cache and skeleton index metadata
    // Provides: joint validity helpers, midpoint/head fallback, smoothing and depth disambiguation

    private bool TryGetJointPoint(Vector3[] jointsWorld, byte[] vis, int idx, out Vector3 point)
    {
        point = Vector3.zero;
        if (jointsWorld == null || vis == null)
        {
            TryLogJointInvalid(idx, -1, Vector3.zero, "null_buffers");
            return false;
        }

        if (idx < 0 || idx >= jointsWorld.Length || idx >= vis.Length)
        {
            TryLogJointInvalid(idx, -1, Vector3.zero, "out_of_range");
            return false;
        }

        byte visFlag = vis[idx];
        Vector3 p = jointsWorld[idx];
        if (visFlag == 0)
        {
            TryLogJointInvalid(idx, visFlag, p, "vis0");
            return false;
        }

        if (float.IsNaN(p.x) || float.IsInfinity(p.x) ||
            float.IsNaN(p.y) || float.IsInfinity(p.y) ||
            float.IsNaN(p.z) || float.IsInfinity(p.z))
        {
            TryLogJointInvalid(idx, visFlag, p, "non_finite");
            return false;
        }

        if (p.sqrMagnitude <= InvalidJointSqrMagnitudeEpsilon)
        {
            TryLogJointInvalid(idx, visFlag, p, "near_zero");
            return false;
        }

        point = p;
        return true;
    }


    private bool TryGetMidPoint(Vector3[] jointsWorld, byte[] vis, int idxA, int idxB, out Vector3 mid)
    {
        mid = Vector3.zero;
        if (!TryGetJointPoint(jointsWorld, vis, idxA, out Vector3 a) || !TryGetJointPoint(jointsWorld, vis, idxB, out Vector3 b))
        {
            return false;
        }

        mid = (a + b) * 0.5f;
        return true;
    }


    private bool TryGetHeadTarget(Vector3[] jointsWorld, byte[] vis, Vector3 shouldersMid, SkeletonIndices idx, out Vector3 headTarget)
    {
        headTarget = Vector3.zero;

        bool hasNose = TryGetJointPoint(jointsWorld, vis, idx.nose, out Vector3 nose);
        bool hasEyes = TryGetMidPoint(jointsWorld, vis, idx.leftEye, idx.rightEye, out Vector3 eyesMid);
        if (hasNose && hasEyes)
        {
            headTarget = (nose + eyesMid) * 0.5f;
            return true;
        }

        if (hasNose)
        {
            headTarget = nose;
            return true;
        }

        if (hasEyes)
        {
            headTarget = eyesMid;
            return true;
        }

        // Final fallback when face points are missing.
        headTarget = shouldersMid + Vector3.up * 0.12f;
        return true;
    }


    private void SmoothJointsWorld(uint trackId, Vector3[] jointsWorld, byte[] vis, float alphaOverride = -1f)
    {
        if (jointsWorld == null || vis == null || jointsWorld.Length == 0)
        {
            return;
        }

        if (!smoothedJointsByTrack.TryGetValue(trackId, out Vector3[] smoothed) || smoothed == null || smoothed.Length != jointsWorld.Length)
        {
            smoothed = new Vector3[jointsWorld.Length];
            for (int i = 0; i < jointsWorld.Length; i++)
            {
                smoothed[i] = jointsWorld[i];
            }
            smoothedJointsByTrack[trackId] = smoothed;
            return;
        }

        float a = alphaOverride >= 0f ? Mathf.Clamp01(alphaOverride) : Mathf.Clamp01(jointSmoothingAlpha);
        for (int i = 0; i < jointsWorld.Length; i++)
        {
            if (i >= vis.Length || vis[i] == 0)
            {
                jointsWorld[i] = smoothed[i];
                continue;
            }

            Vector3 next = Vector3.Lerp(smoothed[i], jointsWorld[i], a);
            smoothed[i] = next;
            jointsWorld[i] = next;
        }
    }


    private void ApplyYawDepthDisambiguation(Vector3[] jointsWorld, byte[] vis, SkeletonIndices idx, Transform root, Vector3 camOrigin, float blendOverride = -1f)
    {
        if (jointsWorld == null || vis == null || root == null)
        {
            return;
        }

        float blend = blendOverride >= 0f ? Mathf.Clamp01(blendOverride) : Mathf.Clamp01(yawDepthBlend);
        float baseOffset = Mathf.Max(0f, yawDepthOffsetMeters) * blend;
        if (baseOffset <= 0.0001f)
        {
            return;
        }

        Vector3 viewAxis = camOrigin - root.position;
        if (viewAxis.sqrMagnitude < 0.000001f)
        {
            return;
        }
        viewAxis.Normalize(); // Toward camera.

        Vector3 right = root.right;
        if (right.sqrMagnitude < 0.000001f)
        {
            return;
        }
        right.Normalize();

        // Positive means camera is more on model-right side: right joints should be closer.
        float sideSign = Mathf.Sign(Vector3.Dot(viewAxis, right));
        if (Mathf.Abs(sideSign) < 0.5f)
        {
            return;
        }

        ApplySideDepthOffset(jointsWorld, vis, idx.rightShoulder, viewAxis * (baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.rightElbow, viewAxis * (baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.rightWrist, viewAxis * (baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.rightHip, viewAxis * (baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.rightKnee, viewAxis * (baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.rightAnkle, viewAxis * (baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.rightFoot, viewAxis * (baseOffset * sideSign));

        ApplySideDepthOffset(jointsWorld, vis, idx.leftShoulder, viewAxis * (-baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.leftElbow, viewAxis * (-baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.leftWrist, viewAxis * (-baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.leftHip, viewAxis * (-baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.leftKnee, viewAxis * (-baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.leftAnkle, viewAxis * (-baseOffset * sideSign));
        ApplySideDepthOffset(jointsWorld, vis, idx.leftFoot, viewAxis * (-baseOffset * sideSign));
    }


    private static void ApplySideDepthOffset(Vector3[] jointsWorld, byte[] vis, int idx, Vector3 offset)
    {
        if (idx < 0 || idx >= jointsWorld.Length || idx >= vis.Length || vis[idx] == 0)
        {
            return;
        }

        jointsWorld[idx] += offset;
    }

}

