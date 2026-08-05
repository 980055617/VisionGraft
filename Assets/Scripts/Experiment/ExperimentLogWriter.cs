using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// 実験ログ CSV をセッション単位のフォルダに書き出す。
//
// 出力先: {persistentDataPath}/ExperimentLogs/{participantId}_{yyyyMMdd_HHmmss}/
//   trials.csv        試行ごとの 1 行（条件・開始終了時刻・視聴周回数）
//   operations.csv    被験者の操作履歴
//   headpose.csv      頭部姿勢サンプル
//   interactions.csv  インタラクティブモーションの発火
//
// Quest 実機からは adb pull で回収する（手順は Docs/experiment-flow.md）。
public sealed class ExperimentLogWriter : IDisposable
{
    public const string TrialsFileName = "trials.csv";
    public const string OperationsFileName = "operations.csv";
    public const string HeadPoseFileName = "headpose.csv";
    public const string InteractionsFileName = "interactions.csv";

    private readonly Dictionary<string, StreamWriter> writers = new Dictionary<string, StreamWriter>();
    private bool disposed;

    public ExperimentLogWriter(string sessionDirectory)
    {
        SessionDirectory = sessionDirectory;
        Directory.CreateDirectory(SessionDirectory);

        WriteHeader(TrialsFileName,
            "participant_id", "group", "video_order_pattern",
            "trial_index", "block_index", "index_in_block",
            "video", "mode", "bundle_file",
            "start_time", "end_time", "duration_sec", "loop_count", "aborted");

        WriteHeader(OperationsFileName,
            "participant_id", "trial_index", "time", "trial_elapsed_sec", "video_time_sec",
            "action", "detail");

        WriteHeader(HeadPoseFileName,
            "participant_id", "trial_index", "time", "trial_elapsed_sec", "video_time_sec",
            "pos_x", "pos_y", "pos_z", "rot_x", "rot_y", "rot_z", "rot_w");

        WriteHeader(InteractionsFileName,
            "participant_id", "trial_index", "time", "trial_elapsed_sec", "video_time_sec",
            "track_id", "kind", "detail");
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

    private void WriteHeader(string fileName, params string[] columns)
    {
        AppendRow(fileName, columns);
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

        try
        {
            string path = Path.Combine(SessionDirectory, fileName);
            StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(false));
            writers[fileName] = writer;
            return writer;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Experiment] ログファイルを開けません: {fileName} | {ex.Message}");
            return null;
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
