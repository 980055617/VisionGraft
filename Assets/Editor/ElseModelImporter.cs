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
