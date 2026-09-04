using System.Collections.Generic;
using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: track/manualYaw dictionaries and selected track state in Model.cs
    // Provides: manual yaw keyframe evaluation, guide object management, joint yaw apply

    // 手動回転を配置回転に重ねる。
    //
    // **1 つの world 空間の回転として合成する。**
    //
    // 以前は yaw だけ base の**前**、pitch / roll は base の**後ろ**に掛けていた:
    //   result = AngleAxis(yaw, up) * base * AngleAxis(pitch, right) * AngleAxis(roll, forward)
    // （途中結果の軸で回す式は、整理するとこの形になる）
    // つまり base を挿んでおり、**1 つの向きとしての意味を持たない**。
    //
    // 一方で掛んで回す側（ApplyGrabRotate）は world の差分をまとめて
    // Quaternion.Euler(pitch, yaw, roll) に分解して保存している。分解と合成が
    // 互いの逆になっていないので、**保存した 3 数と表示される向きが一致しない**。
    // 掛んでいる間これが毎フレーム繰り返され、ずれが積み上がって暴れる
    // （2026-09-04 実機: 1 回の掛みで roll が 40 → 103 → 106 と振れていた）。
    //
    // Quaternion.Euler(pitch, yaw, roll) に揃えると、分解（eulerAngles）と完全に逆になる。
    //
    // **保存済みの yaw は意味が変わらない。** yaw だけのときこの式は
    // AngleAxis(yaw, Vector3.up) * base に等しく、スクリーンは
    // ResolveYawOnlyViewRotation を通したヨーのみの基準で置かれているので
    // screen.up は常に Vector3.up。これまでの式の yaw 軸と一致する。
    private Quaternion ApplyManualTrackYawOffset(uint trackId, int frame, Quaternion baseRotation)
    {
        float yawDeg = EvaluateManualYawOffsetDegForFrame(trackId, frame);
        float pitchDeg = EvaluateManualPitchDegForFrame(trackId, frame);
        float rollDeg = EvaluateManualRollDegForFrame(trackId, frame);

        if (Mathf.Abs(yawDeg) < 0.001f && Mathf.Abs(pitchDeg) < 0.001f && Mathf.Abs(rollDeg) < 0.001f)
        {
            return baseRotation;
        }

        return ManualRotationMath.Apply(baseRotation, yawDeg, pitchDeg, rollDeg);
    }


    private float EvaluateManualPitchDegForFrame(uint trackId, int frame)
    {
        manualPitchKeyframesByTrack.TryGetValue(trackId, out SortedDictionary<int, float> keys);
        return TrackKeyframeCurve.Evaluate(keys, frame, 0f);
    }


    private float EvaluateManualRollDegForFrame(uint trackId, int frame)
    {
        manualRollKeyframesByTrack.TryGetValue(trackId, out SortedDictionary<int, float> keys);
        return TrackKeyframeCurve.Evaluate(keys, frame, 0f);
    }


    private void SetManualRotationForTrack(uint trackId, float yawDeg, float pitchDeg, float rollDeg)
    {
        int frame = GetCurrentPlaybackFrame();
        WriteKey(manualYawKeyframesByTrack, trackId, frame, Mathf.Clamp(yawDeg, -180f, 180f));
        WriteKey(manualPitchKeyframesByTrack, trackId, frame, Mathf.Clamp(pitchDeg, -180f, 180f));
        WriteKey(manualRollKeyframesByTrack, trackId, frame, Mathf.Clamp(rollDeg, -180f, 180f));
    }


    private static void WriteKey(
        Dictionary<uint, SortedDictionary<int, float>> target, uint trackId, int frame, float value)
    {
        if (!target.TryGetValue(trackId, out SortedDictionary<int, float> keys) || keys == null)
        {
            keys = new SortedDictionary<int, float>();
            target[trackId] = keys;
        }

        keys[frame] = value;
    }


    private void GetManualRotationForTrack(uint trackId, out float yaw, out float pitch, out float roll)
    {
        int frame = GetCurrentPlaybackFrame();
        yaw = EvaluateManualYawOffsetDegForFrame(trackId, frame);
        pitch = EvaluateManualPitchDegForFrame(trackId, frame);
        roll = EvaluateManualRollDegForFrame(trackId, frame);
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
            runtimeModelPickerTrackId = selectedManualRotationTrackId;
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
        runtimeModelPickerTrackId = selectedManualRotationTrackId;
        runtimeModelPickerPreferredTrackId = selectedManualRotationTrackId;
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


    // 現在フレームに打ってあるキーを消す。消したら true。
    //
    // **「0 を打つ」とは別物。** Reset は 0 のキーを追加するので、一度打ったフレームは
    // 以後ずっとキーであり続ける。打ち間違えを取り消す手段がこれまで無かった
    // （2026-09-02 の指摘）。
    private bool RemoveManualYawKeyAtCurrentFrame(uint trackId)
    {
        if (!manualYawKeyframesByTrack.TryGetValue(trackId, out SortedDictionary<int, float> keys) || keys == null)
        {
            return false;
        }

        int frame = GetCurrentPlaybackFrame();
        bool removed = keys.Remove(frame);

        // 空になったら辞書からも外す。残しておくと「キーがある track」として扱われる。
        if (keys.Count == 0)
        {
            manualYawKeyframesByTrack.Remove(trackId);
        }

        // pitch / roll も同じフレームのキーを消す。3 軸は 1 回の操作で一緒に打つので、
        // 消すときも揃えないと「yaw だけ残った」半端な状態になる。
        removed |= RemoveKey(manualPitchKeyframesByTrack, trackId, frame);
        removed |= RemoveKey(manualRollKeyframesByTrack, trackId, frame);
        return removed;
    }


    private static bool RemoveKey(
        Dictionary<uint, SortedDictionary<int, float>> target, uint trackId, int frame)
    {
        if (!target.TryGetValue(trackId, out SortedDictionary<int, float> keys) || keys == null)
        {
            return false;
        }

        bool removed = keys.Remove(frame);
        if (keys.Count == 0)
        {
            target.Remove(trackId);
        }

        return removed;
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
        float pitchDeg = EvaluateManualPitchDegForFrame(trackId, frame);
        float rollDeg = EvaluateManualRollDegForFrame(trackId, frame);
        if (Mathf.Abs(yawDeg) < 0.001f && Mathf.Abs(pitchDeg) < 0.001f && Mathf.Abs(rollDeg) < 0.001f)
        {
            return;
        }

        if (upAxis.sqrMagnitude < 0.000001f)
        {
            upAxis = Vector3.up;
        }

        // ApplyManualTrackYawOffset と同じ順で組む。片方だけ変えると
        // モデルの向きと keypoint の向きがずれる。
        Quaternion yawRot = Quaternion.AngleAxis(yawDeg, upAxis.normalized);
        if (Mathf.Abs(pitchDeg) >= 0.001f)
        {
            yawRot = Quaternion.AngleAxis(pitchDeg, yawRot * Vector3.right) * yawRot;
        }
        if (Mathf.Abs(rollDeg) >= 0.001f)
        {
            yawRot = Quaternion.AngleAxis(rollDeg, yawRot * Vector3.forward) * yawRot;
        }

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

