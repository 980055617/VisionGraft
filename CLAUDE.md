# VisionGraft — Claude 作業ガイド

## プロジェクト概要

VR ヘッドセット向けのステレオ動画プレイヤー。別プロジェクトで生成した `.svb` bundle を読み込み、動画視聴と 3D モデル置き換えを行うシステム。

### 目的

bundle 内の検出オブジェクト（Human / Animal / Else）を、対応する 3D モデルに置き換えてリアルタイムに姿勢追従させる没入体験を提供する。将来的にはランダムまたはインタラクション起因の自発的アニメーションも実行予定。

### オブジェクト種別と実装状態

| 種別 | Rig | 姿勢データ | 現状 |
|---|---|---|---|
| **Human** | Unity Humanoid Rig | `meta.bin` 内 SMPL block（4D-Human 由来）| 実装中・メイン課題 |
| **Animal** | カスタム Animal Rig | `meta.bin` 内 SMAL block + AniMer 26 関節 | SMAL FK 経路が有効。ただし paw×4 / toe×2 / head / tailTip は body_pose 未適用（親追従のみ）、AimAt 相当も無し |
| **Else** | なし（剛体） | `meta.bin` 内 anchor / bbox | 配置のみ実装 |

- **Animal の姿勢適用は 2 経路ある。現行 bundle で実際に走るのは SMAL FK のほう**
  - `AnimalSmalFkApplier.TryApplyAnimalSmalFk` … `meta.bin` の SMAL block（globalOrient + body_pose）で駆動。`hasSmalPose && IsAnimalRigReadyForSmalFk` が真なら**こちらだけ**が走る
  - `AnimalPoseApplier` の keypoint 経路（`ApplyAnimalHeadPose` / `ApplyAnimalTailPose` / `ApplyAnimalLimbPose`）… SMAL block が無い bundle 用のフォールバック。**現行 bundle では実行されない**
  - Animal の姿勢を直すときは**まず `[SMAL-PIPE] hasSmalPose=` をログで確認**し、どちらの経路を触るか決める
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
| [docs/bundle-placement.md](docs/bundle-placement.md) | bundle 構造・meta.bin・anchor 配置（発表用まとめ + 実装リファレンス） |
| [docs/bundle-shared/](docs/bundle-shared/) | **bundle 生成側（Python）との共有一式**。このフォルダごとコピーして同期する。`README.md`（課題 D-xxx・合意済みのデータ契約）と `bundle_depth_check.py`（検証ツール）。**共有するファイルを増やすときは必ずこのフォルダに入れる** |
| [docs/smpl-retargeting.md](docs/smpl-retargeting.md) | Human SMPL / Animal SMAL FK・座標変換・調査ログ（発表用まとめ付き） |
| [docs/interactive-motion-events.md](docs/interactive-motion-events.md) | インタラクティブモーションイベント（発表用まとめ付き） |
| [docs/experiment-flow.md](docs/experiment-flow.md) | 被験者実験フロー・シーン構成・ログ仕様（発表用まとめ付き） |
| [docs/DogMetaBoneMapping.md](docs/DogMetaBoneMapping.md) | 犬モデルのボーンマッピング・スケール調査 |
| [docs/human-animation-test-scene.md](docs/human-animation-test-scene.md) | Human アニメーションテストシーンの使い方 |
| [docs/presentations/weekly/](docs/presentations/weekly/) | 週次進捗ファイル（`YYYY-MM-DD.md`、金曜日の日付） |

## 作業方針

- **作業前に関連 docs を参照**する
- **記録は聞かずに必ず行う**。調査・実測・判断の結果が出たら、**対処方針の議論に入る前に** docs へ書く。「記録しますか」と確認しない（記録 → その後に方針、の順を固定する）
- **作業中・作業後にドキュメントを更新**する（新しい知見・NG パターン・調査結果）
- **実装後に `Docs/presentations/` を更新**する（対応する機能ファイルの内容を現状に合わせ、`presentations/weekly/YYYY-MM-DD.md` に今週の差分を追記する）
  - 週次ファイルの命名規則: **金曜日（ゼミ当日）の日付**をファイル名にする。内容はその前の週（土〜金）の作業記録
- **コードを調べてから推測する**（わからない点は実際のコードを確認してからユーザーに質問）
- **ログから実際の値を確認**してから修正方向を決める
- **修正の根本原因が確認できてから変更に入る**
- **変換式を変える前に数学的検証をする**
- **数値検証は実装を移植してから行う**。配置・投影・スケールを Python 等で検証するときは、記憶や docs から式を再構成せず、**必ず先に該当する実装コードを読んで移植する**。既定値ではなく**シーンの serialize 値**を確認する。移植コードはファイルとして残し、次の検証の起点にする（過去に線形/逆数の取り違えとシーン値の見落としで結論が逆転した事例あり: [docs/bundle-placement.md](docs/bundle-placement.md) の「検証コードの誤りと再発防止」）

## 絶対に変えてはいけないこと

- **IK 禁止**: Human モデルの姿勢適用で IK（TwoBone IK 等）を復活させない
- **ShouldUseSmplOnlyPose() = true**: 常に true
- **ShouldUseHumanSmplRootOrientation() = false**: globalOrient は FK 内で処理
- **Animator 無効化**: `DisableHumanAnimatorPlayback` は再有効化しない
- **ApplyLocalRotation 禁止**: FK ループ内では `ApplyWorldRotation` のみ使用する
