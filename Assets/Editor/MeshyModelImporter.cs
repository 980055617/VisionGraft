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
//   - 車: 作者の *_lights prefab を展開し、ランプの光（CarLights、既定は全部消灯。点ける時刻は CarLightSchedule）は残して
//         スポットライト（"lights" の子）だけ外す。既存の車 9 台と同じく root に
//         Y 180°（前 = 視点側。ElseModelImporter.SportCarFacingRotation）を持たせ、FL / FR / RL / RR の目印を置く
//         （走行イベントの前の向き。InteractiveMotion.partial.cs の TryResolveRigidWheels。Meshy の車は 1 枚のメッシュで
//         ホイールの部品が無いので、目印は空の GameObject。ホイールは回らない）
//   - ボール・風船: 作者の prefab の Variant
// 犬・猫は骨の名前が AnimalRigDefinition の正規名と違い（Hips / chest / head / frontleg0..2 / backleg0..2 / tail*、
// 首の骨が無く背骨も chest 1 本）、このままでは SMAL FK に乗らない（位置と向きだけ合う）。
// 2026-10-24: 「新しい10個」（前の 10 個の近しいもの、unitypackage「3Dモデル新しい10個」→ 同じ Assets/MeshyModels。
// Assets/MeshyModels/README_新しい10個.txt）を同じやり方で足した（batch = 2）。並びは前の 10 個と同じ順にし、
// 対になるものの番号の差を一定にする（人・動物 +2、物 +6）。乗り物 3 台は作者の部品にライトが無い（CarLights なし）。
// 狼・キツネの骨は犬・猫と同じ Meshy の四足（SMAL FK に乗らない）。
// Unity を閉じたバッチで -executeMethod MeshyModelImporter.ImportAll。済みのものは飛ばす。
public static class MeshyModelImporter
{
    private const string SourcePrefabDir = "Assets/MeshyModels/Prefabs";
    private const string LogTag = "[MeshyImport]";
    private static readonly Quaternion CarFacingRotation = Quaternion.Euler(0f, 180f, 0f);

    private sealed class Entry
    {
        public string category;
        public string source;
        public string dest;
        public bool car;
        public int batch = 1;
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

        // 2026-10-24「新しい10個」。かっこの中は対になる前の 10 個。
        new Entry { category = "Human",  source = "soccer_woman_run", dest = "19_Female_Meshy_Soccer", batch = 2 },   // ← 17 男性
        new Entry { category = "Human",  source = "zombie_run",       dest = "20_Female_Meshy_Zombie", batch = 2 },   // ← 18 女性
        new Entry { category = "Animal", source = "wolf_walk",        dest = "54_MeshyWolf",           batch = 2 },   // ← 52 犬
        new Entry { category = "Animal", source = "fox_walk",         dest = "55_MeshyFox",            batch = 2 },   // ← 53 猫
        new Entry { category = "Else",   source = "carriage",         dest = "22_Car_Meshy_Carriage",  car = true, batch = 2 },   // ← 16 赤
        new Entry { category = "Else",   source = "flying_car",       dest = "23_Car_Meshy_Flying",    car = true, batch = 2 },   // ← 17 紺
        new Entry { category = "Else",   source = "tuktuk",           dest = "24_Car_Meshy_TukTuk",    car = true, batch = 2 },   // ← 18 軽バン
        new Entry { category = "Else",   source = "star_balloon",     dest = "25_Balloon_Meshy_Star",  batch = 2 },   // ← 19 風船
        new Entry { category = "Else",   source = "volleyball",       dest = "26_Volleyball_Meshy",    batch = 2 },   // ← 20 サッカーボール
        new Entry { category = "Else",   source = "baseball",         dest = "27_Baseball_Meshy",      batch = 2 },   // ← 21 テニスボール
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

    // 車 3 台だけを作り直す（同じパスへ上書きするので GUID・名前は変わらない）。2026-10-22 にランプの光を残す形へ変えたときに使った。
    [MenuItem("Tools/VisionGraft/Rebuild Meshy Cars (keep lamp glow)")]
    public static void RebuildCars()
    {
        foreach (Entry entry in Entries.Where(e => e.car))
        {
            string sourcePath = $"{SourcePrefabDir}/{entry.source}.prefab";
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (source == null)
            {
                Debug.LogError($"{LogTag} source missing: {sourcePath}");
                continue;
            }

            if (SaveCar(source, DestPath(entry), entry.dest) != null)
            {
                Debug.Log($"{LogTag} rebuilt {DestPath(entry)}");
            }
        }

        AssetDatabase.SaveAssets();
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
                    MeshyModels.CarLights carLights = instance.GetComponentInChildren<MeshyModels.CarLights>(true);
                    if (carLights != null)
                    {
                        Debug.Log($"{LogTag} report {entry.dest} carLights head={carLights.headlights} tail={carLights.tailLights} blinker={carLights.blinker} " +
                                  $"target={(carLights.target != null ? carLights.target.name : "null")} lampColor={(carLights.lampColor != null)} shader={(carLights.composeShader != null)}");
                    }
                    string markers = string.Join(",", instance.GetComponentsInChildren<Transform>(true)
                        .Where(t => t.name == "FL" || t.name == "FR" || t.name == "RL" || t.name == "RR")
                        .Select(t => $"{t.name}({t.localPosition.x:F2},{t.localPosition.y:F2},{t.localPosition.z:F2})"));
                    // 三角形・頂点の数（2026-10-24、新しい 10 個の乗り物が 54 万〜200 万三角形あり、Quest の負荷を前の 10 個と比べるため）。
                    long triangles = 0;
                    long vertices = 0;
                    foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
                    {
                        Mesh mesh = renderer is SkinnedMeshRenderer smr
                            ? smr.sharedMesh
                            : renderer.GetComponent<MeshFilter>() != null ? renderer.GetComponent<MeshFilter>().sharedMesh : null;
                        if (mesh == null)
                        {
                            continue;
                        }

                        vertices += mesh.vertexCount;
                        for (int sub = 0; sub < mesh.subMeshCount; sub++)
                        {
                            triangles += (long)mesh.GetIndexCount(sub) / 3;
                        }
                    }
                    Debug.Log(
                        $"{LogTag} report {category}/{entry.dest} runtime_index={names.IndexOf(entry.dest)} of {names.Count} " +
                        $"size=({bounds.size.x:F3},{bounds.size.y:F3},{bounds.size.z:F3}) center=({bounds.center.x:F3},{bounds.center.y:F3},{bounds.center.z:F3}) " +
                        $"rootEuler={instance.transform.localRotation.eulerAngles} skinned={skinned} lights={lights} " +
                        $"animator={(animator != null)} isHuman={(animator != null && animator.isHuman)} " +
                        $"controller={(animator != null && animator.runtimeAnimatorController != null)} markers=[{markers}] " +
                        $"tris={triangles} verts={vertices}");
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
        CaptureBatch(1, "Docs/tmp/meshy_models_20261022/raw");
    }

    // 2026-10-24 の新しい 10 個だけを撮る（出力は Docs/tmp/meshy_models_20261024/raw/）。
    [MenuItem("Tools/VisionGraft/Capture New Meshy Models (10/24)")]
    public static void CaptureNew()
    {
        CaptureBatch(2, "Docs/tmp/meshy_models_20261024/raw");
    }

    private static void CaptureBatch(int batch, string relativeOutDir)
    {
        string outDir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), relativeOutDir);
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
            foreach (Entry entry in Entries.Where(e => e.batch == batch))
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

                    // 車はランプの点き方も撮る（前 = 視聴者の側、後ろ = その反対）。点滅は点いている位相（t = 0.1 s）で止める。
                    MeshyModels.CarLights carLights = instance.GetComponentInChildren<MeshyModels.CarLights>(true);
                    if (carLights != null)
                    {
                        var states = new (string tag, bool head, bool tail, MeshyModels.CarLights.Blinker blinker)[]
                        {
                            ("off", false, false, MeshyModels.CarLights.Blinker.Off),
                            ("headtail", true, true, MeshyModels.CarLights.Blinker.Off),
                            ("hazard", false, false, MeshyModels.CarLights.Blinker.Hazard),
                        };
                        foreach (var s in states)
                        {
                            carLights.headlights = s.head;
                            carLights.tailLights = s.tail;
                            carLights.blinker = s.blinker;
                            carLights.Apply(0.1f);
                            RenderView(cam, b.center, new Vector3(-0.6f, 0.3f, -1f), distance,
                                System.IO.Path.Combine(outDir, $"lights_{entry.dest}_{s.tag}_front.png"));
                            RenderView(cam, b.center, new Vector3(0.6f, 0.3f, 1f), distance,
                                System.IO.Path.Combine(outDir, $"lights_{entry.dest}_{s.tag}_rear.png"));
                        }
                    }
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

            // ランプの光（Emission、CarLights）は残す（2026-10-22 ユーザー「必要です」。点ける時刻は CarLightSchedule の表）。
            // 既定は全部消灯にし、表の区間だけ点ける。作者の既定（ヘッド・テール点灯、赤いセダンはハザード）のままだと、表に無い動画でも光る。
            foreach (MeshyModels.CarLights carLights in body.GetComponentsInChildren<MeshyModels.CarLights>(true))
            {
                carLights.headlights = false;
                carLights.tailLights = false;
                carLights.blinker = MeshyModels.CarLights.Blinker.Off;
                carLights.headBeams = new Light[0];
                carLights.amberLeft = new Light[0];
                carLights.amberRight = new Light[0];
            }

            // まわりを照らすスポットライト（ヘッドライトの光線・ウィンカーの照り返し、1 台 5〜6 灯）は外す。
            // 照らす地面がシーンに無く（背景は動画）、ほかのモデルだけが照らされる。実機（Quest）で動的なライトを車の数だけ増やさないため。
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
