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

    // 機関車の向きを車にそろえる（2026-10-11、9/18 の BakeSportCarFacing の入れ漏れの直し）。車 9 台は root に Y 180°（前 = 視点側）を焼いたが、
    // 機関車の root は GLB の取り込みの X −90°（Z-up → Y-up）のままで、運転室（GLB の y < −7）が配置の +Z（画面の奥）を向いていた。
    // root を Y 180° の空の GameObject にし、GLB のインスタンスを今の X −90° と倍率のまま子に入れて同じパスへ保存する（GUID・名前は変わらない）。
    // 済みなら何もしない。Unity を閉じたバッチで -executeMethod ElseModelImporter.WrapDieselLocomotiveFacing。
    [MenuItem("Tools/VisionGraft/Wrap Diesel Locomotive Facing (Y 180)")]
    public static void WrapDieselLocomotiveFacing()
    {
        string prefabPath = $"{DestDir}/06_{DieselLocomotiveName}.prefab";
        var current = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (current == null)
        {
            Debug.LogError($"[ElseModelImporter] Wrap: prefab not found: {prefabPath}");
            return;
        }
        if (Quaternion.Angle(current.transform.localRotation, SportCarFacingRotation) < 0.01f)
        {
            Debug.Log($"[ElseModelImporter] Wrap: already wrapped (root euler={current.transform.localRotation.eulerAngles})");
            LogDieselLocomotiveFacing(prefabPath, "Wrap");
            return;
        }
        // .glb を LoadAssetAtPath で取り直さない（.meta は DefaultImporter のことがある）。今の prefab の元（GLB の main object）を使う。
        var src = PrefabUtility.GetCorrespondingObjectFromSource(current);
        if (src == null)
        {
            Debug.LogError("[ElseModelImporter] Wrap: the prefab has no source (GLB main object); nothing changed");
            return;
        }
        var root = new GameObject($"06_{DieselLocomotiveName}");
        try
        {
            root.transform.localRotation = SportCarFacingRotation;
            var child = (GameObject)PrefabUtility.InstantiatePrefab(src, root.transform);
            child.transform.localPosition = Vector3.zero;
            child.transform.localRotation = current.transform.localRotation;
            child.transform.localScale = current.transform.localScale;
            child.name = DieselLocomotiveName;
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out bool ok);
            if (!ok)
            {
                Debug.LogError($"[ElseModelImporter] Wrap: SaveAsPrefabAsset failed: {prefabPath}");
                return;
            }
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        LogDieselLocomotiveFacing(prefabPath, "Wrap");
    }

    // 走行イベントの前後の目印（2026-10-11）。車は子 FL/FR/RL/RR（ホイール）から前を決める（InteractiveMotion.partial.cs の TryResolveRigidWheels）。
    // 機関車には無く、前を「視聴者の方向」とみなして向きを変えずに滑っていた。Renderer の無い空の子を 4 つ root の直下に置く（座標は root の局所。
    // GLB の台車の中央の軸 wheel_lf.000（前台車 = 運転室側）と wheel_lf.004（後台車）のメッシュの中心、横 ±0.803、高さ 0.949）。
    // 使われるのは前後の中点の差だけで、新しい係数は無い。包んだ後に 1 回だけ。既にあれば何もしない。
    [MenuItem("Tools/VisionGraft/Add Diesel Locomotive Wheel Markers")]
    public static void AddDieselLocomotiveWheelMarkers()
    {
        string prefabPath = $"{DestDir}/06_{DieselLocomotiveName}.prefab";
        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        int added = 0;
        try
        {
            if (Quaternion.Angle(root.transform.localRotation, SportCarFacingRotation) >= 0.01f)
            {
                Debug.LogError("[ElseModelImporter] Markers: root is not Y 180; run WrapDieselLocomotiveFacing first. Nothing changed");
                return;
            }
            var markers = new (string name, Vector3 position)[]
            {
                ("FL", new Vector3(-0.80f, 0.949f, 4.901f)),
                ("FR", new Vector3(0.80f, 0.949f, 4.901f)),
                ("RL", new Vector3(-0.80f, 0.949f, -5.351f)),
                ("RR", new Vector3(0.80f, 0.949f, -5.351f)),
            };
            foreach (var (name, position) in markers)
            {
                if (root.transform.Find(name) != null)
                {
                    continue;
                }
                var marker = new GameObject(name);
                marker.transform.SetParent(root.transform, false);
                marker.transform.localPosition = position;
                marker.transform.localRotation = Quaternion.identity;
                marker.transform.localScale = Vector3.one;
                added++;
            }
            if (added > 0)
            {
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out bool ok);
                if (!ok)
                {
                    Debug.LogError($"[ElseModelImporter] Markers: SaveAsPrefabAsset failed: {prefabPath}");
                    return;
                }
            }
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[ElseModelImporter] Markers: added={added}");
        LogDieselLocomotiveFacing(prefabPath, "Markers");
    }

    // 確かめ用: root と子の回転、root から見た描画の合成 bounds（TrackInstanceFactory.TryComputeLocalBounds と同じく各 Renderer の bounds の角を
    // root の局所へ写す）、目印の数を 1 行に出す。高さ（bounds の y）は 5.259 になるはず（18.5 なら子の X −90° が落ちた 8/28 の再発）。
    private static void LogDieselLocomotiveFacing(string prefabPath, string tag)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null)
        {
            Debug.LogError($"[ElseModelImporter] {tag}: prefab not found after save: {prefabPath}");
            return;
        }
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        try
        {
            Transform root = instance.transform;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
            {
                Bounds b = r.bounds;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = new Vector3((c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z);
                    Vector3 local = root.InverseTransformPoint(corner);
                    min = Vector3.Min(min, local);
                    max = Vector3.Max(max, local);
                }
            }
            Vector3 size = max - min;
            Transform child = root.Find(DieselLocomotiveName);
            int markerCount = 0;
            foreach (string n in new[] { "FL", "FR", "RL", "RR" })
            {
                if (root.Find(n) != null) markerCount++;
            }
            string line = $"[ElseModelImporter] {tag}: root euler={root.localRotation.eulerAngles} child euler={(child != null ? child.localRotation.eulerAngles.ToString() : "none")} " +
                          $"bounds size=({size.x:F3}, {size.y:F3}, {size.z:F3}) center=({(min.x + max.x) * 0.5f:F3}, {(min.y + max.y) * 0.5f:F3}, {(min.z + max.z) * 0.5f:F3}) markers={markerCount}";
            if (Mathf.Abs(size.y - 5.259f) > 0.01f)
            {
                Debug.LogError(line + " (height is not 5.259)");
            }
            else
            {
                Debug.Log(line);
            }
        }
        finally
        {
            Object.DestroyImmediate(instance);
        }
    }

    [MenuItem("Tools/VisionGraft/Import And Prefix Else Models")]
    public static void ImportAndPrefix()
    {
        ImportFromSaritasa();
        ImportDieselLocomotive();
        PrefixWithIndex();
        // 機関車の向きを車にそろえ、走行イベントの前後の目印を足す（2026-10-11）。番号付けの後のパス（06_）で開くので、ここで呼ぶ。
        WrapDieselLocomotiveFacing();
        AddDieselLocomotiveWheelMarkers();
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
    // 2026-10-11: 機関車も root に同じ Y 180° を持つ（WrapDieselLocomotiveFacing。X −90° は子の GLB に入る）。9/18 に車にだけ入れた入れ漏れの直し。
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
