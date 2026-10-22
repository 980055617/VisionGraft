// 被験者実験の条件を表す型定義。
// 実験デザイン: 6動画 × 3表示条件 = 18試行を 1 セッションで実施する（2026-10-22 に 3 動画 9 試行から変更）。
// 詳細は Docs/experiment-flow.md を参照。

// 提示する動画（= bundle）。2026-10-22 に 30 秒 × 6 本へ変えた。
// 中身（人・動物・物の内訳）は生成待ちなので、番号の枠だけ置く。中身が決まったら名前を付け直してよい
// （trials.csv の video 列にこの名前が出る。どの bundle かは bundle_file・bundle_sha256 列で分かる）。
// 3 本 × 3 条件の旧実験（Human / Animal / Car）はタグ experiment-v2-final にある。
public enum ExperimentVideo
{
    Video1,
    Video2,
    Video3,
    Video4,
    Video5,
    Video6,

    // 操作チュートリアル用。実験の 6 本とは別のクリップを使う（2026-09-11）。
    // ExperimentPlan の試行には含まれない。
    Tutorial,
}

// 操作チュートリアルをいつ挟むか（ExperimentController.tutorialTiming）。
public enum ExperimentTutorialTiming
{
    None,

    // 最初の試行の前に 1 回（単眼の内容のみ）。
    BeforeFirstTrial,

    // 各条件ブロック（6 本）の先頭で 1 回ずつ（BlockCount = 3 回）。**既定**（ExperimentController.tutorialTiming）。
    BeforeEachBlock,
}

// 実験のパネル（チュートリアルの説明・試行中の「視聴を終了」）を動画の画面のどちら側に置くか。
// 画面に被せると画面のコライダーにレイを取られてボタンが押せない（2026-09-11 実機指摘）ので、
// 必ず画面の外側。下はコントロールバー、右は Model パネルが使うので既定は上。
public enum ExperimentPanelSide
{
    AboveScreen,
    LeftOfScreen,
    RightOfScreen,
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

    // 単眼。除去前ステレオ動画の**左目映像を両目に**出す（両眼視差なし）。2026-09-11 に追加。
    // 全参加者が最初のブロックでこれを見る（ExperimentPlan.ResolveBlockMode）。
    Monocular,
}

// 条件ブロックの提示順による群分け。最初のブロックは両群とも単眼で、残り 2 ブロックの順序を
// 入れ替えて順序効果を相殺する（2026-09-11 に 2 ブロック → 3 ブロックへ変更）。
public enum ExperimentGroup
{
    // Monocular 6 本 → StereoOnly 6 本 → ModelReplaced 6 本
    A,

    // Monocular 6 本 → ModelReplaced 6 本 → StereoOnly 6 本
    B,
}
