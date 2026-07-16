using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Diagnoses and fixes baked-in tail curl in Animal prefab bind poses.
//
// Root cause (2026-07-17, see docs/smpl-retargeting.md "尻尾のデフォルト姿勢"): the
// runtime SMAL FK (AnimalSmalFkApplier) uses each bone's authored bind rotation as the
// pose baseline (restWorldRot) and only layers a SMAL body_pose delta on top of it. A
// tail authored with curl already baked into tail_mid/tail_tip's local rotation shows
// that curl on screen essentially all the time, independent of the SMAL data - measured
// directly on Lion vs Dog: Lion's tail_mid carries ~48.6deg of local rotation relative to
// tail_base, Dog's carries ~0deg (a near-straight continuation).
//
// "Straightening" here means zeroing tail_mid's and tail_tip's LOCAL rotation (relative
// to their own immediate parent) to identity, so the tail continues in whatever direction
// tail_base itself already points. tail_base's own local rotation (its attachment angle to
// the spine) is intentionally left untouched - that angle is genuine per-species anatomy
// (a raised-rump vs low-slung tail attachment), not curl, and Dog's own tail_base is not
// itself at identity either.
public static class AnimalTailBindStraightener
{
    private const string AnimalFolder = "Assets/Resources/Models/Animal";

    // Curl below this is treated as "already straight enough" - not touched by the fix
    // pass, so re-running it is idempotent and low-curl models are left alone.
    private const float CurlThresholdDeg = 15f;

    private struct TailCurlReport
    {
        public string prefabName;
        public float tailMidCurlDeg;  // -1 if this model has no tail_mid bone
        public float tailTipCurlDeg;  // -1 if this model has no tail_tip bone
    }

    [MenuItem("Tools/VisionGraft/Animal/Report Tail Bind Curl")]
    public static void ReportTailCurl()
    {
        var reports = new List<TailCurlReport>();

        foreach (string path in FindAnimalPrefabPaths())
        {
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefabAsset == null) continue;

            Transform tailBase = FindByName(prefabAsset.transform, "tail_base");
            if (tailBase == null) continue; // no tail rig on this model (birds, Kangaroo, etc.)

            Transform tailMid = FindByName(prefabAsset.transform, "tail_mid");
            Transform tailTip = FindByName(prefabAsset.transform, "tail_tip");

            reports.Add(new TailCurlReport
            {
                prefabName = Path.GetFileNameWithoutExtension(path),
                tailMidCurlDeg = tailMid != null ? Quaternion.Angle(Quaternion.identity, tailMid.localRotation) : -1f,
                tailTipCurlDeg = tailTip != null ? Quaternion.Angle(Quaternion.identity, tailTip.localRotation) : -1f,
            });
        }

        reports.Sort((a, b) => Mathf.Max(b.tailMidCurlDeg, b.tailTipCurlDeg)
            .CompareTo(Mathf.Max(a.tailMidCurlDeg, a.tailTipCurlDeg)));

        foreach (var r in reports)
        {
            Debug.Log($"[TAIL-CURL-REPORT] model={r.prefabName} tailMidCurlDeg={r.tailMidCurlDeg:F1} tailTipCurlDeg={r.tailTipCurlDeg:F1} (-1=ボーンなし, {CurlThresholdDeg:F0}+=要修正候補)");
        }
        Debug.Log($"[TAIL-CURL-REPORT] Done. {reports.Count} models with a tail_base bone scanned.");
    }

    [MenuItem("Tools/VisionGraft/Animal/Straighten Tail Bind (Flagged Models)")]
    public static void StraightenFlaggedTails()
    {
        int fixedCount = 0;
        foreach (string path in FindAnimalPrefabPaths())
        {
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefabAsset == null) continue;
            if (FindByName(prefabAsset.transform, "tail_base") == null) continue;

            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Transform tailMid = FindByName(root.transform, "tail_mid");
                Transform tailTip = FindByName(root.transform, "tail_tip");
                bool changed = false;

                if (tailMid != null && Quaternion.Angle(Quaternion.identity, tailMid.localRotation) > CurlThresholdDeg)
                {
                    tailMid.localRotation = Quaternion.identity;
                    changed = true;
                }
                if (tailTip != null && Quaternion.Angle(Quaternion.identity, tailTip.localRotation) > CurlThresholdDeg)
                {
                    tailTip.localRotation = Quaternion.identity;
                    changed = true;
                }

                if (changed)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    fixedCount++;
                    Debug.Log($"[AnimalTailBindStraightener] Straightened tail on {path}");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"[AnimalTailBindStraightener] Done. {fixedCount} prefabs modified (threshold={CurlThresholdDeg:F0}deg).");
    }

    // 2026-07-17: after StraightenFlaggedTails, tail_mid/tail_tip are identity, so any
    // remaining "tail points up" symptom must come from tail_base's OWN baseline direction
    // (deliberately left untouched - see class comment). This measures that direction in a
    // frame comparable across models: world Vector3.up (the same "up" convention the runtime
    // rig code assumes via settings.animalModelUpLocal, which is always world-up in practice -
    // see AnimalPoseApplier.cs modelUpLocal) rather than each bone's own local axes, which
    // differ per model's FBX import convention and aren't directly comparable.
    [MenuItem("Tools/VisionGraft/Animal/Report Tail Base World Direction")]
    public static void ReportTailBaseWorldDirection()
    {
        var rows = new List<string>();
        foreach (string path in FindAnimalPrefabPaths())
        {
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefabAsset == null) continue;

            Transform tailBase = FindByName(prefabAsset.transform, "tail_base");
            if (tailBase == null) continue;
            Transform tailNext = FindByName(prefabAsset.transform, "tail_mid") ?? FindByName(prefabAsset.transform, "tail_tip");
            if (tailNext == null) continue;

            Vector3 tailDir = (tailNext.position - tailBase.position).normalized;
            float upDot = Vector3.Dot(tailDir, Vector3.up);

            string fwdInfo = "";
            Transform frontL = FindByName(prefabAsset.transform, "front_l_upper");
            Transform frontR = FindByName(prefabAsset.transform, "front_r_upper");
            Transform rearL = FindByName(prefabAsset.transform, "rear_l_upper");
            Transform rearR = FindByName(prefabAsset.transform, "rear_r_upper");
            if (frontL != null && frontR != null && rearL != null && rearR != null)
            {
                Vector3 frontCenter = (frontL.position + frontR.position) * 0.5f;
                Vector3 rearCenter = (rearL.position + rearR.position) * 0.5f;
                Vector3 bodyForward = (frontCenter - rearCenter).normalized;
                float fwdDot = Vector3.Dot(tailDir, bodyForward);
                fwdInfo = $" fwdDot={fwdDot:F2}";
            }

            rows.Add($"[TAIL-DIR-REPORT] model={Path.GetFileNameWithoutExtension(path)} tailDir={tailDir:F3} upDot={upDot:F2}{fwdInfo} (upDot:+1=真上,-1=真下,0=水平 / fwdDot:+1=前方,-1=後方)");
        }
        rows.Sort((a, b) => b.CompareTo(a));
        foreach (string r in rows) Debug.Log(r);
        Debug.Log($"[TAIL-DIR-REPORT] Done. {rows.Count} models scanned.");
    }

    // 2026-07-17: ReportTailBaseWorldDirection found 04_Lion is the sole outlier among 46
    // scanned models - upDot=+0.53 (tail_base points UP) vs every other model's -0.07..-0.99
    // (tail_base points DOWN, regardless of body shape - dog, bear, deer, horse all agree).
    // Not treated as a general threshold rule (unlike StraightenFlaggedTails) because this is
    // a single, unambiguous outlier, not a distribution to cut at some angle - a blanket rule
    // here risks "fixing" other models whose tail_base direction is legitimately unusual but
    // still correct for their anatomy. Mirrors tail_base's current world direction across the
    // horizontal (XZ) plane (flips the Y/up component only, keeps forward/lateral aspect) via
    // the minimal FromToRotation, same technique the runtime FK itself uses for bend transfer.
    [MenuItem("Tools/VisionGraft/Animal/Fix Lion Tail Base Orientation")]
    public static void FixLionTailBaseOrientation()
    {
        const string path = "Assets/Resources/Models/Animal/04_Lion.prefab";
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            Transform tailBase = FindByName(root.transform, "tail_base");
            Transform tailNext = FindByName(root.transform, "tail_mid") ?? FindByName(root.transform, "tail_tip");
            if (tailBase == null || tailNext == null)
            {
                Debug.LogError("[AnimalTailBindStraightener] Lion tail_base/tail_mid|tip not found - aborting.");
                return;
            }

            Vector3 oldDir = (tailNext.position - tailBase.position).normalized;
            Vector3 mirroredDir = new Vector3(oldDir.x, -oldDir.y, oldDir.z).normalized;
            Quaternion deltaRot = Quaternion.FromToRotation(oldDir, mirroredDir);
            tailBase.rotation = deltaRot * tailBase.rotation;

            Vector3 newDir = (tailNext.position - tailBase.position).normalized;
            Debug.Log($"[AnimalTailBindStraightener] Lion tail_base reoriented: oldDir={oldDir:F3} (upDot={Vector3.Dot(oldDir, Vector3.up):F2}) -> newDir={newDir:F3} (upDot={Vector3.Dot(newDir, Vector3.up):F2})");

            PrefabUtility.SaveAsPrefabAsset(root, path);
            AssetDatabase.SaveAssets();
            Debug.Log("[AnimalTailBindStraightener] Done.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static IEnumerable<string> FindAnimalPrefabPaths()
    {
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { AnimalFolder }))
            yield return AssetDatabase.GUIDToAssetPath(guid);
    }

    private static Transform FindByName(Transform t, string name)
    {
        if (t.name == name) return t;
        for (int i = 0; i < t.childCount; i++)
        {
            Transform found = FindByName(t.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }
}
