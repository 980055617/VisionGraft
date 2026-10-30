using System;
using System.Collections.Generic;

// 操作チュートリアルの進行役。UI は持たず、ExperimentController がこの状態をパネルに描く。
//
// 各ブロックの直前に、そのブロックの表示条件に合わせた内容を出す（2026-09-11 の設計）:
//   A Monocular（必ず最初）: トリガーでボタンを押す → A ボタンで一時停止 → 再開 → シークバーをドラッグ
//                           → 画面の上に出る「視聴を終了」を押す（次の動画に移るときの操作をここで覚える）
//   B StereoOnly           : 立体で見える説明だけ。操作は同じ。「視聴を終了」を押して終わる
//   C ModelReplaced        : モデルが自分から動く例を見る → Model ボタンでモデルを替える
//                           → モデルを掴んで回す → 「視聴を終了」
//                           （2026-09-25 指示「インタラクションモードの切り替えもできて、
//                           そのモードの説明の例も見せたい」。並びは 2026-09-28 に入れ替えた。
//                           掴んで回すは 2026-10-01 に追加（ユーザー「使ってもらうのでチュートリアルをつけたい」）。
//                           Settings の Motion の切り替えは 2026-10-30 に外した（ユーザー「settingはもう使わないので，
//                           チュートリアルから外し，実験でも表示しない」）。ResolveSequence）
//
// 検出は ExperimentLog の sink を横取りして行う（プレイヤー側には手を入れない）。
// 受け取った操作は内側の sink（セッション）へそのまま流すので、チュートリアル中の
// 操作も operations.csv に残る（trial_index = -1）。
//
// 各段階は「済んだかどうか」で持ち、現在の段階はその列の最初の未完了項目。
// 順番どおりでなくても済んだ操作は数える。resume だけは pause の後でないと数えない。置換ありの 1/3〜3/3 は
// その段階を表示している間の操作だけ数える（説明が一度も出ないまま進まないように）。
// 段階を個別に飛ばす手段は置かない（実際に操作しないと進めない）。練習全体は実験者用のボタンで
// いつでも終えられる（ExperimentController が出す。completed=0 と段階名が記録に残る）。
public sealed class ExperimentTutorial : IExperimentLogSink
{
    public enum Step
    {
        PressButton,
        PausePlayback,
        ResumePlayback,
        Seek,
        ChangeModel,
        WatchMotion,
        GrabRotate,
        Done,
    }

    private readonly IExperimentLogSink inner;
    private readonly ExperimentDisplayMode mode;
    private readonly Step[] sequence;
    private readonly HashSet<Step> completed = new HashSet<Step>();

    // モデルが出ている track。model_assigned と change_model の detail から拾う。
    // 全部「表示しない」にされると、その track は配置もモーションのスケジュールも走らないので
    // WatchMotion が永久に待つ（2026-09-25 の監査 D1）。0 個になったら文面で戻し方を出す。
    private readonly HashSet<string> tracksWithVisibleModel = new HashSet<string>();
    private bool sawModelEvent;

    // 「表示しない」を選んだときに detail へ入る prefab 名（StreamingStereoVideoPlayer.HiddenModelName）。
    private const string HiddenPrefabToken = "prefab=(none)";

    // 掴んで回す段を済ませる左右の回転の最小量（度）。体が回ったと目で分かる量。記録のノイズの閾値（0.5°、EndGrabRotate）とは別。
    private const float GrabRotateMinYawDegrees = 10f;

    // "key=value" の並び（空白区切り）から数値を 1 つ読む。無い・読めないなら false。
    private static bool TryReadDetailFloat(string detail, string key, out float value)
    {
        value = 0f;
        int start = detail.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += key.Length;
        int end = detail.IndexOf(' ', start);
        string token = end < 0 ? detail.Substring(start) : detail.Substring(start, end - start);
        return float.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    // 被験者が A ボタンで動画を止めているか。Random イベントは動画が動いている間しか発火しないので、
    // 止めたまま「1 回出るまで見ていてください」を待たせると永久に出ない。文面で戻し方を出す
    // （2026-09-29 の 3 回目の監査）。pause / resume の操作ログから追う。
    private bool videoPausedByParticipant;

    // 例のモーションが走っている（random_* を受けてから、その動画の一時停止が解けるまで）。
    // 走っている間は「動いています」の文面にし、**終わってから**段階を済ませる。発火した瞬間に
    // 次の段階へ進めると、モデルが動いている最中にパネルが差し替わって説明に目を取られ、肝心の
    // 例を見ない（2026-09-29 の 3 回目の監査）。
    private bool motionExampleRunning;

    // manualRotationResetAtCut（2026-10-12、同日夜にユーザーが採用し既定 ON）が ON のとき、掴んで回す段の最後の行を「場面が切り替わると元に戻る」にする。ExperimentController が試行シーンのプレイヤーから写す。
    public bool ManualRotationResetsAtCut { get; set; }

    public ExperimentTutorial(IExperimentLogSink inner, ExperimentDisplayMode mode)
    {
        this.inner = inner;
        this.mode = mode;
        sequence = ResolveSequence(mode);
    }

    // ブロックの表示条件 → 段階の列（Done を含む）。
    public static Step[] ResolveSequence(ExperimentDisplayMode mode)
    {
        switch (mode)
        {
            case ExperimentDisplayMode.Monocular:
                return new[] { Step.PressButton, Step.PausePlayback, Step.ResumePlayback, Step.Seek, Step.Done };
            case ExperimentDisplayMode.ModelReplaced:
                // WatchMotion を先頭に置く。後ろに置くと、モデルを選んでいる最中（練習の発火間隔は 3〜6 秒）に
                // 先に発火して段階が済んでしまい、**「モデルが自分から動く」の説明が一度も出ないまま**
                // 動画だけが不意に止まる（2026-09-25 の監査 F1）。先頭なら説明を読んでいる間に例が出る。
                // Motion の切り替え（Settings）の段は 2026-10-30 に外した。前は GrabRotate をその段の後ろに置き、
                // 参加者が Motion を切ってから掴ませていた（掴もうとしている最中に練習の発火（3〜6 秒おき）が来て、
                // モデルが動き出し動画も止まるため）。今は例を見た後は練習の中で新しく動かさない
                // （ExperimentController.HoldTutorialMotionAfterExample）。
                return new[] { Step.WatchMotion, Step.ChangeModel, Step.GrabRotate, Step.Done };
            default:
                return new[] { Step.Done };
        }
    }

    // 段階が変わったとき。パネルの作り直しに使う。
    public event Action Changed;

    // Done を除いた段階数（見出しの「n/N」用）。
    public int StepCount
    {
        get { return sequence.Length - 1; }
    }

    public Step CurrentStep
    {
        get
        {
            for (int i = 0; i < sequence.Length; i++)
            {
                if (sequence[i] != Step.Done && !completed.Contains(sequence[i]))
                {
                    return sequence[i];
                }
            }

            return Step.Done;
        }
    }

    // 現在の段階が列の何番目か（1 始まり）。Done なら StepCount + 1。
    public int CurrentStepNumber
    {
        get { return Array.IndexOf(sequence, CurrentStep) + 1; }
    }

    public bool IsDone
    {
        get { return CurrentStep == Step.Done; }
    }

    // 自分から動く例を最後まで見たか（置換ありの 1/3 が済んだ）。練習ではこの後、新しく動かさない（ExperimentController）。
    public bool MotionExampleSeen
    {
        get { return completed.Contains(Step.WatchMotion); }
    }

    // 「次へ」ボタン。ボタンを押せたこと自体が課題なので、押されたら済みにする。
    // それ以外の段階は操作ログでしか進まない。
    public void CompleteCurrentStep()
    {
        if (CurrentStep != Step.PressButton)
        {
            return;
        }

        SetDone(Step.PressButton);
    }

    public string Title
    {
        get
        {
            // 被験者向けの語は「練習」で統一する（待機画面・ボタンも同じ。「チュートリアル」は 10 文字で
            // ボタン幅に収まらず 2 行目が切れうる。2026-09-29 の 3 回目の監査）。「ブロック」も出さない。
            Step step = CurrentStep;
            if (step == Step.Done)
            {
                return StepCount == 0 ? "次の動画の説明" : "練習 終了";
            }

            return $"練習 {CurrentStepNumber}/{StepCount}";
        }
    }

    public string Body
    {
        get
        {
            // 1 行 24 文字・5 行以内（CompactLargeTextLayout の本文 44px に収まる長さ）。
            switch (CurrentStep)
            {
                case Step.PressButton:
                    return
                        "光線を下の「次へ」に合わせ、\n" +
                        "人差し指のトリガーを引いてください。\n\n" +
                        "画面のボタンはこの操作で押せます。";
                case Step.PausePlayback:
                    return
                        "右手の A ボタンを押すと\n" +
                        "動画が止まります。\n" +
                        "（左手なら X ボタン）\n" +
                        "押してみてください。";
                case Step.ResumePlayback:
                    return
                        "動画が止まりました。\n\n" +
                        "もう一度 A ボタンを押すと\n" +
                        "再生が再開します。";
                case Step.Seek:
                    return
                        "下のバーにあるつまみを\n" +
                        "光線で指してトリガーを引いたまま\n" +
                        "左右に動かすと、動画の位置を\n" +
                        "変えられます。動かしてみてください。";
                case Step.ChangeModel:
                    // 2026-10-24 から Model ボタンは一覧の窓を出さず、2 組のモデル（既定1・既定2）を切り替えるだけ
                    // （ユーザー「被験者実験に関してはmodelを押すとそのwindowが出てくるのではなくモデルを既定1,既定2で
                    // 切り替えれるようにするだけでいいです」。ExperimentModelPresets）。
                    return
                        "モデルは 2 種類あります。\n" +
                        "下のバーの「Model」ボタンを押すと\n" +
                        "もう一方のモデルに替わります。\n" +
                        "押してみてください（もう一度押すと戻ります）。";
                case Step.WatchMotion:
                    // モデルを全部消されていると、そもそも動く対象が無い。戻し方を先に出す。
                    if (AllModelsHidden)
                    {
                        return AllModelsHiddenBody;
                    }

                    // 止めたままでは出ない。
                    if (videoPausedByParticipant)
                    {
                        return
                            "動画が止まっています。\n" +
                            "A ボタンを押して再開してください。\n" +
                            "（モデルは動画が動いているときに\n" +
                            "動きます）";
                    }

                    if (motionExampleRunning)
                    {
                        return
                            "モデルが動いています。\n" +
                            "動くあいだ動画は自動で止まります。\n" +
                            "終わるまで見ていてください。";
                    }

                    return
                        $"次の {ExperimentPlan.TrialsPerBlock} 本では動画の人や動物が\n" +
                        "3D モデルに置き換わり、ときどき自分から\n" +
                        "動きます。動くあいだ動画は止まります。\n" +
                        "1 回動くまで見ていてください。";
                case Step.GrabRotate:
                    // 掴んで回す（GrabRotate.partial.cs）。置換ありの試行で使えるが、2026-10-01 まで練習に
                    // 一度も出てこず、偶然トリガーを引いた人だけが気づく状態だった（ユーザー「使ってもらうので
                    // チュートリアルをつけたい」）。放した時点の向きは、OFF では動画全体、manualRotationResetAtCut が ON ならその場面（本物のカットで区切った区間）に効く（PersistManualYaw）。
                    if (AllModelsHidden)
                    {
                        return AllModelsHiddenBody;
                    }

                    return
                        "モデルは手で向きを変えられます。\n" +
                        "光線をモデルに合わせ、トリガーを引いたまま\n" +
                        "コントローラを左右に回してください。\n" +
                        "掴んでいる間は動画が止まります。\n" +
                        (ManualRotationResetsAtCut ? "向きは場面が切り替わると元に戻ります。" : "回した向きはそのまま残ります。");
                default:
                    return ResolveDoneBody();
            }
        }
    }

    private string ResolveDoneBody()
    {
        switch (mode)
        {
            case ExperimentDisplayMode.Monocular:
                // 本番の「視聴を終了」は最短視聴時間が過ぎるまで押せないが、残り時間も
                // その説明も画面には出さない（実験者が口頭で伝える。2026-09-11 指示）。
                // 「押すと次の動画に進みます」は事実と違う（待機画面に戻り、もう一度「この動画を開始」を
                // 押す必要がある）。本番でもボタンの位置が同じであることを伝える（2026-09-30 の 5 回目の監査）。
                return
                    "動画は何回でも好きなだけ見られます。\n" +
                    "見終わったら、画面の上の\n" +
                    "「視聴を終了」を押してください。\n" +
                    "本番の動画でも同じ場所に出ます。\n" +
                    "押すと練習が終わります。";
            case ExperimentDisplayMode.StereoOnly:
                // **「操作はこれまでと同じです」と書かない。**群 B ではこの直前が置換ありで、
                // 立体のみのブロックでは「Model」ボタンが消える（2026-09-30 の 5 回目の監査）。
                // 両群で真になる書き方にする。
                return
                    $"次の {ExperimentPlan.TrialsPerBlock} 本は立体（奥行きあり）で\n" +
                    "見えます。\n" +
                    "操作は A ボタンと下のつまみです。\n" +
                    "見終わったら画面の上の\n" +
                    "「視聴を終了」を押してください。";
            default:
                // 置換ありでできること 2 つ（切り替える・回す）をまとめて言う（Motion の切り替えは 2026-10-30 に外した）。
                // 「次の 6 本」（1 ブロックの本数）と書くのは、群 B では置換ありの後に立体のみが来て、そこでは
                // Model も掴む対象も無いため（WatchMotion の「次の 6 本では…」と同じ言い方）。
                return
                    $"操作は以上です。次の {ExperimentPlan.TrialsPerBlock} 本では、モデルの\n" +
                    "切り替えと、掴んで回すことが\n" +
                    "いつでもできます。\n" +
                    "見終わったら画面の上の\n" +
                    "「視聴を終了」を押してください。";
        }
    }

    // モデルが全部「表示しない」になっている（動く対象も掴む対象も無い）。
    private bool AllModelsHidden
    {
        get { return sawModelEvent && tracksWithVisibleModel.Count == 0; }
    }

    private const string AllModelsHiddenBody =
        "モデルが全部「表示しない」に\n" +
        "なっています。下のバーの「Model」で\n" +
        "モデルを 1 つ選んで表示してください。";

    // tutorial_end の detail に書く 1 行。
    public string DescribeResult()
    {
        return $"completed={(IsDone ? 1 : 0)} step={CurrentStep} mode={mode}";
    }

    // ── IExperimentLogSink（プレイヤーからの操作を横取りして段階を進める）──

    public void RecordOperation(string action, string detail)
    {
        inner?.RecordOperation(action, detail);

        switch (action)
        {
            case "pause":
                SetDone(Step.PausePlayback);
                SetVideoPausedByParticipant(true);
                break;
            case "pause_auto":
                // パネル・編集・モデルを掴んだことで止まった。被験者から見れば「止まっている」のは同じなので、
                // 置換ありの練習では A ボタンで戻す案内を出す（2026-09-29 の 4 回目の監査。以前はこの経路で
                // 止まると案内が出ず、モーションは再生中しか発火しないので永久に待つことがあった）。
                SetVideoPausedByParticipant(true);
                break;
            case "resume":
                // 止めていないのに resume だけ来ることはないはずだが、来ても数えない。
                if (completed.Contains(Step.PausePlayback))
                {
                    SetDone(Step.ResumePlayback);
                }
                SetVideoPausedByParticipant(false);
                break;
            case "resume_auto":
                // パネルを閉じた・モーションが終わったときの自動再開。被験者の停止はもう残っていない。
                SetVideoPausedByParticipant(false);
                break;
            case "seek":
                SetDone(Step.Seek);
                break;
            case "change_model":
                NoteModelVisibility(detail);
                // 「表示しない」（prefab=(none)）はモデルを替えたことにしない（2026-10-10）。数えると替え方を覚えないまま
                // 2/4 が済み、1 体だけ消した場合は以降の段に案内も出なかった（AllModelsHidden は全部消えたときだけ真）。
                // **2/4 を表示している間の切り替えだけ数える**（2026-10-24、ほかの段と同じ）。Model ボタンが 1 回押すだけの切り替えになり、
                // 1/4 の間にうっかり押すと 2/4 が先に済んで、切り替えの説明（2 種類あること・もう一度押すと戻ること）が一度も出なかった。
                if (CurrentStep == Step.ChangeModel &&
                    (detail == null || detail.IndexOf(HiddenPrefabToken, StringComparison.Ordinal) < 0))
                {
                    SetDone(Step.ChangeModel);
                }
                break;
            case "model_assigned":
                // 試行（練習）の最初にどの track にモデルが出たか。段階は進めない。
                NoteModelVisibility(detail);
                break;
            case "change_rotation":
                // 掴んで回して放した（GrabRotate.partial.cs が op=grab で出す。0.5° 未満しか回していない掴みは出ない）。
                // Model パネルのボタンで向きを戻した等（op=grab 以外）は数えない。
                // **その段階を表示している間だけ数える。** 先回りで済ませると、説明が一度も出ないまま
                // 練習が終わる（WatchMotion と同じ。ほかの段階でうっかりトリガーを引いて回してしまうことがある）。
                // **左右（yaw）に回したときだけ数える**（2026-10-10）。人・動物の体は手動の回転の yaw にしか付いて回らない
                // （humanFollowManualRotationAllAxes は既定 OFF）。手首をひねる（roll）だけでは体はほとんど回らないのに
                // 段階が済んでいた。dyaw は掴み始めからの yaw の変化（GrabRotate.partial.cs の EndGrabRotate）。dyaw の無い
                // 記録（10/09 以前の形）は今までどおり数える。
                if (CurrentStep == Step.GrabRotate && detail != null && detail.Contains("op=grab") &&
                    (!TryReadDetailFloat(detail, "dyaw=", out float yawChange) || Math.Abs(yawChange) >= GrabRotateMinYawDegrees))
                {
                    SetDone(Step.GrabRotate);
                }
                break;
        }
    }

    public void RecordInteraction(uint trackId, string kind, string detail)
    {
        inner?.RecordInteraction(trackId, kind, detail);

        if (CurrentStep != Step.WatchMotion || kind == null)
        {
            return;
        }

        // モデルが自分から動き始めた（Random イベント。frame-out も一応受けるが、現行の再生経路では
        // 発火しないことを 2026-09-25 の監査で確認した。予備として残す）。
        // **その段階を表示している間の発火だけ数える。** 先回りで済ませると説明が出ないまま次へ進む。
        if (kind.StartsWith("random_", StringComparison.Ordinal) || kind == "system_frameout")
        {
            if (!motionExampleRunning)
            {
                motionExampleRunning = true;
                Changed?.Invoke();
            }
            return;
        }

        // **済ませるのは動きが終わってから。** 発火した瞬間に次の段階へ進めると、モデルが動いている最中に
        // パネルが差し替わって説明に目を取られ、肝心の例を見ない。
        // 判定に使うのは motion_end（プレイヤーが動きの終わりに必ず 1 行出す）。以前は video_pause_end を
        // 見ていたが、被験者が A で戻したときの `released_by=manual_resume` でも来るので、動いている最中に
        // 進んでしまった（2026-09-29 の 4 回目の監査）。
        if (kind != "motion_end")
        {
            return;
        }

        bool wasRunning = motionExampleRunning;
        motionExampleRunning = false;
        // **開始（random_*）を見ていない終わりは数えない。**前の段階で発火したものの終わりや、
        // 試行を閉じるときの打ち切りで済ませてしまわないため。
        if (!wasRunning)
        {
            return;
        }

        if (detail != null && detail.Contains("reason=completed"))
        {
            SetDone(Step.WatchMotion);
            return;
        }

        // 途中で止まった（Motion OFF・モデル非表示・差し替え・track がフレームから消えた）。
        // 例を見たことにはしないので、もう一度待たせる。
        if (wasRunning)
        {
            Changed?.Invoke();
        }
    }

    private void SetVideoPausedByParticipant(bool paused)
    {
        if (videoPausedByParticipant == paused)
        {
            return;
        }

        videoPausedByParticipant = paused;
        // 文面が変わるのは WatchMotion を表示している間だけ（他の段階では A ボタンの案内は出さない）。
        if (CurrentStep == Step.WatchMotion && !AllModelsHidden)
        {
            Changed?.Invoke();
        }
    }

    public void RecordVideoLoop()
    {
        inner?.RecordVideoLoop();
    }

    // detail（"track=1 category=person prefab=36_LabradorDog"）から、その track にモデルが出ているかを覚える。
    // 「表示しない」は prefab=(none)。表示が 0 個になった／戻ったところで文面が変わるのでパネルを作り直す。
    private void NoteModelVisibility(string detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return;
        }

        int trackAt = detail.IndexOf("track=", StringComparison.Ordinal);
        if (trackAt < 0)
        {
            return;
        }

        int valueAt = trackAt + "track=".Length;
        int end = detail.IndexOf(' ', valueAt);
        string track = end < 0 ? detail.Substring(valueAt) : detail.Substring(valueAt, end - valueAt);
        if (track.Length == 0)
        {
            return;
        }

        // 「全部消えている」の案内が出るかどうかが変わったときだけ Changed（パネルの作り直しは
        // ボタンの押下抑止を伴うので、文面が変わらないのに作り直さない）。
        bool hintBefore = AllModelsHidden;
        if (detail.IndexOf(HiddenPrefabToken, StringComparison.Ordinal) >= 0)
        {
            tracksWithVisibleModel.Remove(track);
        }
        else
        {
            tracksWithVisibleModel.Add(track);
        }

        sawModelEvent = true;
        bool hintAfter = tracksWithVisibleModel.Count == 0;
        // 案内を出すのは、モデルが要る段階（自分から動く例を見る・掴んで回す）を表示している間。
        if (hintBefore != hintAfter && (CurrentStep == Step.WatchMotion || CurrentStep == Step.GrabRotate))
        {
            Changed?.Invoke();
        }
    }

    // 列に無い段階は無視する（C のチュートリアル中に一時停止しても何も変わらない）。
    // 段階が実際に変わったときだけ Changed を出す（先回りで済んだ操作では UI を触らない）。
    private void SetDone(Step step)
    {
        if (Array.IndexOf(sequence, step) < 0)
        {
            return;
        }

        Step before = CurrentStep;
        completed.Add(step);
        if (CurrentStep != before)
        {
            Changed?.Invoke();
        }
    }
}
