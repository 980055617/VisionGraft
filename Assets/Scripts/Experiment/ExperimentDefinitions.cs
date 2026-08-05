// 被験者実験の条件を表す型定義。
// 実験デザイン: 3動画 × 2表示条件 = 6試行を 1 セッションで実施する。
// 詳細は Docs/experiment-flow.md を参照。

// 提示する動画（= bundle）。
public enum ExperimentVideo
{
    Human,
    Animal,
    Train,
}

// 表示条件。被験者はこの条件を自分で切り替えられない（mode ボタンを生成しない）。
public enum ExperimentDisplayMode
{
    // 3D モデル置換なし。source/pre_removal_stereo_video.mp4（除去前ステレオ動画）を再生する。
    // video.mp4（検出オブジェクトを消した除去済み映像）ではない点が重要: 除去済み映像を
    // 見せると「穴の空いた映像」との比較になってしまい、実験の対照条件にならない。
    StereoOnly,

    // 3D モデル置換あり。video.mp4 + meta.bin による通常の置換再生。
    ModelReplaced,
}

// 条件ブロックの提示順による群分け。順序効果を相殺するために被験者を 2 群に割り付ける。
public enum ExperimentGroup
{
    // 前半 StereoOnly 3 本 → 後半 ModelReplaced 3 本
    A,

    // 前半 ModelReplaced 3 本 → 後半 StereoOnly 3 本
    B,
}
