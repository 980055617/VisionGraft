using System.Collections;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using UnityEngine.Networking;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // bundleFileName と同名のファイルを共有ストレージから探す。
    // 探索順は bundle picker と同じ（BundlePickerSearchDirectories）。
    private static bool TryResolveBundleInSharedStorage(string fileName, out string path)
    {
        path = null;
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        string[] dirs = BuildBundleSearchDirectories();
        for (int i = 0; i < dirs.Length; i++)
        {
            string dir = dirs[i];
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            string candidate = Path.Combine(dir, fileName).Replace("\\", "/");
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }
        }

        return false;
    }


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

        // bundleFileName で指定されたものは、まず共有ストレージ
        // （/storage/emulated/0/VisionGraft など）から探す。見つからなければ
        // 従来どおり StreamingAssets（APK 内）にフォールバックする。
        //
        // これで**被験者実験も adb push した .svb を読む**ようになり、340MB の bundle を
        // APK に焼かなくて済む。事前に仕込んだ model_selection.json も同じ動画に効く。
        // エディタ・バッチでは共有ストレージが無いので必ず StreamingAssets 側に落ちる。
        if (useStreamingAssets && TryResolveBundleInSharedStorage(bundleFileName, out string sharedPath))
        {
            Debug.Log($"[Bundle] shared storage hit: {sharedPath}");
            selectedBundlePath = sharedPath;
            useStreamingAssets = false;
        }

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

        if (!ManifestLoader.TryLoad(extractedManifestPath, out manifest, out ShotBoundaries loadedShotBoundaries))
        {
            Debug.LogError($"[Bundle] Manifest load failed: {extractedManifestPath}");
            yield break;
        }
        Debug.Log($"[Bundle] Manifest loaded. Video: {extractedVideoPath}");
        ApplyLoadedShotBoundaries(loadedShotBoundaries);
        LoadMeta(extractedMetaPath);
        LoadBundleSidecars(extractedAnimalControlTargetsPath, extractedOtherObjectProxiesPath);
        LoadHumanSmplSidecar(extractedHumanSmplPath);
        RestoreTrackCustomization();

        modelModePlaybackVideoPath = extractedVideoPath.Replace("\\", "/");
        hasNormalModeVideo = File.Exists(extractedNormalModeVideoPath);
        normalModePlaybackVideoPath = hasNormalModeVideo ? extractedNormalModeVideoPath.Replace("\\", "/") : null;
        isNormalMode = ResolveInitialNormalMode();

        vp.url = isNormalMode ? normalModePlaybackVideoPath : modelModePlaybackVideoPath;

        RuntimePlaybackController.Apply(vp, RuntimePlaybackController.Command.Prepare);
    }

    // 起動時点のモードを決める。startInNormalMode は実験の StereoOnly 条件で使う。
    // SetNormalMode（再生中の切り替え）と違い、Prepare する url そのものを差し替えるので
    // 置換モデルが一瞬も表示されない。
    private bool ResolveInitialNormalMode()
    {
        if (!startInNormalMode)
        {
            return false;
        }

        if (!hasNormalModeVideo)
        {
            // 実験の対照条件が成立しない状態。黙って置換モードで再生すると条件が入れ替わった
            // データが取れてしまうので、必ず気付けるようにエラーで出す。
            Debug.LogError(
                $"[Bundle] normal mode 指定だが {BundleNormalModeVideoEntryName} が bundle にありません: " +
                $"{bundleFileName}. 置換モードで再生します。");
            return false;
        }

        return true;
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
