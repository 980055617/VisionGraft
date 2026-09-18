using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Same treatment as AnimalIndexPrefixer/HumanIndexPrefixer, applied to Resources/Models/Else.
// Step 1 copies the Saritasa Sport_Balls prefabs into Resources/Models/Else via
// AssetDatabase.CopyAsset (so Unity assigns fresh GUIDs instead of colliding with the
// originals under Assets/Saritasa - a raw filesystem copy would duplicate GUIDs). The FBX/
// material assets stay in Assets/Saritasa/Models/Sport_Balls; the copied prefabs keep
// referencing them by GUID, mirroring how Resources/Models/Animal/Sources keeps the source
// FBXs external to the renamed prefabs.
// Step 2 prefixes every prefab in Resources/Models/Else with its current runtime array index
// (2-digit zero-padded, e.g. "00_Baseball"), so a future selectedElseIndex Inspector field /
// in-VR picker index maps unambiguously to a filename, exactly like Animal and Human.
// Must be run with Unity Editor closed (batch mode only), per the project's established
// convention for these one-time index tools.
public static class ElseModelImporter
{
    private static readonly string[] SourceNames =
    {
        "Baseball", "Basketball", "Football", "Golf", "Soccer", "Tennis"
    };

    private const string SourceDir = "Assets/Saritasa/Models/Sport_Balls";
    private const string DestDir = "Assets/Resources/Models/Else";

    // Sketchfab glb dropped directly under Assets/ (filename is the Sketchfab model id).
    // "2ТЭ116УД" diesel locomotive by Leafia dev., CC-BY-4.0 - static rigid mesh, no
    // skin/animation, so it fits the Else category (anchor/bbox placement only, no pose
    // following). See docs/bundle-placement.md for licensing/attribution note.
    private const string RawGlbPath = "Assets/dba7a07e293d41a4922ea00bcbfbe804.glb";
    private const string DieselLocomotiveName = "DieselLocomotive";

    [MenuItem("Tools/VisionGraft/Import Saritasa Sport Balls Into Else")]
    public static void ImportFromSaritasa()
    {
        // Existence check compares against bare (prefix-stripped) names already present in
        // DestDir, not the literal "{name}.prefab" path - once PrefixWithIndex has run, the
        // copied prefab is renamed to "00_Baseball.prefab" etc., so a literal-path check would
        // no longer find it and re-copy on a second run, producing duplicates like
        // "06_Baseball.prefab".
        var existingBareNames = new HashSet<string>();
        foreach (var go in Resources.LoadAll<GameObject>("Models/Else"))
        {
            if (go != null)
            {
                existingBareNames.Add(StripExistingDigitPrefix(go.name));
            }
        }

        int copied = 0;
        foreach (var name in SourceNames)
        {
            if (existingBareNames.Contains(name))
            {
                Debug.Log($"[ElseModelImporter] Skip (already exists): {name}");
                continue;
            }

            string srcPath = $"{SourceDir}/{name}.prefab";
            string dstPath = $"{DestDir}/{name}.prefab";

            if (AssetDatabase.LoadAssetAtPath<GameObject>(srcPath) == null)
            {
                Debug.LogError($"[ElseModelImporter] Source missing: {srcPath}");
                continue;
            }

            bool ok = AssetDatabase.CopyAsset(srcPath, dstPath);
            if (!ok)
            {
                Debug.LogError($"[ElseModelImporter] Copy failed: {srcPath} -> {dstPath}");
                continue;
            }
            copied++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[ElseModelImporter] Import done. Copied {copied}/{SourceNames.Length}.");
    }

    [MenuItem("Tools/VisionGraft/Prefix All Else Models With Index (2-digit)")]
    public static void PrefixWithIndex()
    {
        GameObject[] all = Resources.LoadAll<GameObject>("Models/Else");
        var filtered = new List<GameObject>();
        foreach (var go in all)
        {
            if (go == null || go.name.Length == 0 || !(char.IsUpper(go.name[0]) || char.IsDigit(go.name[0])))
            {
                continue;
            }
            // Raw source assets (e.g. Sources/DieselLocomotive.glb) import with a GameObject as
            // their main asset too, via glTFast/UniGLTF's ScriptedImporter (ctx.SetMainObject),
            // so Resources.LoadAll<GameObject> picks them up alongside the actual numbered
            // prefabs. Animal/Sources avoided this by accident (its fbx filenames happen to be
            // lowercase); exclude by path here instead of relying on that.
            string assetPath = AssetDatabase.GetAssetPath(go);
            if (assetPath.Contains("/Sources/"))
            {
                continue;
            }
            filtered.Add(go);
        }

        filtered.Sort((a, b) => string.Compare(a.name, b.name, System.StringComparison.Ordinal));

        var plan = new List<(string path, string targetName)>();
        for (int i = 0; i < filtered.Count; i++)
        {
            string path = AssetDatabase.GetAssetPath(filtered[i]);
            string bareName = StripExistingDigitPrefix(filtered[i].name);
            string targetName = $"{i:D2}_{bareName}";
            plan.Add((path, targetName));
        }

        int renamed = 0;
        foreach (var (path, targetName) in plan)
        {
            var current = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (current == null)
            {
                Debug.LogError("[ElseModelImporter] Missing at rename time: " + path);
                continue;
            }
            if (current.name == targetName)
            {
                continue;
            }

            string error = AssetDatabase.RenameAsset(path, targetName);
            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogError($"[ElseModelImporter] Rename failed for {path} -> {targetName}: {error}");
                continue;
            }
            renamed++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[ElseModelImporter] Prefix done. Renamed {renamed}/{plan.Count}.");
    }

    // UniGLTF's default .glb/.gltf ScriptedImporter registration is meant to auto-disable
    // itself when glTFast is also present, but its version-defines check the legacy package id
    // "com.atteneder.gltfast" (see Packages/com.vrmc.gltf/Editor/UniGLTF.Editor.asmdef) - this
    // project uses the current official "com.unity.cloud.gltfast" id instead, so the check never
    // fires and both importers end up registered for the same extensions, which Unity resolves
    // by rejecting both (DefaultImporter fallback). Adding these symbols reproduces the disable
    // UniGLTF would have applied on its own; existing .glb assets that already carry an explicit
    // importer reference in their .meta (50+ Animated Animals/Badger, Moose_F) are unaffected
    // since UNIGLTF_DISABLE_DEFAULT_GLB_IMPORTER only removes it from the *default* extension
    // registration, not from assets that already pin it explicitly.
    // Changes Scripting Define Symbols for the active build target group - triggers a
    // recompile/domain reload, so run this as its own batchmode invocation before
    // ImportDieselLocomotive, not chained in the same process.
    [MenuItem("Tools/VisionGraft/Disable UniGLTF Default Glb-Gltf Importer")]
    public static void DisableUniGltfDefaultGlbImporter()
    {
        var buildTarget = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup);
        PlayerSettings.GetScriptingDefineSymbols(buildTarget, out var current);
        var symbols = new HashSet<string>(current)
        {
            "UNIGLTF_DISABLE_DEFAULT_GLB_IMPORTER",
            "UNIGLTF_DISABLE_DEFAULT_GLTF_IMPORTER"
        };
        if (symbols.Count == current.Length)
        {
            Debug.Log("[ElseModelImporter] Symbols already present, nothing to do.");
            return;
        }
        PlayerSettings.SetScriptingDefineSymbols(buildTarget, symbols.ToArray());
        Debug.Log($"[ElseModelImporter] Added UNIGLTF_DISABLE_DEFAULT_GLB_IMPORTER / _GLTF_IMPORTER to {buildTarget.TargetName}.");
    }

    // glTFast's GltfImporter (ScriptedImporter) makes the imported scene root GameObject the
    // main asset of the .glb file itself (ctx.SetMainObject in GltfImporter.cs), so a .glb can
    // already be Resources.Load'ed as a GameObject like any .prefab. But to keep Else's asset
    // format consistent with the existing 00_Baseball..05_Tennis prefabs (and to keep the raw
    // source out of the numbered folder, mirroring Resources/Models/Animal/Sources), this moves
    // the glb into Else/Sources/ and saves a proper .prefab wrapping its main object into
    // Resources/Models/Else itself.
    [MenuItem("Tools/VisionGraft/Import Diesel Locomotive Into Else")]
    public static void ImportDieselLocomotive()
    {
        string sourcesDir = $"{DestDir}/Sources";
        string sourceDst = $"{sourcesDir}/{DieselLocomotiveName}.glb";
        string prefabDst = $"{DestDir}/{DieselLocomotiveName}.prefab";

        if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabDst) != null)
        {
            Debug.Log($"[ElseModelImporter] Skip (already exists): {prefabDst}");
            return;
        }

        if (!AssetDatabase.IsValidFolder(sourcesDir))
        {
            AssetDatabase.CreateFolder(DestDir, "Sources");
        }

        if (AssetDatabase.LoadAssetAtPath<Object>(sourceDst) == null)
        {
            if (AssetDatabase.LoadAssetAtPath<Object>(RawGlbPath) == null)
            {
                Debug.LogError($"[ElseModelImporter] Raw glb missing: {RawGlbPath}");
                return;
            }
            string moveError = AssetDatabase.MoveAsset(RawGlbPath, sourceDst);
            if (!string.IsNullOrEmpty(moveError))
            {
                Debug.LogError($"[ElseModelImporter] Move failed: {RawGlbPath} -> {sourceDst}: {moveError}");
                return;
            }
        }

        // Both UniGLTF (VRM package) and glTFast register a ScriptedImporter for ".glb", so run
        // DisableUniGltfDefaultGlbImporter first (adds UNIGLTF_DISABLE_DEFAULT_GLB_IMPORTER to
        // Scripting Define Symbols) - otherwise Unity rejects both candidates and falls back to
        // DefaultImporter, leaving this a plain binary asset instead of a GameObject.
        AssetDatabase.ImportAsset(sourceDst, ImportAssetOptions.ForceUpdate);
        AssetDatabase.Refresh();

        var glbMain = AssetDatabase.LoadAssetAtPath<GameObject>(sourceDst);
        if (glbMain == null)
        {
            Debug.LogError($"[ElseModelImporter] glb main GameObject not found after import: {sourceDst}");
            return;
        }

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(glbMain);
        PrefabUtility.SaveAsPrefabAsset(instance, prefabDst);
        Object.DestroyImmediate(instance);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[ElseModelImporter] Diesel locomotive prefab created: {prefabDst}");
    }

    // One-time cleanup: PrefixWithIndex mistakenly renamed Sources/DieselLocomotive.glb to
    // Sources/07_DieselLocomotive.glb before the /Sources/ exclusion above was added. Restores
    // the bare name; the prefab's mesh/material references are unaffected since they're
    // resolved by GUID, not by path.
    [MenuItem("Tools/VisionGraft/Fix Diesel Locomotive Source Name")]
    public static void FixDieselLocomotiveSourceName()
    {
        string sourcesDir = $"{DestDir}/Sources";
        string wrongPath = $"{sourcesDir}/07_{DieselLocomotiveName}.glb";
        if (AssetDatabase.LoadAssetAtPath<Object>(wrongPath) == null)
        {
            Debug.Log("[ElseModelImporter] Nothing to fix, wrong-named source not found.");
            return;
        }
        string error = AssetDatabase.RenameAsset(wrongPath, DieselLocomotiveName);
        if (!string.IsNullOrEmpty(error))
        {
            Debug.LogError($"[ElseModelImporter] Fix rename failed: {error}");
            return;
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[ElseModelImporter] Fixed source name back to DieselLocomotive.glb.");
    }

    [MenuItem("Tools/VisionGraft/Verify Diesel Locomotive Prefab")]
    public static void VerifyDieselLocomotivePrefab()
    {
        string prefabPath = $"{DestDir}/06_{DieselLocomotiveName}.prefab";
        var go = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (go == null)
        {
            Debug.LogError($"[ElseModelImporter] Prefab not found: {prefabPath}");
            return;
        }
        var renderers = go.GetComponentsInChildren<MeshRenderer>(true);
        var filters = go.GetComponentsInChildren<MeshFilter>(true);
        int nullMeshes = 0;
        int nullMaterials = 0;
        foreach (var f in filters)
        {
            if (f.sharedMesh == null) nullMeshes++;
        }
        foreach (var r in renderers)
        {
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) nullMaterials++;
            }
        }
        Debug.Log($"[ElseModelImporter] Verify: renderers={renderers.Length} meshFilters={filters.Length} nullMeshes={nullMeshes} nullMaterials={nullMaterials}");
    }

    [MenuItem("Tools/VisionGraft/Import And Prefix Else Models")]
    public static void ImportAndPrefix()
    {
        ImportFromSaritasa();
        ImportDieselLocomotive();
        PrefixWithIndex();
    }

    // 2026-09-18: Asset Store の "Sport Car" パッケージ（Assets/SportCar、Blender 製、Readme に
    // "Royalty free license for games and standalone projects"）から車 3 台を Else に入れる。
    // car clip（bundle_car.svb）の置き換え用。prefab は MeshFilter + MeshRenderer だけで、
    // スクリプト・collider・Rigidbody は持たない（実測）。root 回転は 3 台とも恒等、前方向は +Z。
    // 置き場が揃っていない（SportCar_1 は Prefabs 直下、4 / 5 はサブフォルダ）ので明示列挙する。
    // Prefabs/** を舐めると車輪の prefab 6 個まで一覧に入るので、それはしない。
    //
    // 色: 車体の Body マテリアルは単色（テクスチャ無し、_Color だけ）なので、各車 3 色
    // （パッケージの元色 + 2 色）の prefab を作る（ユーザー要望 2026-09-18）。色違いのマテリアルは
    // Resources/Models/Else/Sources/SportCar/ に置き、prefab は元 prefab を実体化 → 完全に unpack →
    // Body のスロットだけ差し替え → SaveAsPrefabAsset で独立した prefab として保存する
    // （FBX / 他のマテリアルは Assets/SportCar のまま GUID 参照。Saritasa と同じ構成）。
    // マテリアルは先に SportCarMaterialUpgrader で URP 化しておく（ここでも呼ぶ。URP 済みなら何もしない）。
    private sealed class SportCarSource
    {
        public string name;          // prefab の基本名
        public string prefabPath;    // 元 prefab
        public string bodyMatPath;   // 車体色のマテリアル
        public string baseColorName; // 元色の名前（prefab 名の接尾辞）
        public (string colorName, Color color)[] extraColors;
    }

    private static readonly SportCarSource[] SportCarSources =
    {
        new SportCarSource
        {
            name = "SportCar_1",
            prefabPath = "Assets/SportCar/Prefabs/SportCar_1.prefab",
            bodyMatPath = "Assets/SportCar/Models/SportCar_1/Materials/Body1.mat",
            baseColorName = "Orange",
            extraColors = new[] { ("White", new Color(0.92f, 0.92f, 0.92f)), ("Blue", new Color(0.10f, 0.25f, 0.80f)) },
        },
        new SportCarSource
        {
            name = "SportCar_4",
            prefabPath = "Assets/SportCar/Prefabs/SportCar_4/SportCar_4.prefab",
            bodyMatPath = "Assets/SportCar/Models/SportCar_4/Materials/Body.mat",
            baseColorName = "Red",
            extraColors = new[] { ("Black", new Color(0.05f, 0.05f, 0.05f)), ("Silver", new Color(0.75f, 0.75f, 0.78f)) },
        },
        new SportCarSource
        {
            name = "SportCar_5",
            prefabPath = "Assets/SportCar/Prefabs/SportCar_5/SportCar_5.prefab",
            bodyMatPath = "Assets/SportCar/Models/SportCar_5/Materials/Body.mat",
            baseColorName = "Green",
            extraColors = new[] { ("White", new Color(0.92f, 0.92f, 0.92f)), ("Blue", new Color(0.10f, 0.25f, 0.80f)) },
        },
    };

    private const string SportCarMaterialDir = "Assets/Resources/Models/Else/Sources/SportCar";

    // 車の向き。Else は meta.bin の回転を使わず、モデルの +Z を画面の奥に向けて置く
    // （GetPinholeBasisRotation + 手動 yaw + prefab の root 補正）。パッケージの車は +Z が前なので
    // そのままだと後ろ姿になる。car clip は路肩の固定カメラに向かって車が来る（正面が見える）ので、
    // root に Y 180° を焼いて「前 = 視点側」にする（2026-09-18 ユーザー合意。yaw キーで持つより
    // 端末ごとの model_selection.json に依存しない）。ReplaceableModel.baseLocalRotation が保持し、
    // 機関車の X −90° と同じ経路で配置・ピッカーのプレビューに掛かる。
    private static readonly Quaternion SportCarFacingRotation = Quaternion.Euler(0f, 180f, 0f);

    [MenuItem("Tools/VisionGraft/Import Sport Cars Into Else")]
    public static void ImportSportCars()
    {
        SportCarMaterialUpgrader.Upgrade();

        var existingBareNames = new HashSet<string>();
        foreach (var go in Resources.LoadAll<GameObject>("Models/Else"))
        {
            if (go != null && !AssetDatabase.GetAssetPath(go).Contains("/Sources/"))
            {
                existingBareNames.Add(StripExistingDigitPrefix(go.name));
            }
        }

        EnsureFolder("Assets/Resources/Models/Else", "Sources");
        EnsureFolder("Assets/Resources/Models/Else/Sources", "SportCar");

        int created = 0;
        int planned = 0;
        foreach (var src in SportCarSources)
        {
            var sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(src.prefabPath);
            var bodyMat = AssetDatabase.LoadAssetAtPath<Material>(src.bodyMatPath);
            if (sourcePrefab == null || bodyMat == null)
            {
                Debug.LogError($"[ElseModelImporter] Source missing: {src.prefabPath} / {src.bodyMatPath}");
                continue;
            }

            var variants = new List<(string colorName, Material mat)> { (src.baseColorName, bodyMat) };
            foreach (var (colorName, color) in src.extraColors)
            {
                string matPath = $"{SportCarMaterialDir}/{src.name}_Body_{colorName}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null)
                {
                    if (!AssetDatabase.CopyAsset(src.bodyMatPath, matPath))
                    {
                        Debug.LogError($"[ElseModelImporter] Material copy failed: {src.bodyMatPath} -> {matPath}");
                        continue;
                    }
                    mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                }
                // URP/Lit は _BaseColor、変換前の Standard は _Color。両方あれば両方に入れる。
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
                EditorUtility.SetDirty(mat);
                variants.Add((colorName, mat));
            }

            foreach (var (colorName, mat) in variants)
            {
                planned++;
                string prefabName = $"{src.name}_{colorName}";
                if (existingBareNames.Contains(prefabName))
                {
                    Debug.Log($"[ElseModelImporter] Skip (already exists): {prefabName}");
                    continue;
                }
                if (TrySaveSportCarVariant(sourcePrefab, bodyMat, mat, $"{DestDir}/{prefabName}.prefab"))
                {
                    created++;
                }
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[ElseModelImporter] Sport cars: created {created}/{planned} prefabs.");

        // 番号付け。既存 00〜06 は名前順で先に来るので変わらず、車は 07 以降になる。
        PrefixWithIndex();
        VerifySportCarPrefabs();

        // runtime は Resources/Models/model_index.txt に載っている prefab しか読まない
        // （LoadModelPrefabsAsync。ビルド前は自動生成されるが Editor / バッチ実行では手動）。
        // 作り直さないと新しい prefab が一覧に入らず、index が既存の末尾（機関車）に丸まる（2026-09-18 に踏んだ）。
        ModelResourceIndexGenerator.Generate();
    }

    private static void EnsureFolder(string parent, string child)
    {
        if (!AssetDatabase.IsValidFolder($"{parent}/{child}"))
        {
            AssetDatabase.CreateFolder(parent, child);
        }
    }

    // 元 prefab を実体化し、Body マテリアルのスロットだけ差し替えて独立した prefab として保存する。
    private static bool TrySaveSportCarVariant(GameObject sourcePrefab, Material bodyMat, Material replacement, string prefabPath)
    {
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(sourcePrefab);
        try
        {
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            int replacedSlots = 0;
            if (replacement != bodyMat)
            {
                foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
                {
                    Material[] mats = r.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] == bodyMat)
                        {
                            mats[i] = replacement;
                            changed = true;
                            replacedSlots++;
                        }
                    }
                    if (changed)
                    {
                        r.sharedMaterials = mats;
                    }
                }
                if (replacedSlots == 0)
                {
                    Debug.LogError($"[ElseModelImporter] Body material not found on {sourcePrefab.name}; not saving {prefabPath}");
                    return false;
                }
            }

            instance.transform.localRotation = SportCarFacingRotation;
            instance.name = System.IO.Path.GetFileNameWithoutExtension(prefabPath);
            var saved = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath, out bool ok);
            if (!ok || saved == null)
            {
                Debug.LogError($"[ElseModelImporter] SaveAsPrefabAsset failed: {prefabPath}");
                return false;
            }
            Debug.Log($"[ElseModelImporter] Saved {prefabPath} (body slots replaced={replacedSlots})");
            return true;
        }
        finally
        {
            Object.DestroyImmediate(instance);
        }
    }

    // 車の prefab を実体化して AABB を測る。Else の scale は AABB の高さ（ReplaceableModel.baseHeightMeters）
    // で正規化されるので、高さが屋根高（全長ではない）になっていること、車体が pivot から横にずれていないことを見る。
    // 06_DieselLocomotive は root 回転のせいで高さが全長になっていた前例がある（2026-08-28）。
    // 既に保存済みの車 prefab の root 回転を SportCarFacingRotation に揃える（ImportSportCars 済みのものに後から掛ける用）。
    [MenuItem("Tools/VisionGraft/Bake Sport Car Facing (Y 180)")]
    public static void BakeSportCarFacing()
    {
        int baked = 0;
        foreach (var go in Resources.LoadAll<GameObject>("Models/Else"))
        {
            string path = go != null ? AssetDatabase.GetAssetPath(go) : null;
            if (string.IsNullOrEmpty(path) || path.Contains("/Sources/") ||
                !StripExistingDigitPrefix(go.name).StartsWith("SportCar_"))
            {
                continue;
            }
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (Quaternion.Angle(root.transform.localRotation, SportCarFacingRotation) < 0.01f)
                {
                    Debug.Log($"[ElseModelImporter] {go.name}: 向きは焼き済み");
                    continue;
                }
                root.transform.localRotation = SportCarFacingRotation;
                PrefabUtility.SaveAsPrefabAsset(root, path, out bool ok);
                if (ok) { baked++; Debug.Log($"[ElseModelImporter] {go.name}: root を Y 180° にした"); }
                else { Debug.LogError($"[ElseModelImporter] {go.name}: 保存失敗"); }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[ElseModelImporter] Bake facing done. baked={baked}");
        VerifySportCarPrefabs();
    }

    [MenuItem("Tools/VisionGraft/Verify Sport Car Prefabs")]
    public static void VerifySportCarPrefabs()
    {
        foreach (var go in Resources.LoadAll<GameObject>("Models/Else"))
        {
            if (go == null || AssetDatabase.GetAssetPath(go).Contains("/Sources/") ||
                !StripExistingDigitPrefix(go.name).StartsWith("SportCar_"))
            {
                continue;
            }
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(go);
            try
            {
                var renderers = instance.GetComponentsInChildren<Renderer>(true);
                int nullMeshes = 0;
                int nullMaterials = 0;
                int nonUrp = 0;
                string bodyColor = "?";
                foreach (var f in instance.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (f.sharedMesh == null) nullMeshes++;
                }
                foreach (var r in renderers)
                {
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) { nullMaterials++; continue; }
                        if (m.shader == null || !m.shader.name.StartsWith("Universal Render Pipeline/")) nonUrp++;
                        if (m.name.StartsWith("Body") || m.name.Contains("_Body_"))
                        {
                            bodyColor = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor").ToString("F2") : m.name;
                        }
                    }
                }
                if (renderers.Length == 0)
                {
                    Debug.LogWarning($"[ElseModelImporter] {go.name}: Renderer が無い");
                    continue;
                }
                Bounds b = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                {
                    b.Encapsulate(renderers[i].bounds);
                }
                Vector3 pivot = instance.transform.position;
                Debug.Log($"[ElseModelImporter] {go.name}: renderers={renderers.Length} nullMeshes={nullMeshes} nullMaterials={nullMaterials} nonUrpMaterials={nonUrp} body={bodyColor} " +
                          $"size=({b.size.x:F3}, {b.size.y:F3}, {b.size.z:F3}) center-pivot=({b.center.x - pivot.x:F3}, {b.center.y - pivot.y:F3}, {b.center.z - pivot.z:F3}) " +
                          $"bottom={b.min.y - pivot.y:F3} rootEuler={instance.transform.localRotation.eulerAngles} rootScale={instance.transform.localScale}");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }
    }

    private static string StripExistingDigitPrefix(string rawName)
    {
        int underscoreIndex = rawName.IndexOf('_');
        if (underscoreIndex <= 0)
        {
            return rawName;
        }
        for (int i = 0; i < underscoreIndex; i++)
        {
            if (!char.IsDigit(rawName[i]))
            {
                return rawName;
            }
        }
        return rawName.Substring(underscoreIndex + 1);
    }
}
