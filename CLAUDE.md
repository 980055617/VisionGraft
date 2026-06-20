# VisionGraft — Claude 作業ガイド

## プロジェクト概要

VR ヘッドセット向けのステレオ動画プレイヤー。別プロジェクトで生成した `.svb` bundle を読み込み、動画視聴と 3D モデル置き換えを行うシステム。

### 目的

bundle 内の検出オブジェクト（Human / Animal / Else）を、対応する 3D モデルに置き換えてリアルタイムに姿勢追従させる没入体験を提供する。将来的にはランダムまたはインタラクション起因の自発的アニメーションも実行予定。

### オブジェクト種別と実装状態

| 種別 | Rig | 姿勢データ | 現状 |
|---|---|---|---|
| **Human** | Unity Humanoid Rig | `meta.bin` 内 SMPL block（4D-Human 由来）| 実装中・メイン課題 |
| **Animal** | カスタム Animal Rig | `meta.bin` 内ジョイント（AniMer + SMAL 予定） | 未実装 |
| **Else** | なし（剛体） | `meta.bin` 内 anchor / bbox | 配置のみ実装 |

- **姿勢追従**（Pose Following）= bundle の推定データをフレームごとに 3D モデルに適用すること（アニメーションとは別概念）
- **アニメーション** = 将来実装。ユーザー言動やランダムタイミングで姿勢追従を一時中断し再生するモーション
- モデル選択: 現状は Inspector で固定。将来的には UI ウィンドウから選択予定

### bundle (.svb) の構成

`.svb` は ZIP アーカイブ形式。別プロジェクト（Python 側）で生成する。

| エントリ | 必須 | 用途 | 内容 |
|---|---|---|---|
| `video.mp4` | Required | **Runtime** | ステレオ動画（左右目の映像） |
| `manifest.json` | Required | **Runtime** | 動画メタ情報（解像度・fps・FOV・座標系など） |
| `meta.bin` | Required | **Runtime** | フレームごとの検出オブジェクト位置・ジョイント・SMPL block 等のバイナリデータ |
| `source/human_smpl_from_sam2.json` | Optional | 検証・debug のみ | Human の SMPL 姿勢パラメータ（全フレーム） |
| `source/animal_control_targets.json` | Optional | 検証・debug のみ | Animal のコントロールターゲット |
| `source/other_object_proxies.json` | Optional | 検証・debug のみ | Else オブジェクトのプロキシ（バウンディングボックス等） |
| `source/pre_removal_stereo_video.mp4` | Optional | **Runtime（通常モードのみ）** | 除去前のオリジナルステレオ動画。`video.mp4` と同じ manifest レイアウト・タイムラインを共有する |

### bundle 使用ルール（絶対に守ること）

- **配置・回転・姿勢追従には `meta.bin` と `manifest.json` のみを使う**
- **`source/*` は runtime 配置に使用禁止** — 検証・debug・将来の再解釈用
  - 例外: `source/pre_removal_stereo_video.mp4` は通常モードの動画ストリームとして runtime 再生に使う。配置データとしては使わない（[docs/adr/0003-normal-mode-playback-video.md](docs/adr/0003-normal-mode-playback-video.md)）
- `meta.bin` には SMPL FK block（rotations・betas・transl）が object payload として含まれており、これが runtime の唯一の SMPL データソース
- `source/human_smpl_from_sam2.json` 等は bundle によっては同梱されない。runtime がこれに依存すると動かなくなる

## ドキュメント構造

作業前に関連ドキュメントを参照すること。ドキュメントがない場合は `docs/` に作成し、作業中・作業後に更新する。

| ドキュメント | 内容 |
|---|---|
| [docs/smpl-retargeting.md](docs/smpl-retargeting.md) | SMPL FK・座標変換・Humanoid リターゲティング実装ガイド・調査ログ |
| [docs/DogMetaBoneMapping.md](docs/DogMetaBoneMapping.md) | 犬モデルのボーンマッピング |
| [docs/human-animation-test-scene.md](docs/human-animation-test-scene.md) | Human アニメーションテストシーンの使い方 |
| [docs/interactive-motion-events.md](docs/interactive-motion-events.md) | インタラクティブモーションイベント |

## 作業方針

- **作業前に関連 docs を参照**する
- **作業中・作業後にドキュメントを更新**する（新しい知見・NG パターン・調査結果）
- **コードを調べてから推測する**（わからない点は実際のコードを確認してからユーザーに質問）
- **ログから実際の値を確認**してから修正方向を決める
- **修正の根本原因が確認できてから変更に入る**
- **変換式を変える前に数学的検証をする**

## 絶対に変えてはいけないこと

- **IK 禁止**: Human モデルの姿勢適用で IK（TwoBone IK 等）を復活させない
- **ShouldUseSmplOnlyPose() = true**: 常に true
- **ShouldUseHumanSmplRootOrientation() = false**: globalOrient は FK 内で処理
- **Animator 無効化**: `DisableHumanAnimatorPlayback` は再有効化しない
- **ApplyLocalRotation 禁止**: FK ループ内では `ApplyWorldRotation` のみ使用する
