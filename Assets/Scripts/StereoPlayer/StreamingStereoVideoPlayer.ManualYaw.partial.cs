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
        manualYawKeyframesByTrack.TryGetValue(trackId, out SortedDictionary<int, float> keys);
        return TrackKeyframeCurve.Evaluate(keys, frame, 0f);
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


    // 触れる track の一覧。
    //
    // **生きているインスタンスだけを見てはいけない。** モデルを変更すると
    // RecreateTrackInstanceForModelSelection がインスタンスを破棄し、次に
    // ApplyTrackFrame が走るまで再生成されない。ところがピッカーを開いている間は
    // 再生を止めているので走らず、**いま選んでいる track が一覧から消える**
    // （2026-08-31 実機: 0 を変更したらタブから 0 が消えて 1 だけになった）。
    //
    // いま表示中のフレームに写っている track（metaFrameObjects）も足して和を取る。
    // インスタンスの有無は「作り直し中かどうか」でしかなく、対象として選べるかとは別。
    private List<uint> GetAvailableTrackIdsForManualRotation()
    {
        var seen = new HashSet<uint>();
        var ids = new List<uint>();

        foreach (KeyValuePair<uint, GameObject> kv in trackInstances)
        {
            if (kv.Value == null || !kv.Value.activeInHierarchy)
            {
                continue;
            }

            if (seen.Add(kv.Key))
            {
                ids.Add(kv.Key);
            }
        }

        for (int i = 0; i < metaFrameObjects.Count; i++)
        {
            if (seen.Add(metaFrameObjects[i].trackId))
            {
                ids.Add(metaFrameObjects[i].trackId);
            }
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

        // **world で測った寸法を local にそのまま入れてはいけない。**
        // ガイドは instance の子で、instance の localScale は bbox 合わせで 0.26 などに
        // なっている。ComputeObjectBounds は world AABB を返すので、その値を local の
        // 位置・大きさに使うと instance のスケールぶんもう一度縮み、**頭上に出るはずの矢印が
        // モデルの中に埋まって小さな点に見える**（2026-08-31 実機で「変な点が見える」と報告）。
        //
        // world で決めた寸法を lossyScale で割って local に直す。こうすると
        // モデルの大小によらずガイドの実寸が一定になる。
        Transform root = instance.transform;
        float lossyY = Mathf.Max(0.0001f, root.lossyScale.y);

        // **ガイド自身を測ってはいけない。** ガイドは instance の子なので、素直に
        // GetComponentsInChildren すると「モデルの上端」にガイドの高さが含まれる。
        // すると次のフレームでガイドがさらに上へ行き、毎フレーム積み上がって
        // **上空へ飛んでいく**（2026-08-31 実機。旧実装は y を [0.8, 2.4] に clamp して
        // いたので気付かなかったが、clamp を外した時点でこの依存が表面化した）。
        Bounds b = ComputeObjectBoundsExcluding(instance, manualYawGuideRoot);
        // モデルの world 上端から、ルート原点までの高さ（local 単位）。
        float topLocal = (b.max.y - root.position.y) / lossyY;

        const float GuideGapMeters = 0.12f;    // 頭上の余白
        const float GuideLengthMeters = 0.30f; // 矢印の長さ
        const float GuideShaftMeters = 0.025f; // 軸の太さ
        const float GuideTipMeters = 0.075f;   // 先端の大きさ

        float y = topLocal + GuideGapMeters / lossyY;
        float len = GuideLengthMeters / lossyY;
        float shaft = GuideShaftMeters / lossyY;
        float tip = GuideTipMeters / lossyY;

        if (manualYawGuideRoot.transform.parent != root)
        {
            manualYawGuideRoot.transform.SetParent(root, false);
        }
        TransformWriter.ApplyLocalTransform(manualYawGuideRoot.transform, Vector3.zero, Quaternion.identity, Vector3.one);

        TransformWriter.ApplyLocalTransform(
            manualYawGuideShaft,
            new Vector3(0f, y, len * 0.5f),
            Quaternion.identity,
            new Vector3(shaft, shaft, len));
        TransformWriter.ApplyLocalTransform(
            manualYawGuideTip,
            new Vector3(0f, y, len),
            Quaternion.identity,
            new Vector3(tip, tip, tip));
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


    private static Bounds ComputeObjectBoundsExcluding(GameObject go, GameObject excludedSubtree)
    {
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
        Transform excluded = excludedSubtree != null ? excludedSubtree.transform : null;

        bool has = false;
        Bounds b = default;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null || (excluded != null && r.transform.IsChildOf(excluded)))
            {
                continue;
            }

            if (!has)
            {
                b = r.bounds;
                has = true;
            }
            else
            {
                b.Encapsulate(r.bounds);
            }
        }

        return has ? b : new Bounds(go.transform.position, Vector3.one * 0.2f);
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

