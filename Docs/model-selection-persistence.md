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
| `UI.Runtime.partial.cs` | 回転操作で `PersistManualYaw` + **`change_rotation` を `operations.csv` に記録** |
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
