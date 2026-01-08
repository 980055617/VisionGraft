using System.Collections;
using System.IO;
using System.IO.Compression;
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
        bool needsBundleCopy = reExtractAlways || !File.Exists(persistentBundlePath);

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
        string extractedVideoPath = Path.Combine(cacheDir, extractedVideoFileName);
        string extractedManifestPath = Path.Combine(cacheDir, extractedManifestFileName);
        string extractedMetaPath = Path.Combine(cacheDir, extractedMetaFileName);

        bool needsExtractVideo = reExtractAlways || !File.Exists(extractedVideoPath);
        bool needsExtractManifest = reExtractAlways || !File.Exists(extractedManifestPath);
        bool needsExtractMeta = reExtractAlways || !File.Exists(extractedMetaPath);
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
            manifest = JsonUtility.FromJson<ManifestData>(json);
            if (manifest == null)
            {
                Debug.LogError($"Manifest parse failed (null). path={manifestPath}");
                return;
            }

            LogBundle($"Manifest parsed. eye_w={manifest.eye_w} eye_h={manifest.eye_h} num_frames={manifest.num_frames} fps={manifest.fps}");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Manifest load failed. path={manifestPath} ({ex.Message})");
        }
    }
}
