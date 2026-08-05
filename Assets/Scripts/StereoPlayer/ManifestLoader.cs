using System.IO;
using UnityEngine;

public static class ManifestLoader
{
    public static bool TryLoad(string manifestPath, out ManifestData manifest)
    {
        return TryLoad(manifestPath, out manifest, out _);
    }

    public static bool TryLoad(string manifestPath, out ManifestData manifest, out ShotBoundaries shots)
    {
        manifest = null;
        shots = ShotBoundaries.Empty;
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            string json = File.ReadAllText(manifestPath);
            manifest = JsonUtility.FromJson<ManifestData>(json);
            bool valid =
                manifest != null &&
                manifest.quant_pos_scale > 0f &&
                manifest.quant_joint_scale > 0f &&
                manifest.joints_space == "camera_xyz_root_relative";
            if (valid)
            {
                // shots は [[start, end), ...] の入れ子配列で JsonUtility が扱えないため、
                // ManifestData には載せず生 JSON から別途読む。
                shots = ShotBoundaries.FromManifestJson(json);
            }

            return valid;
        }
        catch
        {
            manifest = null;
        }
        return false;
    }
}
