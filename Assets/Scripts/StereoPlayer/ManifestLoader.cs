using System.IO;
using UnityEngine;

public static class ManifestLoader
{
    public static bool TryLoad(string manifestPath, out ManifestData manifest)
    {
        manifest = null;
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            manifest = JsonUtility.FromJson<ManifestData>(File.ReadAllText(manifestPath));
            return
                manifest != null &&
                manifest.quant_pos_scale > 0f &&
                manifest.quant_joint_scale > 0f &&
                manifest.joints_space == "camera_xyz_root_relative";
        }
        catch
        {
            manifest = null;
        }
        return false;
    }
}
