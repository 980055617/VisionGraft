using UnityEditor;
using UnityEngine;
using UnityEngine.Video;

// batchmode で走っている間、動画の音を必ず消す。
//
// **AudioListener.volume や EditorUtility.audioMasterMute では消えない。**
// このプロジェクトの VideoPlayer は Direct 出力（RuntimePlaybackController.ApplyMute が
// SetDirectAudioMute を呼んでいるのがその証拠）で、Direct はプラットフォームの音声へ
// 直接出るため AudioListener を経由しない。BatchPlaybackLogger は listener 側だけ
// 消していたので、計測やキャプチャのたびに音が鳴っていた（2026-09-02 指摘）。
//
// 対象は「Unity を閉じてもらって回すバッチ実行」全部。テストランナーのように
// -executeMethod を通らない経路もあるので、editor のループから定期的に掛け直す。
[InitializeOnLoad]
public static class BatchAudioMute
{
    private const double IntervalSeconds = 0.25;
    private static double nextCheck;

    static BatchAudioMute()
    {
        if (!Application.isBatchMode)
        {
            return;
        }

        EditorUtility.audioMasterMute = true;
        AudioListener.volume = 0f;
        EditorApplication.update += Tick;
    }


    private static void Tick()
    {
        if (EditorApplication.timeSinceStartup < nextCheck)
        {
            return;
        }

        nextCheck = EditorApplication.timeSinceStartup + IntervalSeconds;

        // 再生開始のたびに VideoPlayer が作り直されるので、見つけ次第かけ直す。
        // audioTrackCount は Prepare 後に確定するため、一度きりでは足りない。
        VideoPlayer[] players = Object.FindObjectsByType<VideoPlayer>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < players.Length; i++)
        {
            VideoPlayer vp = players[i];
            if (vp == null)
            {
                continue;
            }

            for (ushort track = 0; track < vp.audioTrackCount; track++)
            {
                vp.SetDirectAudioMute(track, true);
            }
        }
    }
}
