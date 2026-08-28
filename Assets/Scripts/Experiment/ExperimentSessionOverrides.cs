using System.Collections.Generic;
using UnityEngine;

// 被験者がセッション中に変更したモデル・向きを保持する（メモリのみ）。
//
// 3 層構成の②。docs/model-selection-persistence.md 参照。
//
//   ① 基準  : persistentDataPath/model_selection.json（研究者が通常利用で仕込む。実験中は読むだけ）
//   ② 上書き: ここ。被験者の調整。**1 セッションだけ生きる**
//   ③ 適用値: ① に ② を重ねたもの
//
// 試行 = シーンのロード／アンロードなので MonoBehaviour のフィールドでは試行をまたげない。
// ExperimentTrialHandoff と同じ理由で static にする。
//
// ただし ExperimentTrialHandoff とは**兼用しない**。あちらは「1 回 Consume して消す」設計で、
// こちらはセッション中ずっと残る。寿命が違う。
public static class ExperimentSessionOverrides
{
    private static readonly Dictionary<string, VideoCustomization> byVideo = new Dictionary<string, VideoCustomization>();

    // 実験中かどうか。true の間はプレイヤーが基準ファイルへ書き込まない。
    public static bool Active { get; private set; }

    public static void BeginSession()
    {
        byVideo.Clear();
        Active = true;
        Debug.Log("[Customization] session overrides begin (基準ファイルへは書き込みません)");
    }

    // 参加者が変わるとき・実験を抜けるときに呼ぶ。**基準ファイルは消さない。**
    public static void EndSession()
    {
        byVideo.Clear();
        Active = false;
        Debug.Log("[Customization] session overrides cleared");
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
