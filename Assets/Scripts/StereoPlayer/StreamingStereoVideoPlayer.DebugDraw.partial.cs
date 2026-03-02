using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: debug draw state buffers, dog chain constants, GUI helpers
    // Provides: debug draw gating, axis compare helpers, OnGUI/OnDrawGizmos rendering

    private bool IsJointDebugEnabled()
    {
        return debugDrawJoints || debugDrawAnchor || debugDisableRigApply;
    }


    private void LogJointDebugSkip(string reason, int frame, uint trackId)
    {
        if (!IsJointDebugEnabled())
        {
            return;
        }

        Debug.Log($"JOINT_DEBUG_SKIP frame={frame} trackId={trackId} reason={reason}");
    }


    private static bool IsDebugJointPairValid(int idxA, int idxB, int jointCount, byte[] vis, float[] camZ)
    {
        if (idxA < 0 || idxB < 0 || idxA >= jointCount || idxB >= jointCount)
        {
            return false;
        }
        if (vis == null || idxA >= vis.Length || idxB >= vis.Length || vis[idxA] == 0 || vis[idxB] == 0)
        {
            return false;
        }
        if (camZ == null || idxA >= camZ.Length || idxB >= camZ.Length)
        {
            return false;
        }

        return !Mathf.Approximately(camZ[idxA], 0f) && !Mathf.Approximately(camZ[idxB], 0f);
    }


    private static int CountSkeletonLineSkipSegments(byte categoryId, int jointCount, byte[] vis, float[] camZ)
    {
        int skip = 0;
        if (categoryId == 2)
        {
            CountChainSkipSegments(DogLeftFrontChain, jointCount, vis, camZ, ref skip);
            CountChainSkipSegments(DogRightFrontChain, jointCount, vis, camZ, ref skip);
            CountChainSkipSegments(DogLeftRearChain, jointCount, vis, camZ, ref skip);
            CountChainSkipSegments(DogRightRearChain, jointCount, vis, camZ, ref skip);
            return skip;
        }

        for (int i = 0; i + 1 < CocoEdges.Length; i += 2)
        {
            int a = CocoEdges[i];
            int b = CocoEdges[i + 1];
            if (!IsDebugJointPairValid(a, b, jointCount, vis, camZ))
            {
                skip++;
            }
        }
        return skip;
    }


    private static void CountChainSkipSegments(int[] chain, int jointCount, byte[] vis, float[] camZ, ref int skip)
    {
        if (chain == null || chain.Length < 2)
        {
            return;
        }

        for (int i = 0; i + 1 < chain.Length; i++)
        {
            if (!IsDebugJointPairValid(chain[i], chain[i + 1], jointCount, vis, camZ))
            {
                skip++;
            }
        }
    }


    private static void DrawJointEdgeIfValid(Vector3[] jointsWorld, int jointCount, byte[] vis, float[] camZ, int a, int b)
    {
        if (!IsDebugJointPairValid(a, b, jointCount, vis, camZ))
        {
            return;
        }
        if (jointsWorld == null || a >= jointsWorld.Length || b >= jointsWorld.Length)
        {
            return;
        }
        Gizmos.DrawLine(jointsWorld[a], jointsWorld[b]);
    }


    private static void DrawJointChainIfValid(Vector3[] jointsWorld, int jointCount, byte[] vis, float[] camZ, int[] chain)
    {
        if (chain == null || chain.Length < 2)
        {
            return;
        }
        for (int i = 0; i + 1 < chain.Length; i++)
        {
            DrawJointEdgeIfValid(jointsWorld, jointCount, vis, camZ, chain[i], chain[i + 1]);
        }
    }


    private static void DrawDebugArrow(Vector3 origin, Vector3 direction, float length)
    {
        if (direction.sqrMagnitude < 0.000001f || length <= 0f)
        {
            return;
        }

        Vector3 dir = direction.normalized;
        Vector3 tip = origin + dir * length;
        Gizmos.DrawLine(origin, tip);

        Vector3 side = Vector3.Cross(dir, Vector3.up);
        if (side.sqrMagnitude < 0.000001f)
        {
            side = Vector3.Cross(dir, Vector3.right);
        }
        side.Normalize();

        float headLen = length * 0.25f;
        Vector3 back = -dir * headLen;
        Vector3 wing = side * (headLen * 0.55f);
        Gizmos.DrawLine(tip, tip + back + wing);
        Gizmos.DrawLine(tip, tip + back - wing);
    }


    private static void PickBestBoneAxisLocal(Transform bone, Vector3 targetDirWorld, out Vector3 selectedAxisLocal, out float minAngle)
    {
        selectedAxisLocal = Vector3.forward;
        minAngle = float.MaxValue;
        if (bone == null || targetDirWorld.sqrMagnitude < 0.000001f)
        {
            return;
        }

        Vector3[] candidates = new Vector3[]
        {
            Vector3.right, -Vector3.right,
            Vector3.up, -Vector3.up,
            Vector3.forward, -Vector3.forward
        };

        Vector3 targetDir = targetDirWorld.normalized;
        for (int i = 0; i < candidates.Length; i++)
        {
            Vector3 axisWorld = bone.TransformDirection(candidates[i]).normalized;
            float angle = Vector3.Angle(axisWorld, targetDir);
            if (angle < minAngle)
            {
                minAngle = angle;
                selectedAxisLocal = candidates[i];
            }
        }
    }


    private void TryApplyAutoBoneAxis(Transform bone, Vector3 targetDirWorld, int frame, uint trackId, string boneName)
    {
        if (!debugAutoBoneAxis || bone == null || targetDirWorld.sqrMagnitude < 0.000001f)
        {
            return;
        }

        Transform parent = bone.parent;
        if (parent == null)
        {
            return;
        }

        Vector3 selectedAxisLocal;
        float minAngle = -1f;
        if (!debugAutoAxisByBone.TryGetValue(bone, out selectedAxisLocal))
        {
            PickBestBoneAxisLocal(bone, targetDirWorld, out selectedAxisLocal, out minAngle);
            debugAutoAxisByBone[bone] = selectedAxisLocal;
            if (debugLogAxisCompare && !debugAutoAxisPickLogged.Contains(bone))
            {
                debugAutoAxisPickLogged.Add(bone);
                Debug.Log(
                    $"AXIS_PICK frame={frame} trackId={trackId} bone={boneName} selectedAxisLocal=({selectedAxisLocal.x:F0},{selectedAxisLocal.y:F0},{selectedAxisLocal.z:F0}) minAngle={minAngle:F2}");
            }
        }

        if (!debugAutoRestLocalRotByBone.TryGetValue(bone, out Quaternion restLocalRotation))
        {
            restLocalRotation = bone.localRotation;
            debugAutoRestLocalRotByBone[bone] = restLocalRotation;
        }

        Vector3 targetDirParent = parent.InverseTransformDirection(targetDirWorld.normalized);
        if (targetDirParent.sqrMagnitude < 0.000001f)
        {
            return;
        }
        targetDirParent.Normalize();

        Vector3 axisParentNow = parent.InverseTransformDirection(bone.TransformDirection(selectedAxisLocal).normalized);
        if (axisParentNow.sqrMagnitude > 0.000001f)
        {
            axisParentNow.Normalize();
        }

        bool flipChosen = false;
        if (axisParentNow.sqrMagnitude > 0.000001f)
        {
            float angleNormal = Vector3.Angle(axisParentNow, targetDirParent);
            float angleFlipped = Vector3.Angle(axisParentNow, -targetDirParent);
            if (angleFlipped < angleNormal)
            {
                targetDirParent = -targetDirParent;
                flipChosen = true;
            }
        }

        Vector3 restAxisLocal = (restLocalRotation * selectedAxisLocal).normalized;
        float angleBefore = Vector3.Angle(bone.TransformDirection(selectedAxisLocal).normalized, targetDirWorld.normalized);
        Quaternion desiredLocal = Quaternion.FromToRotation(restAxisLocal, targetDirParent) * restLocalRotation;
        bone.localRotation = Quaternion.Slerp(bone.localRotation, desiredLocal, Mathf.Clamp01(debugAutoBoneAxisAlpha));
        float angleAfter = Vector3.Angle(bone.TransformDirection(selectedAxisLocal).normalized, targetDirWorld.normalized);

        if (debugLogAxisCompare)
        {
            Debug.Log(
                $"AXIS_SOLVE frame={frame} trackId={trackId} bone={boneName} " +
                $"restAxisLocal=({restAxisLocal.x:F3},{restAxisLocal.y:F3},{restAxisLocal.z:F3}) " +
                $"targetDirParent=({targetDirParent.x:F3},{targetDirParent.y:F3},{targetDirParent.z:F3}) " +
                $"flipChosen={(flipChosen ? 1 : 0)} angleBefore={angleBefore:F2} angleAfter={angleAfter:F2}");
        }

        if (debugLogAxisCompare)
        {
            Debug.Log($"AXIS_COMPARE_AFTER frame={frame} trackId={trackId} bone={boneName} angleDeg={angleAfter:F2}");
        }
    }


    private DebugDrawTrackState GetOrCreateDebugDrawTrackState(uint trackId)
    {
        if (!debugDrawStateByTrack.TryGetValue(trackId, out DebugDrawTrackState state) || state == null)
        {
            state = new DebugDrawTrackState();
            debugDrawStateByTrack[trackId] = state;
        }

        return state;
    }


    private void OnGUI()
    {
        if ((!debugDrawMeta2D || meta2DOverlayItems == null || meta2DOverlayItems.Count == 0) &&
            (!debugDrawJoints2D || joints2DOverlayPoints == null || joints2DOverlayPoints.Count == 0))
        {
            return;
        }

        if (debugDrawMeta2D)
        {
            for (int i = 0; i < meta2DOverlayItems.Count; i++)
            {
                Meta2DOverlayItem item = meta2DOverlayItems[i];
                DrawRectOutline(item.eyeRect, new Color(0.1f, 0.9f, 1f, 0.8f), 1f);

                Color c = (item.trackId % 2u == 0u) ? new Color(1f, 0.92f, 0.1f, 0.95f) : new Color(1f, 0.35f, 0.15f, 0.95f);
                DrawRectOutline(item.bbox, c, 2f);
                Color old = GUI.color;
                GUI.color = c;
                GUI.DrawTexture(new Rect(item.anchor.x - 3f, item.anchor.y - 3f, 6f, 6f), Texture2D.whiteTexture);
                GUI.color = old;
            }
        }

        if (debugDrawJoints2D)
        {
            for (int i = 0; i < joints2DOverlayPoints.Count; i++)
            {
                Joints2DOverlayPoint p = joints2DOverlayPoints[i];
                Color old = GUI.color;
                GUI.color = p.color;
                GUI.DrawTexture(new Rect(p.pos.x - 2f, p.pos.y - 2f, 4f, 4f), Texture2D.whiteTexture);
                GUI.color = old;
            }
        }
    }


    private void OnDrawGizmos()
    {
        foreach (KeyValuePair<uint, DebugDrawTrackState> kv in debugDrawStateByTrack)
        {
            DebugDrawTrackState state = kv.Value;
            if (state == null)
            {
                continue;
            }

            if (debugDrawAnchor && state.hasAnchor)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawSphere(state.anchorWorld, 0.025f);
            }

            // Draw points in world space reconstructed in TryApplySkeleton from jointsCam (+ pinhole basis).
            if (debugDrawJoints && state.jointsWorld != null && state.jointCount > 0)
            {
                Gizmos.color = Color.yellow;
                int n = Mathf.Min(state.jointCount, state.jointsWorld.Length);
                for (int i = 0; i < n; i++)
                {
                    if (state.jointsVis != null && i < state.jointsVis.Length && state.jointsVis[i] == 0)
                    {
                        continue;
                    }

                    Gizmos.DrawSphere(state.jointsWorld[i], 0.012f);
                }
            }

            if (debugDrawSkeletonLines3D && state.jointsWorld != null && state.jointCount > 0)
            {
                Gizmos.color = new Color(0.15f, 1f, 0.35f, 0.9f);
                if (state.categoryId == 2)
                {
                    DrawJointChainIfValid(state.jointsWorld, state.jointCount, state.jointsVis, state.jointsCamZ, DogLeftFrontChain);
                    DrawJointChainIfValid(state.jointsWorld, state.jointCount, state.jointsVis, state.jointsCamZ, DogRightFrontChain);
                    DrawJointChainIfValid(state.jointsWorld, state.jointCount, state.jointsVis, state.jointsCamZ, DogLeftRearChain);
                    DrawJointChainIfValid(state.jointsWorld, state.jointCount, state.jointsVis, state.jointsCamZ, DogRightRearChain);
                }
                else
                {
                    for (int i = 0; i + 1 < CocoEdges.Length; i += 2)
                    {
                        DrawJointEdgeIfValid(state.jointsWorld, state.jointCount, state.jointsVis, state.jointsCamZ, CocoEdges[i], CocoEdges[i + 1]);
                    }
                }
            }

            if (debugDrawBoneAxisCompare && state.hasAxisCompare)
            {
                float axisLen = 0.18f;
                Gizmos.color = Color.green;
                DrawDebugArrow(state.axisBase, state.axisTargetDir, axisLen);
                Gizmos.color = Color.magenta;
                DrawDebugArrow(state.axisBase, state.axisBoneDir, axisLen);
            }
        }
    }
}

