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

`TestScene` をコピー元に（2026-08-06 に SampleScene から改名。コードの `SourceScenePath` は `Assets/Scenes/TestScene.unity`）`ExperimentScene.unity` と `TrialScene.unity` を生成し、Build Settings に登録する。コピー元にするのは OVRCameraRig / OVRInteractionComprehensive の prefab インスタンスとその override をそのまま引き継ぐため。

### 2. 起動シーンを切り替える

- 実験する: **VisionGraft → Experiment → Set Experiment Scene As Startup**
- 通常の単体再生に戻す: **VisionGraft → Experiment → Set Test Scene As Startup**

**2026-08-28 以降はこの手順を使わない。** `HomeSceneBuilder` が Build Settings の先頭を `HomeScene` にしており、起動直後の Home メニューで「自由に見る」（TestScene）か「被験者実験」（ExperimentScene）を選ぶ。上のメニューを実行すると HomeScene が先頭でなくなり Home を経由しなくなる（下記「2026-09-11 実装棚卸し」）。

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
| **読み込み** | bundle 展開中 | （待つだけ。`bundle_human.svb` は 129 MB（2026-09-09 版）あり実機で十数秒） |
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
| `change_rotation` | `track=... yaw=... frame=...`（Reset は `yaw=0 op=reset`） |
| `change_scale` | `track=... scale=... frame=...`（Reset は `scale=1 op=reset`）。自動フィットに対する倍率 |
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

`bundle_human.svb` は 129 MB、`bundle_train.svb` は 113 MB、`bundle_animal.svb` は 112 MB（いずれも 2026-09-09 の推奨ビルド。旧記述の 155 / 117 MB は 2026-08 世代の値）。試行ごとにキャッシュを消して展開し直すため、実機では読み込みに十数秒かかる。教示のタイミングをこれに合わせる。

## 交絡要因: モデルの配置が表示レートに依存する（2026-09-09 判明・未解決）

**被験者間・時間帯間でモデルの見え方が変わりうる。実験計画上の問題。**

### 何が起きているか

⑧ `RefineDepthFromProjectedBones` は毎 Update に「今の投影から比を計算して深度を寄せる」
不動点反復。**1 動画フレームあたり何回走ったかで収束度が変わる。**

| | tick/フレーム | ⑧ の収束 |
|---|---:|---|
| バッチ（約 465fps）| 15.5 | 収束する |
| **実機 72Hz** | **2.4** | **収束しない。`ratio` が 1.17 前後で固定** |
| 実機 120Hz | 4.0 | 途中まで |
| コマ落ち 45fps | 1.5 | さらに収束しない |

実機ログ（2026-09-09、`bundle_human` 33〜37 秒）で確認:

```
[FPS] update=72.0/s  2.40 tick/フレーム
[DEPTH8] before=0.8124 ratio=1.1581 final=0.9408
[DEPTH8] before=0.8141 ratio=1.1613 final=0.9453   ← 押し出したのに戻っている
```

毎フレーム基本配置が `anchorZ` に戻し、⑧ が押し出す、の繰り返し。
**2.4 回では追いつかず、モデルは常に 17% 大きいまま。**

### 実験への影響

- **被験者ごとに違う**: 端末の表示設定（72/90/120Hz）が違えば配置が変わる
- **同じ被験者でも区間で違う**: 描画負荷でコマ落ちすれば、その区間だけ大きさが変わる
- 「モデルの見え方」を扱う研究では**制御できていない独立変数**になる

### 対処の方向

**⑧ を 1 回の呼び出しの中で収束させる**（tick 回数に依存しない形にする）。
平滑化も同様に、1 回の呼び出しで完結する形へ直す必要がある。

**120Hz に上げるのは解決にならない**（4 tick でも収束しないうえ、維持できずコマ落ちすると
かえって不規則になる）。

### 実験を回す前に必ず確認すること

- 全端末の表示レートを揃える
- 実行中の fps を記録する（`logDeviceDiagnostics` の `[FPS]` ログ）
- コマ落ちした試行を除外できるようにする

## 論文側の実験計画との食い違い（2026-09-10、質問回答時に判明）

論文側（`Docs/修論やり取り用/questions/questions_2026-09-10.md`）は
「単眼動画 / 空間動画 / 空間動画＋3Dモデル＋インタラクティブアニメーション」の **3 条件**を想定している。
現行実装との差は 3 点。回答は `Docs/修論やり取り用/answers/answers_2026-09-10.md` の A-4。

| 論文側の想定 | 現行実装 |
|---|---|
| 単眼動画条件 | **未実装**。片目映像を両目に出す経路が無い（シェーダの `_EyeMode` を固定するモードを足せば同じスクリーンで出せる） |
| 空間動画＋モデル＋アニメーション | 「置換あり」条件の中で Motion トグルが ON/OFF できるだけ。**TrialScene の serialize 値は `enableInteractiveMotion: 0` で、実験では OFF で始まる**（コード既定は true だがシーン値が勝つ） |
| 3 条件 | `ExperimentDisplayMode` は `StereoOnly` / `ModelReplaced` の **2 条件**。`ExperimentPlan` も 3 動画 × 2 条件 = 6 試行前提 |

3 条件で回すなら、`ExperimentDisplayMode` の追加・`ExperimentPlan` の拡張・単眼提示モード・条件ごとの Motion 固定、の 4 点が要る。
どちらに寄せるかは論文側と相談中（2026-09-10 時点）。

## 2026-09-11 実装棚卸し（docs と実装・シーン値の差分）

実験フロー全体をコード・シーンの serialize 値・Build Settings から読み直した。上の本文は 2026-08-05〜16 の実装時点の記述で、その後の変更（Home シーン、モデル選択の永続化、bundle 整理）が反映されていなかった箇所を以下に記録する。

### 起動から試行までの実際の経路

```
アプリ起動
 └ HomeScene（Build index 0、HomeMenu）
     ├ 「自由に見る」  → TestScene を Single ロード。HomeLaunchHandoff で bundle ピッカーを出す
     └ 「被験者実験」  → ExperimentScene を Single ロード
          └ ExperimentController: Setup → Waiting → Loading → Trial → Waiting … → Finished
               各 Trial: ExperimentTrialHandoff.SetPending → TrialScene を Additive ロード
                         → SetActiveScene(TrialScene) → プレイヤーの Start() が Consume
                         → IsVideoPlaying まで待つ → 試行パネル → 「視聴を終了」
                         → SetActiveScene(base) → TrialScene アンロード → UnloadUnusedAssets
```

- HomeScene / ExperimentScene はどちらも OVRCameraRig + OVRInteractionComprehensive + EventSystem + Directional Light + Global Volume を持つ（`HomeSceneBuilder` が ExperimentScene から生成し、Controller を HomeMenu に差し替えたもの）。TrialScene は VideoPlayerRoot + Directional Light + Global Volume のみ
- Build Settings の現行順: HomeScene → TestScene → ExperimentScene → TrialScene（全て有効）
- **セッション終了画面にはボタンが無い**（`ShowFinishedPanel` は specs = null）。次の参加者に移るには**アプリを再起動して Home からやり直す**。Home へ戻る経路も ExperimentController には無い
- ExperimentScene を Single ロードし直すたびに `participantNumber` は Inspector 値（1）に戻る。参加者 ID はセットアップ画面で「参加者 ＋」を押して合わせる
- 試行中にアプリが落ちる／ヘッドセットを外す場合: `OnApplicationPause(true)` で Flush、`OnDestroy` / `OnApplicationQuit` で進行中の試行を `aborted=1` として trials.csv に書く。**実験者が手動で試行を中断するボタンは無い**

### 本文の実装表に載っていないファイル

| ファイル | 役割 | 追加日 |
|---|---|---|
| `Assets/Scripts/Experiment/HomeMenu.cs` | 起動直後の入口パネル（自由に見る / 被験者実験） | 2026-08-28 |
| `Assets/Scripts/Experiment/HomeLaunchHandoff.cs` | Home →「自由に見る」で bundle ピッカーを出す 1 回限りの受け渡し | 2026-08-28 |
| `Assets/Scripts/Experiment/ExperimentSessionOverrides.cs` | 被験者がセッション中に変えたモデル・回転・スケールをメモリだけで保持（試行をまたぐ、参加者で捨てる）。実験中は `persistentDataPath/model_selection.json` を**読むだけで書かない**（[model-selection-persistence.md](model-selection-persistence.md)） | 2026-08-28 |
| `Assets/Editor/HomeSceneBuilder.cs` | HomeScene を ExperimentScene から生成し Build Settings の先頭に置く | 2026-08-28 |
| `StreamingStereoVideoPlayer.Customization.partial.cs` | ① 基準ファイルに ② セッション上書きを重ねて復元。`ExperimentSessionOverrides.Active` の間は基準へ保存しない | 2026-08-28 |
| `StreamingStereoVideoPlayer.GrabRotate.partial.cs` | 掴んで 3 軸回転。`change_rotation` を `op=grab yaw= pitch= roll=` で記録 | 2026-08-31 |

### operations.csv に実際に出る action（コードから抽出）

本文の表に無いものを含めた現行の全一覧。

| action | detail | 出る場所 |
|---|---|---|
| `trial_begin` / `trial_end` / `trial_abort` | 試行の内容 / 空 | `ExperimentSession` |
| `trial_end_pressed` | 空 | `ExperimentController.RequestTrialEnd` |
| `pause` / `resume` | 空 | `UI.Runtime` |
| `seek` | 0..1 の正規化位置 | `UI.Runtime` |
| `seek_key` | `frame=`（モデル編集タブのキーフレーム送り） | `UI.ModelEdit` |
| `video_loop` | 何周目か | `ExperimentSession.RecordVideoLoop` |
| `change_model` | `track= category= index= prefab=`（非表示は prefab が `HiddenModelName`） | `UI.ModelPicker`（2 箇所） |
| `change_rotation` | `track= op=grab yaw= pitch= roll= frame=` / `track= yaw=0 op=reset` / `track= op=delete_key frame= yaw= scale=` | `GrabRotate` / `UI.Runtime` |
| `change_scale` | `track= scale= frame=` / `track= scale=1 op=reset` | `UI.Runtime` |
| `model_panel_tab` | `edit` / `models` | `UI.ModelEdit` |

interactions.csv の `kind`: `random_Static` / `random_Dynamic`（detail `subject=human|animal`）、`system_frameout`（detail `subject= frame=`）。

### TrialScene と TestScene のプレイヤー設定差分（serialize 値、2026-09-11 実測）

97 フィールド中、違うのは 4 つだけ。

| フィールド | TestScene | TrialScene | 備考 |
|---|---|---|---|
| `bundleFileName` | `bundle_animal.svb` | `bundle_human.svb` | 実験では `ExperimentTrialHandoff` が上書きするので無関係 |
| `headTransform` | リグ参照 | `{fileID: 0}` | 意図どおり（リグはベースシーン側） |
| `enableNormalModeToggleButton` | 1 | 0 | 意図どおり（Display ボタンを出さない） |
| **`enableHumanBoneLengthCorrection`** | **0** | **1** | **意図しない差。** TrialScene は 2026-08-16 の生成時の値のまま。TestScene は 2026-08-26（コード既定も 2026-08-21）に OFF へ変更された（[smpl-retargeting.md](smpl-retargeting.md) の「脚の骨長補正が足首のずれを作っていた」、[bundle-placement.md](bundle-placement.md) の「骨長補正 OFF で『ボールが離れて見える』原因」）。**実験シーンの Human だけ脚の骨長補正が効き、人の絶対深度が約 10 cm 違う状態**（同節の実測: ON 0.9175 m / OFF 1.0195 m） |

`enableInteractiveMotion` は両シーンとも 0、`screenDistanceMeters` は両方 1.0、`rememberTrackCustomization` は両方 1、`enableRuntimeControls` は両方 1。

`enableHumanBoneLengthCorrection` をどちらに揃えるかは未決（上の 2 つの docs で「姿勢一致 vs 絶対深度」のトレードオフとして保留中）。揃えないまま実験を回すと、開発中に TestScene で見ていた Human の見え方と実験で被験者が見る Human が違う。

### テスト（EditMode、`Assets/Editor/Tests/`）

`ExperimentPlanTests` 15 件、`ExperimentLoggingTests` 17 件、`ExperimentTrialHandoffTests` 11 件。試行順の生成・CSV 書式・受け渡しの 1 回性・StereoOnly のみ normal mode になること、を押さえている。シーン遷移（Additive ロード、アクティブシーン切り替え、アンロード後のリーク）を検証する自動テストは無く、実機での通し確認のみ（2026-08-31 / 09-01 / 09-05 / 09-07 / 09-09 / 09-10）。

## 操作チュートリアル（2026-09-11 実装）

被験者に 3 つの操作を教える練習パートを試行の前に挟む。**実験の 3 本とは別の動画**を使う（練習で実験刺激を先に見せない）。

| 段階 | 教える操作 | 完了の検出 |
|---|---|---|
| 1/4 | コントローラのレイ + トリガーでボタンを押す | 説明パネルの「次へ」が押された |
| 2/4 | A ボタン（左手は X）で動画を止める | プレイヤーの操作ログ `pause` |
| 3/4 | もう一度 A ボタンで再開する | 操作ログ `resume`（`pause` の後のみ数える） |
| 4/4 | コントロールバーの「Model」ボタンでモデルを変える | 操作ログ `change_model` |
| 終了 | 自由に試して「チュートリアルを終了」を押す | ボタン |

順番どおりでなくても済んだ操作は数える（先に Model を変えた参加者にもう一度やらせない）。各段階に「スキップ」があり、押した段階は `tutorial_step_skipped` として残る。

### 仕組み

- 試行と同じ経路: `ExperimentTrialHandoff` に **ModelReplaced** の指示（`ExperimentVideo.Tutorial`、`trialIndex = -1`）を置き、TrialScene を Additive ロードする。プレイヤー側の変更は無し
- 段階の検出は [ExperimentTutorial.cs](../Assets/Scripts/Experiment/ExperimentTutorial.cs) が `ExperimentLog.Sink` を横取りして行う。受け取った操作はセッションへそのまま転送するので operations.csv にも残る
- 説明パネルは `ExperimentPanel` を流用。段階が進んでもパネルの位置は動かさない（`Show(..., keepPlacement: true)`）。作り直しはボタンのクリックハンドラ内ではなく次の Update で行う（押したボタン自身を壊さないため）
- チュートリアル用 bundle が無い・再生できないときは `tutorialLoadTimeoutSeconds`（既定 120 s）で諦め、「チュートリアル無しで続行」の画面を出す。**試行のほうは従来どおり待ち続ける**（試行は飛ばせないため）

### いつ挟むか（`ExperimentController.tutorialTiming`）

| 値 | 動作 |
|---|---|
| `BeforeFirstTrial`（既定） | セッション開始直後、1 試行目の前に 1 回 |
| `BeforeEachBlock` | 各条件ブロックの先頭（1 試行目と 4 試行目の前）に 1 回ずつ |
| `None` | 挟まない |

待機画面の「スキップ」で実験者が飛ばせる（`tutorial_skipped` を記録）。

**Home の「チュートリアル」**からは、セッションもログも作らずに同じチュートリアルだけを回して Home へ戻れる（実験者の動作確認用。`HomeLaunchHandoff.RequestTutorialOnly`）。この間もモデル変更は基準ファイル `model_selection.json` に書かない。

### パネルの位置

`tutorialPanelSizeMeters`（既定 0.64 × 0.48 m、canvas 1200×900 と同じ 4:3 で文字を潰さない）と `tutorialPanelOffsetMeters`（既定 x +0.8, y −0.45 m。視点 1.2 m 先の右下）。コントロールバーは画面下中央に出るので、それと重ならない位置にした。**実機での見え方は未確認**。重なる・読めないときは Inspector で動かす。

### ログ

operations.csv に次が増える。チュートリアル中の行は **`trial_index = -1`**（後半ブロック前のチュートリアルでも直前の試行番号は書かない）。

| action | detail |
|---|---|
| `tutorial_begin` | `bundle=... before_block=0|1` |
| `tutorial_step_skipped` | `PressButton` / `PausePlayback` / `ResumePlayback` / `ChangeModel` |
| `tutorial_end_pressed` | 空 |
| `tutorial_end` | `completed=0|1 skipped=N step=... duration_sec=... before_block=...`。読み込み失敗は `load_failed ...`、中断は `aborted ...` |
| `tutorial_skipped` | 待機画面で実験者が飛ばした。`before_block=...` |

チュートリアル中の `pause` / `resume` / `change_model` などもそのまま記録される。頭部姿勢は記録しない。trials.csv には行を書かない。

### bundle

`ExperimentBundleCatalog.tutorialBundleFileName`（既定 `bundle_tutorial.svb`）。解決順は他の 3 本と同じ（共有ストレージ → StreamingAssets）。条件は「人か動物が 1 体は写っている」「`video.mp4` が H.264」の 2 つ。

**2026-09-11 の暫定版**: 旧 `bundle.svb`（2026-07-30、`01_dog` クリップ、289 フレーム ≈ 9.6 秒、Human + Animal 入り）は実験 3 本とは別クリップだが `video.mp4` が mp4v で Quest では黒画面になる（[ADR 0003](adr/0003-normal-mode-playback-video.md)）。そこで ffmpeg で H.264（libx264 crf 18、289 フレーム維持）に再エンコードし、`video.mp4` / `meta.bin` / `manifest.json` だけを無圧縮 ZIP に詰め直したものを `Assets/StreamingAssets/bundle_tutorial.svb`（6.5 MB、gitignore 対象）と `Docs/tmp/bundle_tutorial.svb` に置いた。sha256 `548ecc8e…`。`meta.bin` は旧世代（zlib 圧縮、`depth_policy` 無し、shots 無し）だが現行ランタイムは両方読める。**正式版は生成側に依頼中**（[bundle-shared/README.md](bundle-shared/README.md) 2026-09-11）。実機（Quest）で pushed した共有ストレージに無ければ APK 内のこのファイルが使われる。

### 追加・変更したファイル

| ファイル | 変更 |
|---|---|
| `Assets/Scripts/Experiment/ExperimentTutorial.cs` | 新規。段階の状態機械 + 操作ログの横取り |
| `ExperimentController.cs` | `Phase.Tutorial`、`tutorialTiming` / パネル位置 / タイムアウトの Inspector 項目、`RunTutorialRoutine`、Home からのチュートリアル専用モード、`UnloadTrialSceneRoutine` に共通化 |
| `ExperimentSession.cs` | `BeginTutorial` / `EndTutorial`、チュートリアル中は `trial_index = -1` |
| `ExperimentDefinitions.cs` | `ExperimentVideo.Tutorial`、`ExperimentTutorialTiming` |
| `ExperimentBundleCatalog.cs` | `tutorialBundleFileName` |
| `ExperimentPanel.cs` | `Show(..., keepPlacement)` |
| `HomeLaunchHandoff.cs` / `HomeMenu.cs` | Home に「チュートリアル」ボタン |
| `Assets/Editor/Tests/ExperimentTutorialTests.cs` | 新規。段階送り・先回り・スキップ・転送・Home 受け渡し（15 件） |

ExperimentScene の serialize 値には新しい項目（`tutorialTiming` 等）が無いので、コードの既定値（BeforeFirstTrial、右下配置、120 s）が使われる。シーンを保存すると書き込まれる。

### 未確認

- 実機での通し（パネルの位置・読みやすさ、A ボタン検出、Model ボタンの案内が実物のラベルと合っているか）。**実機で見て採否を決める**
- 暫定 bundle が Quest で再生されること（H.264 化はしたが実機未確認）
