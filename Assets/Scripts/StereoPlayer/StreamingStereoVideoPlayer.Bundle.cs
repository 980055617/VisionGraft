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

        string extractedVideoPath = Path.Combine(cacheDir, ExtractedVideoFileName);
        string extractedManifestPath = Path.Combine(cacheDir, ExtractedManifestFileName);
        string extractedMetaPath = Path.Combine(cacheDir, ExtractedMetaFileName);
        string extractedAnimalControlTargetsPath = Path.Combine(cacheDir, ExtractedAnimalControlTargetsFileName);
        string extractedOtherObjectProxiesPath = Path.Combine(cacheDir, ExtractedOtherObjectProxiesFileName);
        string extractedHumanSmplPath = Path.Combine(cacheDir, ExtractedHumanSmplFileName);
        string extractedNormalModeVideoPath = Path.Combine(cacheDir, ExtractedNormalModeVideoFileName);

        // Always extract fresh files after cache clear.
        {
            try
            {
                Directory.CreateDirectory(cacheDir);
                using (var fs = new FileStream(bundlePathToLoad, FileMode.Open, FileAccess.Read))
                using (var za = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    LogBundleEntries(za);

                    if (!BundleExtractor.ExtractWithRequirement(za, BundleVideoEntryName, extractedVideoPath, SpatialVideoBundleEntryRequirement.Required))
                    {
                        yield break;
                    }

                    if (!BundleExtractor.ExtractWithRequirement(za, BundleManifestEntryName, extractedManifestPath, SpatialVideoBundleEntryRequirement.Required))
                    {
                        yield break;
                    }

                    if (!BundleExtractor.ExtractWithRequirement(za, BundleMetaEntryName, extractedMetaPath, SpatialVideoBundleEntryRequirement.Required))
                    {
                        yield break;
                    }

                    if (!BundleExtractor.ExtractWithRequirement(za, BundleAnimalControlTargetsEntryName, extractedAnimalControlTargetsPath, SpatialVideoBundleEntryRequirement.Optional))
                    {
                        yield break;
                    }

                    if (!BundleExtractor.ExtractWithRequirement(za, BundleOtherObjectProxiesEntryName, extractedOtherObjectProxiesPath, SpatialVideoBundleEntryRequirement.Optional))
                    {
                        yield break;
                    }

                    BundleExtractor.ExtractWithRequirement(za, BundleHumanSmplEntryName, extractedHumanSmplPath, SpatialVideoBundleEntryRequirement.Optional);
                    BundleExtractor.ExtractWithRequirement(za, BundleNormalModeVideoEntryName, extractedNormalModeVideoPath, SpatialVideoBundleEntryRequirement.Optional);
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

        if (!ManifestLoader.TryLoad(extractedManifestPath, out manifest))
        {
            yield break;
        }
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
