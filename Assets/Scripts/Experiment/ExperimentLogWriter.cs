using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// 実験ログ CSV をセッション単位のフォルダに書き出す。
//
// 出力先: {persistentDataPath}/ExperimentLogs/{participantId}_{yyyyMMdd_HHmmss}/
//   trials.csv        試行ごとの 1 行（条件・開始時の状態・開始終了時刻・視聴周回数）
//   operations.csv    被験者の操作履歴と試行の状態変化
//   headpose.csv      頭部姿勢サンプル（試行中のみ）
//   interactions.csv  インタラクティブモーションの発火
//   perf.csv          1 秒窓の描画レート・動画の進み・コントローラの動き（試行中のみ）
//
// Quest 実機からは adb pull で回収する（手順は Docs/experiment-flow.md）。
public sealed class ExperimentLogWriter : IDisposable
{
    public const string TrialsFileName = "trials.csv";
    public const string OperationsFileName = "operations.csv";
    public const string HeadPoseFileName = "headpose.csv";
    public const string InteractionsFileName = "interactions.csv";
    // 1 秒ごとの描画レート・動画の進み・コントローラの動き（2026-09-25 追加。ExperimentPerfAccumulator）。
    public const string PerfFileName = "perf.csv";

    private readonly Dictionary<string, StreamWriter> writers = new Dictionary<string, StreamWriter>();
    private bool disposed;

    public ExperimentLogWriter(string sessionDirectory)
    {
        SessionDirectory = sessionDirectory;
        Directory.CreateDirectory(SessionDirectory);

        // 2026-09-25 に列を足した（bundle_sha256 〜 bone_length_correction、video_played_sec、abort_reason）。
        // 列の意味は Docs/experiment-flow.md「ログ」。
        WriteHeader(TrialsFileName,
            "participant_id", "group", "video_order_pattern",
            "trial_index", "block_index", "index_in_block",
            "video", "mode", "bundle_file",
            "bundle_sha256", "bundle_bytes", "bundle_source", "app_build",
            "motion_enabled", "monocular", "screen_dist_start", "bone_length_correction", "display_hz",
            "start_time", "end_time", "duration_sec", "video_played_sec", "loop_count",
            "aborted", "abort_reason");

        WriteHeader(OperationsFileName,
            "participant_id", "trial_index", "time", "trial_elapsed_sec", "video_time_sec",
            "action", "detail");

        WriteHeader(HeadPoseFileName,
            "participant_id", "trial_index", "time", "trial_elapsed_sec", "video_time_sec",
            "pos_x", "pos_y", "pos_z", "rot_x", "rot_y", "rot_z", "rot_w");

        WriteHeader(InteractionsFileName,
            "participant_id", "trial_index", "time", "trial_elapsed_sec", "video_time_sec",
            "track_id", "kind", "detail");

        // window_sec / frames は「その行が何秒ぶんの集計か」。窓は 1 秒で閉じるが、1 フレームが長いと
        // それより伸びる。無いと fps 以外の列（long_frames 等）を比較できない（2026-09-25 の監査）。
        // video_seek_jumps は前向きのシークで飛んだ回数。advanced / skips からは除いてある。
        WriteHeader(PerfFileName,
            "participant_id", "trial_index", "time", "trial_elapsed_sec", "video_time_sec",
            "video_frame", "window_sec", "frames", "fps", "frame_time_max_ms", "long_frames",
            "video_frames_advanced", "video_frame_skips", "video_seek_jumps", "video_time_advanced_sec", "video_playing",
            "ctrl_moved_m", "ctrl_trigger_frames", "ctrl_button_frames");
    }

    public string SessionDirectory { get; }

    public static string BuildSessionDirectory(string rootDirectory, string participantId, DateTime startedAt)
    {
        string safeId = SanitizeForFileName(participantId);
        string stamp = startedAt.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(rootDirectory, $"{safeId}_{stamp}");
    }

    public static string DefaultRootDirectory
    {
        get { return Path.Combine(Application.persistentDataPath, "ExperimentLogs"); }
    }

    // 参加者 ID は実験者の手入力なので、パス区切りや無効文字が混ざり得る。
    public static string SanitizeForFileName(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "unknown";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        StringBuilder builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            builder.Append(Array.IndexOf(invalid, c) >= 0 || c == ' ' ? '_' : c);
        }

        string sanitized = builder.ToString();
        return string.IsNullOrEmpty(sanitized) ? "unknown" : sanitized;
    }

    public void AppendRow(string fileName, params string[] fields)
    {
        StreamWriter writer = ResolveWriter(fileName);
        if (writer == null)
        {
            return;
        }

        try
        {
            writer.WriteLine(ExperimentCsv.BuildRow(fields));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Experiment] ログ書き込みに失敗: {fileName} | {ex.Message}");
        }
    }

    // 試行の切れ目とアプリ終了時に呼ぶ。実機がクラッシュしても直前の試行までは
    // 必ず残るようにするため、バッファに溜めっぱなしにしない。
    public void Flush()
    {
        foreach (KeyValuePair<string, StreamWriter> kv in writers)
        {
            try
            {
                kv.Value.Flush();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Experiment] ログ flush に失敗: {kv.Key} | {ex.Message}");
            }
        }
    }

    // ファイルごとの列名。**開いたときに書く**（ResolveWriter）。ここで覚えておかないと、最初に開けなかった
    // ファイルを試行の切れ目で開き直したときにヘッダ無しの CSV になり、解析側が 1 行目を列名として読む
    // （2026-09-29 の 4 回目の監査）。
    private readonly Dictionary<string, string[]> headerColumns = new Dictionary<string, string[]>();

    private void WriteHeader(string fileName, params string[] columns)
    {
        headerColumns[fileName] = columns;
        ResolveWriter(fileName);
        Flush();
    }

    private StreamWriter ResolveWriter(string fileName)
    {
        if (disposed)
        {
            return null;
        }

        if (writers.TryGetValue(fileName, out StreamWriter existing))
        {
            return existing;
        }

        // 一度開けなかったファイルは二度と試さない。試すと headpose の 15 Hz ぶん毎秒十数回の例外とログが出続ける。
        if (failedFiles.Contains(fileName))
        {
            return null;
        }

        try
        {
            string path = Path.Combine(SessionDirectory, fileName);
            StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(false));
            writers[fileName] = writer;
            if (headerColumns.TryGetValue(fileName, out string[] columns) && columns != null)
            {
                writer.WriteLine(ExperimentCsv.BuildRow(columns));
            }
            return writer;
        }
        catch (Exception ex)
        {
            failedFiles.Add(fileName);
            Debug.LogError($"[Experiment] ログファイルを開けません: {fileName} | {ex.Message}（以後このファイルへは書きません）");
            return null;
        }
    }

    private readonly HashSet<string> failedFiles = new HashSet<string>();

    // 開けなかったファイルをもう一度だけ試す。試行・練習の切れ目で呼ぶ（ExperimentSession）。
    // 一時的な失敗（ストレージの一瞬の不調）でセッションの残り全部を失わないため。失敗が続くなら
    // 試行ごとに 1 回ずつエラーが出るだけで、以前のように 15 Hz で例外が続くことはない。
    public void AllowRetryOfFailedFiles()
    {
        if (failedFiles.Count > 0)
        {
            Debug.LogWarning($"[Experiment] 開けなかったログファイルを次の書き込みで再試行します: {string.Join(", ", failedFiles)}");
            failedFiles.Clear();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (KeyValuePair<string, StreamWriter> kv in writers)
        {
            try
            {
                kv.Value.Flush();
                kv.Value.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Experiment] ログクローズに失敗: {kv.Key} | {ex.Message}");
            }
        }

        writers.Clear();
    }
}
