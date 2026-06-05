using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: track/manualYaw dictionaries and selected track state in Model.cs
    // Provides: manual yaw keyframe evaluation, guide object management, joint yaw apply

    private Quaternion ApplyManualTrackYawOffset(uint trackId, int frame, Quaternion baseRotation, Vector3 upAxis)
    {
        float yawDeg = EvaluateManualYawOffsetDegForFrame(trackId, frame);

        if (Mathf.Abs(yawDeg) < 0.001f)
        {
            return baseRotation;
        }

        if (upAxis.sqrMagnitude < 0.000001f)
        {
            upAxis = Vector3.up;
        }

        return Quaternion.AngleAxis(yawDeg, upAxis.normalized) * baseRotation;
    }


    private float EvaluateManualYawOffsetDegForFrame(uint trackId, int frame)
    {
        if (!manualYawKeyframesByTrack.TryGetValue(trackId, out SortedDictionary<int, float> keys) || keys == null || keys.Count == 0)
        {
            return 0f;
        }

        if (keys.Count == 1)
        {
            foreach (KeyValuePair<int, float> kv in keys)
            {
                return kv.Value;
            }
        }

        int firstFrame = int.MaxValue;
        int lastFrame = int.MinValue;
        float firstYaw = 0f;
        float lastYaw = 0f;
        int prevFrame = int.MinValue;
        int nextFrame = int.MaxValue;
        float prevYaw = 0f;
        float nextYaw = 0f;

        foreach (KeyValuePair<int, float> kv in keys)
        {
            int keyFrame = kv.Key;
            float keyYaw = kv.Value;
            if (keyFrame < firstFrame)
            {
                firstFrame = keyFrame;
                firstYaw = keyYaw;
            }
            if (keyFrame > lastFrame)
            {
                lastFrame = keyFrame;
                lastYaw = keyYaw;
            }

            if (keyFrame <= frame && keyFrame > prevFrame)
            {
                prevFrame = keyFrame;
                prevYaw = keyYaw;
            }
            if (keyFrame >= frame && keyFrame < nextFrame)
            {
                nextFrame = keyFrame;
                nextYaw = keyYaw;
            }
        }

        if (frame <= firstFrame)
        {
            return firstYaw;
        }
        if (frame >= lastFrame)
        {
            return lastYaw;
        }
        if (prevFrame == int.MinValue)
        {
            return nextYaw;
        }
        if (nextFrame == int.MaxValue)
        {
            return prevYaw;
        }
        if (prevFrame == nextFrame)
        {
            return prevYaw;
        }

        float t = Mathf.InverseLerp(prevFrame, nextFrame, frame);
        return Mathf.Lerp(prevYaw, nextYaw, t);
    }


    private bool TryGetSelectedManualRotationTrack(out uint trackId)
    {
        trackId = 0u;
        if (selectedManualRotationTrackId < 0)
        {
            return false;
        }

        trackId = (uint)selectedManualRotationTrackId;
        return true;
    }


    private void EnsureSelectedManualRotationTrack()
    {
        if (selectedManualRotationTrackId >= 0)
        {
            return;
        }

        List<uint> ids = GetAvailableTrackIdsForManualRotation();
        if (ids.Count <= 0)
        {
            return;
        }

        selectedManualRotationTrackId = (int)ids[0];
    }


    private bool StepSelectedManualRotationTrack(int direction)
    {
        List<uint> ids = GetAvailableTrackIdsForManualRotation();
        if (ids.Count <= 0)
        {
            selectedManualRotationTrackId = -1;
            return false;
        }

        if (direction == 0)
        {
            selectedManualRotationTrackId = (int)ids[0];
            return true;
        }

        int current = selectedManualRotationTrackId;
        int index = ids.FindIndex(id => id == (uint)current);
        if (index < 0)
        {
            selectedManualRotationTrackId = (int)ids[0];
            return true;
        }

        int next = index + (direction > 0 ? 1 : -1);
        if (next < 0)
        {
            next = ids.Count - 1;
        }
        else if (next >= ids.Count)
        {
            next = 0;
        }

        selectedManualRotationTrackId = (int)ids[next];
        return true;
    }


    private List<uint> GetAvailableTrackIdsForManualRotation()
    {
        var ids = new List<uint>();
        foreach (KeyValuePair<uint, GameObject> kv in trackInstances)
        {
            if (kv.Value == null || !kv.Value.activeInHierarchy)
            {
                continue;
            }
            ids.Add(kv.Key);
        }

        ids.Sort();
        return ids;
    }


    private float GetManualYawOffsetDegForTrack(uint trackId)
    {
        return EvaluateManualYawOffsetDegForFrame(trackId, GetCurrentPlaybackFrame());
    }


    private void SetManualYawOffsetDegForTrack(uint trackId, float yawDeg)
    {
        int frame = GetCurrentPlaybackFrame();
        if (!manualYawKeyframesByTrack.TryGetValue(trackId, out SortedDictionary<int, float> keys) || keys == null)
        {
            keys = new SortedDictionary<int, float>();
            manualYawKeyframesByTrack[trackId] = keys;
        }

        keys[frame] = Mathf.Clamp(yawDeg, -180f, 180f);
    }


    private int GetManualYawKeyCountForTrack(uint trackId)
    {
        if (!manualYawKeyframesByTrack.TryGetValue(trackId, out SortedDictionary<int, float> keys) || keys == null)
        {
            return 0;
        }

        return keys.Count;
    }


    private bool HasManualYawKeyAtCurrentFrame(uint trackId)
    {
        if (!manualYawKeyframesByTrack.TryGetValue(trackId, out SortedDictionary<int, float> keys) || keys == null)
        {
            return false;
        }

        return keys.ContainsKey(GetCurrentPlaybackFrame());
    }


    private void UpdateManualYawGuide(bool visible)
    {
        if (!visible)
        {
            SetManualYawGuideVisible(false);
            return;
        }

        if (!TryResolveGuideTrackInstance(out _, out GameObject instance))
        {
            SetManualYawGuideVisible(false);
            return;
        }

        EnsureManualYawGuideCreated();
        if (manualYawGuideShaft == null || manualYawGuideTip == null)
        {
            return;
        }

        Bounds b = ComputeObjectBounds(instance);
        float height = Mathf.Max(0.1f, b.size.y);
        float len = Mathf.Clamp(height * 0.5f, 0.3f, 0.85f);
        float y = Mathf.Clamp(height * 1.1f, 0.8f, 2.4f);

        if (manualYawGuideRoot.transform.parent != instance.transform)
        {
            manualYawGuideRoot.transform.SetParent(instance.transform, false);
        }
        TransformWriter.ApplyLocalTransform(manualYawGuideRoot.transform, Vector3.zero, Quaternion.identity, Vector3.one);

        TransformWriter.ApplyLocalTransform(
            manualYawGuideShaft,
            new Vector3(0f, y, len * 0.5f),
            Quaternion.identity,
            new Vector3(0.04f, 0.04f, len));
        TransformWriter.ApplyLocalTransform(
            manualYawGuideTip,
            new Vector3(0f, y, len),
            Quaternion.identity,
            new Vector3(0.14f, 0.14f, 0.14f));
        SetManualYawGuideVisible(true);
    }


    private bool TryResolveGuideTrackInstance(out uint trackId, out GameObject instance)
    {
        trackId = 0u;
        instance = null;

        if (TryGetSelectedManualRotationTrack(out uint selectedId) &&
            trackInstances.TryGetValue(selectedId, out GameObject selected) &&
            selected != null && selected.activeInHierarchy)
        {
            trackId = selectedId;
            instance = selected;
            return true;
        }

        selectedManualRotationTrackId = -1;
        EnsureSelectedManualRotationTrack();
        if (TryGetSelectedManualRotationTrack(out uint ensuredId) &&
            trackInstances.TryGetValue(ensuredId, out GameObject ensured) &&
            ensured != null && ensured.activeInHierarchy)
        {
            trackId = ensuredId;
            instance = ensured;
            return true;
        }

        foreach (KeyValuePair<uint, GameObject> kv in trackInstances)
        {
            if (kv.Value == null || !kv.Value.activeInHierarchy)
            {
                continue;
            }

            trackId = kv.Key;
            instance = kv.Value;
            selectedManualRotationTrackId = (int)kv.Key;
            return true;
        }

        return false;
    }


    private void EnsureManualYawGuideCreated()
    {
        if (manualYawGuideRoot != null)
        {
            return;
        }

        ManualYawGuideFactory.Guide guide = ManualYawGuideFactory.Create();
        manualYawGuideRoot = guide.root;
        manualYawGuideShaft = guide.shaft;
        manualYawGuideTip = guide.tip;

        SetManualYawGuideVisible(false);
    }


    private static Bounds ComputeObjectBounds(GameObject go)
    {
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers == null || renderers.Length == 0)
        {
            return new Bounds(go.transform.position, Vector3.one * 0.2f);
        }

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            b.Encapsulate(renderers[i].bounds);
        }
        return b;
    }


    private void SetManualYawGuideVisible(bool visible)
    {
        if (manualYawGuideRoot == null)
        {
            return;
        }

        SceneObjectWriter.ApplyActive(manualYawGuideRoot, visible);
    }


    private void ApplyManualYawToJoints(uint trackId, int frame, Vector3[] jointsWorld, byte[] vis, Vector3 pivotWorld, Vector3 upAxis)
    {
        if (jointsWorld == null || vis == null || jointsWorld.Length == 0)
        {
            return;
        }

        float yawDeg = EvaluateManualYawOffsetDegForFrame(trackId, frame);
        if (Mathf.Abs(yawDeg) < 0.001f)
        {
            return;
        }

        if (upAxis.sqrMagnitude < 0.000001f)
        {
            upAxis = Vector3.up;
        }

        Quaternion yawRot = Quaternion.AngleAxis(yawDeg, upAxis.normalized);
        for (int i = 0; i < jointsWorld.Length && i < vis.Length; i++)
        {
            if (vis[i] == 0)
            {
                continue;
            }

            Vector3 local = jointsWorld[i] - pivotWorld;
            jointsWorld[i] = pivotWorld + (yawRot * local);
        }
    }

}

