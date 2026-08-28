// Home シーンから「自由に見る」で入ったことを、次のシーンのプレイヤーへ伝える受け渡し口。
//
// TestScene の showBundlePickerOnStart は **0（出さない）** で焼き込まれている。
// バッチ実行と EditMode テストが TestScene を開くので、ここを 1 にすると
// ピッカーが選択待ちで止まってしまう。だからシーンの値は変えず、
// Home から来たときだけ実行時に上書きする。
//
// ExperimentTrialHandoff と同じ「static に置いて Start() で Consume」方式。
// 1 回で消すので、実験に入ったあと手動でシーンを開いても効き続けることはない。
public static class HomeLaunchHandoff
{
    public static bool PendingShowBundlePicker { get; private set; }

    public static void RequestBundlePicker()
    {
        PendingShowBundlePicker = true;
    }

    public static bool ConsumeShowBundlePicker()
    {
        bool value = PendingShowBundlePicker;
        PendingShowBundlePicker = false;
        return value;
    }

    public static void Clear()
    {
        PendingShowBundlePicker = false;
    }
}
