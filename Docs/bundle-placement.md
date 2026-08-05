# Bundle 読み込み・配置

## 発表用まとめ

`.svb` ファイル（ZIP アーカイブ）を開き、`meta.bin` と `manifest.json` だけを使って配置・姿勢追従を行う。

```
.svb (ZIP)
├─ video.mp4        必須・Runtime 再生
├─ manifest.json    必須・解像度/fps/FOV/座標系/カット境界
├─ meta.bin         必須・フレームごとの検出データ（位置・姿勢・種別）
└─ source/*         任意・debug 専用（Runtime 配置には使わない）
```

**鉄則**: 配置・回転・姿勢追従に使えるのは `meta.bin` + `manifest.json` のみ。

オブジェクトは `categoryId` で 3 種別に分類：

| 種別 | データ |
|---|---|
| **Human** | SMPL block（globalOrient + body_pose×22） |
| **Animal** | SMAL block（globalOrient + body_pose×34） |
| **Else** | anchor 座標 + bbox のみ（剛体配置・回転なし） |

**強調ポイント**: Python 側が動画から検出・推定して meta.bin に格納。Unity 側はそれを読んで配置と姿勢追従に専念する分業構造。

---

## 実装リファレンス

### 配置に使う実データ

- **位置**: `anchor_u / anchor_v`（画面 UV）+ `anchor_z`（深度）→ `AnchorUvZToWorldPinhole` → world 座標
  - 深度の符号: bundle 側は **0=far / 1=near** の正規化値。`DecodeAnchorDepthMetersFromBundle` で変換（2026-06-19 に符号反転バグ修正済み）
- **向き・姿勢**: SMPL/SMAL block の 3×3 回転行列群（row-major、9float ずつ）
- **スケール**: `bboxWorldH` 基準の uniform scale（`TrackModelPlacement.ResolveDesiredLocalScale`）。Human/Animal は track ごとに初回フレームでロックし、カット（shot）境界で解除する（後述）。モデル間の体型差は吸収しない（既知の制限、[DogMetaBoneMapping.md](DogMetaBoneMapping.md) 参照）
- **カット**: `manifest.json` の `shots`（`[[start, end), ...]`）。同じ trackId でも shot をまたげば見かけサイズが正当に変わるため、境界でスケールと平滑化をリセットする（後述）
- **形状（betas）**: 読み込むが現状未使用

### 関連ファイル

| ファイル | 役割 |
|---|---|
| `StreamingStereoVideoPlayer.Bundle.cs` | ZIP 展開・エントリ読み込み |
| `StreamingStereoVideoPlayer.Meta.cs` | `meta.bin` パース・フレームデータ格納 |
| `StreamingStereoVideoPlayer.Manifest.partial.cs` | `manifest.json` パース・座標系設定 |
| `PinholePlacementSpace.cs` | UV + 深度 → world 座標変換 |
| `TrackModelPlacement.cs` | bbox ベースのスケール決定 |
| `ShotBoundaries.cs` | `manifest.json` の `shots`（カット境界）のパース・フレーム → shot index 解決 |
| `StreamingStereoVideoPlayer.ShotBoundary.partial.cs` | shot 境界の検出と track ごとの状態リセット |

### NG パターン: `source/other_object_proxies.json` を配置に使う（2026-07-30 修正済み）

**症状**: `bundle_human.svb`（Human + Else のボールを検出した動画）で、Human がボールで遊んでいるのに Unity 上ではボールが Human の後ろ（奥）に配置される。

**原因**: Else カテゴリだけ、`ApplyMetaTarget` が `meta.bin` の anchor を捨てて sidecar 由来の world 座標で上書きしていた。sidecar 側の宣言は

```json
"units": { "cameraXyz": "same_as_depth_npz", "proxy3d": "same_as_depth_npz" }
```

で、`anchorCameraXyz.z` / `proxy3d.center.z` は **`depth_npz` の正規化深度（0=far / 1=near）** であってメートルではない（`bundle_human.svb` の実測値域 0.256〜0.906、median 0.746。`meta.bin` の `z01` と同一ソースで `trackDepthMedian: 0.7456` が一致する）。これをカメラ空間のメートルとしてそのまま `camOrigin + camRotation * center` に流していたため、

- Human / Animal は `DecodeAnchorDepthMetersFromBundle` で 0=far/1=near を正しく反転して配置（`screenDistanceMeters: 1` の SampleScene では 0.69〜0.78 m）
- Else は正規化値をそのまま world z（大きい = 遠い）に入れるので **前後関係が反転**

という不一致が生じた。全 2156 フレームの検証で **55.2%（1190 フレーム）でボールが Human より奥**に置かれていた（frame 0 では Human 0.704 m に対しボール 0.870 m = 17 cm 奥）。

加えて sidecar のカメラは `"width": 1280, "height": 720` だが manifest の eye は 1280x640（`fy_norm = 2.856` は 1280x640 のアスペクト前提）。proxy center を eye 座標に再投影すると縦位置が **中央値 -39.6 px**（-66〜+120 px）ずれ、ボールが上に浮く。

**教訓**: `source/*` を runtime 配置に使わないという鉄則は、単に方針の問題ではなく **単位系が runtime と揃っていない**という実害を伴う。sidecar は「値域が偶然 runtime の配置レンジ（0.69〜0.78 m）と近い」ため、一見それらしく配置されて前後だけ壊れるという気づきにくい壊れ方をする。

**修正内容**:

| ファイル | 変更 |
|---|---|
| `StreamingStereoVideoPlayer.Playback.partial.cs` | `ApplyMetaTarget` の proxy による `anchorWorld` 上書きを削除。`ApplyReplaceableModelTransform` の `hasOtherProxySize` 分岐（proxy size でスケール決定して early return）を削除 |
| `TrackModelPlacement.cs` | `ScaleRequest` から `otherProxySize` / `hasOtherProxySize` / `isOther` を削除。proxy size ベースのスケール分岐を削除し、Else も bbox + `anchorZ` ベースに統一 |
| `StreamingStereoVideoPlayer.Sidecars.partial.cs` | `TryOtherProxyWorld` / `LoadOtherObjectProxiesSidecar` に debug 可視化専用であることと単位問題を明記 |
| `StreamingStereoVideoPlayer.Meta.cs` | `MetaObj` の proxy フィールド群に debug 専用であることを明記 |

sidecar の読み込みと `showOtherProxyBoxes` によるデバッグボックス表示は残している（`source/*` の中身を目視するための道具）。

修正後は Else も Human / Animal と同じ `anchorZ` 経路になり、**83.6% のフレームでボールが Human より手前**という bundle 本来の前後関係が復元される。副次的に、スケールが `anchorZ` と連動するようになった（proxy 経路では深度と無関係にサイズが決まっていた）。

### 深度レンジの圧縮は実害が小さい（2026-07-30 検証）

上記のバグ修正時に「`PopoutRangeMeters = 0.35f` の固定レンジに 0..1 の正規化深度全体を押し込んでいるため、Human とボールの深度差が中央値 1.6 cm しかなく、ボールが Human のメッシュに埋まる」と当初判断したが、**これは配置スケールを無視した誤りだった**。実測して否定された記録として残す。

配置後の実寸を計算すると（`screenDistanceMeters = 1.0`、`bundle_human.svb` 全 2156 フレーム）:

| 項目 | 値 |
|---|---|
| Human の配置身長 | median **0.305 m**（min 0.097 / max 0.395） |
| ボールの配置直径 | median 0.040 m |
| ボール ↔ Human anchor の 3D 距離 | median **0.098 m**（身長比 0.41） |
| ↑ のうち \|dy\|（高さ方向） | median 0.090 m ← **支配的** |
| ↑ のうち \|dz\|（奥行き方向） | median 0.016 m ← 補助的 |

スクリーンが 1 m 先にあり、そこに映る見た目サイズに合わせてスケールするため、**Human は身長 30 cm のミニチュアとして配置される**。実物大の胴体の厚み（20〜30 cm）と深度差を比較したのが誤りで、この身長なら胴体の厚みは 4 cm 程度。

そして 2 体を離しているのは主に**高さ**であって奥行きではない。奥行き差が 1.6 cm しかなくても高さが 9 cm 離れているため、3D では身長の 41% も離れている。胴体半径（身長 × 0.07）以内に入るフレームは **24 / 2156 = 1.1%** のみで、しかもその 24 フレームは実際に体に接触している瞬間（胸トラップ・ヘディング）なので多少の重なりは物理的に自然。

ボールの縦位置も Human bbox 内で大きく動いており、胴体中央に留まらない:

| Human bbox 内の縦位置（0=頭頂 1=足元） | 割合 |
|---|---|
| 頭より上 | 32.5% |
| 頭〜胸 | 28.0% |
| 胸〜腰 | 23.6% |
| 腰〜膝 | 13.7% |
| 足元 | 2.2% |

**結論**: 深度レンジの拡大（`PopoutRangeMeters` の変更、`z01` の再正規化など）は解決すべき問題を持たないため、優先度は低い。データ上の残課題は次の 2 点だが、どちらも深度レンジの拡大では直らない:

1. **深度順序の反転 16.4%** — 奥行き差が 1.6 cm しかないため bundle 側の depth 推定ノイズで簡単に逆転する。ただし高さで 9 cm 離れているので視覚的な破綻は軽微
2. **立体感の乏しさ** — 1 m 先で 1.6 cm の奥行き差は両眼視差の角度差が 0.1 度未満でほぼ知覚できない。「ボールが手前にある」感覚は出ないが、シーン全体が 30 cm のジオラマとして見える設計なので身長比 5% の差は相対的には破綜していない

**注意**: 「足でボールを扱う」「胸でストップする」といった接触シーンで整合が取れるかは、深度レンジではなく **Human の SMPL 姿勢追従の精度とボール anchor の精度**の問題。将来のインタラクション実装（手足とボールの接触）ではこちらが本題になる。[smpl-retargeting.md](smpl-retargeting.md) を参照。

なお根本には「`depth_npz` はシーン全体（背景含む）で正規化された相対深度なので、絶対距離を復元する情報が bundle に無い」という構造的制約がある。物理的に正しい距離が必要になった時点で、bundle 側で metric depth または depth のスケール・シフト係数を `manifest.json` に載せる対応が本筋。

### NG パターン: rest pose 基準の bottom alignment で座位モデルが浮く（2026-07-30 修正）

**症状**: `bundle_human.svb` の座るシーンで、Human モデルが地面ではなく浮いた状態で座る。

**座位区間**（SMPL の膝・股関節の回転角から検出。全 2167 フレーム中 573 = **26.4%** が座位）:

| フレーム | 時刻 | 長さ | 膝角度 | bbox 高 | 配置身長 |
|---|---|---|---|---|---|
| **373-799** | **12.4-26.6 s** | **14.2 s** | 90° | 189 px | 0.153 m |
| 1221-1278 | 40.7-42.6 s | 1.9 s | 94° | 315 px | 0.243 m |
| 2002-2040 | 66.7-68.0 s | 1.3 s | 119° | 227 px | 0.190 m |
| 209-248 | 7.0-8.3 s | 1.3 s | 94° | 186 px | 0.144 m |

立位の参考値は bbox 高 418 px / 配置身長 0.339 m。**座ると bbox が立位の 48% に縮む。**

**原因**: bottom alignment が 2 段階とも rest pose（立ちポーズ）基準になっていた。

1. **`ApplyReplaceableModelTransform` の bottom alignment** — 使う `baseBottomOffsetLocal` は `ReplaceableModel.Awake()` の中、つまり prefab の初期ポーズで一度だけ計算される固定値（root = Hips から足裏までの距離 ≈ 身長の 53%）。root を「bbox 下端 + 身長 × 53%」に置くので、実際の最下点が root の近くにある座位（膝が曲がって足が上がる、床座りなら尻が最下点）では root が高すぎる
2. **FK 後の `FitDisplayedModelToBBox` 補正が効かない** — 「モデルの見た目の底を bbox 下端へ合わせ直す」補正パスは既に存在するが、`TryGetRendererWorldBounds` が読む `renderer.bounds` は Human prefab の `m_UpdateWhenOffscreen: 0`（既定値）のため固定の `m_AABB` を root bone の transform で変換した値で、**スキニング変形を反映しない**。座位でも bounds が rest pose 相当のままなので `projectedBottomV ≈ targetBottomV` となり補正量がほぼ 0 になる

浮き量の概算（配置身長 0.16 m の座位区間）:

```
椅子座り（足先が底、hips-25%） → 浮き 4.5 cm（身長の 28%）
床座り  （尻が底、  hips-8%） → 浮き 7.3 cm（身長の 45%）
```

**修正**: `TrackInstanceFactory.Create` でインスタンス生成時に全 `SkinnedMeshRenderer` の `updateWhenOffscreen = true` を設定し、`bounds` をポーズに追従させて既存の `FitDisplayedModelToBBox` 補正を機能させる。prefab は FBX への prefab variant なので `m_UpdateWhenOffscreen` の差分を持たず、実行時設定で全モデルに効く。

`baseBoundsSize` / `baseBottomOffsetLocal` は `ReplaceableModel.Awake` で確定済みなので、スケール基準（`scaleH` の分母）は従来どおり変わらない。

**この修正で注意する点**:

- **立位の見え方も変わり得る**。`m_AABB` はアニメーション範囲を含む余裕のある AABB なので、rest pose 基準の `baseBottomOffsetLocal` は実際の足裏より下を指している。bounds が実変形を反映すると立位でも `FitDisplayedModelToBBox` が補正を入れるようになる（より正確になる方向だが、従来の位置とは変わる）
- **1 フレーム遅延の可能性**。`DisplayModelTick` は `Update()` から呼ばれ、FK でボーンを動かした直後に `renderer.bounds` を読む。Unity のスキニング評価タイミング次第で bounds が 1 フレーム前の姿勢になり得る。座位のように 14 秒続く静的な姿勢では問題ないが、動きの速い場面では補正が遅れる可能性がある
- **ジッター**。bounds が毎フレーム変わるため補正量が揺れる可能性がある。出る場合は補正結果に EMA 平滑化を入れる（未実装）
- **CPU コスト**。毎フレーム bounds を再計算する。表示は 2〜3 体なので許容範囲と判断

効果が不十分だった場合の次案は「`HumanoidRigCache` から足ボーン（LeftFoot / RightFoot / Toes）の world 位置を取り、その最下点を bbox 下端に合わせる」。bounds に依存せずジッターも出にくいが、床座りで尻が最下点になるケースは足ボーンでは捉えられない。

### カット（shot 境界）でスケールと平滑化をリセットする（2026-08-02 実装）

**症状**: カットが多い bundle（`bundle_animal.svb` は 15 shot）で、カットが切り替わった後もモデルが前のカットの大きさのまま表示され、極端に大きい／小さい見た目になる。

**原因**: Human / Animal のスケールは `GetOrLockModelLocalScale`（`StreamingStereoVideoPlayer.Playback.partial.cs`）が **trackId ごとに初回フレームで確定してロックし続ける**。ロックが解除されるのは prefab の差し替え時（`TrackInstanceLifecycle`）と VR 内 Change Model パネルでの手動変更時だけだった。ロック自体は必要な設計で、外すと bbox のブレでモデルが毎フレーム伸縮する。問題は解除点にカット境界が含まれていなかったこと。

**bundle 側は既に対応済み**。`manifest.json` に `shots` と `shot_boundary_policy` がある。

```json
"shots": [[0, 258], [258, 338], [338, 427], ...],
"shot_boundary_policy": {
  "schema": "master_project.shot_boundary_policy.v1",
  "unity_guidance": "Do not interpolate or spring position/scale across a shot boundary
                     for the same trackId; snap to the new shot's first-frame anchor instead."
}
```

`shots` の各要素は `[start, end)` のフレーム範囲で、範囲内にハードカットがない連続テイクを表す。**同じ trackId でも shot をまたげばカメラ距離・見かけサイズが正当に変わる**（被写体が動いたのではない）ため、track ごとに持ち越している状態は境界で捨てる必要がある。

bundle ごとの `shots`（2026-08-02 時点）:

| bundle | num_frames | shots |
|---|---|---|
| `bundle_animal.svb` | 2120 | 15 shot |
| `bundle_human.svb` | 2167 | 1 shot（`[[0, 2167]]`） |
| `bundle.svb` / `bundle_old.svb` | 289 | フィールドなし（旧 bundle） |

**実装**:

- `ShotBoundaries.cs`: `shots` を開始フレームの昇順配列に正規化し、`ResolveShotIndex(frame)` で「そのフレームがどの shot に属すか」を二分探索で返す。`JsonUtility` は `[[start, end), ...]` のような入れ子配列を扱えないため、`ManifestLoader` が生 JSON から `MiniJson` で読む
- `StreamingStereoVideoPlayer.ShotBoundary.partial.cs`: `DisplayModelTick` から `SyncShotBoundaryForFrame(frame)` を呼び、shot index が変わったフレームで `ResetPerShotTrackState()` を実行する。シークも shot index の変化として検出される（同一 shot 内へのシークはカメラが連続しているのでリセットしない）

**リセットするもの**（すべて「前フレームからの連続性」を前提にした状態）:

| 状態 | 理由 |
|---|---|
| `lockedModelLocalScaleByTrack` | 前 shot のカメラ距離で確定した表示スケール（**主対象**） |
| `smoothedJointsByTrack` / `personRootYawForwardByRoot` | ジョイント位置・root yaw の平滑化 |
| `AnimalPoseApplier.ResetMotionState()` | Animal の OneEuro フィルタと SMAL 回転平滑化 |
| `ResetHumanSmplSmoothingForShotBoundary()` | SMPL 回転平滑化と root 位置・深度の平滑化 |
| `lastGoodBottomAlign{Area,VEye}` | 前 shot の bbox を基準にした下端合わせのホールド |
| `humanOtherContactStateByTrack` | 前 shot で確定した接触オフセット |

**リセットしないもの**: `HumanoidRigCache` / `AnimalRigCache`（ボーン解決結果）、`rootYawFix`（実データから一度だけ決めてセッション中保持する向き判定。消すとカットごとにやり直しになりちらつく）、SMPL/SMAL の参照姿勢、`humanKeypointHeightMetersByTrack`（root 相対の実寸から求めた骨長でカメラ距離に依存しない）、ユーザーが選んだモデル、手動 yaw キーフレーム。いずれも shot とは無関係。

`shots` を持たない旧 bundle では `ShotBoundaries.Empty` になり `ResolveShotIndex` が常に 0 を返すため、全編 1 shot 扱い = 従来どおりロックしっぱなしの挙動になる。

**betas は shot でリセットしない**。`shot_boundary_policy` が明記しているとおり、SMPL/SMAL の betas はフレームごとに独立推定される値で、そのフレーム間ノイズは shot 境界とは無関係の別問題。

### モデルアセット（`Resources/Models/`）

`Assets/Resources/Models/{Human,Animal,Else}/` 配下の各 prefab は、2 桁ゼロ埋めの番号プレフィックス（例: `00_Baseball`）をファイル名先頭に付ける運用。番号は `selected{Human,Animal}Index`（Inspector の生配列インデックス）や実行時 UI のインデックスと一致させるため、モデルを追加・削除したら欠番なく連番になっているか必ず確認・振り直すこと。

- **Human/Animal**: `Assets/Editor/HumanIndexPrefixer.cs` / `AnimalIndexPrefixer.cs`（`Tools > VisionGraft > Prefix All {Human,Animal} Models With Index`）で振り直す。
- **Else**: `Assets/Saritasa/Models/Sport_Balls/` の 6 種（Baseball/Basketball/Football/Golf/Soccer/Tennis）を `Assets/Editor/ElseModelImporter.cs`（`Tools > VisionGraft > Import And Prefix Else Models`）で `Resources/Models/Else/00_Baseball.prefab`〜`05_Tennis.prefab` としてコピー・採番した（2026-07-17）。元の fbx/material は `Assets/Saritasa/Models/Sport_Balls/` に残したまま GUID 参照する構成（Animal の `Sources/` と同じパターン）で、`AssetDatabase.CopyAsset` を使い GUID 衝突を避けている。
  - 追加で Sketchfab 製のディーゼル機関車モデル（`2ТЭ116УД`, 作者 Leafia dev., **CC-BY-4.0**）を `06_DieselLocomotive.prefab` として取り込んだ。元の `.glb` は `Resources/Models/Else/Sources/DieselLocomotive.glb` に保管。スケルトン・アニメーションなしの静的剛体メッシュなので Else に適合。**CC-BY-4.0 のため最終成果物にクレジット表示が必要**（未対応、要フォロー）。
  - ランタイム側の選択ロジックを実装済み（2026-07-30）。`StreamingStereoVideoPlayer.Core.cs` に `selectedElseIndex`（グローバルデフォルト）と `elsePrefabs`（`Resources/Models/Else` から自動ロード）を追加し、`ResolveTrackPrefab`（`StreamingStereoVideoPlayer.Playback.partial.cs`）に `IsCategoryOther` 分岐を追加した。修正前は Animal 以外のカテゴリを無条件に Human 用ロジックへフォールバックしていたため、Else の track にも Human プレハブが割り当てられていた（既知のバグ、修正済み）。
  - track ごとにモデルを個別指定したい場合は `trackModelIndices`（`{trackId, modelIndex}` の Inspector 配列）を使う。優先順位は「VR 内 Change Model パネルでの変更 > `trackModelIndices` > `selectedHumanIndex`/`selectedAnimalIndex`/`selectedElseIndex` のグローバルデフォルト」。ただし VR 内 Change Model パネルは Person/Animal の track のみが対象で、Else の track には現状表示されない（`TryGetRuntimeModelPickerTarget` が Person/Animal のみを候補にしているため）。
- **`.glb` インポーターの競合に注意**: プロジェクトには UniGLTF（VRM 用、`Packages/com.vrmc.gltf`）と glTFast（`com.unity.cloud.gltfast`）の両方が入っており、どちらも `.glb`/`.gltf` の ScriptedImporter を登録する。UniGLTF 側の自動棲み分け（`UniGLTF.Editor.asmdef` の `versionDefines`）は旧パッケージ名 `com.atteneder.gltfast` を見ているため、現行の `com.unity.cloud.gltfast` とは噛み合わず、新規 `.glb` を追加すると両方拒否されて `DefaultImporter` にフォールバックする（既存の `50+ Animated Animals` 内の `.glb` は `.meta` に旧来のインポーター参照が焼き込まれているため影響を受けない）。対処として Scripting Define Symbols に `UNIGLTF_DISABLE_DEFAULT_GLB_IMPORTER` / `UNIGLTF_DISABLE_DEFAULT_GLTF_IMPORTER` を追加済み（`ElseModelImporter.DisableUniGltfDefaultGlbImporter`）。今後 `.glb` を追加する際はこの設定により glTFast 側が自動で使われる。
- いずれのツールも Unity Editor を閉じてバッチモード（`Unity.exe -batchmode -nographics -quit -projectPath <path> -executeMethod <クラス.メソッド> -logFile <path>`）で実行する必要がある（同一プロジェクトの多重起動不可のため）。`Resources.LoadAll<GameObject>` は `Sources/` サブフォルダ内の `.glb`/`.fbx` もメインアセットが `GameObject` である限り拾ってしまう点に注意（`PrefixWithIndex` はパスに `/Sources/` を含むものを除外するよう対応済み）。
