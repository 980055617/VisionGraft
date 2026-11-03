using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Video;

// 音の確認（2026-11-03）。セッション開始の直後、最初の練習の前に 1 回だけ出す（ユーザー「開始時だけ」）。
//
// ユーザー「同じヘッドセットでも被験者変わるときに音声なくなることがあり」「鳴らして聞こえなかったら実験者に伝えるような
// windowでいいかな」。原因は未確定（Docs/experiment-flow.md「2026-11-03 被験者が替わるときに音が出なくなることがある」）。
// 練習の動画には音声トラックが無いので、これまでは最初の本番まで無音に気づけなかった。
//
// **試行と同じ経路で鳴らす。**TrialScene の VideoPlayer と同じ Direct 出力で、StreamingAssets から取り出したファイルを
// 再生する（試行も bundle から取り出した mp4 を再生している）。AudioSource で鳴らすと Unity のミキサーと空間化
// （Meta XR Audio）を通る別の経路になり、こちらは鳴るのに動画は無音、がありうる。
//
// 音は 1.5 秒ごとの 2 音のチャイム（3 秒、ループ）。大きさは −28 LUFS で、本番 6 本の音（−24〜−46 LUFS）の中ほど。
// 作り方は scratchpad 625c2ddd の audio/make_audio_check.py。
//
// operations.csv に残すもの: audio_check_play（「音を鳴らす」）・audio_check_heard（「聞こえた」、鳴らしてからの秒数）・
// audio_check_failed（鳴らせなかった）・audio_check_skipped（実験者が飛ばした）。どれも本体の音量（Android の
// STREAM_MUSIC、今の値/最大）を付ける。音が出なかったときに本体の音量が原因かを記録から見分けるため。
public sealed partial class ExperimentController
{
    public enum AudioCheckStage
    {
        Ready,
        Playing,
        Failed,
    }

    public const string AudioCheckTitle = "音の確認";
    public const string AudioCheckPlayLabel = "音を鳴らす";
    public const string AudioCheckHeardLabel = "聞こえた";
    public const string AudioCheckRetryLabel = "もう一度鳴らす";
    public const string AudioCheckSkipLabel = "実験者用: 飛ばす";

    [Header("Audio check")]
    public bool audioCheckAtSessionStart = true;
    // StreamingAssets のファイル名。
    public string audioCheckFileName = "audio_check.mp4";

    // 鳴り始めてからこの秒数で「聞こえた」を押せるようにする（鳴る前に押されないように）。
    private const float AudioCheckHeardEnableSeconds = 1f;
    // この秒数で鳴り始めなければ、ファイルか再生の不具合として実験者に知らせる。
    private const float AudioCheckStartTimeoutSeconds = 8f;

    private GameObject audioCheckObject;
    private VideoPlayer audioCheckPlayer;
    private string audioCheckPlayerError;
    private Coroutine audioCheckRoutine;
    private float audioCheckPlayRequestedAt = -1f;

    public static string BuildAudioCheckBody(AudioCheckStage stage)
    {
        switch (stage)
        {
            case AudioCheckStage.Playing:
                return "チャイムの音が繰り返し鳴っています。\n" +
                       $"聞こえたら「{AudioCheckHeardLabel}」を押してください。\n\n" +
                       "聞こえないときは、ヘッドセットをつけたまま\n" +
                       "実験者に声をかけてください。";
            case AudioCheckStage.Failed:
                return "音を鳴らせませんでした。\n\n" +
                       "ヘッドセットをつけたまま、\n" +
                       "実験者に声をかけてください。";
            default:
                return "ヘッドセットから音が出るか確かめます。\n" +
                       $"「{AudioCheckPlayLabel}」を押してください。";
        }
    }

    private void ShowAudioCheckPanel()
    {
        phase = Phase.AudioCheck;
        PreparePanel(FullPanelSizeMeters, Vector2.zero, ExperimentPanel.DefaultLayout);
        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create(AudioCheckPlayLabel, BeginAudioCheckPlayback),
            ExperimentPanel.ButtonSpec.CreateConfirm("中止して Home へ", ReturnToHome),
        };
        panel.Show(AudioCheckTitle, BuildAudioCheckBody(AudioCheckStage.Ready), specs);
    }

    private void BeginAudioCheckPlayback()
    {
        if (phase != Phase.AudioCheck || audioCheckRoutine != null)
        {
            return;
        }

        audioCheckRoutine = StartCoroutine(RunAudioCheckPlayback());
    }

    private IEnumerator RunAudioCheckPlayback()
    {
        StopAudioCheckPlayer();
        // 「聞こえた」は鳴り始めるまで押せない。同じ画面の書き換えなので位置は保つ。
        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create(AudioCheckHeardLabel, ConfirmAudioCheckHeard, false),
            ExperimentPanel.ButtonSpec.CreateConfirm("中止して Home へ", ReturnToHome),
        };
        panel.Show(AudioCheckTitle, BuildAudioCheckBody(AudioCheckStage.Playing), specs, true);
        audioCheckPlayRequestedAt = Time.realtimeSinceStartup;
        string volume = DescribeSystemMusicVolume();
        Debug.Log($"[AudioCheck] 音を鳴らします system_volume={volume}");
        session?.RecordOperation("audio_check_play", $"system_volume={volume}");

        string path = null;
        string error = null;
        yield return CopyAudioCheckFileToCache(p => path = p, e => error = e);
        if (error == null)
        {
            StartAudioCheckPlayer(path);
        }

        float deadline = Time.realtimeSinceStartup + AudioCheckStartTimeoutSeconds;
        float playingSince = -1f;
        while (error == null)
        {
            if (audioCheckPlayerError != null)
            {
                error = audioCheckPlayerError;
                break;
            }

            if (audioCheckPlayer != null && audioCheckPlayer.isPlaying)
            {
                if (playingSince < 0f)
                {
                    playingSince = Time.realtimeSinceStartup;
                }
                if (Time.realtimeSinceStartup - playingSince >= AudioCheckHeardEnableSeconds)
                {
                    break;
                }
            }
            else if (Time.realtimeSinceStartup > deadline)
            {
                error = "start_timeout";
                break;
            }

            yield return null;
        }

        audioCheckRoutine = null;
        if (phase != Phase.AudioCheck || returningToHome)
        {
            yield break;
        }

        if (error != null)
        {
            StopAudioCheckPlayer();
            ShowAudioCheckFailedPanel(error);
            yield break;
        }

        Debug.Log($"[AudioCheck] 鳴っています（押してから {Time.realtimeSinceStartup - audioCheckPlayRequestedAt:F2}s）");
        panel.SetButtonState(0, AudioCheckHeardLabel, true);
    }

    // 試行と同じく、StreamingAssets から取り出したファイルを再生する（Android の StreamingAssets は APK の中で、
    // 試行の bundle も UnityWebRequest で読んでいる）。中身を替えたときに古い写しが残らないよう、毎回書き直す（34 KB）。
    private IEnumerator CopyAudioCheckFileToCache(Action<string> done, Action<string> fail)
    {
        string url = Path.Combine(Application.streamingAssetsPath, audioCheckFileName).Replace("\\", "/");
        string destination = Path.Combine(Application.temporaryCachePath, audioCheckFileName);
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[AudioCheck] 音のファイルを読めません: {url} | {request.error}");
                fail("read_failed");
                yield break;
            }

            try
            {
                File.WriteAllBytes(destination, request.downloadHandler.data);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AudioCheck] 音のファイルを書き出せません: {destination} | {ex.Message}");
                fail("write_failed");
                yield break;
            }
        }

        done(destination);
    }

    private void StartAudioCheckPlayer(string path)
    {
        audioCheckPlayerError = null;
        audioCheckObject = new GameObject("AudioCheckPlayer");
        audioCheckPlayer = audioCheckObject.AddComponent<VideoPlayer>();
        audioCheckPlayer.playOnAwake = false;
        // 試行の VideoPlayer と同じ設定（URL・ループ・APIOnly）。
        RuntimePlaybackController.ConfigureForApiPlayback(audioCheckPlayer);
        // TrialScene の VideoPlayer と同じ音の設定（Direct・1 トラック・音量 1・ミュートなし）。
        audioCheckPlayer.audioOutputMode = VideoAudioOutputMode.Direct;
        audioCheckPlayer.controlledAudioTrackCount = 1;
        audioCheckPlayer.EnableAudioTrack(0, true);
        audioCheckPlayer.SetDirectAudioMute(0, false);
        audioCheckPlayer.SetDirectAudioVolume(0, 1f);
        audioCheckPlayer.url = path;
        audioCheckPlayer.errorReceived += OnAudioCheckPlayerError;
        audioCheckPlayer.prepareCompleted += OnAudioCheckPlayerPrepared;
        audioCheckPlayer.Prepare();
    }

    private void OnAudioCheckPlayerPrepared(VideoPlayer source)
    {
        if (source == audioCheckPlayer)
        {
            source.Play();
        }
    }

    private void OnAudioCheckPlayerError(VideoPlayer source, string message)
    {
        if (source != audioCheckPlayer)
        {
            return;
        }

        Debug.LogError($"[AudioCheck] 再生できません: {message}");
        audioCheckPlayerError = "player_error";
    }

    // Home へ戻る・壊れるとき。待ちのコルーチンも止める（止めないと 8 秒の期限で「鳴らせませんでした」が
    // 「お待ちください」を上書きする）。
    private void StopAudioCheck()
    {
        if (audioCheckRoutine != null)
        {
            StopCoroutine(audioCheckRoutine);
            audioCheckRoutine = null;
        }
        StopAudioCheckPlayer();
    }

    private void StopAudioCheckPlayer()
    {
        if (audioCheckPlayer != null)
        {
            audioCheckPlayer.errorReceived -= OnAudioCheckPlayerError;
            audioCheckPlayer.prepareCompleted -= OnAudioCheckPlayerPrepared;
            audioCheckPlayer.Stop();
        }
        if (audioCheckObject != null)
        {
            Destroy(audioCheckObject);
        }

        audioCheckObject = null;
        audioCheckPlayer = null;
    }

    private void ConfirmAudioCheckHeard()
    {
        if (phase != Phase.AudioCheck)
        {
            return;
        }

        float playedSeconds = audioCheckPlayRequestedAt >= 0f ? Time.realtimeSinceStartup - audioCheckPlayRequestedAt : -1f;
        string volume = DescribeSystemMusicVolume();
        Debug.Log($"[AudioCheck] 聞こえた（鳴らしてから {playedSeconds:F1}s）system_volume={volume}");
        session?.RecordOperation(
            "audio_check_heard",
            $"played_sec={ExperimentCsv.Format(playedSeconds)} system_volume={volume}");
        session?.FlushLogs();
        StopAudioCheckPlayer();
        ShowWaitingPanel();
    }

    // 鳴らせなかった（ファイルが読めない・再生が始まらない）。被験者には実験者を呼んでもらい、実験者は
    // 鳴らし直すか、確認の窓を通して飛ばす。
    private void ShowAudioCheckFailedPanel(string reason)
    {
        string volume = DescribeSystemMusicVolume();
        Debug.LogWarning($"[AudioCheck] 鳴らせませんでした reason={reason} system_volume={volume}");
        session?.RecordOperation("audio_check_failed", $"reason={reason} system_volume={volume}");
        session?.FlushLogs();
        List<ExperimentPanel.ButtonSpec> specs = new List<ExperimentPanel.ButtonSpec>
        {
            ExperimentPanel.ButtonSpec.Create(AudioCheckRetryLabel, BeginAudioCheckPlayback),
            ExperimentPanel.ButtonSpec.CreateConfirm(AudioCheckSkipLabel, SkipAudioCheckByExperimenter),
            ExperimentPanel.ButtonSpec.CreateConfirm("中止して Home へ", ReturnToHome),
        };
        panel.Show(AudioCheckTitle, BuildAudioCheckBody(AudioCheckStage.Failed), specs, true);
    }

    private void SkipAudioCheckByExperimenter()
    {
        if (phase != Phase.AudioCheck)
        {
            return;
        }

        string volume = DescribeSystemMusicVolume();
        Debug.LogWarning($"[AudioCheck] 実験者が音の確認を飛ばしました system_volume={volume}");
        session?.RecordOperation("audio_check_skipped", $"system_volume={volume}");
        session?.FlushLogs();
        StopAudioCheckPlayer();
        ShowWaitingPanel();
    }

    // 本体の音量（Android の STREAM_MUSIC、「今の値/最大」）。読めなければ unknown、Editor では na。
    private static string DescribeSystemMusicVolume()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            const int StreamMusic = 3;
            using (AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (AndroidJavaObject audioManager = activity.Call<AndroidJavaObject>("getSystemService", "audio"))
            {
                int current = audioManager.Call<int>("getStreamVolume", StreamMusic);
                int max = audioManager.Call<int>("getStreamMaxVolume", StreamMusic);
                return $"{current}/{max}";
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[AudioCheck] 本体の音量を読めません: {ex.Message}");
            return "unknown";
        }
#else
        return "na";
#endif
    }
}
