using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: diag counters, pendingAnimatorChecks buffer, debug compare flags
    // Provides: rig diagnostic budget checks, frame/apply logs, late-update animator flush

    private bool TryLogJointInvalid(int idx, int visFlag, Vector3 p, string reason)
    {
        if (!debugLogAxisCompare)
        {
            return false;
        }

        if (debugJointInvalidLogFrame != debugJointContextFrame)
        {
            debugJointInvalidLogFrame = debugJointContextFrame;
            debugJointInvalidLogCount = 0;
        }

        if (debugJointInvalidLogCount >= MaxJointInvalidLogsPerFrame)
        {
            return false;
        }

        debugJointInvalidLogCount++;
        Debug.Log(
            $"JOINT_INVALID frame={debugJointContextFrame} trackId={debugJointContextTrackId} idx={idx} vis={visFlag} " +
            $"p=({p.x:F3},{p.y:F3},{p.z:F3}) reason={reason}");
        return true;
    }


    private bool ShouldEmitRigDiag(int frame, uint trackId)
    {
        if (!debugLogAxisCompare)
        {
            return false;
        }
        if (frame < DiagFrameStart || frame > DiagFrameEnd)
        {
            return false;
        }
        return trackId == 0u || trackId == 1u;
    }


    private bool TryConsumeDiagBudget(int frame)
    {
        if (debugDiagLogFrame != frame)
        {
            debugDiagLogFrame = frame;
            debugDiagLogCount = 0;
        }
        if (debugDiagLogCount >= MaxDiagLogsPerFrame)
        {
            return false;
        }
        debugDiagLogCount++;
        return true;
    }


    private void TryLogSpaceCheck(int frame, MetaObj obj, bool rootRel, Vector3 rootWorld, Transform screen, Vector3[] jointsWorld)
    {
        if (!ShouldEmitRigDiag(frame, obj.trackId) || !TryConsumeDiagBudget(frame))
        {
            return;
        }
        Vector3 jCam0 = (obj.jointsCam != null && obj.jointsCam.Length > 0) ? obj.jointsCam[0] : Vector3.zero;
        Vector3 jWorld0 = (jointsWorld != null && jointsWorld.Length > 0) ? jointsWorld[0] : Vector3.zero;
        Vector3 screenPos = screen != null ? screen.position : Vector3.zero;
        Debug.Log(
            $"SPACE_CHECK frame={frame} trackId={obj.trackId} jointsSpace={(rootRel ? "RootRel" : "CamSpace")} rootSubtracted={(rootRel ? 1 : 0)} " +
            $"jointsCam0=({jCam0.x:F3},{jCam0.y:F3},{jCam0.z:F3}) jointsWorld0=({jWorld0.x:F3},{jWorld0.y:F3},{jWorld0.z:F3}) " +
            $"instancePos=({rootWorld.x:F3},{rootWorld.y:F3},{rootWorld.z:F3}) screenPos=({screenPos.x:F3},{screenPos.y:F3},{screenPos.z:F3})");
    }


    private void TryLogAnchorCheck(int frame, MetaObj obj, Vector3 modelPosBefore, Vector3 modelPosAfter, Vector3 rootWorld, Vector3[] jointsWorld)
    {
        if (!ShouldEmitRigDiag(frame, obj.trackId) || !TryConsumeDiagBudget(frame))
        {
            return;
        }
        Vector3 jwRoot = (jointsWorld != null && jointsWorld.Length > 0) ? jointsWorld[0] : Vector3.zero;
        Debug.Log(
            $"ANCHOR_CHECK frame={frame} trackId={obj.trackId} anchorWorldUsed=1 anchor=({rootWorld.x:F3},{rootWorld.y:F3},{rootWorld.z:F3}) " +
            $"rootWorld=({rootWorld.x:F3},{rootWorld.y:F3},{rootWorld.z:F3}) instanceBefore=({modelPosBefore.x:F3},{modelPosBefore.y:F3},{modelPosBefore.z:F3}) " +
            $"instanceAfter=({modelPosAfter.x:F3},{modelPosAfter.y:F3},{modelPosAfter.z:F3}) jointsWorldRoot=({jwRoot.x:F3},{jwRoot.y:F3},{jwRoot.z:F3})");
    }


    private void TryLogCloudBoneErr(int frame, uint trackId, string boneName, Transform bone, Vector3[] jointsWorld, int jointIndex)
    {
        if (!ShouldEmitRigDiag(frame, trackId) || !TryConsumeDiagBudget(frame))
        {
            return;
        }
        if (bone == null || jointsWorld == null || jointIndex < 0 || jointIndex >= jointsWorld.Length)
        {
            return;
        }
        Vector3 jw = jointsWorld[jointIndex];
        Vector3 bw = bone.position;
        float err = Vector3.Distance(jw, bw);
        Debug.Log(
            $"CLOUD_BONE_ERR frame={frame} trackId={trackId} bone={boneName} " +
            $"jointWorld=({jw.x:F3},{jw.y:F3},{jw.z:F3}) boneWorld=({bw.x:F3},{bw.y:F3},{bw.z:F3}) err={err:F3} " +
            $"usedJointIndex={jointIndex} usedArray=jointsWorld localOrWorld=world");
    }


    private void QueueAnimatorCheckSample(int frame, uint trackId, string boneName, Transform bone, Vector3 before, Vector3 after, Animator animator)
    {
        if (!ShouldEmitRigDiag(frame, trackId) || bone == null)
        {
            return;
        }
        pendingAnimatorChecks.Add(new AnimatorCheckSample
        {
            frame = frame,
            trackId = trackId,
            boneName = boneName,
            bone = bone,
            boneBeforeApply = before,
            boneAfterApply = after,
            animatorEnabled = animator != null && animator.enabled,
            updateMode = animator != null ? animator.updateMode : AnimatorUpdateMode.Normal
        });
    }


    private void FlushAnimatorCheckLateUpdate()
    {
        if (pendingAnimatorChecks.Count == 0)
        {
            return;
        }
        for (int i = 0; i < pendingAnimatorChecks.Count; i++)
        {
            AnimatorCheckSample s = pendingAnimatorChecks[i];
            if (!ShouldEmitRigDiag(s.frame, s.trackId) || !TryConsumeDiagBudget(s.frame))
            {
                continue;
            }
            Vector3 late = s.bone != null ? s.bone.position : Vector3.zero;
            Debug.Log(
                $"ANIMATOR_CHECK frame={s.frame} trackId={s.trackId} bone={s.boneName} " +
                $"boneBeforeApply=({s.boneBeforeApply.x:F3},{s.boneBeforeApply.y:F3},{s.boneBeforeApply.z:F3}) " +
                $"boneAfterApply=({s.boneAfterApply.x:F3},{s.boneAfterApply.y:F3},{s.boneAfterApply.z:F3}) " +
                $"boneAfterLateUpdate=({late.x:F3},{late.y:F3},{late.z:F3}) animatorEnabled={(s.animatorEnabled ? 1 : 0)} updateMode={s.updateMode}");
        }
        pendingAnimatorChecks.Clear();
    }


    private void TryLogFrameApplySummary(
        int frame,
        uint trackId,
        byte categoryId,
        int kpCount,
        int visCount,
        int invalidCount,
        string jointsSpaceMode,
        bool anchorWorldUsed,
        Vector3 anchorWorld,
        Vector3 rootWorld,
        Vector3 modelPosBefore,
        Vector3 modelPosAfter,
        string reasonSkipped)
    {
        if (!debugLogAxisCompare)
        {
            return;
        }

        if (debugFrameApplySummaryLogFrame != frame)
        {
            debugFrameApplySummaryLogFrame = frame;
            debugFrameApplySummaryLogCount = 0;
        }

        if (debugFrameApplySummaryLogCount >= MaxFrameApplySummaryLogsPerFrame)
        {
            return;
        }

        debugFrameApplySummaryLogCount++;
        Vector3 delta = modelPosAfter - modelPosBefore;
        Debug.Log(
            $"FRAME_APPLY_SUMMARY frame={frame} trackId={trackId} category={categoryId} kpCount={kpCount} visCount={visCount} invalidCount={invalidCount} " +
            $"jointsSpace={jointsSpaceMode} anchorWorldUsed={(anchorWorldUsed ? 1 : 0)} " +
            $"anchor=({anchorWorld.x:F3},{anchorWorld.y:F3},{anchorWorld.z:F3}) root=({rootWorld.x:F3},{rootWorld.y:F3},{rootWorld.z:F3}) " +
            $"modelPosBefore=({modelPosBefore.x:F3},{modelPosBefore.y:F3},{modelPosBefore.z:F3}) modelPosAfter=({modelPosAfter.x:F3},{modelPosAfter.y:F3},{modelPosAfter.z:F3}) " +
            $"delta=({delta.x:F3},{delta.y:F3},{delta.z:F3}) reasonSkipped={reasonSkipped}");
    }

}

