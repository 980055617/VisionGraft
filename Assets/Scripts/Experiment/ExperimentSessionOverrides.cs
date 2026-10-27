using System.Collections.Generic;
using UnityEngine;

// 被験者がセッション中に変更したモデル・向きを保持する（メモリのみ）。
//
// 3 層構成の②。docs/model-selection-persistence.md 参照。
//
//   ① 基準  : persistentDataPath/model_selection.json（研究者が通常利用と実験者のデバッグで仕込む。参加者のセッション中は読むだけ）
//   ② 上書き: ここ。被験者の調整。**1 セッションだけ生きる**
//   ③ 適用値: ① に ② を重ねたもの
//
// 実験者のデバッグ（ExperimentController の「デバッグ」、参加者 ID DEBUG）のセッションは、調整を②ではなく①へ書く（2026-10-27、ユーザー
// 「被験者実験のデバッグで調整した向きとかはbundleに反映するようにしたい．実際の被験者実験で調整された向きは引き続き反映しない」）。
// 書き込み先の決まりは WritesToBaseline / ResolveWritable の 1 か所に置く。デバッグの印は BeginSession・EndSession のたびに下ろす
// （参加者のセッションへ持ち越すと、被験者の調整が黙って基準に入る）。
//
// 試行 = シーンのロード／アンロードなので MonoBehaviour のフィールドでは試行をまたげない。
// ExperimentTrialHandoff と同じ理由で static にする。
//
// ただし ExperimentTrialHandoff とは**兼用しない**。あちらは「1 回 Consume して消す」設計で、
// こちらはセッション中ずっと残る。寿命が違う。
public static class ExperimentSessionOverrides
{
    private static readonly Dictionary<string, VideoCustomization> byVideo = new Dictionary<string, VideoCustomization>();

    // 実験中かどうか（参加者のセッションとデバッグのセッション）。
    public static bool Active { get; private set; }

    // 実験者のデバッグのセッションか（調整を基準ファイルへ書く）。BeginSession・EndSession のたびに決め直す。
    public static bool DebugWritesBaseline { get; private set; }

    // 調整を基準ファイルへ書くか: 実験の外（通常の視聴）とデバッグのセッションは書く、参加者のセッションは書かない。
    public static bool WritesToBaseline
    {
        get { return !Active || DebugWritesBaseline; }
    }

    // 参加者のセッション（調整はセッションの間だけ）。
    public static void BeginSession()
    {
        BeginSession(false);
    }

    // writesBaseline: 実験者のデバッグのセッション（調整を基準ファイルへ書く）。
    public static void BeginSession(bool writesBaseline)
    {
        byVideo.Clear();
        Active = true;
        DebugWritesBaseline = writesBaseline;
        Debug.Log(writesBaseline
            ? "[Customization] session overrides begin (デバッグ: 調整は基準ファイルへ書きます)"
            : "[Customization] session overrides begin (基準ファイルへは書き込みません)");
    }

    // 参加者が変わるとき・実験を抜けるときに呼ぶ。**基準ファイルは消さない。**
    public static void EndSession()
    {
        byVideo.Clear();
        Active = false;
        DebugWritesBaseline = false;
        Debug.Log("[Customization] session overrides cleared");
    }

    // 調整の書き込み先（WritesToBaseline なら基準、そうでなければセッション上書き）。
    public static VideoCustomization ResolveWritable(string videoKey, int numFrames)
    {
        if (string.IsNullOrEmpty(videoKey))
        {
            return null;
        }

        return WritesToBaseline
            ? TrackCustomizationStore.GetOrCreate(videoKey, numFrames)
            : GetOrCreate(videoKey, numFrames);
    }

    // 記録用の今のモード（[Customization] の行）。
    public static string DescribeWriteMode()
    {
        return !Active ? "normal(書き込み可)" : DebugWritesBaseline ? "debug(基準へ書く)" : "experiment(読むだけ)";
    }

    public static VideoCustomization Get(string videoKey)
    {
        if (string.IsNullOrEmpty(videoKey))
        {
            return null;
        }

        return byVideo.TryGetValue(videoKey, out VideoCustomization v) ? v : null;
    }

    public static VideoCustomization GetOrCreate(string videoKey, int numFrames)
    {
        if (!byVideo.TryGetValue(videoKey, out VideoCustomization v) || v == null)
        {
            v = new VideoCustomization();
            byVideo[videoKey] = v;
        }

        v.numFrames = numFrames;
        return v;
    }
}
