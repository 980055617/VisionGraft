using System;
using System.Collections.Generic;

// 操作チュートリアルの進行役。UI は持たず、ExperimentController がこの状態をパネルに描く。
//
// 各ブロックの直前に、そのブロックの表示条件に合わせた内容を出す（2026-09-11 の設計）:
//   A Monocular（必ず最初）: トリガーでボタンを押す → A ボタンで一時停止 → 再開 → シークバーをドラッグ
//                           → 画面の上に出る「視聴を終了」を押す（次の動画に移るときの操作をここで覚える）
//   B StereoOnly           : 立体で見える説明だけ。操作は同じ。「視聴を終了」を押して終わる
//   C ModelReplaced        : Model ボタンでモデルを替える → 「視聴を終了」
//
// 検出は ExperimentLog の sink を横取りして行う（プレイヤー側には手を入れない）。
// 受け取った操作は内側の sink（セッション）へそのまま流すので、チュートリアル中の
// 操作も operations.csv に残る（trial_index = -1）。
//
// 各段階は「済んだかどうか」で持ち、現在の段階はその列の最初の未完了項目。
// 順番どおりでなくても済んだ操作は数える。resume だけは pause の後でないと数えない。
// 段階を飛ばす手段は置かない（実際に操作しないと進めない）。
public sealed class ExperimentTutorial : IExperimentLogSink
{
    public enum Step
    {
        PressButton,
        PausePlayback,
        ResumePlayback,
        Seek,
        ChangeModel,
        Done,
    }

    private readonly IExperimentLogSink inner;
    private readonly ExperimentDisplayMode mode;
    private readonly Step[] sequence;
    private readonly HashSet<Step> completed = new HashSet<Step>();

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
                return new[] { Step.ChangeModel, Step.Done };
            default:
                return new[] { Step.Done };
        }
    }

    // 段階が変わったとき。パネルの作り直しに使う。
    public event Action Changed;

    public ExperimentDisplayMode Mode
    {
        get { return mode; }
    }

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
            Step step = CurrentStep;
            if (step == Step.Done)
            {
                return StepCount == 0 ? "このブロックの説明" : "チュートリアル 終了";
            }

            return $"チュートリアル {CurrentStepNumber}/{StepCount}";
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
                        "下のバーの上にあるつまみを\n" +
                        "光線で指してトリガーを引いたまま\n" +
                        "左右に動かすと、動画の位置を\n" +
                        "変えられます。動かしてみてください。";
                case Step.ChangeModel:
                    return
                        "このブロックでは動画の人や動物が\n" +
                        "3D モデルに置き換わります。\n" +
                        "下のバーの「Model」ボタンを押し、\n" +
                        "好きなモデルを選んでください。";
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
                return
                    "動画は何回でも好きなだけ見られます。\n" +
                    "見終わったら、画面の上のこのボタン\n" +
                    "「視聴を終了」を押すと次の動画に\n" +
                    "進みます。押して練習を終えてください。";
            case ExperimentDisplayMode.StereoOnly:
                return
                    "このブロックでは動画が\n" +
                    "立体（奥行きあり）で見えます。\n" +
                    "操作はこれまでと同じです。\n" +
                    "「視聴を終了」を押して始めてください。";
            default:
                return
                    "操作は以上です。\n" +
                    "見終わったら画面の上の\n" +
                    "「視聴を終了」を押してください。\n" +
                    "いま押すと練習を終えます。";
        }
    }

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
                break;
            case "resume":
                // 止めていないのに resume だけ来ることはないはずだが、来ても数えない。
                if (completed.Contains(Step.PausePlayback))
                {
                    SetDone(Step.ResumePlayback);
                }
                break;
            case "seek":
                SetDone(Step.Seek);
                break;
            case "change_model":
                SetDone(Step.ChangeModel);
                break;
        }
    }

    public void RecordInteraction(uint trackId, string kind, string detail)
    {
        inner?.RecordInteraction(trackId, kind, detail);
    }

    public void RecordVideoLoop()
    {
        inner?.RecordVideoLoop();
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
