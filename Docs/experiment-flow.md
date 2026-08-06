# 被験者実験フロー

VR ヘッドセット上で 6 試行を順に提示し、条件・操作・視線・インタラクションを記録するための実験用シーンとその実装。

---

## 発表用まとめ

### 何をする実験か

3 本の動画（Human / Animal / Train）を、**3D モデル置換あり**と**置換なし（ステレオ動画のみ）**の 2 条件で提示する。1 参加者あたり 3 × 2 = **6 試行**。

条件はブロック化し、参加者を 2 群に割り付けて順序効果を相殺する。

| 群 | 前半 3 試行 | 後半 3 試行 |
|---|---|---|
| **A** | 置換なし（StereoOnly） | 置換あり（ModelReplaced） |
| **B** | 置換あり（ModelReplaced） | 置換なし（StereoOnly） |

動画の提示順は 3! = **6 パターン**を番号で指定する。前半・後半とも同じ順序を使うので、同じ動画が両条件で同じ位置に来る。

| パターン | 順序 |
|---|---|
| 1 | Human → Animal → Train |
| 2 | Human → Train → Animal |
| 3 | Animal → Human → Train |
| 4 | Animal → Train → Human |
| 5 | Train → Human → Animal |
| 6 | Train → Animal → Human |

群 2 通り × 動画順 6 パターン = **12 通り**の割り付けを参加者に振る。

### 設計上の要点

**1. 「置換なし」条件には除去前動画を使う**

bundle には 2 本の動画が入っている。

| エントリ | 内容 | 実験での用途 |
|---|---|---|
| `video.mp4` | 検出オブジェクトを**消した**除去済み映像 | 置換あり条件（この上にモデルを重ねる） |
| `source/pre_removal_stereo_video.mp4` | 除去**前**のオリジナルステレオ映像 | 置換なし条件 |

置換なし条件に `video.mp4` を使ってしまうと「人や動物が消えて穴の空いた映像」を見せることになり、対照条件として成立しない。実装では既存の normal mode（[docs/adr/0003-normal-mode-playback-video.md](adr/0003-normal-mode-playback-video.md)）をそのまま条件に対応させている。

**2. 条件は最初のフレームから確定させる**

既存の `SetNormalMode()` は「再生中に切り替える」ための実装で、再生位置を引き継いで `VideoPlayer.url` を差し替える。これを試行開始時に呼ぶと、切り替わるまでの数フレームだけ置換モデルが見えてしまう。そこで `Prepare` する url そのものを最初から選ぶ経路（`ResolveInitialNormalMode`）を追加した。

**3. 試行ごとにシーンを捨てる**

`StreamingStereoVideoPlayer` の `Start()` は一度きりの coroutine で、2 本目の bundle に差し替える経路がない。モデルインスタンス・プロキシ・interactive motion の状態を安全に捨てる手段がないため、**試行 = シーンのロードとアンロード**にした。状態リークが原理的に起きず、プレイヤー本体のライフサイクル（CLAUDE.md の「絶対に変えてはいけないこと」に近い領域）に手を入れずに済む。

**4. 表示条件だけは被験者に触らせない**

被験者は一時停止・シーク・モデル変更・設定を自由に操作できる。ただし表示条件（置換あり／なし）を切り替える Display ボタンだけは**生成しない**。これを押されると条件そのものが壊れる。

**5. 動画はループ再生する**

`RuntimePlaybackController.ConfigureForApiPlayback` が `isLooping = true` を設定しているため、動画は終端で先頭に戻って再生し続ける。被験者は納得するまで 2 周でも 3 周でも見られる。試行ログには何周見たかを記録する。

---

## シーン構成

```
ExperimentScene（ベースシーン・常駐）
  ├ OVRCameraRig / OVRInteractionComprehensive   XR リグ
  ├ EventSystem
  └ ExperimentController                         セッション進行・ログ

TrialScene（試行ごとに Additive でロード／アンロード）
  ├ VideoPlayerRoot (StreamingStereoVideoPlayer)
  ├ Directional Light
  └ Global Volume
```

### なぜ Additive なのか

XR リグをベースシーンに置いたまま試行シーンだけを付け外しするため。`Single` でロードするとリグごと作り直しになり、トラッキングの連続性と操作パネルが失われる。

プレイヤーは `ViewCameraSelection` でシーンをまたいでカメラを探す（`Camera.main` へのフォールバックも持つ）ので、TrialScene に XR リグを置く必要はない。**TrialScene にリグを置いてはいけない**（カメラが 2 つになる）。

### アクティブシーンの切り替えが必須

`StreamingStereoVideoPlayer` が実行時に生成するモデルインスタンス（`TrackInstanceFactory`）と UI ルート（`RuntimeUiRootFactory`）は**親を持たない root オブジェクト**として生成される。root オブジェクトは「アクティブシーン」に属するため、TrialScene をロードした直後に `SceneManager.SetActiveScene(trialScene)` しておかないと、生成物が ExperimentScene 側に積み上がり、**次の試行に前の試行のモデルが残る**。

アンロード前には `SetActiveScene(baseScene)` で戻す（アクティブシーンをアンロードすると以後の生成先が不定になる）。

---

## 使い方

### 1. シーンを生成する

Unity メニュー **VisionGraft → Experiment → Create Experiment Scenes**

`SampleScene` をコピー元に `ExperimentScene.unity` と `TrialScene.unity` を生成し、Build Settings に登録する。コピー元にするのは OVRCameraRig / OVRInteractionComprehensive の prefab インスタンスとその override をそのまま引き継ぐため。

### 2. 起動シーンを切り替える

- 実験する: **VisionGraft → Experiment → Set Experiment Scene As Startup**
- 通常の単体再生に戻す: **VisionGraft → Experiment → Set Sample Scene As Startup**

### 3. bundle を配置する

`Assets/StreamingAssets/` に 3 本を置く（既に配置済み）。

| 動画 | ファイル |
|---|---|
| Human | `bundle_human.svb` |
| Animal | `bundle_animal.svb` |
| Train | `bundle_train.svb` |

ファイル名は `ExperimentController` の Bundle Catalog で変更できる。

### 4. セッションを実施する

| 局面 | 画面 | 操作 |
|---|---|---|
| **セットアップ** | 参加者 ID・群・動画順の設定 | 実験者が割り付け表どおりに設定し「セッション開始」 |
| **待機** | 次の試行の内容を表示 | 実験者が「この試行を開始」 |
| **読み込み** | bundle 展開中 | （待つだけ。`bundle_human.svb` は 155MB あり実機で十数秒） |
| **試行** | 動画再生。視界の下に小さなパネル | 被験者が満足したら「視聴を終了」 |
| **待機** | 次の試行 | **ここでアンケートに回答してもらう**。終わったら実験者が次を開始 |
| **終了** | ログ出力先を表示 | — |

参加者 ID は `P01` 形式（プレフィックスと番号は Inspector で変更可）。

---

## ログ

出力先: `{Application.persistentDataPath}/ExperimentLogs/{参加者ID}_{yyyyMMdd_HHmmss}/`

Quest 実機からは adb で回収する。

```
adb shell ls /sdcard/Android/data/<パッケージ名>/files/ExperimentLogs
adb pull /sdcard/Android/data/<パッケージ名>/files/ExperimentLogs ./logs
```

### trials.csv

試行ごとに 1 行。アンケート結果と突き合わせる主キーになる。

`participant_id, group, video_order_pattern, trial_index, block_index, index_in_block, video, mode, bundle_file, start_time, end_time, duration_sec, loop_count, aborted`

`loop_count` = その試行で動画が何周したか。

### operations.csv

被験者の操作履歴。

`participant_id, trial_index, time, trial_elapsed_sec, video_time_sec, action, detail`

| action | detail |
|---|---|
| `trial_begin` / `trial_end` / `trial_abort` | 試行の内容 |
| `pause` / `resume` | — |
| `seek` | シーク先（0..1 の正規化位置） |
| `change_model` | `track=... category=... index=... prefab=...` |
| `video_loop` | 何周目か |
| `trial_end_pressed` | 被験者が視聴終了を押した |

### headpose.csv

頭部姿勢。既定 15Hz。

`participant_id, trial_index, time, trial_elapsed_sec, video_time_sec, pos_x, pos_y, pos_z, rot_x, rot_y, rot_z, rot_w`

### interactions.csv

インタラクティブモーションの発火。

`participant_id, trial_index, time, trial_elapsed_sec, video_time_sec, track_id, kind, detail`

| kind | 意味 |
|---|---|
| `random_Static` / `random_Dynamic` | ランダム発火（[interactive-motion-events.md](interactive-motion-events.md)） |
| `system_frameout` | フレームアウト起因のシステムトリガ |

### CSV の書式

分析は後日 pandas 等で行うため、崩れない CSV を出すことを優先している。

- 区切り文字・引用符・改行を含む値は RFC 4180 準拠でクォートする（`prefab=` に何が入っても列がずれない）
- 数値は必ず `InvariantCulture`。実験機のロケール次第で小数点が `,` になると CSV が壊れる
- 試行の切れ目で必ず `Flush()` する。実機がクラッシュしても直前の試行までは残る

---

## 実装リファレンス

### 新規ファイル

| ファイル | 役割 |
|---|---|
| `Assets/Scripts/Experiment/ExperimentDefinitions.cs` | `ExperimentVideo` / `ExperimentDisplayMode` / `ExperimentGroup` |
| `Assets/Scripts/Experiment/ExperimentTrial.cs` | 1 試行の内容 |
| `Assets/Scripts/Experiment/ExperimentPlan.cs` | 群 + 動画順パターン → 6 試行の提示順 |
| `Assets/Scripts/Experiment/ExperimentBundleCatalog.cs` | 動画種別 → bundle ファイル名 |
| `Assets/Scripts/Experiment/ExperimentTrialHandoff.cs` | シーンをまたいだ試行設定の受け渡し |
| `Assets/Scripts/Experiment/ExperimentSession.cs` | セッション状態とログ書き出し |
| `Assets/Scripts/Experiment/ExperimentLog.cs` | プレイヤーからログへの受け口（static sink） |
| `Assets/Scripts/Experiment/ExperimentCsv.cs` | CSV 行の組み立てとエスケープ |
| `Assets/Scripts/Experiment/ExperimentLogWriter.cs` | CSV ファイル出力 |
| `Assets/Scripts/Experiment/ExperimentPanel.cs` | ワールド空間パネル（局面ごとに中身を差し替え） |
| `Assets/Scripts/Experiment/ExperimentController.cs` | セッション進行・シーン管理 |
| `Assets/Editor/ExperimentSceneBuilder.cs` | シーン生成メニュー |

### StreamingStereoVideoPlayer への変更

実験を行わない通常シーンの挙動は変えていない。`ExperimentTrialHandoff.Pending` が null、`ExperimentLog.Sink` が null のとき、すべて従来どおり動く。

| 箇所 | 変更 |
|---|---|
| `Core.cs` | `startInNormalMode` / `enableNormalModeToggleButton` を追加 |
| `Bundle.cs` | `ResolveInitialNormalMode()` を追加し、`Prepare` する url を条件で選ぶ |
| `Core.partial.cs` | `ApplyPendingExperimentTrialRequest()`、`loopPointReached` の購読、`CurrentVideoTimeSeconds` / `IsVideoPlaying` |
| `UI.Controls.partial.cs` | Display ボタンを生成しない分岐（prefab 由来のものは非アクティブ化） |
| `UI.Runtime.partial.cs` | pause / seek のログフック |
| `UI.ModelPicker.partial.cs` | モデル変更のログフック（prefab 名まで記録） |
| `InteractiveMotion.partial.cs` | インタラクション発火のログフック |

### static sink を挟んだ理由

プレイヤーが `ExperimentController` を直接参照すると、実験を行わない通常シーンでも実験コードに依存する。`ExperimentLog` を no-op 可能な static sink にすることで、プレイヤー側の変更を数行のフック追加だけに留めている。

### テスト

`Assets/Editor/Tests/` に EditMode テストを追加。

| ファイル | 対象 |
|---|---|
| `ExperimentPlanTests.cs` | 試行順の生成（6 パターンが互いに異なる順列であること、各ブロックが 3 動画を 1 回ずつ含むこと、群 A/B が鏡像であること、範囲外パターンで例外を投げること） |
| `ExperimentLoggingTests.cs` | CSV エスケープ、ロケール非依存の数値書式、参加者 ID のサニタイズ、sink 不在時に例外を出さないこと |
| `ExperimentTrialHandoffTests.cs` | bundle 対応表、受け渡しが 1 回限りであること、**StereoOnly のみ normal mode になること** |

---

## 注意点・既知の論点

### モデル変更を許可したことによる交絡

被験者が Change Model を操作できるため、置換あり条件で見るモデルが参加者ごとに変わる。「置換ありのほうが没入感が高い」といった結果が出たとき、モデルの見た目が交絡要因になり得る。

`operations.csv` に `change_model` として **prefab 名まで**記録しているので、分析時に統制できる。統制が難しいと判断した場合は、`StreamingStereoVideoPlayer.enableRuntimeControls` を false にするか、モデルピッカーのボタンを Display ボタンと同様に非生成にする。

**表示サイズの交絡は 2026-08-07 に解消した**。以前はモデルを差し替えた瞬間のフレームの bbox で大きさが決まっていたため、変更タイミングが違う参加者どうしで同じ track が別のサイズに見えていた。現在はスケールの基準を shot 先頭フレームに固定してあるので、いつ変えても同じ大きさになる（[bundle-placement.md](bundle-placement.md) の「スケールの基準フレームは shot 先頭に固定する」）。

### モデル変更の対象選択

Change Model パネルが操作する track は `TryGetRuntimeModelPickerTarget` が決める。優先順位は「直前に選んだ track（`runtimeModelPickerTrackId`）→ `displayTrackIds[0]` → Settings の Track `<` `>` → 直近の自動選択 → 現フレーム最初の person/animal」。

被験者が対象を選ぶ手段は**動画の中の人／動物をコントローラで指してトリガー**（`TrySelectDisplayTrackFromPick`、anchor から 80px 以内の最近傍）。2026-08-07 まではこの入力がマウス専用（`Mouse.current`）で、**Quest では一度も発火せず `displayTrackIds[0]` に固定されていた**。現在はコントローラの aim pose からレイを作って同じ経路に流している。

パネル（Settings / Change Model / bundle picker）を開いている間は、UI 操作のトリガーで背後のスクリーンを拾わないよう pick を止める。選択は `[Pick] track=... pixel=...` としてログに出るので、被験者がどの対象を選んだかは操作ログと突き合わせて確認できる。

### 試行の終了操作

現状は**被験者自身が「視聴を終了」を押す**。Quest 単体では実験者に入力手段がないための設計。パネルは映像を隠さないよう視線の下（既定 -0.5m）に置いてあり、位置は `ExperimentController.trialPanelVerticalOffsetMeters` で調整できる。

実験者が終了を制御したい場合は、この方式を変更する必要がある。

### bundle に除去前動画が入っていること

StereoOnly 条件は `source/pre_removal_stereo_video.mp4` に依存する。入っていない bundle を StereoOnly で指定すると、`ResolveInitialNormalMode()` が **`Debug.LogError` を出して置換モードにフォールバック**する。黙って条件が入れ替わったデータを取らないための設計だが、実験前に必ずログを確認すること。

現時点で 3 本とも同梱済みであることは確認済み。

### 読み込み時間

`bundle_human.svb` は 155MB、`bundle_train.svb` は 117MB。試行ごとにキャッシュを消して展開し直すため、実機では読み込みに十数秒かかる。教示のタイミングをこれに合わせる。
