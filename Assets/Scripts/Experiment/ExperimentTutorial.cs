using System;

// 操作チュートリアルの進行役。UI は持たず、ExperimentController がこの状態をパネルに描く。
//
// 教える操作は 3 つ（2026-09-11、被験者実験の試行の前に置く）:
//   1. トリガーでボタンを押す        … このパネルの「次へ」を押してもらう
//   2. A ボタンで一時停止 → 再開      … プレイヤーが出す pause / resume の操作ログで検出
//   3. Model ボタンでモデルを変える   … change_model の操作ログで検出
//
// 検出は ExperimentLog の sink を横取りして行う（プレイヤー側には手を入れない）。
// 受け取った操作は内側の sink（セッション）へそのまま流すので、チュートリアル中の
// 操作も operations.csv に残る（trial_index = -1）。
//
// 各操作は「済んだかどうか」のフラグで持ち、現在の段階は最初の未完了項目。
// 順番どおりでなくても済んだ操作は数える（先に Model を変えた参加者にもう一度
// やらせない）。resume だけは pause の後でないと数えない。
public sealed class ExperimentTutorial : IExperimentLogSink
{
    public enum Step
    {
        PressButton,
        PausePlayback,
        ResumePlayback,
        ChangeModel,
        Done,
    }

    public const int StepCount = 4;

    private readonly IExperimentLogSink inner;
    private bool buttonDone;
    private bool pauseDone;
    private bool resumeDone;
    private bool modelDone;
    private int skippedCount;

    public ExperimentTutorial(IExperimentLogSink inner)
    {
        this.inner = inner;
    }

    // 段階が変わったとき。パネルの作り直しに使う。
    public event Action Changed;

    public Step CurrentStep
    {
        get
        {
            if (!buttonDone)
            {
                return Step.PressButton;
            }

            if (!pauseDone)
            {
                return Step.PausePlayback;
            }

            if (!resumeDone)
            {
                return Step.ResumePlayback;
            }

            if (!modelDone)
            {
                return Step.ChangeModel;
            }

            return Step.Done;
        }
    }

    public bool IsDone
    {
        get { return CurrentStep == Step.Done; }
    }

    public int SkippedCount
    {
        get { return skippedCount; }
    }

    // 「次へ」ボタン。ボタンを押せたこと自体が 1 つ目の課題なので、押されたら済みにする。
    // それ以外の段階は操作ログで進むので何もしない。
    public void CompleteCurrentStep()
    {
        if (CurrentStep != Step.PressButton)
        {
            return;
        }

        SetDone(ref buttonDone);
    }

    // 操作ができずに詰まった参加者のための逃げ道。飛ばした段階はログに残す。
    public void SkipCurrentStep()
    {
        Step step = CurrentStep;
        if (step == Step.Done)
        {
            return;
        }

        skippedCount++;
        inner?.RecordOperation("tutorial_step_skipped", step.ToString());

        switch (step)
        {
            case Step.PressButton:
                SetDone(ref buttonDone);
                break;
            case Step.PausePlayback:
                SetDone(ref pauseDone);
                break;
            case Step.ResumePlayback:
                SetDone(ref resumeDone);
                break;
            case Step.ChangeModel:
                SetDone(ref modelDone);
                break;
        }
    }

    public string Title
    {
        get
        {
            Step step = CurrentStep;
            return step == Step.Done
                ? "チュートリアル 終了"
                : $"チュートリアル {(int)step + 1}/{StepCount}";
        }
    }

    public string Body
    {
        get
        {
            switch (CurrentStep)
            {
                case Step.PressButton:
                    return
                        "コントローラから出ている光線をこのパネルの「次へ」ボタンに合わせ、\n" +
                        "人差し指のトリガーを引いてください。\n\n" +
                        "画面のボタンはすべてこの操作で押せます。";
                case Step.PausePlayback:
                    return
                        "右手コントローラの A ボタンを押すと動画が止まります。\n" +
                        "（左手なら X ボタン）\n\n" +
                        "押してみてください。";
                case Step.ResumePlayback:
                    return
                        "動画が止まりました。\n\n" +
                        "もう一度 A ボタンを押すと再生が再開します。";
                case Step.ChangeModel:
                    return
                        "画面の下にあるバーの「Model」ボタンを押すとモデルの一覧が開きます。\n\n" +
                        "好きなモデルを選んでください。\n" +
                        "動画の中の人や動物がそのモデルに置き換わります。";
                default:
                    return
                        "操作の説明は以上です。\n\n" +
                        "自由に試したら「チュートリアルを終了」を押してください。";
            }
        }
    }

    // tutorial_end の detail に書く 1 行。
    public string DescribeResult()
    {
        return $"completed={(IsDone ? 1 : 0)} skipped={skippedCount} step={CurrentStep}";
    }

    // ── IExperimentLogSink（プレイヤーからの操作を横取りして段階を進める）──

    public void RecordOperation(string action, string detail)
    {
        inner?.RecordOperation(action, detail);

        switch (action)
        {
            case "pause":
                SetDone(ref pauseDone);
                break;
            case "resume":
                // 止めていないのに resume だけ来ることはないはずだが、来ても数えない。
                if (pauseDone)
                {
                    SetDone(ref resumeDone);
                }
                break;
            case "change_model":
                SetDone(ref modelDone);
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

    // 段階が実際に変わったときだけ Changed を出す（先回りで済んだ操作では UI を触らない）。
    private void SetDone(ref bool flag)
    {
        Step before = CurrentStep;
        flag = true;
        if (CurrentStep != before)
        {
            Changed?.Invoke();
        }
    }
}
