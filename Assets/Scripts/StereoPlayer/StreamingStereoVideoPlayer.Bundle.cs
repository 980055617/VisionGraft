using System.Collections;
using System.IO;
using System.IO.Compression;
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

        // Use Android's getCacheDir() for truly internal storage accessible to the media codec
        string cacheDir = GetSvbCacheDir();
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

        string extractedVideoPath = Path.Combine(cacheDir, ExtractedVideoFileName);
        string extractedManifestPath = Path.Combine(cacheDir, ExtractedManifestFileName);
        string extractedMetaPath = Path.Combine(cacheDir, ExtractedMetaFileName);
        string extractedAnimalControlTargetsPath = Path.Combine(cacheDir, ExtractedAnimalControlTargetsFileName);
        string extractedOtherObjectProxiesPath = Path.Combine(cacheDir, ExtractedOtherObjectProxiesFileName);
        string extractedHumanSmplPath = Path.Combine(cacheDir, ExtractedHumanSmplFileName);
        string extractedNormalModeVideoPath = Path.Combine(cacheDir, ExtractedNormalModeVideoFileName);

        bool useStreamingAssets = string.IsNullOrEmpty(selectedBundlePath);
        byte[] streamingBytes = null;

        if (useStreamingAssets)
        {
            string streamingBundleUrl = Path.Combine(Application.streamingAssetsPath, bundleFileName);
            streamingBundleUrl = streamingBundleUrl.Replace("\\", "/");
            Debug.Log($"[Bundle] Requesting: {streamingBundleUrl}");
            using (UnityWebRequest request = UnityWebRequest.Get(streamingBundleUrl))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"[Bundle] Download failed: {request.result} | {request.error} | URL: {streamingBundleUrl}");
                    yield break;
                }

                streamingBytes = request.downloadHandler.data;
                Debug.Log($"[Bundle] Downloaded {streamingBytes?.Length} bytes (in-memory, no disk write)");
            }
        }
        else
        {
            if (!File.Exists(selectedBundlePath))
            {
                Debug.LogError($"[Bundle] File not found: {selectedBundlePath}");
                yield break;
            }
            Debug.Log($"[Bundle] Opening: {selectedBundlePath}");
        }

        try
        {
            Directory.CreateDirectory(cacheDir);

            if (useStreamingAssets)
            {
                using (var ms = new MemoryStream(streamingBytes))
                using (var za = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    if (!ExtractBundleEntries(za, extractedVideoPath, extractedManifestPath, extractedMetaPath,
                            extractedAnimalControlTargetsPath, extractedOtherObjectProxiesPath,
                            extractedHumanSmplPath, extractedNormalModeVideoPath))
                    {
                        yield break;
                    }
                }
            }
            else
            {
                using (var fs = new FileStream(selectedBundlePath, FileMode.Open, FileAccess.Read))
                using (var za = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    if (!ExtractBundleEntries(za, extractedVideoPath, extractedManifestPath, extractedMetaPath,
                            extractedAnimalControlTargetsPath, extractedOtherObjectProxiesPath,
                            extractedHumanSmplPath, extractedNormalModeVideoPath))
                    {
                        yield break;
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[Bundle] Extraction failed: {ex.Message}");
            yield break;
        }

        if (!File.Exists(extractedVideoPath))
        {
            Debug.LogError($"[Bundle] Extracted video not found: {extractedVideoPath}");
            yield break;
        }

        if (!ManifestLoader.TryLoad(extractedManifestPath, out manifest))
        {
            Debug.LogError($"[Bundle] Manifest load failed: {extractedManifestPath}");
            yield break;
        }
        Debug.Log($"[Bundle] Manifest loaded. Video: {extractedVideoPath}");
        LoadMeta(extractedMetaPath);
        LoadBundleSidecars(extractedAnimalControlTargetsPath, extractedOtherObjectProxiesPath);
        LoadHumanSmplSidecar(extractedHumanSmplPath);

        modelModePlaybackVideoPath = extractedVideoPath.Replace("\\", "/");
        hasNormalModeVideo = File.Exists(extractedNormalModeVideoPath);
        normalModePlaybackVideoPath = hasNormalModeVideo ? extractedNormalModeVideoPath.Replace("\\", "/") : null;
        isNormalMode = false;

        vp.url = modelModePlaybackVideoPath;

        RuntimePlaybackController.Apply(vp, RuntimePlaybackController.Command.Prepare);
    }

    private bool ExtractBundleEntries(ZipArchive za,
        string videoPath, string manifestPath, string metaPath,
        string animalControlTargetsPath, string otherObjectProxiesPath,
        string humanSmplPath, string normalModeVideoPath)
    {
        LogBundleEntries(za);

        if (!BundleExtractor.ExtractWithRequirement(za, BundleVideoEntryName, videoPath, SpatialVideoBundleEntryRequirement.Required))
            return false;
        if (!BundleExtractor.ExtractWithRequirement(za, BundleManifestEntryName, manifestPath, SpatialVideoBundleEntryRequirement.Required))
            return false;
        if (!BundleExtractor.ExtractWithRequirement(za, BundleMetaEntryName, metaPath, SpatialVideoBundleEntryRequirement.Required))
            return false;
        if (!BundleExtractor.ExtractWithRequirement(za, BundleAnimalControlTargetsEntryName, animalControlTargetsPath, SpatialVideoBundleEntryRequirement.Optional))
            return false;
        if (!BundleExtractor.ExtractWithRequirement(za, BundleOtherObjectProxiesEntryName, otherObjectProxiesPath, SpatialVideoBundleEntryRequirement.Optional))
            return false;

        BundleExtractor.ExtractWithRequirement(za, BundleHumanSmplEntryName, humanSmplPath, SpatialVideoBundleEntryRequirement.Optional);
        BundleExtractor.ExtractWithRequirement(za, BundleNormalModeVideoEntryName, normalModeVideoPath, SpatialVideoBundleEntryRequirement.Optional);
        return true;
    }

    private static string GetSvbCacheDir()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var dir = activity.Call<AndroidJavaObject>("getCacheDir"))
            {
                string internalPath = dir.Call<string>("getAbsolutePath");
                Debug.Log($"[Bundle] Internal cache path: {internalPath}");
                return Path.Combine(internalPath, "svb_cache");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[Bundle] getCacheDir failed ({ex.Message}), falling back to temporaryCachePath");
        }
#endif
        return Path.Combine(Application.temporaryCachePath, "svb_cache");
    }

    private void LogBundleEntries(ZipArchive za)
    {
        if (za == null)
        {
            return;
        }

        try
        {
            for (int i = 0; i < za.Entries.Count; i++)
            {
                ZipArchiveEntry entry = za.Entries[i];
                if (entry != null)
                {
                    Debug.Log($"SVB entry: {entry.FullName} ({entry.Length} bytes)");
                }
            }
        }
        catch
        {
        }
    }

}
