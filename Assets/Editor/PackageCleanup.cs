using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// One-time cleanup (2026-07-16): removes non-Unity-importable source files from the "50+
// Animated Animals" package. Unity only ever uses the .fbx/textures/.mat inside this folder
// (confirmed via GUID cross-reference against every Resources/Models/Animal prefab) - the
// .blend/.blend1 (Blender edit files + autosave backups), .zip (original download archives),
// .gltf/.bin (unused alternate export format) and .unitypackage (distribution package, already
// imported) are pure deadweight, together several GB, with nothing in the project able to
// reference them.
public static class PackageCleanup
{
    private static readonly string[] Extensions =
    {
        ".blend", ".blend1", ".zip", ".gltf", ".bin", ".unitypackage",
    };

    [MenuItem("Tools/VisionGraft/Delete Unused Source Files In 50+ Animated Animals")]
    public static void Run()
    {
        const string root = "Assets/50+ Animated Animals";
        var toDelete = new List<string>();
        foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            string normalized = path.Replace('\\', '/');
            if (normalized.EndsWith(".meta"))
            {
                continue; // handled automatically by AssetDatabase.DeleteAsset
            }
            foreach (var ext in Extensions)
            {
                if (normalized.EndsWith(ext, System.StringComparison.OrdinalIgnoreCase))
                {
                    toDelete.Add(normalized);
                    break;
                }
            }
        }

        Debug.Log($"[PackageCleanup] Found {toDelete.Count} files to delete.");

        int deleted = 0;
        long totalBytes = 0;
        foreach (var path in toDelete)
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                totalBytes += info.Length;
            }

            if (AssetDatabase.DeleteAsset(path))
            {
                deleted++;
            }
            else
            {
                Debug.LogError("[PackageCleanup] Failed to delete: " + path);
            }
        }

        AssetDatabase.Refresh();
        double gb = totalBytes / 1024.0 / 1024.0 / 1024.0;
        Debug.Log($"[PackageCleanup] Done. Deleted {deleted}/{toDelete.Count} files (~{gb:F2} GB).");
    }
}
