# モデル選択の永続化（設計、2026-08-28）

VR 内で選んだモデルを、次回ロード時の既定にする。あわせて Else もピッカーの対象にする。
**未実装。** ここは着手前の設計と、その根拠になる実測をまとめたもの。

## 現状（実測）

| | |
|---|---|
| ピッカー本体 | `StreamingStereoVideoPlayer.UI.ModelPicker.partial.cs`（765 行） |
| 対象カテゴリ | **Human と Animal のみ。Else は対象外** |
| 選択の保持 | `selectedModelIndexByTrack`（`Dictionary<uint,int>`、MonoBehaviour の素のフィールド） |
| 解決順 | `selectedModelIndexByTrack` → `trackModelIndices`（Inspector） → `selectedHumanIndex` / `selectedAnimalIndex` |
| 消えるタイミング | **試行 = シーンのロード／アンロード**（`experiment-flow.md`）なので毎回消える |

### 対象 track の決め方は「指して選ぶ」

`StreamingStereoVideoPlayer.Meta.cs` の pick 処理が、VR で指したオブジェクトの trackId を
`runtimeModelPickerTrackId` に入れる（`[Pick] track=... pixel=... eye=...`）。パネルには
`Track {trackId} | Selected: {name}` が出る。

**したがって train の Else が 8 個あっても「どれを変えているか」の UX 問題は無い。** 既存の
仕組みで解決済み。

## 保存先

### 訂正: 実機は StreamingAssets から読んでいない

当初「Quest では `StreamingAssets` が APK 内で読み取り専用だから書き込めない」と書いたが、
**実機の経路が違った。訂正する。**

`EnsureBundleAndPrepareVideo(string selectedBundlePath = null)` には 2 経路ある。

| 経路 | 使う場面 | 書き込み |
|---|---|---|
| `selectedBundlePath == null` | エディタ・バッチ。`StreamingAssets` から `UnityWebRequest` でメモリに読む | 不可 |
| `selectedBundlePath` 指定 | **実機。`BundlePicker` で選んだファイル**（`bundlePickerInitialDirectory = "/storage/emulated/0"`） | **可能** |

実機では Quest の共有ストレージから `.svb` を選ぶので、**その隣に自前のファイルを置ける。**

### ただし `.svb` の中には書かない

- 生成側プロジェクトの成果物で、書き込むとデータ契約が壊れる（CLAUDE.md「bundle 使用ルール」）
- **再生成で消える**

**自前の別ファイルにする。**

### 置き場所は `Application.persistentDataPath`

| 案 | 利点 | 欠点 |
|---|---|---|
| **`persistentDataPath/model_selection.json`** | **エディタと実機で経路が 1 本。** 既存の `ExperimentLogs` と同じ場所（`ExperimentLogWriter`） | ヘッドセットを変えると引き継がれない |
| `.svb` と同じディレクトリ | フォルダごとコピーすれば選択も付いてくる | StreamingAssets 経路（エディタ・バッチ）では書けないので**分岐が要る** |

**`persistentDataPath` を推す。** バッチ測定でも同じ経路が通るので、挙動が実機とずれない。

`.svb` を再生成して差し替えても、キーが `inputs.video_mp4` なのでこのファイルは無関係に残る（下記）。

## 動画の同一性キー: `manifest.inputs.video_mp4`

**`.svb` のファイル名をキーにしてはいけない。** 再生成で名前が変わり、記憶が消える。

実測（`scratchpad/inputs_probe.py`）:

| bundle | `generated_at` | `inputs.video_mp4` |
|---|---|---|
| `bundle_animal.svb` | 2026-08-06 | `animal_demo_work_1280x720_rose_2x2_video_3D_with_audio.mp4` |
| `bundle_animal_shots_depthdriftfix_shotsfix.svb` | 2026-08-27 | **同上** |
| `bundle_human.svb` | 2026-08-19 | `Human_demo_work_1280x720_rose_2x2_video_3D_with_audio.mp4` |
| `bundle_human_shots_driftfix_test.svb` | 2026-08-20 | **同上** |
| `bundle_train.svb` | 2026-08-19 | `train_demo_work_1280x720_rose_2x2_video_3D_with_audio.mp4` |

**再生成をまたいで同一。** これを動画の ID にする。

## 保存するのは index ではなく prefab 名

`LoadPrefabsFromResources` は名前順ソート、Animal はさらに `AnimalModelPriorityOrder` で
並べ替える。**モデルを 1 つ足すと以降の index が全部ずれる**（[[model-index-numbering-rule]]）。
名前なら追加・並べ替えで壊れない。

## 手動回転（yaw）も同じ問題を抱えている

VR でオブジェクトを選んで向きを調整する機能がある（`StreamingStereoVideoPlayer.ManualYaw.partial.cs`）。

| | |
|---|---|
| 状態 | `manualYawKeyframesByTrack`（`Dictionary<uint, SortedDictionary<int,float>>`） |
| 中身 | **trackId → (フレーム番号 → yaw 角度)**。キーフレーム間は補間される |
| 操作 | 設定パネルのスライダー・リセット・対象送り（`UI.Runtime.partial.cs`） |
| 永続化 | **無し。** モデル選択と同じくシーンのロードで消える |

**モデル選択より永続化の価値が高い。** キーフレームは手で作った作業内容なので、毎回作り直すのは
モデルを選び直すのとは負担が違う。

同じキー（動画 × trackId）で同じファイルに入れられる。

### 注意: キーフレームはフレーム番号に紐づく

bundle を再生成してフレーム数が変わると、キーフレームが別の場面に当たる。
実測では animal も human も再生成をまたいで `num_frames` が同じだった（2120 / 2167）が、
保証は無い。**`num_frames` を一緒に保存して、読み込み時に食い違ったら破棄してログを出す。**

### 見つかった穴: 回転は `operations.csv` に記録されていない

`ExperimentLog.Operation` が記録しているのは `trial_end_pressed` / `change_model` / `seek` /
`pause` / `resume` の 5 つ。**回転操作は入っていない。**

回転 UI は `enableRuntimeControls` の設定パネル内にあり、`experiment-flow.md` は被験者が
「設定」を操作できるとしているので、**被験者が触れるのに記録されない**状態になっている。
モデル変更は prefab 名まで記録しているのと不揃い。

`experiment-flow.md`「モデル変更を許可したことによる交絡」にも回転の記述が無い。
**永続化とは別件だが、実験前に塞ぐべき穴。**

## 保存の形

`Application.persistentDataPath/model_selection.json`

```json
{
  "animal_demo_work_1280x720_rose_2x2_video_3D_with_audio.mp4": {
    "numFrames": 2120,
    "tracks": {
      "0": { "model": "36_LabradorDog", "yaw": { "0": -12.5, "480": 8.0 } },
      "1": { "model": "39_Lynx" }
    }
  },
  "train_demo_work_1280x720_rose_2x2_video_3D_with_audio.mp4": {
    "numFrames": 1830,
    "tracks": {
      "0": { "model": "03_Boxcar" },
      "5": { "model": "01_Locomotive", "yaw": { "1300": 45.0 } }
    }
  }
}
```

**粒度は「動画 × trackId」。** train は Else が 8 track あり、別々のオブジェクトなので
カテゴリ単位では足りない。モデル選択と yaw キーフレームを同じ track エントリに入れる。

`numFrames` は yaw キーフレームの整合チェック用（上の「注意」参照）。

## 読み込みの入れ方

**解決順のコードには触らない。** 起動時（meta 読み込み後、最初のインスタンス生成前）に
永続化ファイルを読み、名前を index に解決して `selectedModelIndexByTrack` に流し込むだけ。

以降は既存の解決順がそのまま動く。`selectedModelIndexByTrack` が最優先なので、
永続化された選択が Inspector の `trackModelIndices` を上書きする（要望どおり）。

`rememberLastSelectedModel` フラグを付けて**既定 false**。バッチ測定が Inspector 設定に
依存しているので、既定 true だと A/B が汚染される。

## 名前が見つからないとき

モデルの追加・削除・改名で保存名が消える。**既定に戻してログを出す。** 落とさない、
別のモデルを選ばない。

## Else をピッカー対象にする

変更は 3 箇所。

| 箇所 | 変更 |
|---|---|
| `ResolveRuntimeModelPickerPrefabs(bool isAnimal)` | カテゴリ 3 値を受けて `humanPrefabs` / `animalPrefabs` / `elsePrefabs` を返す |
| `TryGetRuntimeModelPickerTarget` の最終ループ | `!IsCategoryPerson && !IsCategoryAnimal` で `continue` している判定を外す |
| `isAnimal`(bool) を引き回している箇所 | カテゴリ値に置き換え（`UpdateRuntimeModelPickerUiState` のタイトル・エラー文言も 3 分岐） |

`TryResolveRuntimeModelPickerTargetFromTrack` と pick 側の raycast が Else を拾うかは
実装時に確認する（未確認）。

## 実験中は「読むだけ・書かない」（2026-08-28 決定）

### 当初案（ファイルを消す）は撤回

最初は「セッション開始時に永続化ストアを消す」と書いたが、**これだと事前に仕込んだ
向きとモデルが参加者 1 人目の時点で消える。** 採れない。

### Else は向きのデータを持たないので、仕込みが要る

train の全 8 track は `skel=0 smpl=0 smal=0`（実測）。**anchor と bbox しか無く、向きの
推定値が存在しない。** だから Else の向きは人が仕込むしかない。

手動 yaw は Else にも効く（`ApplyManualTrackYawOffset` は `Playback.partial.cs:199` で
カテゴリ分岐の手前に置かれており、`GetAvailableTrackIdsForManualRotation` も
`trackInstances` を全部並べるだけでカテゴリで絞っていない）。**追加実装は不要。**

### 方針: 3 層にする

| 層 | 置き場所 | 寿命 | 誰が書くか |
|---|---|---|---|
| **① 基準** | `persistentDataPath/model_selection.json` | 永続 | **研究者が通常利用で仕込む** |
| **② セッション上書き** | `static`（メモリ） | **1 セッション**（試行シーンのロードをまたぐ） | 被験者の調整 |
| ③ 適用値 | — | 1 試行 | ① に ② を重ねたもの |

- **① は毎試行・毎参加者で必ず読み込まれる**（消さない）
- **② は同じ参加者のセッション中は残る**（試行 1 の調整が試行 3 でも効く）
- **参加者が変わったら ② をクリア** → ① だけの状態に戻る

| 場面 | 読み | 書き先 |
|---|---|---|
| 通常利用（`ExperimentTrialHandoff.Pending` が null） | ① | **①（ファイル）** |
| 被験者実験 | ① + ② | **②（メモリ）** |

**ファイルは実験中に一切書き換えない。** 仕込んだ内容が参加者の操作で汚れない。

### 置き場所

`ExperimentTrialHandoff` は **`static` クラス**で、試行シーンのロード／アンロードをまたいで
残る（同ファイルのコメント参照）。② も同じ性質でよいので、隣に `static` クラスを 1 つ足す。

`ExperimentTrialHandoff` は「1 回だけ Consume して消す」設計なので**兼用しない。** 寿命が違う。

キー構造は ① と同じ（動画 × trackId）。②が動画ごとに分かれるので、
animal の犬をいじっても train の貨車には影響しない。

### クリアするタイミング

| 場所 | 処理 |
|---|---|
| `ExperimentController.StartSession()`（`ExperimentController.cs:204`） | ② をクリア（参加者が変わる） |
| `session.Dispose()`（同 115 行付近） | ② をクリア（実験を抜けたあと手動でシーンを開いても残らないように。`ExperimentTrialHandoff.Clear()` と同じ配慮） |

### 記録

`operations.csv` の `change_model` は prefab 名まで記録しているので、被験者が何に変えたかは
追える。**ただし回転は記録されていない**（下記の穴）。②に入る操作なので、記録が無いと
分析側から復元できない。

## 実装量の見積もり

| ファイル | 内容 |
|---|---|
| `StreamingStereoVideoPlayer.Core.cs` | `rememberLastSelectedModel` フラグ |
| `StreamingStereoVideoPlayer.Model.cs` | 保存・復元（JSON 読み書き、名前↔index 解決） |
| `StreamingStereoVideoPlayer.UI.ModelPicker.partial.cs` | Else 対応 + 選択時の保存フック |

新規 80 行程度、既存の変更は Else の 3 分岐が中心。

---

## 実装（2026-08-28）

| ファイル | 内容 |
|---|---|
| `ManifestData.cs` | `inputs.video_mp4` を読めるように `ManifestInputsData` を追加 |
| **`TrackCustomization.cs`**（新規） | データモデル + `persistentDataPath/model_selection.json` の読み書き。`MiniJson` は Parse しか無いので書き出しは手書き |
| **`ExperimentSessionOverrides.cs`**（新規） | ② セッション上書き（`static`、メモリ）。`Active` が true の間はプレイヤーが基準ファイルへ書かない |
| **`StreamingStereoVideoPlayer.Customization.partial.cs`**（新規） | 復元・保存。`RestoreTrackCustomization` は ① に ② を重ねて `selectedModelIndexByTrack` / `manualYawKeyframesByTrack` を埋める |
| `Core.cs` | `rememberTrackCustomization`（既定 false） |
| `Bundle.cs` | `LoadHumanSmplSidecar` の直後に `RestoreTrackCustomization()` |
| `Playback.partial.cs` | `ResolvePrefabsForCategory` を追加。`ResolveTrackPrefab` の冒頭で `ResolvePendingModelSelection` |
| `UI.ModelPicker.partial.cs` | Else 対応（3 カテゴリ）+ 選択時に `PersistModelSelection` |
| `UI.Runtime.partial.cs` | 回転操作で `PersistManualYaw` + **`change_rotation` を `operations.csv` に記録**。スケール操作で `PersistManualScale` + `change_scale` |
| `ExperimentController.cs` | `StartSession()` で `BeginSession()`、セッション終了で `EndSession()` |

### prefab 名 → index は遅延解決

復元の時点では track のカテゴリが分からない（`meta.bin` を走査すれば分かるが高くつく）。
`pendingModelNameByTrack` に名前のまま預け、**カテゴリが確定する `ResolveTrackPrefab` で
1 回だけ解決**する。見つからなければ既定のまま進み、警告を出す。

### Else をピッカー対象にするために外した制限

| 箇所 | 変更 |
|---|---|
| `TryGetRuntimeModelPickerTarget` の最終ループ | person / animal 以外を `continue` していた判定を削除 |
| `TryResolveRuntimeModelPickerTargetFromTrack` | 同じ判定で `return false` していたのを削除 |
| `UpdateRuntimeModelPickerEntryButtons` | ボタンの活性条件が `IsCategoryPerson \|\| IsCategoryAnimal` だったのを常時活性に |
| `ResolveRuntimeModelPickerPrefabs(bool isAnimal)` | `byte categoryId` を受けて 3 カテゴリを返す |

### 動作確認の手順（未実施）

1. `rememberTrackCustomization` を ON にして通常起動 → モデルと向きを変える →
   `persistentDataPath/model_selection.json` ができることを確認
2. シーンを開き直して復元されることを確認（`[Customization] applied track=...`）
3. 実験フローで `StartSession` → 調整 → 試行をまたいで残る → 次の参加者で消えることを確認
4. `operations.csv` に `change_rotation` が出ることを確認

### 動作確認（2026-08-28、バッチ）

コンパイル通過（`error CS` / `NullReference` / `Exception` ともゼロ）。

`persistentDataPath`（エディタでは `AppData/LocalLow/DefaultCompany/stereoCrafter`）に
既定と違うモデル（`01_Wolf`。Inspector の `trackModelIndices` は track0→36 = LabradorDog）と
yaw キーフレーム 2 点を仕込んで `-remember true` で実行した。

```
[Customization] loaded 1 video(s) from .../model_selection.json
[Customization] restored video=animal_demo_work_....mp4 models=1 yawTracks=1 session=normal(書き込み可)
[Customization] applied track=0 model=01_Wolf index=1
```

**キャプチャでも `01_Wolf` が表示されていることを確認。** 永続化された選択が Inspector の
`trackModelIndices` を上書きする、という設計どおりに動いている。

確認できていないもの（実機が要る）:

- VR のピッカーで選んだときの**保存**（バッチでは UI を操作できない）
- Else をピッカーで選べること
- yaw の**復元後の見た目**（`yawTracks=1` のログまでは確認、角度の正しさは未確認）
- 実験フローでのセッション上書き（②）の挙動

テスト用に仕込んだ `model_selection.json` は確認後に削除した。

---

## bundle ピッカーの起点をコードに移した（2026-08-28）

### 見つかった事故

`bundlePickerInitialDirectory` は Inspector の公開フィールドで、コードの既定は
`"/storage/emulated/0"` だった。しかし **`TestScene` と `TrialScene` に別の値が焼き込まれていた**。

```
bundlePickerInitialDirectory: /storage/emulated/0/Android/data/com.UnityTechnologies.com.unity.template.urpblank/files
```

これは `persistentDataPath`。**コードに書いてある `/storage/emulated/0` は一度も効いていなかった。**
しかも `/Android/data` は Android 11 以降 MTP から見えないので、PC から `.svb` を置くのが面倒な
場所でもある。

**シーンの serialize 値がコードの既定を黙って上書きする**という、このセッションで何度も踏んだ罠
（[[verify-code-path-executes]]）と同型。

### 対応

公開フィールドを廃止し、コードの探索順に置き換えた。

```csharp
private static readonly string[] BundlePickerSearchDirectories =
{
    "/storage/emulated/0/VisionGraft",   // .svb を置く既定
    "/sdcard/VisionGraft",
    "/storage/emulated/0",               // 無ければ共有ストレージ直下から手で辿る
    "/sdcard",
};
```

上から順に実在する最初のディレクトリを使い、どれも無ければ `persistentDataPath` →
`dataPath` に落ちる。**シーンからは該当行を削除した。**

### `/storage/emulated/0/VisionGraft` を既定にした理由

| | |
|---|---|
| MTP から見える | `/Android/data` は Android 11 以降見えない |
| adb push が単純 | `adb push x.svb /sdcard/VisionGraft/` |
| システムフォルダに混ざらない | ルート直下は雑然としている |
| 設定と一緒に運べる | 将来 `model_selection.json` をここに置けばフォルダごとコピーで済む |

存在しなければ `/storage/emulated/0` に落ちるので、push する前でも壊れない。
**ディレクトリの自動作成はしない**（ピッカーは読むだけの機能なので）。

## StreamingAssets を動画ごと 1 本に整理した（2026-08-28）

| ファイル | 中身 | generated_at | frames | shots |
|---|---|---|---|---|
| `bundle_human.svb` | driftfix 版 | 2026-08-20 | 2167 | 1 |
| `bundle_animal.svb` | depthdriftfix + shotsfix 版 | 2026-08-27 | 2120 | **28** |
| `bundle_train.svb` | 据え置き（再生成不要と確認済み） | 2026-08-19 | 1830 | 1 |
| `bundle.svb` | 別動画（`01_dog_propainter`、289f）。git に載っている唯一の bundle | 2026-06-22 | 289 | 0 |

**名前は元のままで中身が再生成版に差し替わっている。** ファイル名から版を判別できないので、
**動画の同一性も版も `manifest` を見て確かめること**（`inputs.video_mp4` と `generated_at`）。

---

## EditMode テストの既知の失敗（2026-08-28 時点のベースライン）

今回の一連の変更後に全件走らせた結果。**508 件中 483 passed / 25 failed。**

自分の変更で壊したのは 1 件だけで、直した（下記）。**残る 25 件はすべて変更前から失敗していた。**
今後の変更はこの 25 件を基準に判断すること。

### 今回直した 2 件

| テスト | 内容 |
|---|---|
| `AnimalPoseJointChainsTests` | チェーンを D-007 の対応表で訂正したのに旧値（誤り）を期待していた。**私が壊した唯一の 1 件。** 正しい値に更新し、左右で同じ keypoint を使っていないかの検査も追加 |
| `AnimalSmalFkPolicyTests` | 2026-07-16 に tail 25/26 を body_pose 駆動へ変えたのに、期待値が変更前のまま。既存の失敗だが同じ領域なので更新 |

### 残る 25 件（すべて既存）

| 件数 | テスト | 原因 |
|---|---|---|
| 12 | `UniVRM10.*` / `UniGLTF.*` | `Tests/Models/Alicia_vrm-0.51/*.vrm` が存在しない。サードパーティ |
| 3 | `AnimalBodyBasisResolverTests` | **`TrackedJointPoints.TryGet` がゼロベクトルを無効として弾く**（`sqrMagnitude <= 1e-10`）のに、fixture が `joints[7] = Vector3.zero` を使う。テストと実装のどちらが正かは未判断 |
| 3 | `HumanOtherContactCorrectionIntegrationTests` | `TestScene` を開くが、そこの bundle は **animal**。Human のボールを計測できない |
| 3 | `HumanSmplRootPlacementMathTests` | 未調査 |
| 2 | `RuntimePlaybackControllerTests` | 未調査 |
| 1 | `SceneObjectWriterTests` | 未調査 |
| 1 | `ManualYawGuideFactoryTests` | edit mode で `renderer.material` を呼んでいる（`sharedMaterial` にすべき） |

### 実行方法

```powershell
powershell -File scratchpad/run_tests.ps1 -Platform EditMode
```

`-runTests -testPlatform EditMode -testResults <xml>` を batchmode で叩くだけ。**Unity を閉じてから**。

---

## 起動導線を Home シーンに集約した（2026-08-28）

`Build And Run` はビルド設定の**先頭シーン**から起動するので、入口を 1 枚作って分岐させる。

```
0: HomeScene       ← 起動。「自由に見る」「被験者実験」の 2 ボタン
1: TestScene       ← ピッカー経路
2: ExperimentScene ← 被験者実験
3: TrialScene      ← ExperimentScene が additive で読む
```

### 作り方

**シーンの YAML は手書きしない。** XR リグの参照が壊れる。`Assets/Editor/HomeSceneBuilder.cs` が
`ExperimentScene` を開いて `ExperimentController` を `HomeMenu` に差し替え、`HomeScene` として
保存する。動いているリグ設定をそのまま引き継げる。

```
Unity.exe -batchmode -projectPath . -executeMethod HomeSceneBuilder.Build -quit
```

`ExperimentScene` は Save As なので**無傷**（git 差分ゼロで確認）。UI は `ExperimentPanel` を
流用し、パネル prefab の参照も `ExperimentController` から引き継ぐので Inspector 設定は不要。

ビルド設定に未知のシーンがあれば**無効化して末尾に残し警告を出す**（黙って消さない）。

### HomeMenu に入れた配慮

| | |
|---|---|
| 二重ロード防止 | VR のレイは 1 フレームに複数回クリックを飛ばすことがある |
| `ExperimentTrialHandoff.Clear()` | 前の実験の指示が残っていると、ピッカー経路なのに実験の bundle を読む |

### 未実装: 戻る導線

**実験に入ると Home に戻れない。** 試行を中断したときに詰む。ただし被験者が誤って押すと
実験が飛ぶので、置き場所（全試行完了後だけ／確認ダイアログを挟む）を決めてから入れる。

## 実験も共有ストレージから読む（2026-08-28）

`bundleFileName` の解決を 2 段にした（`Bundle.cs` の `TryResolveBundleInSharedStorage`）。

1. 共有ストレージ（`/storage/emulated/0/VisionGraft` など。探索順はピッカーと同じ配列）
2. 無ければ StreamingAssets（APK 内）

| | |
|---|---|
| 実機の実験 | **adb push した `.svb` を読む。** APK に 340MB 焼かなくて済む |
| 事前設定 | 同じ動画なので `model_selection.json` がそのまま効く |
| エディタ・バッチ | 共有ストレージが無いので必ず StreamingAssets 側。挙動不変 |

## `rememberTrackCustomization` を既定 ON にした

当初 false にした理由（「バッチ測定が汚染される」）は**弱かった**ので撤回した。

汚染は `model_selection.json` が存在するときだけ起き、それは既定値と無関係。既定 OFF では
汚染は防げず、**「Inspector で ON にし忘れると黙って機能しない」**という害だけが残る。

対策を既定値ではなく `BatchPlaybackLogger` に移した。**バッチは `-remember true` を明示
しない限り強制 OFF**（測定環境なので決め打ちが正しい）。

## テストのベースライン（変更後）

**508 件中 483 passed / 25 failed。** 失敗は上記「既知の失敗」と**完全に同一の 25 件**で、
今回の一連の変更（永続化・Else・HomeScene・共有ストレージ・既定 ON）で**新しい失敗はゼロ**。

## 実機 1 回目: Home は出たがピッカーが出なかった（2026-08-28）

### 原因

`TestScene` に **`showBundlePickerOnStart: 0`** が焼き込まれていた。ピッカーを出さずに
`bundleFileName` を直接読む設定。

### シーンの値を 1 にしなかった理由

**バッチ実行と EditMode テストが `TestScene` を開く。** 1 にするとピッカーが選択待ちで
止まり、測定と統合テストが全部ハングする。

### 対応: `HomeLaunchHandoff`

`ExperimentTrialHandoff` と同じ「static に置いて `Start()` で Consume」方式。

- **Home の「自由に見る」から来たときだけ**実行時に `showBundlePickerOnStart = true`
- **シーンの値は 0 のまま** → バッチ・テストは従来どおり
- 実験の指示が来たらピッカー要求は捨てる（両方立つことはないが、残ると次に手動で
  開いたときに誤爆する）

### 実機のログが取れない問題

`adb logcat -s Unity` に **1 行も出なかった**。アプリは動いている（プロセス確認済み）が、
**リリースビルドでは `Debug.Log` が logcat に出ない。**

今回はシーンファイルを読んで特定できたが、**保存・復元の確認（`[Customization]`）は
ログでしか追えない。実機で確認するときは `Development Build` にすること。**

### 期待するログ

| 場面 | ログ |
|---|---|
| 「自由に見る」を押す | `[Home] load scene: TestScene` |
| TestScene 起動 | `[Home] bundle picker requested` |
| bundle 選択 | `[Bundle] Opening: /storage/emulated/0/VisionGraft/...` |
| 復元 | `[Customization] loaded ... / applied track=...` |
| 保存 | `[Customization] saved: ...` |
| 実験経路 | `[Bundle] shared storage hit: ...` |

`[Home] bundle picker requested` が出ているのにピッカーが見えないなら、UI 生成側
（`bundlePickerCanvasWithInteractionRayPrefab` の割り当て）を疑う。

## 実機 2 回目: ピッカーが出てすぐ消えた（2026-08-28）

### 原因は tracking origin の切り替え。境界線の件と同根

`StreamingStereoVideoPlayer.Core.cs`:

```csharp
private static readonly bool ForceStationaryTrackingOrigin = true;
```

このアプリは `TryApplyPreferredTrackingOriginMode` で tracking origin を
**`TrackingOriginModeFlags.Device`（着座／静止モード）** に強制する。Quest の床基準
ガーディアンではなく**起動時のヘッドセット位置が原点**になる。

これが 2 つの症状の共通原因。

| 症状 | 説明 |
|---|---|
| **設定した境界線と合わない** | 床基準を使っていないので当然。**意図的な設定**（スクリーンを頭の高さに出すため） |
| **ピッカーが出てすぐ消える** | モード切り替えでワールドが丸ごとずれる。ピッカーは最初の 1 フレームで置いて固定するので視界の外へ飛ぶ |

`RecenterScreensToCurrentFacing()` は `PlaceScreens()` を呼ぶだけで、**UI パネルを置き直さない。**
スクリーンだけ追従して UI が取り残される非対称があった。

### 対応

`UpdateBundlePickerPlacement` の「最初の 1 回で固定」をやめ、**ユーザーが最初に触るまで頭に
追従**させる。触ったら固定する（閲覧中に追いかけてくるのは煩わしい）。

操作の検知は `OnBundlePickerEntryClicked` / `NavigateBundlePickerUp` /
`PrevBundlePickerPage` / `NextBundlePickerPage` の 4 箇所。

`ExperimentPanel` も同じロック方式だが、Home パネルは実機で正常に出たので今回は触らない。
**同じ問題を抱えているので、症状が出たら同じ直し方をする。**

### 未決: 境界線をどうするか

`ForceStationaryTrackingOrigin` を false にすれば実際のガーディアンに合うが、
**スクリーンの配置が変わる**（頭の高さ基準 → 床基準）。要判断。

## ビルドが 5 分かかる件

`Assets/StreamingAssets/` の `.svb` 4 本（**約 370MB**）が毎回 APK に焼かれている。
**共有ストレージから読めるようになったので、もう APK に入れる必要はない。**

| 案 | 効果 | 手間 |
|---|---|---|
| **A. `Patch And Run`** | 2 回目以降が大幅に速い | Development Build にするだけ |
| **B. `.svb` を StreamingAssets から出す** | **APK が 370MB 減る** | エディタ・バッチの参照先の手当てが要る |

まず A。足りなければ B。

## 実機 3 回目: 「found 0 entries」— scoped storage（2026-08-28）

### 権限は付いていた。原因は別

`adb shell dumpsys package <pkg>` で確認したところ、**両方 granted**。

```
android.permission.READ_EXTERNAL_STORAGE:  granted=true
android.permission.WRITE_EXTERNAL_STORAGE: granted=true
minSdk=32 targetSdk=32
```

**targetSdk 32 では scoped storage が効き、`READ_EXTERNAL_STORAGE` を持っていても
`/sdcard` 直下の「メディア以外のファイル」は File API から見えない。**
`.svb` は画像・動画・音声のどれでもないので `Directory.GetFiles` が空を返す。

`Directory.Exists` は true を返すので、**ディレクトリはあるのに中身が見えない**という
紛らわしい状態になる。「起点の解決は成功したのに 0 件」がこれ。

### 対応

| | |
|---|---|
| **探索先の先頭に `Application.persistentDataPath`** | `/sdcard/Android/data/<pkg>/files` は**権限不要で必ず読める**。adb push は通るが MTP からは見えない |
| **「中身が見えるか」で選ぶ** | `Directory.Exists` ではなく `.svb` が 1 本以上見えることを条件にする |
| **`MANAGE_EXTERNAL_STORAGE` を宣言** | 付与すれば `/sdcard/VisionGraft` も読める。MTP から見えるので PC からの差し替えが楽 |
| **診断ログ** | `isExternalStorageManager` の値と、各ディレクトリで何本見えたか |

探索先は静的配列から `BuildBundleSearchDirectories()` に変えた（`persistentDataPath` は
実行時にしか分からないため）。`Bundle.cs` の `TryResolveBundleInSharedStorage` も同じものを使う。

### 権限の付与

**マニフェストに宣言が無いと `appops` でも付けられない。** 宣言前に試したら
`default; rejectTime=...` で弾かれた。宣言入りのビルド後なら:

```
adb shell "appops set --uid <package> MANAGE_EXTERNAL_STORAGE allow"
```

### 置き場所の使い分け

| 置き場所 | 権限 | MTP | 用途 |
|---|---|---|---|
| `/sdcard/Android/data/<pkg>/files` | **不要** | 見えない（adb のみ） | 確実に動く。既定 |
| `/storage/emulated/0/VisionGraft` | 全ファイルアクセスが要る | **見える** | PC から差し替えたいとき |

## 実機 4 回目: tracking origin の無限ループ（2026-08-28）— **既存の重大バグ**

### 症状

`adb logcat -s Unity` が **`[MetaXRFeature] OnAppSpaceChange: 103 / 101` で 4532 行**
埋まっていた。約 14ms ごと、つまり毎フレーム tracking origin が振動している。

### 原因

```
OnTrackingOriginUpdated → TryApplyPreferredTrackingOriginMode → TrySetTrackingOriginMode(Device)
            ↑                                                              ↓
            └──────────────── trackingOriginUpdated が発火 ─────────────────┘
```

**`trackingOriginUpdated` のハンドラが、その中で tracking origin を設定していた。**
設定するとイベントが再発火して無限ループになる。

### これで説明がつくもの

| 症状 | |
|---|---|
| **ガーディアンの境界が毎回合わない** | ワールドが毎フレームずれる |
| **ピッカーが消える／視界の外へ飛ぶ** | 同上。1 回置いて固定する UI は特に影響を受ける |
| **自前の `Debug.Log` が 1 行も見えない** | 4532 行の洪水でバッファから押し出される |

**私が今日入れたものではなく前からあった。** 「境界線が毎回設定したものと違う」という
長く続いていた違和感の正体でもある。

### 対応

1. **モード一致チェック** — すでに `Device` なら何もしない（これだけでループは止まる）
2. **再入防止フラグ** — ハンドラの中から再入させない
3. **失敗の記憶** — `TrySetTrackingOriginMode` が通らない環境で毎回試し続けない
4. `[XR] tracking origin -> Device` を 1 回だけ出す

## 実機 5 回目: ピッカーが目線より上に出る（2026-08-28）

### 原因

```csharp
Vector3 pos = head.position + head.forward * distance + ...;
```

**`head.forward` をそのまま使っていた。** 開いた瞬間に上を向いていると、1.1m 先では
その角度ぶん持ち上がる（30 度で 0.55m）。

### 対応

**水平面に射影して常に目の高さに置く。**

```csharp
Vector3 flatForward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
Vector3 pos = head.position + flatForward * distance + ...;
```

`head.position` は目の位置なので、パネルの中心が必ず目線の高さに来る。どこを向いて
開いても同じ。

あわせて**追従はやめた**（要望）。ただし開いた直後 0.5 秒だけ置き直す。tracking origin が
確定する前に固定すると変な場所に張り付くため。上のループ修正で振動自体は止まるので、
この保険が要る場面は少ないはず。

### 未対応: `ExperimentPanel` も同じ式

Home パネルは実機で正常に見えたので今回は触っていない。**同じ「見上げていた角度ぶん
持ち上がる」問題を抱えている**ので、症状が出たら同じ直し方をする。

## 視聴の基準をヨーだけにした（2026-08-28）

### ピッカーと動画スクリーンが同じ原因で上に出ていた

3 箇所が同じ式を持っていた。

```csharp
pos = head.position + (head.rotation * Vector3.forward) * distance + ...;
```

**ピッチを含んだ前方**を使うので、置いた瞬間に上を向いていた角度ぶん持ち上がる
（1.5m 先で 30 度なら 0.87m）。

| 対象 | 状態 |
|---|---|
| bundle ピッカー | **修正済み**（`ProjectOnPlane`） |
| 動画スクリーン（`StereoScreenPlacement.ResolvePlacement`） | **修正済み**（`ResolveYawOnlyViewRotation`） |
| `ExperimentPanel`（Home パネル） | **未修正**。実機で正常に見えたので触っていないが同じ問題を持つ |

### スクリーンと pinhole 基準は必ず揃える

`LockPinholeBasis` は**モデル配置の投影基準**で、スクリーンと同じ `head.rotation` を
使っていた。**片方だけ水平化すると「スクリーンは水平なのにモデルは傾いた基準で置かれる」**
ことになり、映像とモデルがずれる。両方に同じ `viewRotation` を渡すよう `PlaceScreens` を
書き換えた。

エディタ・バッチではカメラがほぼ無回転なので、EditMode テスト 508 件の結果は変わらなかった
（483 passed / 25 failed、ベースラインと同一）。**この修正が効くのは実機で見上げていたときだけ。**

## Home / Bundle へ戻る導線（2026-08-28）

操作パネルの下段に 2 つ足した。**どちらも実験中は生成しない**（`CanReturnToHomeScene`）。
`Display` ボタンと同じ理由で、被験者に押されると試行が飛ぶ。

| ボタン | 動作 |
|---|---|
| `Home` | `HomeScene` へ戻る |
| `Bundle` | **シーンごと読み直してピッカーから始める** |

bundle ピッカーにも `Home` を足した（選ぶ前に戻れるように）。

### なぜ再生中に差し替えないか

`experiment-flow.md` に「2 本目の bundle に差し替える経路がない。モデルインスタンス・
プロキシ・interactive motion の状態を安全に捨てる手段がない」と記録がある。
**シーンを読み直すのが唯一安全な方法。** `HomeLaunchHandoff` でピッカー表示を要求してから
`LoadScene` する。

戻る前に `FlushTrackCustomizationSaveNow()` を呼び、保存待ちのモデル・向きを取りこぼさない。


## 手動スケール（`"scale"`）を足したときの注意（2026-08-28）

`model_selection.json` の track に `"scale"` を足した。書式は `"yaw"` と同じ
`{"フレーム番号": 値}` で、値は**自動フィットに対する倍率**（既定 1.0）。

```json
{
  "demo_video.mp4": {
    "numFrames": 1830,
    "tracks": {
      "1": {"model": "06_DieselLocomotive", "yaw": {"0": 15.5}, "scale": {"0": 1, "900": 2.75}}
    }
  }
}
```

**MiniJson に writer が無く書き出しは手書き**なので、フィールドを 1 つ足すのに触る場所が 5 か所ある。
どれか 1 つ落とすと「保存したのに読めない」「読めるのに保存されない」が静かに起きる。

1. `TrackCustomization` のフィールド
2. `IsEmpty` — ここを落とすと、**scale だけの track が「空」と判定されて丸ごと書き出されない**
3. `Clone`
4. `VideoCustomization.OverlayWith` — ここを落とすと、**実験中のセッション上書きだけが消える**
5. `ToJson` / `ParseKeyframes`

`TrackCustomizationJsonTests` が 1〜5 をすべて踏む往復テストになっている。次にフィールドを
足すときも、まずこのテストを増やしてから実装する。

シリアライズはファイル IO から切り離して `ToJson` / `FromJson` を公開してある
（テストが `persistentDataPath` を汚さないため）。

`numFrames` 不一致のときは yaw と scale を**まとめて**破棄する。片方だけ残すと、
向きだけ合っていて大きさが合わない、という分かりにくい状態になる。
