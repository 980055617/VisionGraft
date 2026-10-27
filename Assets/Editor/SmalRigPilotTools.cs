using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

// 動物のモデルの骨を Blender で SMAL（AniMer）の 35 関節にそろえる試作（2026-10-23 ユーザー「blender で現状のすべてのモデルを
// 合わせだっていい」→ まず Meshy の犬 1 体で通す）。Blender のスクリプトはリポジトリの外（scratchpad）、書き出した FBX は
// Assets/MeshyModels/SmalRig/（Resources の外）に置く。ここは Unity 側の確かめ用。
// 手順 0: Blender で読み込んで何も変えずに書き出した FBX（往復）が元と同じか（大きさ・向き・骨の名前と位置・頂点の数・材質）。
// Unity を閉じたバッチで -executeMethod SmalRigPilotTools.CompareDogRoundTrip。
public static class SmalRigPilotTools
{
    private const string LogTag = "[SMALRIG]";
    private const string DogOriginal = "Assets/MeshyModels/Models/dog/Meshy_AI_Golden_Labrador_Portr_quadruped_Character_output.fbx";
    private const string DogRoundTrip = "Assets/MeshyModels/SmalRig/dog_roundtrip.fbx";

    private const string DogSmal = "Assets/MeshyModels/SmalRig/dog_smal.fbx";
    private const string DogSmalPrefab = "Assets/Resources/Models/Animal/54_MeshyDogSmal.prefab";

    // 手順 1〜3 の結果（Blender の smal_rig.py が書き出した dog_smal.fbx）を取り込みの設定を元に合わせて読み、Animal の末尾に prefab を作る。
    // 今の 52_MeshyDog は残す（比べるため。置き換えるかはユーザーが絵を見て決める）。作り直すときは同じパスに上書き（GUID は同じ）。
    [MenuItem("Tools/VisionGraft/SMAL Rig Pilot/Create Dog SMAL Prefab")]
    public static void CreateDogSmalPrefab()
    {
        CopyImportSettings(DogSmal, DogOriginal);
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(DogSmal);
        if (model == null)
        {
            Debug.LogError($"{LogTag} missing {DogSmal}");
            return;
        }

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
        try
        {
            instance.name = "54_MeshyDogSmal";
            foreach (Animator animator in instance.GetComponentsInChildren<Animator>(true))
            {
                animator.runtimeAnimatorController = null;
                animator.applyRootMotion = false;
            }

            // 材質は元の FBX のものをそのまま使う。Blender で書き出した FBX は、何も変えない往復でも Unity で白黒のまだらに写った
            // （埋め込みの画像の読み込みが元と違う。2026-10-23、unity_check/variants.png）。UV は Blender でも保たれている。
            SkinnedMeshRenderer source = AssetDatabase.LoadAssetAtPath<GameObject>(DogOriginal).GetComponentInChildren<SkinnedMeshRenderer>(true);
            foreach (SkinnedMeshRenderer smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                smr.sharedMaterials = source.sharedMaterials;
            }

            PrefabUtility.SaveAsPrefabAsset(instance, DogSmalPrefab, out bool ok);
            Debug.Log($"{LogTag} prefab {(ok ? "saved" : "FAILED")} {DogSmalPrefab}");
        }
        finally
        {
            Object.DestroyImmediate(instance);
        }

        AssetDatabase.SaveAssets();
        ModelResourceIndexGenerator.Generate();
        CompareModels(DogOriginal, DogSmalPrefab);
    }

    private const string CatOriginal = "Assets/MeshyModels/Models/cat/Meshy_AI_Misty_quadruped_Character_output.fbx";
    private const string CatSmal = "Assets/MeshyModels/SmalRig/cat_n3r.fbx";
    private const string CatSmalPrefab = "Assets/Resources/Models/Animal/55_MeshyCatSmal.prefab";

    // 首の直しの第 1 回（2026-10-23、複数エージェント）の判定役が作った猫（N3r: 首を前へ倒した形に作り直し＋口の骨を目と鼻先から置く＋
    // 重みの無い Nose の骨。scratchpad の rig/judge/rebake/cat_n3r_eye.fbx を写したもの）を、犬の試作と同じ手順で prefab にする。
    // 実験の猫の動画（bundle_cat）の 53_MeshyCat は SMAL の骨ではないので姿勢が当たらない。置き換えるかはユーザーが絵を見て決める。
    [MenuItem("Tools/VisionGraft/SMAL Rig Pilot/Create Cat SMAL Prefab")]
    public static void CreateCatSmalPrefab()
    {
        CreateSmalPrefab(CatSmal, CatOriginal, CatSmalPrefab, "55_MeshyCatSmal");
    }

    // 2026-10-25: 骨の作り直しの仕上げ（Workflow finish-rigs-and-armpit の c4c）。犬・狼・キツネを SMAL の 35 関節にそろえ、犬だけ下あごの骨
    // （Jaw、Head の子）を足したもの。scratchpad の rig/r3/c4/final/ の FBX を SmalRig/ に写した。prefab は Resources の外（Pilot）に作るので
    // 一覧と番号は変わらない。バッチでは -animalPrefab <パス> で読む。名前は置き換え先の実験のモデル（今の 4 体は SMAL の骨ではなく固まる）。
    private const string PilotFolder = "Assets/MeshyModels/Pilot";

    private static readonly string[][] C4cJobs =
    {
        new[] { "Assets/MeshyModels/SmalRig/dog_c4c.fbx", DogOriginal, PilotFolder + "/52_MeshyDog_c4c.prefab", "52_MeshyDog_c4c" },
        new[] { "Assets/MeshyModels/SmalRig/wolf_c4c.fbx", "Assets/MeshyModels/Models/wolf/Meshy_AI_Lone_Wolf_quadruped_Character_output.fbx",
                PilotFolder + "/54_MeshyWolf_c4c.prefab", "54_MeshyWolf_c4c" },
        new[] { "Assets/MeshyModels/SmalRig/fox_c4c.fbx", "Assets/MeshyModels/Models/fox/Meshy_AI_Amber_Fox_Portrait_quadruped_Character_output.fbx",
                PilotFolder + "/55_MeshyFox_c4c.prefab", "55_MeshyFox_c4c" },
    };

    // 見る用の APK（2026-10-25、ユーザー「今から見ようかな？ビルドしてくれたら」）: 作り直した 4 体を実験のモデルの位置（Resources の 52〜55）へ
    // 一時的に入れる。prefab の文面を写して名前の override だけ実験の名前にする（.meta と GUID はそのまま、採用と同じ写し方）。
    // ビルドの後で git checkout で 4 つを元に戻す（採否はユーザーが Quest で見てから決める）。
    // 10/25 夜の版から狼は口を開けた版（54_MeshyWolf_mouth、ボールをくわえる）。
    private static readonly string[][] PreviewSwaps =
    {
        new[] { PilotFolder + "/52_MeshyDog_c4c.prefab", "52_MeshyDog_c4c", "Assets/Resources/Models/Animal/52_MeshyDog.prefab", "52_MeshyDog" },
        new[] { PilotFolder + "/55_MeshyCatSmal.prefab", "55_MeshyCatSmal", "Assets/Resources/Models/Animal/53_MeshyCat.prefab", "53_MeshyCat" },
        new[] { PilotFolder + "/54_MeshyWolf_mouth.prefab", "54_MeshyWolf_mouth", "Assets/Resources/Models/Animal/54_MeshyWolf.prefab", "54_MeshyWolf" },
        new[] { PilotFolder + "/55_MeshyFox_c4c.prefab", "55_MeshyFox_c4c", "Assets/Resources/Models/Animal/55_MeshyFox.prefab", "55_MeshyFox" },
    };

    // バッチ: -executeMethod SmalRigPilotTools.PreviewSwapRebuiltAnimals（-quit 付き）
    public static void PreviewSwapRebuiltAnimals()
    {
        foreach (string[] swap in PreviewSwaps)
        {
            string text = System.IO.File.ReadAllText(swap[0]);
            var nameLine = new System.Text.RegularExpressions.Regex(@"(?m)^(\s+value: )" + System.Text.RegularExpressions.Regex.Escape(swap[1]) + @"(\r?)$");
            int hits = nameLine.Matches(text).Count;
            if (hits != 1)
            {
                Debug.LogError($"{LogTag} preview: {swap[0]} の名前の override が 1 行でない（{hits} 行）。写さない");
                continue;
            }

            string guidBefore = AssetDatabase.AssetPathToGUID(swap[2]);
            System.IO.File.WriteAllText(swap[2], nameLine.Replace(text, "${1}" + swap[3] + "$2"), new System.Text.UTF8Encoding(false));
            AssetDatabase.ImportAsset(swap[2], ImportAssetOptions.ForceUpdate);
            GameObject swapped = AssetDatabase.LoadAssetAtPath<GameObject>(swap[2]);
            GameObject source = swapped != null ? PrefabUtility.GetCorrespondingObjectFromSource(swapped) : null;
            SkinnedMeshRenderer smr = swapped != null ? swapped.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
            Debug.Log($"{LogTag} preview: {swap[2]} <- {swap[0]} guid {guidBefore} -> {AssetDatabase.AssetPathToGUID(swap[2])} name={(swapped != null ? swapped.name : "null")} " +
                      $"source={(source != null ? AssetDatabase.GetAssetPath(source) : "-")} bones={(smr != null ? smr.bones.Length : 0)}");
        }

        AssetDatabase.SaveAssets();
    }

    // 狼の口（2026-10-25、Workflow wolf-mouth-build の作る人 wA、ユーザー「狼もできれば加えれるようにしたい」）: c4c の狼の唇をテクスチャの線で切り、
    // 頭の子に顎の骨 Jaw と口の中（材質の枠 1、755 頂点）を足した FBX。枠 0 は元の FBX の材質、枠 1 は口の中の暗い赤
    // （URP Lit、sRGB (45,18,18) = #2D1212、なめらかさ 0。作る人の指定。当てないと口の中が暗くならない）。
    // 記録 Docs/tmp/survey_20261025/README.md「狼の口を開けられるようにした」。バッチ: -executeMethod SmalRigPilotTools.CreateWolfMouthPrefab（-quit 付き）
    private const string WolfMouthFbx = "Assets/MeshyModels/SmalRig/wolf_mouth.fbx";
    private const string WolfMouthPrefab = PilotFolder + "/54_MeshyWolf_mouth.prefab";
    private const string WolfMouthInnerMaterial = PilotFolder + "/MeshyWolf_MouthInner.mat";
    private const string WolfOriginalFbx = "Assets/MeshyModels/Models/wolf/Meshy_AI_Lone_Wolf_quadruped_Character_output.fbx";

    [MenuItem("Tools/VisionGraft/SMAL Rig Pilot/Create Wolf Mouth Prefab")]
    public static void CreateWolfMouthPrefab()
    {
        GameObject imported = AssetDatabase.LoadAssetAtPath<GameObject>(WolfMouthFbx);
        SkinnedMeshRenderer importedSmr = imported != null ? imported.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
        if (importedSmr == null)
        {
            Debug.LogError($"{LogTag} wolf mouth: {WolfMouthFbx} に SkinnedMeshRenderer が無い");
            return;
        }

        Debug.Log($"{LogTag} wolf mouth: FBX の材質 {string.Join(",", importedSmr.sharedMaterials.Select(m => m != null ? m.name : "null"))} " +
                  $"subMeshes={importedSmr.sharedMesh.subMeshCount} vertices={importedSmr.sharedMesh.vertexCount} bones={importedSmr.bones.Length}");
        CreateSmalPrefab(WolfMouthFbx, WolfOriginalFbx, WolfMouthPrefab, "54_MeshyWolf_mouth");

        Material inner = AssetDatabase.LoadAssetAtPath<Material>(WolfMouthInnerMaterial);
        if (inner == null)
        {
            inner = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "MeshyWolf_MouthInner" };
            inner.SetColor("_BaseColor", new Color(45f / 255f, 18f / 255f, 18f / 255f, 1f));
            inner.SetFloat("_Smoothness", 0f);
            AssetDatabase.CreateAsset(inner, WolfMouthInnerMaterial);
        }

        GameObject root = PrefabUtility.LoadPrefabContents(WolfMouthPrefab);
        try
        {
            foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                int subMeshes = smr.sharedMesh != null ? smr.sharedMesh.subMeshCount : 0;
                if (subMeshes < 2)
                {
                    continue;
                }

                Material[] current = smr.sharedMaterials;
                var materials = new Material[subMeshes];
                materials[0] = current.Length > 0 ? current[0] : null;
                materials[1] = inner;
                for (int i = 2; i < subMeshes; i++)
                {
                    materials[i] = i < current.Length ? current[i] : inner;
                }

                smr.sharedMaterials = materials;
                Debug.Log($"{LogTag} wolf mouth: {smr.name} subMeshes={subMeshes} materials={string.Join(",", materials.Select(m => m != null ? m.name : "null"))}");
            }

            PrefabUtility.SaveAsPrefabAsset(root, WolfMouthPrefab, out bool ok);
            Debug.Log($"{LogTag} wolf mouth prefab {(ok ? "saved" : "FAILED")} {WolfMouthPrefab}");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.SaveAssets();
    }

    // バッチ: -executeMethod SmalRigPilotTools.CreateC4cPrefabs（-quit 付き）
    [MenuItem("Tools/VisionGraft/SMAL Rig Pilot/Create c4c Prefabs")]
    public static void CreateC4cPrefabs()
    {
        foreach (string[] job in C4cJobs)
        {
            CreateSmalPrefab(job[0], job[1], job[2], job[3]);
        }
    }

    private static void CreateSmalPrefab(string fbxPath, string originalPath, string prefabPath, string prefabName)
    {
        CopyImportSettings(fbxPath, originalPath);
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        if (model == null)
        {
            Debug.LogError($"{LogTag} missing {fbxPath}");
            return;
        }

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
        try
        {
            instance.name = prefabName;
            foreach (Animator animator in instance.GetComponentsInChildren<Animator>(true))
            {
                animator.runtimeAnimatorController = null;
                animator.applyRootMotion = false;
            }

            // 材質は元の FBX のものを描画の部品ごとに当てる（Unity は FBX の画像を名前で探すので、Meshy の texture_0.png が
            // 別の動物のものに当たる。2026-10-23 に犬で確かめた）。
            SkinnedMeshRenderer source = AssetDatabase.LoadAssetAtPath<GameObject>(originalPath).GetComponentInChildren<SkinnedMeshRenderer>(true);
            foreach (SkinnedMeshRenderer smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                smr.sharedMaterials = source.sharedMaterials;
            }

            PrefabUtility.SaveAsPrefabAsset(instance, prefabPath, out bool ok);
            Debug.Log($"{LogTag} prefab {(ok ? "saved" : "FAILED")} {prefabPath}");
        }
        finally
        {
            Object.DestroyImmediate(instance);
        }

        AssetDatabase.SaveAssets();
        ModelResourceIndexGenerator.Generate();
        CompareModels(originalPath, prefabPath);
    }

    // 見た目の切り分け用: 元の FBX・往復・SMAL の骨の FBX を同じカメラで撮る（Docs/tmp/smal_rig_20261023/unity_check/）。
    // 法線の数え方（取り込みの normals）も一緒に出す。
    [MenuItem("Tools/VisionGraft/SMAL Rig Pilot/Capture Dog Variants")]
    public static void CaptureDogVariants()
    {
        string outDir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Docs/tmp/smal_rig_20261023/unity_check");
        System.IO.Directory.CreateDirectory(outDir);
        Light light = new GameObject("CaptureLight").AddComponent<Light>();
        light.type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(40f, -30f, 0f);
        Camera cam = new GameObject("CaptureCamera").AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.82f, 0.84f, 0.88f);
        cam.fieldOfView = 30f;
        try
        {
            foreach ((string tag, string path) in new[] { ("original", DogOriginal), ("roundtrip", DogRoundTrip), ("smal", DogSmal), ("prefab54", DogSmalPrefab) })
            {
                ModelImporter importer = AssetImporter.GetAtPath(path) as ModelImporter;
                GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (model == null)
                {
                    continue;
                }

                GameObject instance = Object.Instantiate(model);
                try
                {
                    Bounds b = RendererBounds(instance);
                    float size = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                    cam.transform.position = b.center + new Vector3(1f, 0.35f, 0.4f).normalized * size * 2.2f;
                    cam.transform.LookAt(b.center);
                    const int Px = 640;
                    RenderTexture rt = new RenderTexture(Px, Px, 24, RenderTextureFormat.ARGB32);
                    cam.targetTexture = rt;
                    cam.Render();
                    RenderTexture.active = rt;
                    Texture2D tex = new Texture2D(Px, Px, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, Px, Px), 0, 0);
                    tex.Apply();
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, tag + ".png"), tex.EncodeToPNG());
                    cam.targetTexture = null;
                    RenderTexture.active = null;
                    Object.DestroyImmediate(tex);
                    rt.Release();
                    Object.DestroyImmediate(rt);
                    SkinnedMeshRenderer smr = instance.GetComponentInChildren<SkinnedMeshRenderer>(true);
                    Mesh mesh = smr != null ? smr.sharedMesh : null;
                    Debug.Log($"{LogTag} capture {tag} normals={importer?.importNormals} tangents={importer?.importTangents} " +
                              $"uv0={(mesh != null ? mesh.uv.Length : -1)} normalsCount={(mesh != null ? mesh.normals.Length : -1)} " +
                              $"tangentsCount={(mesh != null ? mesh.tangents.Length : -1)} verts={(mesh != null ? mesh.vertexCount : -1)} " +
                              $"mat={(smr != null && smr.sharedMaterial != null ? smr.sharedMaterial.name + "/" + string.Join(",", smr.sharedMaterial.GetTexturePropertyNames().Where(p => smr.sharedMaterial.GetTexture(p) != null).Select(p => p + "=" + smr.sharedMaterial.GetTexture(p).name)) : "none")}");
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

    // 全動物の棚卸し（2026-10-23、全モデルを SMAL の骨にそろえる前）: model_index.txt の Animal を全部、実行時と同じ規則・上書き
    // （AnimalLegMappingFix.Table を AnimalBoneMappingOverride で付ける。プレイヤーの ApplyAnimalBoneOverride と同じ）で役の骨を決め、
    // 骨（prefab 名・元の FBX 名・親・bind の位置・皮膚に使われているか）と見た目の部品（SMR / MeshRenderer・材質・ブレンドシェイプ）を JSON に書く。
    [MenuItem("Tools/VisionGraft/SMAL Rig Pilot/Dump Animal Rig Inventory")]
    public static void DumpAnimalRigInventory()
    {
        string outDir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Docs/tmp/smal_rig_20261023/inventory");
        System.IO.Directory.CreateDirectory(outDir);
        TextAsset index = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Resources/Models/model_index.txt");
        var sb = new System.Text.StringBuilder();
        sb.Append("[\n");
        bool firstModel = true;
        foreach (string raw in index.text.Split('\n'))
        {
            string resPath = raw.Trim();
            if (!resPath.StartsWith("Models/Animal/"))
            {
                continue;
            }

            string assetPath = "Assets/Resources/" + resPath + ".prefab";
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                Debug.LogWarning($"{LogTag} inventory: missing {assetPath}");
                continue;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                Animator animator = instance.GetComponentInChildren<Animator>(true);
                Transform host = animator != null ? animator.transform : instance.transform;

                // bind の位置は、役を決める（名簿のモデルは skin 姿勢を書く）前に採る。
                var boneSet = new HashSet<Transform>();
                foreach (SkinnedMeshRenderer smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    foreach (Transform b in smr.bones)
                    {
                        if (b != null)
                        {
                            boneSet.Add(b);
                        }
                    }
                }

                var boneLines = new List<string>();
                foreach (Transform t in instance.GetComponentsInChildren<Transform>(true))
                {
                    Object src = PrefabUtility.GetCorrespondingObjectFromOriginalSource(t);
                    string fbxName = src != null ? src.name : t.name;
                    Vector3 p = instance.transform.InverseTransformPoint(t.position);
                    boneLines.Add($"{{\"name\":{Q(t.name)},\"fbxName\":{Q(fbxName)},\"path\":{Q(PathOf(instance.transform, t))},\"parent\":{Q(t.parent != null && t != instance.transform ? PathOf(instance.transform, t.parent) : "")}," +
                                  $"\"pos\":[{F(p.x)},{F(p.y)},{F(p.z)}],\"inSkin\":{(boneSet.Contains(t) ? "true" : "false")},\"source\":{Q(src != null ? AssetDatabase.GetAssetPath(src) : "")}}}");
                }

                var rendererLines = new List<string>();
                foreach (Renderer r in instance.GetComponentsInChildren<Renderer>(true))
                {
                    Mesh mesh = r is SkinnedMeshRenderer s ? s.sharedMesh : (r.GetComponent<MeshFilter>() != null ? r.GetComponent<MeshFilter>().sharedMesh : null);
                    string mats = string.Join(",", r.sharedMaterials.Select(m => Q(m != null ? m.name + "|" + m.shader.name : "null")));
                    rendererLines.Add($"{{\"type\":{Q(r.GetType().Name)},\"path\":{Q(PathOf(instance.transform, r.transform))},\"mesh\":{Q(mesh != null ? mesh.name : "")}," +
                                      $"\"meshSource\":{Q(mesh != null ? AssetDatabase.GetAssetPath(mesh) : "")},\"verts\":{(mesh != null ? mesh.vertexCount : 0)}," +
                                      $"\"subMeshes\":{(mesh != null ? mesh.subMeshCount : 0)},\"blendShapes\":{(mesh != null ? mesh.blendShapeCount : 0)}," +
                                      $"\"bones\":{(r is SkinnedMeshRenderer s2 ? s2.bones.Length : 0)},\"rootBone\":{Q(r is SkinnedMeshRenderer s3 && s3.rootBone != null ? s3.rootBone.name : "")},\"materials\":[{mats}]}}");
                }

                string key = AnimalLegMappingFix.Key(prefab.name);
                string spec = null;
                if (AnimalLegMappingFix.Table.TryGetValue(key, out string tableSpec))
                {
                    spec = tableSpec;
                    AnimalBoneMappingOverride ov = host.gameObject.AddComponent<AnimalBoneMappingOverride>();
                    foreach (string pair in tableSpec.Split(';'))
                    {
                        int eq = pair.IndexOf('=');
                        if (eq > 0)
                        {
                            AnimalLegMappingFix.TrySetOverrideField(ov, pair.Substring(0, eq).Trim(), pair.Substring(eq + 1).Trim());
                        }
                    }
                }

                var applier = new AnimalPoseApplier();
                AnimalPoseSettings settings = AnimalPoseSettingsFactory.Create(1f, true, true, 1f, 1f, Vector3.zero, Vector3.zero);
                Dictionary<string, Transform> roles = applier.DebugResolveRoleBones(host, settings);
                string roleLines = string.Join(",", roles.Select(kv => $"{Q(kv.Key)}:{Q(kv.Value != null ? PathOf(instance.transform, kv.Value) : "")}"));

                if (!firstModel)
                {
                    sb.Append(",\n");
                }

                firstModel = false;
                sb.Append($"{{\"prefab\":{Q(prefab.name)},\"assetPath\":{Q(assetPath)},\"host\":{Q(PathOf(instance.transform, host))},\"legFixSpec\":{Q(spec ?? "")}," +
                          $"\"rootRot\":[{F(instance.transform.localRotation.x)},{F(instance.transform.localRotation.y)},{F(instance.transform.localRotation.z)},{F(instance.transform.localRotation.w)}]," +
                          $"\"roles\":{{{roleLines}}},\n \"renderers\":[{string.Join(",", rendererLines)}],\n \"bones\":[{string.Join(",\n  ", boneLines)}]}}");
                Debug.Log($"{LogTag} inventory {prefab.name} bones={boneLines.Count} renderers={rendererLines.Count} spec={(spec != null ? "table" : "-")} " +
                          $"roles={string.Join(" ", roles.Where(kv => !kv.Key.StartsWith("_")).Select(kv => kv.Key + "=" + (kv.Value != null ? kv.Value.name : "-")))}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"{LogTag} inventory {prefab.name} failed: {ex}");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        sb.Append("\n]\n");
        System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "animals.json"), sb.ToString(), new System.Text.UTF8Encoding(false));
        Debug.Log($"{LogTag} inventory written {outDir}/animals.json");
    }

    private static string PathOf(Transform root, Transform t)
    {
        if (t == root)
        {
            return "";
        }

        var parts = new List<string>();
        for (Transform c = t; c != null && c != root; c = c.parent)
        {
            parts.Add(c.name);
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    private static string Q(string s)
    {
        return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string F(float v)
    {
        return v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
    }

    // 往復の FBX が Unity で白黒のまだらに写った原因を探す（2026-10-23、ユーザー「ふつうはしないと思うけど」）。
    // 元と往復の両方について、材質の全部の値（色・数値・画像・キーワード）、画像の asset と読み込み設定（種類・sRGB・圧縮）、
    // メッシュの法線・接線・UV を頂点ごとに比べて書き出す。何も変えない。
    [MenuItem("Tools/VisionGraft/SMAL Rig Pilot/Diagnose Round Trip Materials")]
    public static void DiagnoseRoundTripMaterials()
    {
        var sb = new System.Text.StringBuilder();
        var meshes = new Dictionary<string, Mesh>();
        foreach ((string tag, string path) in new[] { ("original", DogOriginal), ("roundtrip", DogRoundTrip), ("smal", DogSmal) })
        {
            ModelImporter importer = AssetImporter.GetAtPath(path) as ModelImporter;
            sb.AppendLine($"=== {tag} {path}");
            if (importer != null)
            {
                sb.AppendLine($"  importer: materialImportMode={importer.materialImportMode} materialLocation={importer.materialLocation} materialSearch={importer.materialSearch} " +
                              $"materialName={importer.materialName} importNormals={importer.importNormals} normalCalc={importer.normalCalculationMode} normalSmoothingAngle={importer.normalSmoothingAngle} " +
                              $"importTangents={importer.importTangents} useFileScale={importer.useFileScale} globalScale={importer.globalScale} bakeAxis={importer.bakeAxisConversion} " +
                              $"externalObjects={string.Join(",", importer.GetExternalObjectMap().Select(kv => kv.Key.name + "->" + AssetDatabase.GetAssetPath(kv.Value)))}");
            }

            foreach (Object sub in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (sub is Texture2D t)
                {
                    sb.AppendLine($"  sub-asset texture: {t.name} {t.width}x{t.height} format={t.format} mip={t.mipmapCount} isLinear?={!GraphicsFormatUtility.IsSRGBFormat(t.graphicsFormat)}");
                }
            }

            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            SkinnedMeshRenderer smr = model.GetComponentInChildren<SkinnedMeshRenderer>(true);
            meshes[tag] = smr.sharedMesh;
            foreach (Material m in smr.sharedMaterials)
            {
                sb.AppendLine($"  material {m.name} shader={m.shader.name} assetPath={AssetDatabase.GetAssetPath(m)} keywords=[{string.Join(" ", m.shaderKeywords)}] renderQueue={m.renderQueue}");
                Shader s = m.shader;
                for (int i = 0; i < s.GetPropertyCount(); i++)
                {
                    string pn = s.GetPropertyName(i);
                    switch (s.GetPropertyType(i))
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Color:
                            sb.AppendLine($"    {pn} = {m.GetColor(pn)}");
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Float:
                        case UnityEngine.Rendering.ShaderPropertyType.Range:
                            sb.AppendLine($"    {pn} = {m.GetFloat(pn):F4}");
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Texture:
                            Texture tex = m.GetTexture(pn);
                            if (tex != null)
                            {
                                string tp = AssetDatabase.GetAssetPath(tex);
                                TextureImporter ti = AssetImporter.GetAtPath(tp) as TextureImporter;
                                string tiInfo = ti != null ? $"importer type={ti.textureType} sRGB={ti.sRGBTexture} compression={ti.textureCompression}" : "no TextureImporter (sub-asset of the model)";
                                var t2 = tex as Texture2D;
                                sb.AppendLine($"    {pn} = {tex.name} asset={tp} {tex.width}x{tex.height} " +
                                              $"{(t2 != null ? "format=" + t2.format + " linear=" + !GraphicsFormatUtility.IsSRGBFormat(t2.graphicsFormat) : "")} {tiInfo} " +
                                              $"scale={m.GetTextureScale(pn)} offset={m.GetTextureOffset(pn)}");
                            }
                            break;
                    }
                }
            }
        }

        // 頂点ごとの比べ（頂点の並びが同じなら位置・法線・接線・UV の差、違えば最も近い頂点で）。
        foreach (string other in new[] { "roundtrip", "smal" })
        {
            Mesh a = meshes["original"];
            Mesh b = meshes[other];
            Vector3[] pa = a.vertices, pb = b.vertices, na = a.normals, nb = b.normals;
            Vector2[] ua = a.uv, ub = b.uv;
            Vector4[] ta = a.tangents, tb = b.tangents;
            bool sameOrder = pa.Length == pb.Length;
            int n = Mathf.Min(pa.Length, pb.Length);
            float maxPos = 0f, maxUv = 0f;
            int flipped = 0, bent30 = 0, tanSignDiff = 0;
            var normalAngles = new List<float>();
            for (int i = 0; i < n && sameOrder; i++)
            {
                maxPos = Mathf.Max(maxPos, Vector3.Distance(pa[i], pb[i]));
                if (ua.Length == pa.Length && ub.Length == pb.Length)
                {
                    maxUv = Mathf.Max(maxUv, Vector2.Distance(ua[i], ub[i]));
                }

                float ang = Vector3.Angle(na[i], nb[i]);
                normalAngles.Add(ang);
                if (ang > 90f) flipped++;
                if (ang > 30f) bent30++;
                if (ta.Length == pa.Length && tb.Length == pb.Length && Mathf.Sign(ta[i].w) != Mathf.Sign(tb[i].w)) tanSignDiff++;
            }

            normalAngles.Sort();
            sb.AppendLine($"=== vertex compare original vs {other}: verts {pa.Length} / {pb.Length} sameCount={sameOrder} maxPosDiff={maxPos:F6} maxUvDiff={maxUv:F6} " +
                          (normalAngles.Count > 0 ? $"normalAngle median={normalAngles[normalAngles.Count / 2]:F2} p99={normalAngles[(int)(normalAngles.Count * 0.99f)]:F2} max={normalAngles[normalAngles.Count - 1]:F2} >30deg={bent30} >90deg(flipped)={flipped} tangentSignDiff={tanSignDiff}" : "") +
                          $" uvCount {ua.Length}/{ub.Length} tangents {ta.Length}/{tb.Length} subMeshes {a.subMeshCount}/{b.subMeshCount}");
        }

        string outPath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Docs/tmp/smal_rig_20261023/unity_check/roundtrip_material_diagnosis.txt");
        System.IO.File.WriteAllText(outPath, sb.ToString(), new System.Text.UTF8Encoding(false));
        Debug.Log($"{LogTag} diagnosis written {outPath}");
    }

    [MenuItem("Tools/VisionGraft/SMAL Rig Pilot/Compare Dog Round Trip")]
    public static void CompareDogRoundTrip()
    {
        CopyImportSettings(DogRoundTrip, DogOriginal);
        CompareModels(DogOriginal, DogRoundTrip);
    }

    // 元の FBX の取り込みの設定（Generic・Avatar・倍率・軸・材質）を写す。アニメーションは取り込まない。
    public static void CopyImportSettings(string path, string like)
    {
        ModelImporter target = AssetImporter.GetAtPath(path) as ModelImporter;
        ModelImporter source = AssetImporter.GetAtPath(like) as ModelImporter;
        if (target == null || source == null)
        {
            Debug.LogError($"{LogTag} importer missing: {path} / {like}");
            return;
        }

        target.animationType = source.animationType;
        target.avatarSetup = source.avatarSetup;
        target.globalScale = source.globalScale;
        target.useFileScale = source.useFileScale;
        target.bakeAxisConversion = source.bakeAxisConversion;
        target.materialImportMode = source.materialImportMode;
        target.importAnimation = false;
        target.SaveAndReimport();
    }

    public static void CompareModels(string pathA, string pathB)
    {
        GameObject prefabA = AssetDatabase.LoadAssetAtPath<GameObject>(pathA);
        GameObject prefabB = AssetDatabase.LoadAssetAtPath<GameObject>(pathB);
        if (prefabA == null || prefabB == null)
        {
            Debug.LogError($"{LogTag} model missing: {pathA} / {pathB}");
            return;
        }

        GameObject a = Object.Instantiate(prefabA);
        GameObject b = Object.Instantiate(prefabB);
        try
        {
            Describe("A", pathA, a);
            Describe("B", pathB, b);

            Dictionary<string, Vector3> bonesA = BonePositions(a);
            Dictionary<string, Vector3> bonesB = BonePositions(b);
            float maxDiff = 0f;
            string worst = "";
            foreach (KeyValuePair<string, Vector3> kv in bonesA)
            {
                if (!bonesB.TryGetValue(kv.Key, out Vector3 pb))
                {
                    continue;
                }

                float d = Vector3.Distance(kv.Value, pb);
                if (d > maxDiff)
                {
                    maxDiff = d;
                    worst = kv.Key;
                }
            }

            Debug.Log($"{LogTag} bones A={bonesA.Count} B={bonesB.Count} common={bonesA.Keys.Intersect(bonesB.Keys).Count()} " +
                      $"onlyA=[{string.Join(",", bonesA.Keys.Except(bonesB.Keys))}] onlyB=[{string.Join(",", bonesB.Keys.Except(bonesA.Keys))}] " +
                      $"maxPosDiff={maxDiff * 1000f:F2}mm at {worst}");
        }
        finally
        {
            Object.DestroyImmediate(a);
            Object.DestroyImmediate(b);
        }
    }

    private static void Describe(string tag, string path, GameObject go)
    {
        Bounds bounds = RendererBounds(go);
        SkinnedMeshRenderer[] skinned = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        string meshes = string.Join(",", skinned.Select(s =>
            $"{s.name}(v={(s.sharedMesh != null ? s.sharedMesh.vertexCount : 0)} bones={s.bones.Length} root={(s.rootBone != null ? s.rootBone.name : "null")} " +
            $"mat={string.Join("/", s.sharedMaterials.Select(m => m == null ? "null" : $"{m.name}:{m.shader.name}:{(m.mainTexture != null ? m.mainTexture.name : "noTex")}"))})"));
        Animator animator = go.GetComponentInChildren<Animator>(true);
        Debug.Log($"{LogTag} {tag} {path} size=({bounds.size.x:F3},{bounds.size.y:F3},{bounds.size.z:F3}) center=({bounds.center.x:F3},{bounds.center.y:F3},{bounds.center.z:F3}) " +
                  $"skinned={skinned.Length} [{meshes}] avatar={(animator != null && animator.avatar != null ? animator.avatar.name : "none")}");
    }

    private static Dictionary<string, Vector3> BonePositions(GameObject go)
    {
        var result = new Dictionary<string, Vector3>();
        foreach (SkinnedMeshRenderer smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            foreach (Transform bone in smr.bones)
            {
                if (bone != null && !result.ContainsKey(bone.name))
                {
                    result[bone.name] = go.transform.InverseTransformPoint(bone.position);
                }
            }
        }

        return result;
    }

    private static Bounds RendererBounds(GameObject go)
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
