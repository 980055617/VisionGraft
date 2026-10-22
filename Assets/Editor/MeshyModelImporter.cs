using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

// 2026-10-22: 新しい被験者実験（30 秒 × 6 本）用に受け取った Meshy のモデル（unitypackage
// 「3Dモデル全部_MeshyModels」→ Assets/MeshyModels）を、人・動物・物に分けて Resources/Models/{Human,Animal,Else}
// に置く。元の FBX・テクスチャ・作者の prefab・見本のシーンは Assets/MeshyModels に残し、ここでは作者の prefab
// （中の子に向きと大きさの調整が入っている。Assets/MeshyModels/README_3Dモデル.txt）から作る。
//   - 番号は各フォルダの末尾に続ける。既存の番号・過去のログの index を動かさないため（振り直しはユーザー判断。
//     Human は 06_ が欠番のままなので、17_ / 18_ の実行時の index は 16 / 17）
//   - 人・動物: 作者の prefab の Variant。Animator の controller（走る・歩く）は外す（姿勢は bundle で動かす）
//   - 車: 作者の *_lights prefab を展開してライト（CarLights と "lights" の子）を外し、既存の車 9 台と同じく root に
//         Y 180°（前 = 視点側。ElseModelImporter.SportCarFacingRotation）を持たせ、FL / FR / RL / RR の目印を置く
//         （走行イベントの前の向き。InteractiveMotion.partial.cs の TryResolveRigidWheels。Meshy の車は 1 枚のメッシュで
//         ホイールの部品が無いので、目印は空の GameObject。ホイールは回らない）
//   - ボール・風船: 作者の prefab の Variant
// 犬・猫は骨の名前が AnimalRigDefinition の正規名と違い（Hips / chest / head / frontleg0..2 / backleg0..2 / tail*、
// 首の骨が無く背骨も chest 1 本）、このままでは SMAL FK に乗らない（位置と向きだけ合う）。
// Unity を閉じたバッチで -executeMethod MeshyModelImporter.ImportAll。済みのものは飛ばす。
public static class MeshyModelImporter
{
    private const string SourcePrefabDir = "Assets/MeshyModels/Prefabs";
    private const string LogTag = "[MeshyImport]";
    private const string CarLightsTypeName = "MeshyModels.CarLights";
    private static readonly Quaternion CarFacingRotation = Quaternion.Euler(0f, 180f, 0f);

    private sealed class Entry
    {
        public string category;
        public string source;
        public string dest;
        public bool car;
    }

    private static readonly Entry[] Entries =
    {
        new Entry { category = "Human",  source = "man_run",         dest = "17_Male_Meshy" },
        new Entry { category = "Human",  source = "woman_run",       dest = "18_Female_Meshy" },
        new Entry { category = "Animal", source = "dog_walk",        dest = "52_MeshyDog" },
        new Entry { category = "Animal", source = "cat_walk",        dest = "53_MeshyCat" },
        new Entry { category = "Else",   source = "car_red_lights",  dest = "16_Car_Meshy_Red",  car = true },
        new Entry { category = "Else",   source = "car_navy_lights", dest = "17_Car_Meshy_Navy", car = true },
        new Entry { category = "Else",   source = "car_van_lights",  dest = "18_Car_Meshy_Van",  car = true },
        new Entry { category = "Else",   source = "balloon",         dest = "19_Balloon_Meshy" },
        new Entry { category = "Else",   source = "soccer_ball",     dest = "20_Soccer_Meshy" },
        new Entry { category = "Else",   source = "tennis_ball",     dest = "21_Tennis_Meshy" },
    };

    [MenuItem("Tools/VisionGraft/Import Meshy Models (Human / Animal / Else)")]
    public static void ImportAll()
    {
        int made = 0;
        foreach (Entry entry in Entries)
        {
            string destPath = DestPath(entry);
            if (AssetDatabase.LoadAssetAtPath<GameObject>(destPath) != null)
            {
                Debug.Log($"{LogTag} skip (already exists): {destPath}");
                continue;
            }

            string sourcePath = $"{SourcePrefabDir}/{entry.source}.prefab";
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (source == null)
            {
                Debug.LogError($"{LogTag} source missing: {sourcePath}（先に unitypackage を読み込む）");
                continue;
            }

            GameObject saved = entry.car
                ? SaveCar(source, destPath, entry.dest)
                : SaveVariant(source, destPath, entry.dest);
            if (saved != null)
            {
                made++;
                Debug.Log($"{LogTag} made {destPath} from {sourcePath}");
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        ModelResourceIndexGenerator.Generate();
        Debug.Log($"{LogTag} done: made={made}");
        Report();
    }

    // 作ったものの中身を確かめる（大きさ・Humanoid か・骨・実行時の index）。何も変えない。
    [MenuItem("Tools/VisionGraft/Report Meshy Models")]
    public static void Report()
    {
        foreach (string category in new[] { "Human", "Animal", "Else" })
        {
            // 実行時と同じ並び（名前の ordinal）。Animal の先頭固定（AnimalModelPriorityOrder）は 00〜05 で、末尾の並びは変えない。
            List<string> names = AssetDatabase.FindAssets("t:Prefab", new[] { $"Assets/Resources/Models/{category}" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(p => System.IO.Path.GetFileNameWithoutExtension(p))
                .OrderBy(n => n, System.StringComparer.Ordinal)
                .ToList();

            foreach (Entry entry in Entries.Where(e => e.category == category))
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(DestPath(entry));
                if (prefab == null)
                {
                    Debug.LogWarning($"{LogTag} report: missing {DestPath(entry)}");
                    continue;
                }

                GameObject instance = Object.Instantiate(prefab);
                try
                {
                    Bounds bounds = ComputeRendererBounds(instance);
                    Animator animator = instance.GetComponentInChildren<Animator>(true);
                    int skinned = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
                    int lights = instance.GetComponentsInChildren<Light>(true).Length;
                    string markers = string.Join(",", instance.GetComponentsInChildren<Transform>(true)
                        .Where(t => t.name == "FL" || t.name == "FR" || t.name == "RL" || t.name == "RR")
                        .Select(t => $"{t.name}({t.localPosition.x:F2},{t.localPosition.y:F2},{t.localPosition.z:F2})"));
                    Debug.Log(
                        $"{LogTag} report {category}/{entry.dest} runtime_index={names.IndexOf(entry.dest)} of {names.Count} " +
                        $"size=({bounds.size.x:F3},{bounds.size.y:F3},{bounds.size.z:F3}) center=({bounds.center.x:F3},{bounds.center.y:F3},{bounds.center.z:F3}) " +
                        $"rootEuler={instance.transform.localRotation.eulerAngles} skinned={skinned} lights={lights} " +
                        $"animator={(animator != null)} isHuman={(animator != null && animator.isHuman)} " +
                        $"controller={(animator != null && animator.runtimeAnimatorController != null)} markers=[{markers}]");
                    if (category == "Animal")
                    {
                        string bones = string.Join(",", instance.GetComponentsInChildren<Transform>(true)
                            .Where(t => t != instance.transform)
                            .Select(t => t.name));
                        Debug.Log($"{LogTag} report {entry.dest} bones=[{bones}]");
                    }
                }
                finally
                {
                    Object.DestroyImmediate(instance);
                }
            }
        }
    }

    // 分類の確認用の絵: 各 prefab を置いたままの向き（root の回転込み）で、視聴者の側（−Z、左上から）と真横（+X）から撮る。
    // 車は前（ライト側）が視聴者の側に見えれば root の Y 180° が合っている。出力は Docs/tmp/meshy_models_20261022/raw/。
    [MenuItem("Tools/VisionGraft/Capture Meshy Models")]
    public static void Capture()
    {
        string outDir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Docs/tmp/meshy_models_20261022/raw");
        System.IO.Directory.CreateDirectory(outDir);

        Light light = new GameObject("CaptureLight").AddComponent<Light>();
        light.type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(40f, -30f, 0f);
        light.intensity = 1.2f;
        Camera cam = new GameObject("CaptureCamera").AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.82f, 0.84f, 0.88f);
        cam.fieldOfView = 30f;
        cam.nearClipPlane = 0.005f;
        cam.farClipPlane = 100f;
        try
        {
            foreach (Entry entry in Entries)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(DestPath(entry));
                if (prefab == null)
                {
                    continue;
                }

                GameObject instance = Object.Instantiate(prefab);
                try
                {
                    Bounds b = ComputeRendererBounds(instance);
                    float size = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                    float distance = size * 0.5f / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.35f;
                    RenderView(cam, b.center, new Vector3(-0.55f, 0.35f, -1f), distance,
                        System.IO.Path.Combine(outDir, $"{entry.category}_{entry.dest}_viewer.png"));
                    RenderView(cam, b.center, new Vector3(1f, 0.15f, 0f), distance,
                        System.IO.Path.Combine(outDir, $"{entry.category}_{entry.dest}_side.png"));
                }
                finally
                {
                    Object.DestroyImmediate(instance);
                }
            }
        }
        finally
        {
            Object.DestroyImmediate(cam.gameObject);
            Object.DestroyImmediate(light.gameObject);
        }
    }

    private static void RenderView(Camera cam, Vector3 target, Vector3 direction, float distance, string path)
    {
        const int Size = 640;
        cam.transform.position = target + direction.normalized * distance;
        cam.transform.LookAt(target);
        RenderTexture rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32);
        RenderTexture previousActive = RenderTexture.active;
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        Texture2D texture = new Texture2D(Size, Size, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
        texture.Apply();
        System.IO.File.WriteAllBytes(path, texture.EncodeToPNG());
        cam.targetTexture = null;
        RenderTexture.active = previousActive;
        Object.DestroyImmediate(texture);
        rt.Release();
        Object.DestroyImmediate(rt);
        Debug.Log($"{LogTag} capture {path}");
    }

    private static string DestPath(Entry entry)
    {
        return $"Assets/Resources/Models/{entry.category}/{entry.dest}.prefab";
    }

    private static GameObject SaveVariant(GameObject source, string destPath, string name)
    {
        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
        try
        {
            instance.name = name;
            foreach (Animator animator in instance.GetComponentsInChildren<Animator>(true))
            {
                animator.runtimeAnimatorController = null;
                animator.applyRootMotion = false;
            }

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(instance, destPath, out bool ok);
            if (!ok)
            {
                Debug.LogError($"{LogTag} SaveAsPrefabAsset failed: {destPath}");
                return null;
            }

            return saved;
        }
        finally
        {
            Object.DestroyImmediate(instance);
        }
    }

    private static GameObject SaveCar(GameObject source, string destPath, string name)
    {
        GameObject body = (GameObject)PrefabUtility.InstantiatePrefab(source);
        GameObject root = new GameObject(name);
        try
        {
            // 完全に展開する（既存の車 9 台と同じ。中の子の向き・大きさの調整は値としてそのまま残る）。
            // 外側だけの展開だと "lights" が FBX のインスタンスの中に付いた子になり、消せないことがある。
            PrefabUtility.UnpackPrefabInstance(body, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            body.name = "Body";

            foreach (MonoBehaviour behaviour in body.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour != null && behaviour.GetType().FullName == CarLightsTypeName)
                {
                    Object.DestroyImmediate(behaviour);
                }
            }

            // ライトの入れ物（"lights"）ごと外す。実機（Quest）で動的なスポットライトを車の数だけ増やさないため。
            List<GameObject> lightObjects = body.GetComponentsInChildren<Transform>(true)
                .Where(t => t != body.transform && (t.name == "lights" || t.GetComponent<Light>() != null))
                .Select(t => t.gameObject)
                .ToList();
            foreach (GameObject go in lightObjects)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }

            root.transform.localRotation = CarFacingRotation;
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = Quaternion.identity;
            body.transform.localScale = Vector3.one;

            AddWheelMarkers(root);

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, destPath, out bool ok);
            if (!ok)
            {
                Debug.LogError($"{LogTag} SaveAsPrefabAsset failed: {destPath}");
                return null;
            }

            return saved;
        }
        finally
        {
            Object.DestroyImmediate(root);
            if (body != null)
            {
                Object.DestroyImmediate(body);
            }
        }
    }

    // README の「前は +Z」に従い、root のローカルで +Z 側を前輪にする。位置はメッシュの外形からの目安
    // （前後は中心から ±0.3 × 全長、左右は ±0.4 × 全幅、高さは底から 0.08 × 全長）。走行イベントが使うのは
    // 前輪の中点 − 後輪の中点の向きだけなので、正確な車軸の位置でなくてよい。
    private static void AddWheelMarkers(GameObject root)
    {
        Bounds world = ComputeRendererBounds(root);
        Bounds local = new Bounds(root.transform.InverseTransformPoint(world.center), Vector3.zero);
        Vector3 e = world.extents;
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = world.center + new Vector3(
                (i & 1) == 0 ? -e.x : e.x,
                (i & 2) == 0 ? -e.y : e.y,
                (i & 4) == 0 ? -e.z : e.z);
            local.Encapsulate(root.transform.InverseTransformPoint(corner));
        }

        float length = local.size.z;
        float width = local.size.x;
        float y = local.min.y + 0.08f * length;
        float frontZ = local.center.z + 0.3f * length;
        float rearZ = local.center.z - 0.3f * length;
        float halfTrack = 0.4f * width;
        AddMarker(root, "FL", new Vector3(local.center.x - halfTrack, y, frontZ));
        AddMarker(root, "FR", new Vector3(local.center.x + halfTrack, y, frontZ));
        AddMarker(root, "RL", new Vector3(local.center.x - halfTrack, y, rearZ));
        AddMarker(root, "RR", new Vector3(local.center.x + halfTrack, y, rearZ));
    }

    private static void AddMarker(GameObject root, string name, Vector3 localPosition)
    {
        GameObject marker = new GameObject(name);
        marker.transform.SetParent(root.transform, false);
        marker.transform.localPosition = localPosition;
        marker.transform.localRotation = Quaternion.identity;
    }

    private static Bounds ComputeRendererBounds(GameObject go)
    {
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            return new Bounds(go.transform.position, Vector3.zero);
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        return bounds;
    }
}
