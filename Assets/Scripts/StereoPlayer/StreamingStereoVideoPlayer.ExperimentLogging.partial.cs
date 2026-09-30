using UnityEngine;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // Depends on: runtime UI state flags (runtimeSettingsOpen / runtimeModelPickerOpen), bundle load flow (Bundle.cs)
    // Provides: 実験ログへの追加フック（2026-09-25、論文側の依頼 §3.1〜3.3）と、bundle 読み込みの状態の読み取り口。
    //
    // ここに置いたのは「実験のためだけに増えた記録」。プレイヤー本体の挙動は変えない。
    // ExperimentLog.Sink が無い（自由視聴・バッチ）ときは何も書かない。

    // ── bundle 読み込みの状態（ExperimentController が timeout / 失敗 / 中止の判断に使う）──

    // EnsureBundleAndPrepareVideo が途中で諦めたら true。以前は yield break で静かに終わり、
    // ExperimentController が IsVideoPlaying を永久に待っていた（Docs/architecture-audit-2026-09-18.md #1）。
    private bool bundleLoadFailed;
    private string bundleLoadFailureMessage;

    // ワーカースレッドで展開している間 true。中止で試行シーンを捨てる前に待つ
    // （走ったまま次の試行がキャッシュを消すと、書きかけのファイルと衝突する）。
    private bool bundleIoBusy;

    // 読み込んだ .svb の SHA-256 / サイズ / 出どころ（sharedStorage / streamingAssets / picker）。
    // trials.csv に書いて「どの参加者がどのファイルを見たか」を一意にする。
    private string loadedBundleSha256;
    private long loadedBundleSizeBytes;
    private string loadedBundleSource;

    public bool BundleLoadFailed
    {
        get { return bundleLoadFailed; }
    }

    public string BundleLoadFailureMessage
    {
        get { return bundleLoadFailureMessage ?? string.Empty; }
    }

    public bool IsBundleIoBusy
    {
        get { return bundleIoBusy; }
    }

    public string LoadedBundleSha256
    {
        get { return loadedBundleSha256 ?? string.Empty; }
    }

    public long LoadedBundleSizeBytes
    {
        get { return loadedBundleSizeBytes; }
    }

    public string LoadedBundleSource
    {
        get { return loadedBundleSource ?? string.Empty; }
    }

    private void FailBundleLoad(string reason)
    {
        bundleLoadFailed = true;
        bundleLoadFailureMessage = reason;
        Debug.LogError($"[Bundle] load failed: {reason}");
    }

    private void ResetBundleLoadStatus()
    {
        bundleLoadFailed = false;
        bundleLoadFailureMessage = null;
        loadedBundleSha256 = null;
        loadedBundleSizeBytes = 0;
        loadedBundleSource = null;
    }

    // ── パネルの開閉 ──
    //
    // Settings / Model パネルの開閉は複数の経路（ボタン・再生再開で自動的に閉じる・片方を開くと
    // もう片方が閉じる）で起きるので、各経路にフックを置かず、毎フレーム状態の変化を見て 1 行出す。
    private bool experimentLoggedSettingsOpen;
    private bool experimentLoggedModelPickerOpen;

    private void LogExperimentPanelStateChanges()
    {
        if (!ExperimentLog.IsActive)
        {
            experimentLoggedSettingsOpen = runtimeSettingsOpen;
            experimentLoggedModelPickerOpen = runtimeModelPickerOpen;
            return;
        }

        if (runtimeSettingsOpen != experimentLoggedSettingsOpen)
        {
            experimentLoggedSettingsOpen = runtimeSettingsOpen;
            ExperimentLog.Operation(runtimeSettingsOpen ? "panel_open" : "panel_close", "panel=settings");
        }

        if (runtimeModelPickerOpen != experimentLoggedModelPickerOpen)
        {
            experimentLoggedModelPickerOpen = runtimeModelPickerOpen;
            ExperimentLog.Operation(runtimeModelPickerOpen ? "panel_open" : "panel_close", "panel=model");
        }
    }

    // ── Screen Dist ──
    //
    // スライダーはドラッグ中に毎フレーム値を出すので、0.5 秒動きが止まったら 1 行だけ書く。
    private bool experimentScreenDistLogPending;
    private float experimentScreenDistPendingValue;
    private float experimentScreenDistChangedAt;
    private const float ExperimentScreenDistLogSettleSeconds = 0.5f;

    private void NoteExperimentScreenDistChange(float value)
    {
        experimentScreenDistLogPending = true;
        experimentScreenDistPendingValue = value;
        experimentScreenDistChangedAt = Time.unscaledTime;
    }

    // force: 試行・練習を閉じる直前に呼ぶ。沈静（0.5 秒）を待っている間に試行が終わると、
    // ExperimentLog.Sink が外れたあとに書こうとして**行が丸ごと捨てられ**、その試行を最終的に何 m で
    // 見たのかが復元できなかった（2026-09-30 の 5 回目の監査。panel_reanchored と同じ形の穴）。
    private void FlushPendingExperimentScreenDistLog(bool force = false)
    {
        if (!experimentScreenDistLogPending)
        {
            return;
        }

        if (!force && Time.unscaledTime - experimentScreenDistChangedAt < ExperimentScreenDistLogSettleSeconds)
        {
            return;
        }

        experimentScreenDistLogPending = false;
        ExperimentLog.Operation("screen_dist", $"value={ExperimentCsv.Format(experimentScreenDistPendingValue)}");
    }

    // ── モデルの割り当て ──
    //
    // 試行の最初に「どの track にどのモデルが出たか」を残す。change_model は被験者が替えたときだけなので、
    // これが無いと「試行開始時に何が出ていたか」（基準ファイル model_selection.json の値）が分からない。
    // モデルの差し替え（change_model の直後の再生成）でもインスタンスが作り直されるのでもう 1 行出る。
    private void LogExperimentModelAssigned(uint trackId, byte categoryId, GameObject prefab)
    {
        if (!ExperimentLog.IsActive)
        {
            return;
        }

        ExperimentLog.Operation(
            "model_assigned",
            $"track={trackId} category={DescribeCategoryForLog(categoryId)} prefab={(prefab != null ? prefab.name : "(none)")}");
    }

    private string DescribeCategoryForLog(byte categoryId)
    {
        if (IsCategoryPerson(categoryId))
        {
            return "person";
        }
        if (IsCategoryAnimal(categoryId))
        {
            return "animal";
        }
        if (IsCategoryOther(categoryId))
        {
            return "other";
        }
        return categoryId.ToString();
    }
}
