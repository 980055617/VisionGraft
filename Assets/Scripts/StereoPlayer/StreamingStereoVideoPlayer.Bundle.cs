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
        string extractedKeypoints3dPath = Path.Combine(cacheDir, ExtractedKeypoints3dFileName);
        string extractedOtherObjectProxiesPath = Path.Combine(cacheDir, ExtractedOtherObjectProxiesFileName);

        // Always extract fresh files after cache clear.
        {
            try
            {
                Directory.CreateDirectory(cacheDir);
                using (var fs = new FileStream(bundlePathToLoad, FileMode.Open, FileAccess.Read))
                using (var za = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    LogBundleEntries(za);

                    if (!ExtractZipEntry(za, BundleVideoEntryName, extractedVideoPath))
                    {
                        yield break;
                    }

                    if (!ExtractZipEntry(za, BundleManifestEntryName, extractedManifestPath))
                    {
                        yield break;
                    }

                    if (!ExtractZipEntry(za, BundleMetaEntryName, extractedMetaPath))
                    {
                        yield break;
                    }

                    if (!ExtractZipEntry(za, BundleKeypoints3dEntryName, extractedKeypoints3dPath))
                    {
                        yield break;
                    }

                    if (!ExtractZipEntry(za, BundleOtherObjectProxiesEntryName, extractedOtherObjectProxiesPath))
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

        if (!TryLoadManifest(extractedManifestPath))
        {
            yield break;
        }
        LoadMeta(extractedMetaPath);
        LoadBundleSidecars(extractedKeypoints3dPath, extractedOtherObjectProxiesPath);

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

    private bool TryLoadManifest(string manifestPath)
    {
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
