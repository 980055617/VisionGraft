using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    private IEnumerator EnsureBundleAndPrepareVideo()
    {
        if (vp == null)
        {
            Debug.LogError("EnsureBundleAndPrepareVideo: VideoPlayer is null.");
            yield break;
        }

        string streamingBundleUrl = Path.Combine(Application.streamingAssetsPath, bundleFileName);
        streamingBundleUrl = streamingBundleUrl.Replace("\\", "/");
        string persistentBundlePath = Path.Combine(Application.persistentDataPath, bundleFileName);
        LogBundle($"Streaming bundle url: {streamingBundleUrl}");
        LogBundle($"Persistent bundle path: {persistentBundlePath}");
        // Always refresh the persistent bundle so replaced StreamingAssets/bundle.svb is picked up.
        bool needsBundleCopy = true;

        if (needsBundleCopy)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(streamingBundleUrl))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"Failed to read bundle from StreamingAssets. url={streamingBundleUrl} result={request.result} error={request.error}");
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
                catch (System.Exception ex)
                {
                    Debug.LogError($"Failed to write bundle. path={persistentBundlePath} ({ex.Message})");
                    yield break;
                }
            }
        }

        if (!File.Exists(persistentBundlePath))
        {
            Debug.LogError($"Bundle not found after copy. path={persistentBundlePath}");
            yield break;
        }

        string cacheDir = Path.Combine(Application.persistentDataPath, "svb_cache");
        bool cacheCleared = false;
        try
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
                cacheCleared = true;
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[cache] clear_failed cacheDir={cacheDir} ({ex.Message})");
        }
        Debug.Log($"[cache] cacheDir={cacheDir} cleared={cacheCleared}");

        string extractedVideoPath = Path.Combine(cacheDir, extractedVideoFileName);
        string extractedManifestPath = Path.Combine(cacheDir, extractedManifestFileName);
        string extractedMetaPath = Path.Combine(cacheDir, extractedMetaFileName);

        // Always extract fresh files after cache clear.
        bool needsExtractVideo = true;
        bool needsExtractManifest = true;
        bool needsExtractMeta = true;
        bool needsExtractAny = needsExtractVideo || needsExtractManifest || needsExtractMeta;

        LogBundle($"Extracted paths: video={extractedVideoPath} exists={File.Exists(extractedVideoPath)} needsExtract={needsExtractVideo}");
        LogBundle($"Extracted paths: manifest={extractedManifestPath} exists={File.Exists(extractedManifestPath)} needsExtract={needsExtractManifest}");
        LogBundle($"Extracted paths: meta={extractedMetaPath} exists={File.Exists(extractedMetaPath)} needsExtract={needsExtractMeta}");

        if (needsExtractAny)
        {
            try
            {
                Directory.CreateDirectory(cacheDir);
                using (var fs = new FileStream(persistentBundlePath, FileMode.Open, FileAccess.Read))
                using (var za = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    if (needsExtractVideo && !ExtractZipEntry(za, bundleVideoEntryName, extractedVideoPath))
                    {
                        yield break;
                    }

                    if (needsExtractManifest && !ExtractZipEntry(za, bundleManifestEntryName, extractedManifestPath))
                    {
                        yield break;
                    }

                    if (needsExtractMeta && !ExtractZipEntry(za, bundleMetaEntryName, extractedMetaPath))
                    {
                        yield break;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Failed to extract files. cacheDir={cacheDir} ({ex.Message})");
                yield break;
            }
        }

        LogExtractedFileStatus(extractedVideoPath, "video");
        LogExtractedFileStatus(extractedManifestPath, "manifest");
        LogExtractedFileStatus(extractedMetaPath, "meta");

        if (!File.Exists(extractedVideoPath))
        {
            Debug.LogError($"Extracted video missing. path={extractedVideoPath}");
            yield break;
        }

        TryLoadManifest(extractedManifestPath);
        LoadMeta(extractedMetaPath);

        LogBundle($"Extracted video path: {extractedVideoPath}");
        string normalizedVideoPath = extractedVideoPath.Replace("\\", "/");
        vp.url = normalizedVideoPath;

        vp.Prepare();
    }

    private bool ExtractZipEntry(ZipArchive za, string entryName, string outPath)
    {
        var entry = za.GetEntry(entryName);
        if (entry == null)
        {
            Debug.LogError($"Entry not found in bundle. entry={entryName}");
            return false;
        }

        using (var entryStream = entry.Open())
        using (var outStream = new FileStream(outPath, FileMode.Create, FileAccess.Write))
        {
            entryStream.CopyTo(outStream);
        }

        LogBundle($"Extracted entry. entry={entryName} outPath={outPath} size={new FileInfo(outPath).Length} bytes");
        return true;
    }

    private void LogExtractedFileStatus(string path, string label)
    {
        if (!File.Exists(path))
        {
            Debug.LogWarning($"Extracted file missing. label={label} path={path}");
            return;
        }

        long size = new FileInfo(path).Length;
        LogBundle($"Extracted file exists. label={label} size={size} bytes path={path}");
    }

    private void TryLoadManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            Debug.LogError($"Manifest not found. path={manifestPath}");
            return;
        }

        try
        {
            string json = File.ReadAllText(manifestPath);
            LogManifestRawInfo(manifestPath, json);
            // Intentional: manifest logs are disabled in the category-only logger.
            manifest = JsonUtility.FromJson<ManifestData>(json);
            if (manifest == null)
            {
                Debug.Log("[manifest_parse] joints_space=<null_manifest> fx_norm=0 fy_norm=0 eye_w=0 eye_h=0");
                Debug.LogError($"Manifest parse failed (null). path={manifestPath}");
                return;
            }

            Debug.Log(
                $"[manifest_parse] joints_space={SafeString(manifest.joints_space)} fx_norm={manifest.fx_norm:F6} fy_norm={manifest.fy_norm:F6} " +
                $"eye_w={manifest.eye_w} eye_h={manifest.eye_h}");

            bool hasJointsSpaceInRaw = json.Contains("\"joints_space\"");
            bool hasFxNormInRaw = json.Contains("\"fx_norm\"");
            bool hasFyNormInRaw = json.Contains("\"fy_norm\"");
            bool parseMissingJointsSpace = string.IsNullOrEmpty(manifest.joints_space);
            bool parseMissingFxFy = manifest.fx_norm <= 0f || manifest.fy_norm <= 0f;
            if ((hasJointsSpaceInRaw && parseMissingJointsSpace) || ((hasFxNormInRaw || hasFyNormInRaw) && parseMissingFxFy))
            {
                FillManifestMissingFieldsFromRawJson(json);
                Debug.Log(
                    $"[manifest_parse_fallback] joints_space={SafeString(manifest.joints_space)} fx_norm={manifest.fx_norm:F6} fy_norm={manifest.fy_norm:F6} " +
                    $"eye_w={manifest.eye_w} eye_h={manifest.eye_h}");
            }

            if (!string.IsNullOrEmpty(manifest.joints_space) &&
                manifest.joints_space != "camera_xyz_absolute" &&
                manifest.joints_space != "camera_xyz_root_relative")
            {
                Debug.LogWarning($"Manifest joints_space unsupported '{manifest.joints_space}', fallback={GetEffectiveJointsSpaceTag()}");
            }

            LogBundle($"Manifest parsed. eye_w={manifest.eye_w} eye_h={manifest.eye_h} num_frames={manifest.num_frames} fps={manifest.fps}");
            LogMeta($"Manifest extras: fovx_deg={manifest.fovx_deg} quant_pos_scale={manifest.quant_pos_scale} crop=({manifest.crop_x},{manifest.crop_y},{manifest.crop_w},{manifest.crop_h}) crop0=({manifest.crop_x0},{manifest.crop_y0}) has_crop={manifest.has_crop}");
            LogMeta($"Manifest skeleton: joints_space={GetEffectiveJointsSpaceTag()} camera_axes={manifest.camera_axes} uv_origin={manifest.uv_origin} joints_quant_scale={manifest.joints_quant_scale}");
            Debug.Log(
                $"[manifest_verify] joints_space={GetEffectiveJointsSpaceTag()} fx_norm={manifest.fx_norm:F6} fy_norm={manifest.fy_norm:F6} " +
                $"eye=({manifest.eye_w},{manifest.eye_h}) joints_quant_scale={GetQuantJointScale():F6}");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Manifest load failed. path={manifestPath} ({ex.Message})");
        }
    }

    private void LogManifestRawInfo(string manifestPath, string json)
    {
        long size = 0;
        string mtime = "n/a";
        try
        {
            FileInfo info = new FileInfo(manifestPath);
            size = info.Length;
            mtime = info.LastWriteTime.ToString("o");
        }
        catch
        {
        }

        Debug.Log($"[manifest_path] manifestFullPath={manifestPath} size={size} mtime={mtime}");

        string head = string.IsNullOrEmpty(json)
            ? string.Empty
            : json.Substring(0, Mathf.Min(200, json.Length));
        head = head.Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");
        Debug.Log($"[manifest_head] {head}");

        const string key = "\"joints_space\"";
        int idx = string.IsNullOrEmpty(json) ? -1 : json.IndexOf(key, System.StringComparison.Ordinal);
        if (idx < 0)
        {
            Debug.Log("[manifest_grep] joints_space_found=false");
            return;
        }

        int from = Mathf.Max(0, idx - 30);
        int to = Mathf.Min(json.Length, idx + key.Length + 30);
        string around = json.Substring(from, to - from).Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");
        Debug.Log($"[manifest_grep] joints_space_found=true around={around}");
    }

    private static string SafeString(string value)
    {
        return string.IsNullOrEmpty(value) ? "<empty>" : value;
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
