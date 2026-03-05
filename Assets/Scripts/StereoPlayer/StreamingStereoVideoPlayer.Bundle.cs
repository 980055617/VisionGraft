using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private IEnumerator EnsureBundleAndPrepareVideo(string selectedBundlePath = null)
    {
        if (vp == null)
        {
            yield break;
        }

        string bundlePathToLoad = selectedBundlePath;
        if (string.IsNullOrEmpty(bundlePathToLoad))
        {
            string streamingBundleUrl = Path.Combine(Application.streamingAssetsPath, bundleFileName);
            streamingBundleUrl = streamingBundleUrl.Replace("\\", "/");
            string persistentBundlePath = Path.Combine(Application.persistentDataPath, bundleFileName);
            // Always refresh the persistent bundle so replaced StreamingAssets/bundle.svb is picked up.
            using (UnityWebRequest request = UnityWebRequest.Get(streamingBundleUrl))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    yield break;
                }

                try
                {
                    string bundleDir = Path.GetDirectoryName(persistentBundlePath);
                    if (!string.IsNullOrEmpty(bundleDir))
                    {
                        Directory.CreateDirectory(bundleDir);
                    }

                    File.WriteAllBytes(persistentBundlePath, request.downloadHandler.data);
                }
                catch
                {
                    yield break;
                }
            }

            bundlePathToLoad = persistentBundlePath;
        }

        if (string.IsNullOrEmpty(bundlePathToLoad) || !File.Exists(bundlePathToLoad))
        {
            yield break;
        }

        string cacheDir = Path.Combine(Application.persistentDataPath, "svb_cache");
        try
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }
        }
        catch
        {
        }

        string extractedVideoPath = Path.Combine(cacheDir, extractedVideoFileName);
        string extractedManifestPath = Path.Combine(cacheDir, extractedManifestFileName);
        string extractedMetaPath = Path.Combine(cacheDir, extractedMetaFileName);

        // Always extract fresh files after cache clear.
        {
            try
            {
                Directory.CreateDirectory(cacheDir);
                using (var fs = new FileStream(bundlePathToLoad, FileMode.Open, FileAccess.Read))
                using (var za = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    if (!ExtractZipEntry(za, bundleVideoEntryName, extractedVideoPath))
                    {
                        yield break;
                    }

                    if (!ExtractZipEntry(za, bundleManifestEntryName, extractedManifestPath))
                    {
                        yield break;
                    }

                    if (!ExtractZipEntry(za, bundleMetaEntryName, extractedMetaPath))
                    {
                        yield break;
                    }
                }
            }
            catch
            {
                yield break;
            }
        }

        if (!File.Exists(extractedVideoPath))
        {
            yield break;
        }

        TryLoadManifest(extractedManifestPath);
        LoadMeta(extractedMetaPath);

        string normalizedVideoPath = extractedVideoPath.Replace("\\", "/");
        vp.url = normalizedVideoPath;

        vp.Prepare();
    }

    private bool ExtractZipEntry(ZipArchive za, string entryName, string outPath)
    {
        var entry = za.GetEntry(entryName);
        if (entry == null)
        {
            return false;
        }

        using (var entryStream = entry.Open())
        using (var outStream = new FileStream(outPath, FileMode.Create, FileAccess.Write))
        {
            entryStream.CopyTo(outStream);
        }

        return true;
    }

    private void TryLoadManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(manifestPath);
            manifest = JsonUtility.FromJson<ManifestData>(json);
            if (manifest == null)
            {
                return;
            }

            bool hasJointsSpaceInRaw = json.Contains("\"joints_space\"");
            bool hasFxNormInRaw = json.Contains("\"fx_norm\"");
            bool hasFyNormInRaw = json.Contains("\"fy_norm\"");
            bool parseMissingJointsSpace = string.IsNullOrEmpty(manifest.joints_space);
            bool parseMissingFxFy = manifest.fx_norm <= 0f || manifest.fy_norm <= 0f;
            if ((hasJointsSpaceInRaw && parseMissingJointsSpace) || ((hasFxNormInRaw || hasFyNormInRaw) && parseMissingFxFy))
            {
                FillManifestMissingFieldsFromRawJson(json);
            }
        }
        catch
        {
        }
    }

    private void FillManifestMissingFieldsFromRawJson(string json)
    {
        if (manifest == null || string.IsNullOrEmpty(json))
        {
            return;
        }

        if (string.IsNullOrEmpty(manifest.joints_space) && TryExtractJsonString(json, "joints_space", out string jointsSpace))
        {
            manifest.joints_space = jointsSpace;
        }
        if (manifest.fx_norm <= 0f && TryExtractJsonFloat(json, "fx_norm", out float fxNorm))
        {
            manifest.fx_norm = fxNorm;
        }
        if (manifest.fy_norm <= 0f && TryExtractJsonFloat(json, "fy_norm", out float fyNorm))
        {
            manifest.fy_norm = fyNorm;
        }
        if (manifest.eye_w <= 0 && TryExtractJsonInt(json, "eye_w", out int eyeW))
        {
            manifest.eye_w = eyeW;
        }
        if (manifest.eye_h <= 0 && TryExtractJsonInt(json, "eye_h", out int eyeH))
        {
            manifest.eye_h = eyeH;
        }
        if (manifest.joints_quant_scale <= 0f && TryExtractJsonFloat(json, "joints_quant_scale", out float jointsQuantScale))
        {
            manifest.joints_quant_scale = jointsQuantScale;
        }
    }

    private static bool TryExtractJsonString(string json, string key, out string value)
    {
        value = null;
        Match m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"");
        if (!m.Success || m.Groups.Count < 2)
        {
            return false;
        }

        value = m.Groups[1].Value;
        return !string.IsNullOrEmpty(value);
    }

    private static bool TryExtractJsonFloat(string json, string key, out float value)
    {
        value = 0f;
        Match m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)");
        if (!m.Success || m.Groups.Count < 2)
        {
            return false;
        }

        return float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryExtractJsonInt(string json, string key, out int value)
    {
        value = 0;
        Match m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*(-?[0-9]+)");
        if (!m.Success || m.Groups.Count < 2)
        {
            return false;
        }

        return int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
