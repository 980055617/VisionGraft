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
  - 深度の符号: **bundle 世代によって向きが逆**。`manifest.json` に `depth_policy` があれば `anchor_z` は `larger = farther`、無ければ `larger = nearer`。`DecodeAnchorDepthMetersFromBundle` が `IsAnchorDepthLargerMeansFarther()` で判定して吸収する（2026-08-06、後述の「anchor_z の向きは bundle 世代で異なる」を参照）
- **向き・姿勢**: SMPL/SMAL block の 3×3 回転行列群（row-major、9float ずつ）
- **スケール**: `bboxWorldH` 基準の uniform scale（`TrackModelPlacement.ResolveDesiredLocalScale`）。**Human / Animal / Else すべて bbox の高さだけで決める**（幅は使わない。2026-08-07 に Animal の幅制限を撤廃、後述の NG パターン参照）。Human/Animal は track ごとに初回フレームでロックし、カット（shot）境界で解除する（後述）。モデル間の体型差は吸収しない（既知の制限、[DogMetaBoneMapping.md](DogMetaBoneMapping.md) 参照）
- **カット**: `manifest.json` の `shots`（`[[start, end), ...]`）。同じ trackId でも shot をまたげば見かけサイズが正当に変わるため、境界でスケールと平滑化をリセットする（後述）
- **形状（betas）**: 読み込むが現状未使用

### 配置パイプラインの実行順

`ApplyMetaTarget`（`StreamingStereoVideoPlayer.Playback.partial.cs`）の実行順。番号はコード内のコメント（`Debug.cs` の「④」「⑦」）に合わせている。

| # | 処理 | 対象 | 内容 |
|---|---|---|---|
| ① | `AnchorUvZToWorldPinhole` | 共通 | `anchor_u / anchor_v` + `anchor_z` から world 座標を復元 |
| ② | `ResolveDesiredLocalScale` | 共通 | `bboxWorldH ÷ modelHeightMeters`。**Human/Animal は shot 先頭でロック、Else は毎フレーム** |
| ③ | `ApplyAnchoredPose` + `ApplyLocalScaleWithGroundAlignment` | 共通 | ①の位置・回転と②のスケールを適用 |
| ④ | `ApplyBottomAlignment` | **Else のみ** | **world 空間**の up 方向で、AABB 下端を bbox 下端（`anchorZ` 基準）に合わせる |
| ⑤ | `TryApplySkeleton` | Human/Animal | **SMPL/SMAL FK でボーン回転を適用** |
| ⑥ | `ResolveRootPositionPreservingScreenHeight` | Human（条件付き） | 姿勢適用で動いた root の画面上の高さを戻す |
| ⑦ | `FitDisplayedModelToBBox` → `AlignProjectedModelBottomToBBox` | **Human/Animal のみ** | **camera 空間の Y だけ**動かし、骨格最下点の投影 V を bbox 下端 V に合わせる |

**押さえるべき性質:**

- **深度（Z）は ① で決まり、以降変わらない。** ⑦ は camera 空間の Y 成分にしかオフセットを加えない
- **Human の縦位置に `anchor_v` は残らない。** ⑦ が bbox 下端で上書きするため、胸や頭の位置は「bbox 下端から姿勢と骨長をたどった先」として決まる。一方 Else は `anchor_v` と bbox 下端で決まり、両者は独立
- **world 座標が適用されるのは root**。`ReplaceableModel.anchor` は prefab に設定されておらず `TrackInstanceFactory` が `AddComponent` するため常に `null`。`ApplyAnchoredPose` は `anchor == null` で早期 return し、GameObject の root がそのまま `anchor(u,v,anchorZ)` に置かれる
- **同じ「bbox 下端に合わせる」処理が対象で別実装**（④ と ⑦）。④ は world 空間・`anchorZ` 基準・AABB 下端、⑦ は camera 空間 Y・**モデル AABB 中心の `depthMeters`** 基準・骨格最下点。`depthMeters` と `anchorZ` は 3〜4% ずれる

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

> **この結論は現行 bundle には当てはまらない**（2026-08-17 追記）。以下の実測値は 2026-07-30 時点の `bundle_human.svb` のもので、bundle は 2026-08-06 に再生成されて値域が変わっている。また接触シーンを「1.1% なので自然」と評価した際に、スケールロックによる Human 側の 1.94 倍の拡大を勘定に入れていなかった。最新の実測は後述の「bundle_human.svb でボールが人に埋もれる（2026-08-17 調査）」を参照。

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

### スケールの基準フレームは shot 先頭に固定する（2026-08-07 実装）

**症状**: VR 内の Change Model パネルでモデルを差し替えると、その瞬間のフレームの bbox で大きさが決まる。同じ shot の同じ track を見ていても、**いつモデルを変えたかで表示サイズが変わる**。

**原因**: スケールのロックが外れる契機は 2 つあり、基準フレームが揃っていなかった。

| きっかけ | 処理 | ロックされる bbox |
|---|---|---|
| shot 境界 | `ResetPerShotTrackState()` の `lockedModelLocalScaleByTrack.Clear()` | その shot の先頭フレーム |
| モデル変更 | `RecreateTrackInstanceForModelSelection` の `Remove(trackId)` | **変更した瞬間のフレーム** |

`GetOrLockModelLocalScale` は「ロックが無ければ、いま渡された `desiredScale` をそのまま焼き付ける」だけなので、解除したタイミングのフレームがそのまま基準になる。

**なぜ揃えるべきか**:

- 通常再生の基準（shot 先頭）と食い違う。ユーザーから見ると「モデルを変えただけなのに大きさも変わった」挙動になる
- 被験者実験ではモデル変更を開放しているので、**変更タイミングが人によって違えば表示サイズも人によって変わる**。`operations.csv` には prefab 名しか残らず、後から統制できない
- 変更した瞬間に bbox が潰れていれば（画面端で切れている等）、その値が以後ずっと固定される

**実装**: `TryResolveShotStartScaleReference(trackId, out MetaObj)`（`Playback.partial.cs`）。ロックが無い track についてのみ、現在の shot の先頭フレームを `TryReadFrameObjects` で読み直し、その bbox（`bboxW` / `bboxH` / `anchorZ`）で `desiredScale` を計算する。ロック済みなら早期 return するので、毎フレーム meta.bin を引くことはない。

**フォールバックする条件**（いずれも従来どおり現在フレームでロックする）:

- shot 先頭にその track が存在しない（shot 途中から登場する被写体）
- shot 先頭の bbox の幅か高さが 0
- shot 先頭フレーム == 現在フレーム（読み直す意味がない）

**副次的な効果**: シークでも基準が揃う。同じ shot の中央へ飛んだ場合、従来は「シーク先のフレーム」でロックされていたが、shot 先頭に揃うようになった。`shots` を持たない旧 bundle は `GetStartFrame` が 0 を返すので、全編 1 shot = フレーム 0 が基準になる。

**不採用にした案**: 「shot 内から立位に最も近いフレーム（bbox のアスペクト比が最大のフレーム）を基準にする」案は 2026-08-07 に実測で悪化した。アスペクト比が最大のフレームは人物が横向きで細く写っているだけのことがあり、bbox 高さで絞り直しても骨格スパンが bbox の 112%（初回基準では 86%）と過大になった。今回の変更は「基準を増やさず通常再生と同じ 1 点に揃える」方向なので、この不採用理由は当てはまらない。

### NG パターン: Animal のスケールを bbox の「幅」でも制限する（2026-08-07 修正済み）

**症状**: Animal のモデルが shot によって極端に小さく配置される。どのモデルを選ぶかで起きたり起きなかったりする。

**原因**: `ResolveDesiredLocalScale` は Animal だけ `Mathf.Min(scaleW, scaleH)` を採り、幅も高さも bbox に収める設計だった。「bbox に入れる」という意図自体は達成されるが、`scaleW` が比べていた 2 つの値が**同じものを測っていない**。

| | 何を測っているか |
|---|---|
| `bboxWorldW` | 映像内の被写体の投影幅。四足動物では yaw によって体長〜体幅の間を動く |
| `baseBoundsSize.x` | prefab の bind pose での X 幅。固定値で、しかも prefab によって X 軸が体長だったり体幅だったりする |

高さ軸（`bboxWorldH` ↔ AABB 高さ）は意味が対応しているので健全。壊れていたのは幅軸だけ。

**実測（`bundle_animal.svb` 全 2120 フレーム）**:

| | 値 |
|---|---|
| bbox の W/H（全フレーム、同一 track） | min 0.46 / p10 0.63 / median 1.10 / p90 2.20 / max 3.79 |
| bbox の W/H（15 shot の先頭フレーム = スケール決定点） | 0.63 〜 2.16 |
| モデル AABB の W/H（`animal_diagnostics.txt` の 12 モデル） | 0.33 〜 1.84（**モデル間で 5.5 倍の開き**） |

`fx_norm = 1.428` / `fy_norm = 2.856 = 2 × fx_norm` で `eye_w / eye_h = 2` なので、ワールド換算後のアスペクト比は素の `bboxW / bboxH` と一致する（投影による歪みは入っていない）。

縮小率 = `(bboxW/bboxH) / (モデルW/モデルH)`。`Min` は必ず小さい側を採るため、このミスマッチは**常に「縮む」方向にしか働かない**:

| モデル | AABB の W/H | 縮んだ shot | 最悪値（表示高さ ÷ bbox 高さ） |
|---|---|---|---|
| 22_Elk1.0 / 17_Deer1.0 | 1.83 | **13/15** | **0.34** |
| 42_Moose1.0 | 1.57 | 9/15 | 0.40 |
| 30_Goose | 1.42 | 9/15 | 0.44 |
| 14_BoarV2 | 1.25 | 8/15 | 0.50 |
| 00_Dog / 04_Lion / 13_Bloodhund | 0.77〜0.80 | 1/15 | 0.79〜0.82 |
| 47_Pronghorn1.0 / 16_Deer1 | 0.33〜0.50 | 0/15 | 1.00 |

さらにスケールは shot 先頭フレームでロックされるため、先頭フレームがたまたま縦長 bbox（動物が正面を向いているカット）だと**その shot の間ずっと小さいまま**になる。

**修正**: `isAnimal` 分岐を削除し、Human / Else と同じく `scaleH` のみにした。あわせて `ScaleRequest` から幅に関わる引数（`bboxWidthPixels` / `fx` / `eyeWidthPixels` / `isAnimal`）を落とし、`baseBoundsSize` は高さだけを渡す `baseHeightMeters` に置き換えた。幅を渡せない構造にすることで再発を防ぐ。

**検討して不採用にした案**:

- **向きを考慮した幅制限**（モデルの水平寸法を体長 `max(X,Z)` と体幅 `min(X,Z)` に分け、SMAL の globalOrient から求めた yaw で期待投影幅を計算して比べる）— 幅制限の意図を保ったまま正しくする案だが、prefab ごとの前方向の定義（X 前 / Z 前が混在）を先に確定させる必要があり、コストに見合わない
- **下限クランプ**（`scaleW` が `scaleH` の 0.8 倍を下回ったら切り捨てる）— 最悪ケースは防げるが原因である幅軸のミスマッチは残り、閾値の根拠が実測でしか決まらない対症療法になる

**なぜ高さ基準だけで足りるか**: 動物が正面を向いているカット（縦長 bbox）ではそもそも高さ基準が正しく、横を向いているカット（横長 bbox）では従来から `scaleH` 側が効いていた。つまり `Min` が効いていたのは「幅軸の測り方が間違っているとき」だけで、正しく効いていた場面がない。

### anchor_z（z01）は disparity 系（2026-08-05 確定）

**生成側の調査で確定した事実**:

- `depth.npz` の中身は **disparity（視差 = 1/距離 に比例）系**。コード中では一貫して "depth" と呼ばれているが、規約は「近い = 大きい値 / 遠い = 小さい値」で metric depth ではない
- DepthCrafter の正規化済み出力をそのまま線形スケールして disparity として使っており、`disparity ∝ 1/depth` の逆数変換はパイプラインのどこにも無い
- 正規化は min-max [0,1] 化のみで、規約変換はしていない
- プロジェクト過去の実測（2026-06-19）でも「0.0 = far / 1.0 = near」と確認済み

**Unity 側の含意**: `DecodeAnchorDepthMetersFromBundle` は `z01` に**線形**（`screenDist - eps - popout × z01`）だが、`z01 ∝ 1/Z` である以上、本来は**反比例**（`Z ∝ 1/z01`）にすべき。**未修正**。

ただし min-max 正規化で `Zmin`/`Zmax` が捨てられているため、変換式を直しても得られるのは「相対的な前後関係の正しさ」までで、メートル単位の絶対距離は復元できない（affine-invariant な相対深度）。絶対距離が必要になった時点で metric depth モデルへの置き換えかカメラキャリブレーションが要る。

### チャンク間ドリフトによる z01 の破綻と修正（2026-08-05）

**症状**: `z01` が実距離とまったく相関しない（人物のサイズは一定なので、姿勢を補正した投影サイズから相対距離が逆算できる。その相対距離と `z01` の相関を測った）。

```
旧 bundle: z01 と 1/Z の全体 R² = 0.0037   ← 使い物にならない
           ただし chunk 11 単体では R² = 0.9261 ← disparity として正常に機能している
```

**原因**: `--depth-chunk-size=140`（overlap 25）でチャンク分割され、`_fit_depth_affine` が隣接チャンクのオーバーラップ 25 フレームだけを使って percentile ベースのアフィン合わせをチェーン式に繋いでいた。2167 フレームでは 19 チャンクになり、アフィン係数の誤差が蓄積して動画全体で `z01` の意味がドリフトする。

境界での急な不連続は出ない（`|Δz01|` は境界付近 0.00153 / それ以外 0.00146 = 1.05 倍）ため、「つなぎ目は滑らかなのに全体では壊れている」という気づきにくい壊れ方をする。**チャンク境界を探すのではなく、チャンク内 R² と全体 R² を比較することで検出できる。**

**修正後（再生成した bundle）**:

| 指標 | 旧 | 新 |
|---|---|---|
| 全体 R² | 0.0037 | **0.1657**（45 倍） |
| disparity/depth の判別 | 決め手なし | disparity 系と判別可能 |
| 境界の \|Δz01\| 比 | 1.05 倍 | 0.78 倍（不連続なし） |

なお同じ再生成で `shots` / `shot_boundary_policy` も追加されているが、こちらは Unity 側で対応済み（後述の「カット（shot 境界）でスケールと平滑化をリセットする」を参照）。`bundle_human.svb` は 1 ショットなので、今回の深度調査の結果には影響しない。

> **2026-08-17 追記: ドリフトは現行 bundle にも残っている。** 全体 R² 0.1657 は相関で言えば 0.41 で、まだ足りない。1 秒 median で測ると `corr(人の z01, 経過時間) = +0.658`、`corr(ボールの z01, 経過時間) = +0.415` で、72 秒のクリップを通して `z01` が単調に「遠い」方向へ流れている。詳細は後述の「bundle_human.svb でボールが人に埋もれる（2026-08-17 調査）」を参照。

### anchor_z の向きは bundle 世代で異なる（2026-08-06 確定・実装済み）

生成側が深度規約を統一し、`manifest.json` に `depth_policy` ブロックを追加した。この結果 **`anchor_z` の向きが bundle 世代で逆になった**。

| bundle | `depth_policy` | `anchor_z` の向き |
|---|---|---|
| `bundle.svb` / `bundle_old.svb` / `bundle_train.svb`（〜8/5） | なし | `larger = nearer` |
| `bundle_human.svb` / `bundle_animal.svb`（8/6 以降） | あり | **`larger = farther`** |

Unity 側は `IsAnchorDepthLargerMeansFarther()` で `depth_policy` の有無を見て分岐する（`StreamingStereoVideoPlayer.Manifest.partial.cs`）。**値そのものから向きを推定してはいけない** — 深度が中央付近に固まっている bundle では推定が成立しない。

修正の効果（`bundle_human.svb`、共存 2156 フレーム）:

| | ボールが人より手前 |
|---|---|
| 修正前（`larger=nearer` 前提） | 16.4% |
| 修正後 | **83.0%** |

#### 符号の判定方法（NG パターンを含む）

この符号の特定には遠回りをした。再発防止のため手法ごとの可否を残す。

| 手法 | 可否 | 理由 |
|---|---|---|
| `rawAnchor.z` と `depthStats.median` の照合 | **経路による** | `depth_sample` / `bbox_center_depth` では有効。**`animal_camera_root` では `median` が `anchor_z` のコピーなので同語反復になり無効** |
| `z` と bbox サイズの相関（無条件） | **NG** | `edgeTouch`（bbox が画面端で切れる）と `held_previous_high_conf`（z 固定のまま bbox だけ動く）が混ざると符号がショットごとにバラつく。`bundle_animal.svb` は edgeTouch 80.5% / hold 22.6% |
| `z` と bbox サイズの相関（`edgeTouch` と `hold` を除外・ショット内） | **有効** | 符号既知の `bundle_human.svb` で較正できる（person -0.217 / other -0.428 = `larger=farther`）。同条件で animal は修正前 +0.628 → 修正後 -0.591 と符号だけ反転し、絶対値はほぼ保存された |
| `z` と SMPL `transl.z` の相関 | **NG** | `transl.z` は `pred_cam_t` 系で bbox サイズから決まる量。深度の独立検証にならない（循環参照） |
| `depth.npz` を anchor の `(u,v)` で直接サンプルして照合 | **決定的** | 生成側でのみ実行可能。これで「未反転」が確定した |

**教訓**: 「bundle の値 A と sidecar の値 B が一致する」は**搬送の証明であって規約の証明ではない**。反転漏れ・二重反転のどちらでも一致は起こる。規約を確かめるには、その値が作られる**前段のデータ**（`depth.npz`）まで遡る必要がある。

#### 未解決のまま残ること

符号は直ったが、**人とボールの配置深度差は平均 1.9 cm のまま**（`PopoutRangeMeters = 0.35` に 0..1 を線形で押し込むため）。`disp_min` / `disp_max` は manifest に載るようになったが、DepthCrafter は affine-invariant（`disp_raw ≈ a/Z + b`、`a`,`b` は clip ごとに未知）なので、この 2 値だけでは絶対距離に較正できない。

### 配置の検算結果（2026-08-06 実測）

配置したモデルを画面へ再投影して `meta.bin` の bbox と比べた実測。計測手順は [smpl-retargeting.md](smpl-retargeting.md) の「配置の実測方法」を参照。

| 対象 | 大きさ | 位置 | 判定 |
|---|---|---|---|
| **Else（ボール）** | `sizeRatio` 全 70 サンプルで **1.000** | 上端・下端とも **0.0 px** | **完全に正しい** |
| **Human** | `sizeRatio` median 1.169 / p90 1.537 / max 1.741 | 下端 0.0 px、**上端 median -61.5 px（最悪 -163.4）** | 姿勢が深いほど縦にはみ出す |

**Else が完璧なのは毎フレーム bbox からスケールを計算し直しているため。** Human/Animal は `GetOrLockModelLocalScale` で初回フレームに固定する設計で、これ自体は正しい（人体のサイズは不変であるべき）。実際 `scale` のユニーク値は 1 個で、frame 0 では `sizeRatio = 1.020` と一致している。

崩れるのは姿勢が深くなったときだけ:

```
立位   : 1.020 / 1.045 / 1.052    ← 正しい
仰向け : 1.569                     ← 縦に 1.57 倍
座位   : 1.537 / 1.582             ← 縦に 1.5 倍
```

**この件でボール側を疑う必要はない。** 「ボールが体の正しい位置に来ない」と見えていた症状は、ボールではなく Human 側が映像とずれていたことによる。原因の切り分けと消去した仮説の一覧は [smpl-retargeting.md](smpl-retargeting.md) の調査ログ（2026-08-06）に記録した。

### bundle_human.svb でボールが人に埋もれる（2026-08-17 調査）

**症状**: 胸トラップ・逆立ちして足でボールを扱う場面で、ボールが Human モデルの内部に埋まる。

**測定方法**: `meta.bin` を runtime と同じ手順（`DecodeAnchorDepthMetersFromBundle` → `ResolvePopoutFraction` → `PinholePlacementSpace` → `TrackModelPlacement.ResolveDesiredLocalScale`）で全 2167 フレーム再現し、`TrialScene` の設定値（`screenDistanceMeters = 1`, `popoutRangeMeters = 0.35`, `enableAnchorDepthRangeNormalization = 0`）で計算した。

#### 原因: 人が同じ場所に立っていても配置深度が身長の 64% ふらつく

この動画はカメラがほぼ固定で、人物の移動も小さい。SMPL の `transl.z`（4D-Human の weak-perspective カメラ距離）は立位区間で 98.3〜128.9 の **1.31 倍**しか動かない。

姿勢の影響を除くため **keypoint 身長 1.45〜1.75 m の立位フレーム（全体の 53%、1154 フレーム）** に絞り、さらに `transl.z` が 100〜106 のフレーム（＝人がほぼ同じ位置に立っている 671 フレーム、0.0〜71.0 s に散在）だけを見ると:

| 項目 | 値 |
|---|---|
| `z01` | 0.180 〜 0.370 |
| 配置深度 z | 0.680 〜 0.933 m → **同じ距離なのに 253 mm ばらつく** |
| 表示身長に対する比 | **64%**（表示身長 median 0.398 m） |
| 人とボールの深度差 median | 0.080 m |
| 人の深度のばらつきはその | **3.8 倍** |

**人の配置深度のふらつきが、人とボールの深度差そのものより 3.8 倍大きい。** ボールの anchor がどれだけ正確でも前後関係は成立しない。

1 秒 median の推移で見ると分かりやすい:

| 時刻 | `transl.z` | `bboxH` | `z01` |
|---|---|---|---|
| 0 s | 102.2 | 414 | **0.195** |
| 48 s | 100.4 | 441 | **0.310** |

`transl.z` も `bboxH` もほぼ同じ = 同じ距離に同じ姿勢で立っているのに、`z01` は 0.195 と 0.310。

#### 根本原因: Human の `anchor_z` が距離情報を持っていない

同じ bundle でも Human と Else で品質がまったく違う。

相関はフレーム単位のノイズを潰すため **1 秒ごとの median に平滑化してから**取った（生フレームでも同じ傾向）。

| 検証 | 実測 | あるべき値 |
|---|---|---|
| corr(SMPL `transl.z`, Human の `anchor_z`) | **+0.026** | +1 に近い |
| ↑ 立位フレーム（keypoint 1.45-1.75m）のみ | +0.078 | +1 に近い |
| corr(1/`bboxH`, Human の `anchor_z`) | −0.109 | +1 に近い |
| corr(ボール径, ボールの `anchor_z`) | **−0.456** | −1 に近い ✓ |
| corr(`transl.z`, 1/`bboxH`) | **+0.691** | ✓ 独立推定同士は一致 |

`transl.z` と `bboxH` は互いに一致しているので、この 2 つは距離を正しく捉えている。**`anchor_z` だけが無相関**。立位に絞っても、時間平滑化しても変わらない。

`source/placement_observations.json`（検証専用）を見ると Human は confidence 1.0 / `placementHeld` 0% / status 全 2167 フレーム `high`、anchor source は全て `depth_sample`、bbox 内 depth の IQR は median 0.00195（49 サンプル）。つまり **bundle 側の観測ゲートは正常に通っており、anchor のサンプリング自体は想定どおり動いている**。壊れているのはサンプリング元の depth 値のほう。

**これは DepthCrafter の原理的な限界ではない**（当初そう判断したが、他 bundle の実測で否定した）。同じパイプラインで生成された `bundle_animal.svb` は正常である。

| bundle | shots | 長さ | 判定 | 指標 |
|---|---|---|---|---|
| `bundle_animal.svb` | 15 | 71 s | **OK** | corr(SMAL `transl.z`, `z01`) = **+0.550 / +0.624** |
| `bundle_human.svb` | 1 | 72 s | NG | corr(SMPL `transl.z`, `z01`) = **+0.026** |
| `bundle_train.svb` | 1 | 61 s | NG | corr(bbox 径, `z01`) が全 8 track で正（負であるべき） |

#### 真の原因（2026-08-17、生成側の回答で判明）

**配布されていた `bundle_human.svb` / `bundle_train.svb` が、2026-08-05 のチャンク間ドリフト修正より前に作られた `depth.npz` のままビルドされていた。** 修正（チェーン式アフィン合わせ → 全チャンクの重なりを同時に解く joint least-squares、`_solve_global_chunk_affine`）自体は正しく効いており、検証専用ジョブで `depth.npz` は再生成されていたが、それが本番ディレクトリにコピーされていなかった。

生成側が修正後の `depth.npz` で再ビルドして検証した結果:

| bundle / track | 指標 | 配布版 | 再ビルド版 |
|---|---|---|---|
| human track0 | corr(`transl.z`, `z01`) | +0.010 NG | **+0.649 OK** |
| human track0 | corr(`z01`, 経過時間) | ±0.68 | −0.12 |
| train track0 | corr(`z01`, 経過時間) | +0.951 | +0.33（推移ほぼ平坦） |

紛らわしい点として、`bundle_shots_depthfix.svb` / `bundle_shots_h264fix.svb` は名前に "fix" が付くが、それぞれ `anchor_z` の符号規約統一と Quest 向け H264 再エンコードで、チャンクドリフト修正とは無関係。命名が衝突していた。

#### 修正版での改善度（2026-08-17 実測）

修正後の `depth.npz` で再ビルドされた検証用 bundle を、Unity 実装と同じ計算（`screenDistanceMeters = 1`, `popoutRangeMeters = 0.35`, 正規化 OFF）で全フレーム評価した結果。

| 指標 | 配布版 | 修正版 |
|---|---|---|
| corr(`transl.z`, `z01`) | +0.026 | **+0.649** |
| corr(`z01`, 経過時間) | +0.657 | **−0.116** |
| 立位・同距離フレームでの配置深度のばらつき | 253 mm（表示身長の 64%） | **180 mm（49%）** |
| ↑ が人とボールの深度差の何倍か | 3.8 倍 | **2.0 倍** |
| 胸まわり接触（110f）でのめり込み | 20% | **5%** |
| 足で扱うシーン（320f, f251-987）でのめり込み | **42%** | **7%** |
| 逆さ姿勢（121f, f252-875）でのめり込み | **40%** | **12%** |

**症状として問題になっていた 2 シーンが最も大きく改善している。** ただし配置深度のばらつきは依然 180 mm（表示身長の 49%、人とボールの深度差の 2.0 倍）あり、「実用上ほぼ問題ない水準まで改善した」であって「深度が正しくなった」ではない。

この検証用ビルドは `video.mp4` が inpaint 前だったため（[bundle-shared/README.md](bundle-shared/README.md) の D-002）、実機での見た目確認は正式な再配布ビルドを待つ。

#### NG パターン: 少数サンプルの相関を機構の確認なしに因果として書く（2026-08-17 撤回）

当初この節に「**shot 数と対応している。** カットが多い素材では shot 境界がドリフトの蓄積を実質的に断ち切る」と書いたが、**誤りだった**。生成側が実装を確認したところ、`shots.json` は inpaint 段と bundle 構築段にしか渡っておらず、**`depth_splatting_inference.py` には shots の概念自体がない**。shot 数でドリフトの蓄積量が変わる機構は存在しない。

3 bundle という少ないサンプルで観測された相関を、相手側の実装を確認できないまま因果として提示したのが原因。**別プロジェクトの挙動を推論するときは、観測された対応関係を「仮説」として明示し、機構を確認できる側に確認を依頼する。**（この撤回自体は生成側の切り分けを妨げなかったが、判断材料としては誤りを渡していた）

なお `bundle_animal.svb` だけ正常だった理由は未確定。生成側は「たまたま残差が小さかった」としているが、`FINNAL_ANIMAL` の `depth.npz` だけ修正後のものが本番に反映されていた可能性を確認依頼中。

#### animal の `depth_policy` は宣言どおり（2026-08-17 確認）

生成側から「`bundle_shots_depthfix.svb`（animal）の `anchor_z` が `depth_policy` なし版とビット単位で同一なので、宣言が誤っているかもしれない」という指摘があったが、**Unity 側の実測では宣言どおり `larger = farther` で正しい**。

| track | corr(`transl.z`, `z01`) 生の値 | `farther` 仮定 | `nearer` 仮定 |
|---|---|---|---|
| track 0 | +0.550 | **+0.550 OK** | −0.550 NG |
| track 1 | +0.624 | **+0.624 OK** | −0.624 NG |

ビット同一なのは、animal の anchor が `animal_camera_root` 経路で**以前から独立して符号反転済み**だったため。旧版の時点ですでに `larger = farther` だったので、`depth_policy` を宣言してもデータを変える必要がなかった。person / other は旧版が `larger = nearer` だったので depthfix で反転してデータが変わった。**animal だけビット同一なのは期待どおりの挙動。**

生成側とのやり取りは [bundle-shared/README.md](bundle-shared/README.md) の D-001 に記録。再現ツールは [`bundle-shared/bundle_depth_check.py`](bundle-shared/bundle_depth_check.py)。

接触シーンでは配置深度差 5.0〜5.8 cm に対し必要分離（胴体半径 + ボール半径）が 4.8〜5.0 cm でギリギリのため、40% のフレームで不足する。

> **NG パターン: 実サイズ仮定に依存する指標で前後関係を判定した**（2026-08-17 撤回）。当初ここに「人とボールの前後関係の一致率は 33%（偶然の 50% より悪い）」と書いたが、**ボールの実サイズをサッカーボールの 0.22 m と仮定していたための誤り**。この素材はハンドボール（男子 3 号 = 直径 0.185〜0.19 m）。0.185 m で計算すると一致率は **77.6%** になる。3 cm の仮定違いで 32% → 76% に振れるので、**この指標は判定に使えない**。「映像の bbox 径から真の距離比を逆算する」系の指標を使うときは、実サイズが確定している対象に限ること。

#### ドリフトの正体と、差分を取れば相殺できること

`z01` の狂いはランダムノイズではなく、**クリップ全体を通した時間ドリフト**である。これは上の「チャンク間ドリフトによる z01 の破綻と修正（2026-08-05）」で一度対処された問題の残存で、全体 R² 0.1657（相関 0.41）ではまだ足りていない。

1 秒 median での実測:

| 検証 | 実測 |
|---|---|
| corr(人の `z01`, **経過時間**) | **+0.658** |
| corr(ボールの `z01`, 経過時間) | +0.415 |
| corr(人の `z01`, ボールの `z01`) | **+0.679** |

**ドリフトは人とボールに共通**なので、差分を取れば相殺される。

| 前後関係の一致率（映像の投影サイズから推定した真の前後関係との比較） | |
|---|---|
| 生の `z01` をそのまま popout に流す（現行実装） | **33%** |
| 人の `z01` − ボールの `z01` の差分で判定 | **74%** |

つまり `anchor_z` は **絶対深度としては使えないが、同一フレーム内のオブジェクト間の相対順序としてはまだ情報を持っている**。`manifest.json` の `depth_policy` が `"relative (not metric) depth ordering signal"` と書いているのは正確で、現行の `DecodeAnchorDepthMetersFromBundle` はこれを絶対値として popout レンジへ直接流している。

#### 棄却した仮説: スケールロックが原因（2026-08-17 → **2026-08-18 に撤回**）

> **この棄却は誤りだった。** 2026-08-18 のバッチ実測（後述「確定: Human の投影サイズが映像より 1.24〜1.56 倍大きい」）で、最初の仮説どおりスケールロックが主因と確定した。以下は「meta データからの代用値で表示の仮説を棄却してはいけない」という失敗の記録として残す。

調査の途中で「`lockScale = IsCategoryPerson || IsCategoryAnimal` により Human だけが shot 先頭でロックされ、1 shot の `bundle_human.svb` では全編 frame 0 のサイズに固定される。その結果モデルが映像の 1.94 倍に膨らんでボールを飲み込む」と判断したが、**誤り**だったので記録して残す。

- 「映像であるべき表示身長」として `(2 × bboxH / eye_h) × (z / fy)` を全フレームで計算し、ロック値と比べた。しかし **`bboxH` は姿勢で縮む**（座位で立位の 48%、逆立ちでも縮む）。人体のサイズは姿勢で変わらないので、bbox 高さに合わせるべきという前提そのものが誤っていた
- 姿勢の影響を除いた指標として `bboxH / keypoint 身長` を使い「カメラ距離が 2.47 倍変動」と述べたが、これも誤り。分母の keypoint 身長が推定ノイズを拾う。**28.2s → 28.4s の 0.2 秒（6 フレーム）で keypoint 身長が 0.820 m → 0.464 m へ半減**しており、この 1 点で比が 2.5 倍跳ねていた。5 秒刻みの median で見れば 228.8〜273.3（**1.19 倍**）でほぼ一定
- 立位フレーム（keypoint 1.45-1.75 m）だけで測り直すと、frame 0 のロック値 0.347 m は立位 median 0.393 m の **0.88 倍**。膨らむのではなく 12% 小さい

**スケールロックは正しい設計**。上の「配置の検算結果（2026-08-06 実測）」にある「崩れるのは姿勢が深くなったときだけ」という既存の結論のほうが正確だった。

**教訓**: 配置スケールの妥当性を `bboxH` 由来の量で評価してはいけない。`bboxH` は姿勢と距離の両方を含む。距離だけを見たいときは SMPL の `transl.z`（`meta.bin` 内にある）を使い、姿勢を揃えたいときは keypoint 身長でフレームを絞り込む。keypoint 身長そのものを分母にすると推定ノイズが比に乗る。

#### 修正: popout の disparity レンジが nearness と別経路だった（2026-08-17 修正済み）

`ResolvePopoutFraction` が使う `dMin` / `dMax` は `1 - anchorZ01RangeMax` / `1 - anchorZ01RangeMin` を直接組み立てていた。一方 `nearness` のほうは `NormalizeAnchorZ01`（正規化）と `IsAnchorDepthLargerMeansFarther()`（向き判定）を通ってから渡される。**同じ量を比べているのに片方だけが 2 つの変換を通る**という単位の不一致で、2 つの症状が出ていた。

**症状 1: 旧 bundle（`larger = nearer`）で全オブジェクトが同一深度になる** ← 発動していた

`bundle.svb` は `depth_policy` を持たないので `nearness = z01` がそのまま渡る（反転しない）。ところがレンジ側は `1 - z01` の向きで作られる。`z01 ∈ [0.602, 0.836]` に対しレンジが `[0.164, 0.398]` になり、**全サンプルがレンジの外側**に落ちて `dMax` に張り付く。

**症状 2: `enableAnchorDepthRangeNormalization` を ON にすると飽和する** ← 未発動（シーンは全て `0`）

正規化で 0..1 に張り直したあとの `nearness` を、正規化前のスケールで Clamp していた。

修正前後の実測（全 bundle、`popoutRangeMeters = 0.35`）:

| bundle | 向き | 現行 OFF | 修正 OFF | 現行 ON | 修正 ON |
|---|---|---|---|---|---|
| `bundle_human.svb` | farther | clamp 2.9% / 幅 0.350 m | **完全一致** | clamp 77.3% | clamp 0.0% |
| `bundle_animal.svb` | farther | clamp 4.4% / 幅 0.350 m | **完全一致** | clamp 58.4% | clamp 0.0% |
| `bundle_train.svb` | farther | clamp 3.6% / 幅 0.350 m | **完全一致** | clamp 87.2% | clamp 0.0% |
| `bundle.svb`（旧） | **nearer** | clamp **100%** / 幅 **0.000 m** | clamp 2.7% / 幅 **0.350 m** | clamp 80.7% | clamp 0.0% |

**修正内容**: `Z01ToNearness()`（向き変換を 1 か所に集約）と `TryResolveNearnessRange()`（レンジも `NormalizeAnchorZ01` → `Z01ToNearness` の同じ経路に通す）を追加し、`ResolvePopoutFraction` はそこからレンジを受け取る。`larger = farther` かつ正規化 OFF（現行の全シーン）では値が従来と完全に一致するので、既存の見え方は変わらない。

正規化を通すと disparity の絶対スケールが失われて `dMin` が 0 に落ちるため、その場合は逆数変換を諦めて線形へ退避する（従来の `AnchorDisparityMinimum` チェックがそのまま働く）。

**なお `enableAnchorDepthRangeNormalization` は本来不要**である。`ResolvePopoutFraction` の逆数変換は

```
farness(d) = (1/d - 1/dMax) / (1/dMin - 1/dMax)
  d = dMax（最も近い）→ farness = 0 → popout 1.0（最も手前）
  d = dMin（最も遠い）→ farness = 1 → popout 0.0（スクリーン面）
```

の形で **`[dMin, dMax]` の全域を popout の `[0,1]` に写している**。実測でも 4 bundle すべて popout レンジ使用率 100%（上の修正後）。「レンジを使い切る」という正規化の目的は逆数変換の導入時点で果たされており、二重に掛ける意味がない。フラグは Inspector 互換のため残してあるが、ON にする理由はない。

#### この bundle の実測値（旧記録との差分）

`bundle_human.svb` は 2026-08-06 に再生成されており、上の「深度レンジの圧縮は実害が小さい（2026-07-30 検証）」の実測値は旧 bundle のもの。現行値は次のとおり。

| 項目 | 旧記録（2026-07-30） | 現行（2026-08-06 生成） |
|---|---|---|
| z01 の値域 | 0.256〜0.906（median 0.746） | **0.178〜0.408** |
| 配置深度 z | 0.69〜0.78 m | 0.678〜0.980 m |
| Human の表示身長（frame 0 ロック値） | median 0.305 m | 0.347 m |
| ボール ↔ Human の \|dz\| | median 0.016 m | median 0.059 m（立位のみ 0.080 m） |
| ボールが手前 | 83.6% | 82.9%（立位のみ 89%） |

深度差そのものは 1.6 cm → 5.9 cm に改善している。当時の「接触シーンは全体の 1.1% なので多少の重なりは自然」という結論は、**深度差の絶対値ではなく人の配置深度のばらつき（同じ距離で 253 mm）が問題**であることを見ていなかったため、現状には当てはまらない。深度差 5.9 cm はばらつき 25.3 cm に埋もれる。

### 根本原因: `bboxWorldH` の式が「被写体は単一深度にある」前提（2026-08-18 確定）

上の「確定: Human の投影サイズが映像より 1.24〜1.56 倍大きい」の**さらに上流**。なぜ scale が過大になるのかの答え。

#### 決定的な検証: keypoints でも同じことが起きる

`meta.bin` の keypoints3d（映像から推定した正しい 3D 関節位置）を、Unity と同じ表示スケールで root（深度 `anchorZ`）に置いて投影し、その縦幅を bbox と比べた。

| | / bbox（median） |
|---|---|
| **keypoints を表示スケールで投影** | **1.205** |
| モデルの実ボーン投影（`boneRatio`） | 1.238 |

4-6s では keypoints 1.415 / モデル 1.627、9-12s では 1.202 / 1.316。**両者はほぼ一致する。**

**つまり FK もスケール計算も「壊れて」はいない。映像そのままの正しい 3D 関節を表示スケールで投影しても bbox を 20% 超える。**

#### 式の欠陥

```
bboxWorldH = (2 × bboxH / eye_h) × (anchorZ / fy)
```

これは **「被写体が `anchorZ` という 1 枚の面にある」**前提の式である。実際の人体は前後（カメラ視線方向）に広がっており、**手前の部位は大きく、奥の部位は小さく投影される**。その合計の縦幅は、単一深度で計算した高さより必ず大きくなる。

姿勢による誤差の拡大（keypoints 投影 / bbox）:

| フレーム | 姿勢 | keypoints 投影 / bbox |
|---|---|---|
| f=0 | 立位（前後の広がり小） | **1.073** |
| f=110 | 立位 | 1.232 |
| f=150 | 深い前傾 | 1.423 |
| f=160 | 深い前傾 | **1.759** |

**立位ですでに 7% 過大**で、前後に広がる姿勢ほど拡大する。

#### Else が無傷な理由

ボールは直径 4 cm の球で**前後の広がりが無視できる**ため、この式が正確に成立する。実測でも `sizeRatio` は全フレーム **1.000**。

```
Human : 前後に広がる  → bboxWorldH が過小評価 → scale が過大 → 1.2〜1.8 倍に膨らむ
Else  : 前後に広がらない → 式が正確       → 常に正しい
```

**この非対称が「人だけがボールを飲み込む」の最終的な理由。**

#### 否定された容疑者（すべて実測で）

| 容疑者 | 判定 | 根拠 |
|---|---|---|
| 深度（D-001 のドリフト） | 否定 | `boneRatio`=1.0 に必要な depth が popout レンジ外（4-6s で 1.165 m > 0.98 m） |
| FK の姿勢 | 否定 | 膝・肘の角度が `meta.bin` と**完全一致**（差 0.0°）、肩 2.7° |
| 骨長差 | 否定 | 腕の長さの差を投影に直すと −16 px。実際の `boneTopDelta` は −113 px |
| bbox の精度 | 否定 | フレームに bbox を描画して目視確認。**人物を頭頂から足先まで正確に囲んでいる** |
| `anchor_z` が表面の深度 | 否定 | 「表面 + 体の厚み半分」で補正すると 4-6s が 30 mm → 101 mm となり逆に離れすぎる（depth median は既に体の中央付近を指している） |
| **`bboxWorldH` の単一深度前提** | **確定** | keypoints でも 1.205、ボールは 1.000 |

#### 実測で分かった副次的な事実

- **脚の骨長補正が過剰**: `HumanBoneLengthCorrection` は 2026-08-06 に「モデルの脚が映像より 8.3% 短い」と実測した固定係数を埋め込んでいるが、現在の既定モデル（`00_Female_A_01`）では**補正後に 7% 長い**。**固定係数がモデルに依存している。**
- **前腕は未補正で 25% ずれる**（モデルが短い）。補正対象が脚だけのため。
- 骨長比（胴で正規化、モデル / 映像）: 大腿 1.073 / 下腿 1.066 / 上腕 1.054 / **前腕 0.746**

#### 制約

**単一の scale ではどの姿勢でも合わせられない。** 必要な補正量が姿勢で 1.07〜1.76 と変動するため、基準フレームを立位に取れば立位で合い、深い姿勢では残る。人体のサイズは不変であるべきなので毎フレーム合わせ直すこともできない（Else と同じ扱いにはできない）。

### 棄却: `anchor_z` は「表面の深度」なのに root へ適用している（2026-08-17 提起 → 2026-08-18 棄却）

> **棄却。** 「表面 + 体の厚みの半分」で root を奥へずらす補正を実際に計算したところ、4-6s の頭とボールの 3D 距離が 30 mm → **101 mm** となり、接触に必要な 41 mm を大きく超えて逆に浮いた。`anchor_z` は bbox 内 depth の **median** なので、前傾時は頭（手前）から腰（奥）までの中央値、すなわち**既に体の中央付近**を指している。`p10` を使っていれば「表面」だったが、median では補正は不要かつ過剰になる。考え方は妥当だったが実測で否定された。真の原因は後述の「根本原因: `bboxWorldH` の式が『被写体は単一深度にある』前提」。

**深度が直っても「ボールが人に埋もれる」が変わらなかったことから、user の指摘で判明した。**

depth map は視線方向の最前面（表面）を返す。bundle は人物マスク内の depth median を `anchor_z` にしているので、`anchor_z` は実質「その人が写っている面の深度」＝**表面の代表値**である。しかも `source/placement_observations.json` の実測では、人物領域の depth はほぼ一様（`IQR` median 0.00195、`p10-p90` median 0.00391 = この bundle の深度レンジ全体の **1.7%**）で、**体の前後の厚みをまったく捉えていない**。

一方 Unity は ① でその world 位置に **root（腰相当）** を置く。root は体の内部なので、本来は「表面 + 体の厚みの半分」だけ奥に置くべきである。

`bundle_human.svb` での実測（表示身長 0.3916 m 基準）:

| | 表示スケール換算 |
|---|---|
| 体の前後の広がり（keypoints の z スパン median 0.676 m） | 165 mm |
| → 表面から root までの距離 ≒ その半分 | **83 mm** |
| ボール半径 | 21 mm |
| **補正しないことで生じる相対ずれ** | **+62 mm**（人が余計に手前へ寄る） |
| 実測の人・ボール深度差 median | 89 mm |

**ずれ 62 mm は深度差 89 mm の 70% を食いつぶす。** さらに姿勢依存で悪化する。

| 姿勢（keypoints の z スパン） | 表面 → root の距離 |
|---|---|
| 下位 25%（体が前後に薄い） | 61 mm |
| **上位 25%（前傾・寝そべり）** | **115 mm** ← 深度差 89 mm を超える |

体が前後に広がる姿勢では、ずれが深度差そのものより大きくなる。**ボール側は正しい位置にあるのに、人体だけが手前へ飛び出してボールを飲み込む。** 症状が出る 4〜6 秒（深い前傾でボールを頭の横に保持）と 9〜12 秒（肩立ちでつま先にボールを乗せる）は、まさにこの区間にあたる。

Else 側にも同じ性質のずれがある（球の中心を表面深度に置くので半径ぶん手前）が、量が 21 mm と小さいため、**相対的に人だけが寄る**形になる。

**未検証**: 「表面から root まで = z スパンの半分」は粗い近似。`anchor_z` が体のどの部位の深度に対応しているかを keypoints の z 分布と突き合わせる必要がある。

### 確定: Human の投影サイズが映像より 1.24〜1.56 倍大きい（2026-08-18 バッチ実測）

**「ボールが人に埋もれる」の直接原因はこれ。** 深度でも FK でもなかった。

**測定方法**: `Assets/Editor/BatchPlaybackLogger.cs` を追加し、Unity を**バッチモードで PlayMode 実行**して `[GAP]` / `[PLACE]` / `[BALLHEAD]` を採取した（TestScene / 16 秒 / 47 フレーム）。`-nographics` を**外せば** VideoPlayer が動きフレームが進む。Unity が開いていると `Multiple Unity instances cannot open the same project` で落ちるので、事前に閉じてもらうこと。

```
Unity.exe -batchmode -projectPath <proj> -executeMethod BatchPlaybackLogger.Run \
          -scene Assets/Scenes/TestScene.unity -playSeconds 16 -logFile out.log
```

#### 実測結果

| | `scale` の挙動 | 投影サイズ |
|---|---|---|
| **Human** | **1 値に固定**（0.2300、shot 先頭でロック） | `boneRatio` median **1.238**（4-6s **1.562** / 最大 2.31） |
| **Else** | **42 種類**（0.1153〜0.2274、毎フレーム更新） | `sizeRatio` **1.000**（全フレーム完璧） |

`boneBottomDelta` median **+8.4 px**（下端合わせは効いている）に対し、`boneTopDelta` median **-67.2 px**（4-6s は **-115.3 px**）。**下端は bbox に合っているのに上端がはみ出す** = モデルの縦の投影が大きい。

| 区間 | `boneRatio` | `boneTopDelta` |
|---|---|---|
| 0-3s 立位 | 1.149 | -56 px |
| **4-6s（深い前傾）** | **1.562** | **-115.3 px** |
| 9-12s（肩立ち） | 1.316 | -74.8 px |

#### 原因の切り分け

| 候補 | 指標 | 判定 |
|---|---|---|
| **bbox の縮小に scale が追従しない** | corr(`boneRatio`, `bboxH`) = **-0.457**（最も強い） | **主因** |
| 深度誤差 | corr(`boneRatio`, 1/`depth`) = +0.253。`boneRatio` = 1.0 にするのに必要な depth は 4-6s で **1.165 m**、9-12s で **1.124 m** で、いずれも **popout レンジの上限 0.98 m を超える** | **否定** |
| FK 姿勢の誤り | `skeletonLocal`（姿勢適用後の実 Head-Foot 距離）が bind pose の **0.76 倍**まで縮んでおり姿勢は正しく再現されている。corr(`boneRatio`, `skeletonLocal`) = -0.152 | **否定** |

**深度をどう補正しても届かない。** D-001 の深度修正で症状が変わらなかったことと完全に整合する。

#### 非対称が症状の正体

`ApplyReplaceableModelTransform` の `lockScale = IsCategoryPerson || IsCategoryAnimal` により、

- **Else** は毎フレーム bbox に合わせ直すので、**深度が狂っても見た目サイズは常に正しい**（実測 `sizeRatio` = 1.000）
- **Human** だけ scale 固定なので、**映像で人が小さく写るフレームで相対的に膨らむ**

結果、**人だけが 1.24〜1.56 倍になってボールを飲み込む**。`[GAP]` でも 4-6s の最寄りボーンが `spine_01/02/03`・`upperleg_r` になっており（映像ではボールは頭の横）、**膨らんだ胴体がボールを包み込んでいる**ことが裏づけられる。

#### 「棄却した仮説」の撤回

下の「棄却した仮説: スケールロックが原因（2026-08-17）」は**撤回する。最初の仮説が正しかった**。

棄却の根拠にした「あるべき表示身長」は `bboxH` から計算した粗い代用値だった。本来見るべきは `boneRatio`（**モデルの実ボーン投影 ÷ bbox**）で、これは実行時にしか取れない。「人体は姿勢で縮まないのだから bbox に合わせる前提が誤り」という論理自体は正しいが、**FK が姿勢を正しく再現してもなお投影が 1.56 倍大きい**という事実は代用値では見えなかった。

**教訓**: 表示に関する仮説は、`bboxH` などの meta データからの代用値で棄却してはいけない。`[PLACE]` / `[GAP]` の実行時ログで裏を取る。バッチ再生の手順は上記のとおり確立した。

### 対処 A: shot 先頭で FK 適用後の投影から scale を測り直す（2026-08-18 実装・実測）

上の「根本原因」への 1 つ目の対処。**`boneRatio` は改善したが、症状（ボールの埋もれ）には効かなかった。**

#### 実装

`RefineLockedScaleFromProjectedBones()`（`Playback.partial.cs`）を ⑦ の直後に追加。track ごとに 1 回だけ、FK 適用後の骨格投影高さを `TryProjectBonesToEyeHeight` で実測し、`boneRatio = 投影 / bboxH` で `scale /= boneRatio` としてロックを上書きし、下端を合わせ直す。

`GetOrLockModelLocalScale` が新しくロックを作った時点で補正フラグ（`scaleRefinedByTrack`）が外れるので、**shot 境界・モデル変更・インスタンス再生成のいずれでも自動的にやり直される**。`boneRatio` が 0.4〜3.0 の外なら補正しない（bbox が画面端で切れているケースで誤った基準を焼き付けないため）。Inspector の `refineScaleFromProjectedBones`（既定 true）で切り替え。

#### 実測（TestScene / 16 秒 / 共通 43 フレームで補正前後を比較）

`[SCALEFIX] track=0 boneRatio=1.056 scale 0.2300 → 0.2178 (×0.947) bboxH=441`

| 区間 | `boneRatio` 前 → 後 | 1.0 からの乖離 | `boneTopDelta` 前 → 後 |
|---|---|---|---|
| 全体 | 1.270 → **1.197** | 27.0% → **19.7%** | −71.6 → −47.9 px |
| 0-3s 立位 | 1.180 → **1.106** | 18.0% → **10.6%** | −67.2 → −36.3 px |
| 4-6s 深い前傾 | 1.627 → **1.536** | 62.7% → **53.6%** | −114.2 → −97.3 px |
| 9-12s 肩立ち | 1.316 → **1.243** | 31.6% → **24.3%** | −74.8 → −58.1 px |

基準フレーム f=0 では **1.056 → 0.995** とほぼ完全に一致し、意図どおり動いている。下端合わせの残差（`|boneBottomDelta|` 14.8 → 13.9 px）も悪化していない。

#### しかし症状は改善しなかった

| 区間 | `overlap`（めり込み）前 → 後 | `dist` median 前 → 後 |
|---|---|---|
| 全体 | **4 → 4**（変化なし） | 42 → 38 mm |
| 4-6s | **0 → 2**（悪化） | 53 → 47 mm |
| 9-12s | **2 → 0**（改善） | 37 → 31 mm |

**トータルのめり込み回数は変わらない。** モデルが 5% 小さくなったぶんボーンが体の内側へ寄り、ボールとの距離も一律に縮んだ（42 → 38 mm）ため、区間ごとに改善と悪化が入れ替わっただけだった。

#### 評価

- **効果があった点**: `boneRatio` の乖離を約 27% 削減。基準フレームでの誤差はほぼゼロになった。`bundle_animal` のように shot 先頭が代表的でない bundle（8 shot 中 4 shot が 25% 以上外れ、最悪 2.62 倍）では効果が大きいはずで、そちらは未検証
- **効果がなかった点**: 深い姿勢の誤差（4-6s で 1.536 = 53.6% 過大）が残るため、症状は消えない。**単一 scale では姿勢ごとの変動（1.07〜1.76）を吸収できない**という制約が予測どおり効いた
- **補正量が一律 5% にしかならない理由**: 基準フレーム（shot 先頭 = 立位）の `boneRatio` が 1.056 と小さいため。深い姿勢を基準に取れば補正量は増えるが、今度は立位が小さくなりすぎる

**この実装は残す**（基準フレームでの誤差が消えるのは純粋な改善で、副作用は `dist` が一律 4 mm 縮む程度）。ただし**症状の解決には別の手が要る**。

### 棄却: `screenDistanceMeters` を上げて遠近感を弱める（2026-08-19 実測で否定）

「`boneRatio` は深度 z に依存するので、スクリーンを遠ざけて遠近感を弱めれば投影の膨らみが減る」という案を実測した。**効果がないどころか、めり込みは悪化した。**

#### 実測（TestScene / 16 秒 / 共通 43 フレーム）

| `screenDistanceMeters` | 1.0 | 1.3 | 1.5 |
|---|---|---|---|
| 人の配置深度 median | 0.766 m | 1.061 m | 1.260 m |
| ロック後の scale | 0.2178 | 0.2983 | 0.3519 |
| **`boneRatio` 全体** | **1.197** | **1.195** | **1.196** |
| `boneRatio` 4-6s | 1.536 | 1.495 | 1.478 |
| `boneRatio` 9-12s | 1.243 | 1.252 | **1.256（悪化）** |
| **`overlap` 全体** | **4 回** | **8 回** | **9 回（悪化）** |
| `overlap` 9-12s | 0 回 | 2 回 | **5 回（悪化）** |
| \|depthGap\| median | 20 mm | 19 mm | 19 mm |

深度を 1.64 倍にしても **`boneRatio` は全体で 1.197 → 1.196 とまったく動かない。**

#### 予測が外れた理由

「`boneRatio` = 1.0 に必要な depth は 4-6s で 1.165 m」という逆算は、**scale を固定したまま depth だけ変える**前提だった。実際には

```
scale = bboxWorldH / modelH ∝ anchorZ
```

なので **scale も depth に比例して大きくなる**（実測 0.2178 → 0.3519）。すると各ボーンの root 相対深度も同じ比率で伸びるため、

```
遠近感の強さ = 体の前後の広がり ÷ root 深度   ← 分子・分母がともに ∝ z
```

**比が z によらず一定になる。** 遠近感は `screenDistanceMeters` では変えられない。

`overlap` が増えたのは、モデルもボールも一律に大きくなった結果、ボーンとボール中心の距離（`dist` 38 → 48 mm）よりボール半径のほうが速く増えたため。奥行き感（\|depthGap\|）も 20 → 19 mm とむしろ痩せており、**得るものがない。**

#### この設計での構造的な限界

`scale ∝ anchorZ` である限り、**遠近感による投影の誇張は避けられない**。誇張を消す唯一の方法は「投影が bbox に一致するよう姿勢ごとに scale を調整する」＝毎フレーム補正だが、それは人体が姿勢のたびに伸縮することを意味する。**単一 scale と正しい投影サイズは両立しない。**

### 棄却: 毎フレーム scale を補正して `boneRatio` を 1.0 に保つ（2026-08-19 試算で否定）

「単一 scale と正しい投影サイズは両立しない」なら毎フレーム合わせればよい、という案。**実測ログからの試算で、視覚的に破綻することが確定したので採らない。**

`scale_t = scale / boneRatio_t` とした場合の表示身長（案 A 適用後のロック値を 1.0 とする）:

| | 値 |
|---|---|
| 倍率の範囲 | **0.456 〜 1.017** |
| 最大 / 最小 | **2.23 倍の伸縮** |
| 表示身長に直すと | **17.9 cm 〜 39.8 cm** |
| フレーム間（1/3 秒）の変化率 | median 7.3% / p90 **28.9%** / max **61.2%** |
| 15% 以上変わる区間 | 10 / 42 |

具体例（いずれも 1/3 秒での変化）:

```
f170→180 (5.7→6.0s)  身長 ×1.61   boneRatio 1.98→1.23
f180→190 (6.0→6.3s)  身長 ×0.56   boneRatio 1.23→2.19
f190→200 (6.3→6.7s)  身長 ×1.29   boneRatio 2.19→1.70
```

**0.3 秒で人が半分になったり 1.6 倍になったりする。** 埋もれは消えても、それ以上に不自然な見た目になる。`boneRatio` 自体が姿勢推定のフレーム間ノイズを含む（0.983〜2.193）ため、平滑化しても追随の遅れと伸縮のどちらかが残る。

**結論**: 単一 scale を維持し、投影サイズの誤差（`boneRatio` median 1.197）は**既知の制限として受け入れる**。

### 訂正: 骨長補正は正しく動いている（2026-08-19、測定ミスの記録）

2026-08-18 に「`HumanBoneLengthCorrection` の固定係数がモデルに合っておらず、補正後に脚が 7% 長い」「前腕は未補正で 25% ずれる」と書いたが、**どちらも当方の測定ミス**だったので訂正する。

#### 何を間違えたか

`meta.bin` の keypoints3d は 44 点（`hmr2_openpose25_extra19`）で、**先頭 25 点が OpenPose BODY_25、26 点目以降が SMPL 由来の extra19**。胴の基準として当方は **MidHip(8)** を使ったが、実装が使っているのは **Pelvis(39)**（`HumanSourceKeypointPelvis = 39`、`HumanOtherContact.partial.cs`）。

| 胴の長さ（f=0） | 値 |
|---|---|
| MidHip(8) 基準 | 0.5007 m |
| **Pelvis(39) 基準（実装）** | **0.4776 m** |
| 比 | **1.048** |

胴で正規化する指標なので、この 4.8% の差がそのまま全部位の比に乗っていた。

#### 正しい実測

`BoneHierarchyDump`（一時ツール）で prefab の骨長を直接測り、Pelvis(39) 基準の keypoints と突き合わせた結果:

| | prefab（補正前） | 映像 f=0 | factor | `[BONELEN]` 実測（補正後） | 一致度 |
|---|---|---|---|---|---|
| 大腿 | 0.777 | 0.811 | 1.045 | **0.819** | **1.009** |
| 下腿 | 0.725 | 0.844 | 1.164 | **0.852** | **1.009** |

**補正後のモデルが映像の比とほぼ完全に一致している（誤差 0.9%、FK 適用後の微差）。補正は意図どおり動いている。**

#### あわせて判明したこと

- **係数は固定ではない**。`TryResolveLegBoneLengthFactors` が毎回 keypoints3d とモデルの実測から算出しており、モデルを差し替えれば自動で追従する（「固定係数がモデル依存」という指摘も誤りだった）
- **twist ボーンは無い**。`LeftUpLeg → LeftLeg`、`LeftLeg → LeftFoot` はいずれも hops=1 の直結で、`localPosition` の倍率がそのまま区間長の倍率になる（比 1.000）。胴だけ `Hips → Spine → Spine1 → Neck` の hops=3
- **腕は本当に未補正**だが、ずれは 25% ではなく **6〜9%**

| | prefab | 映像 f=0 | 必要な factor |
|---|---|---|---|
| 上腕 | 0.506 | 0.536 | **1.060** |
| 前腕 | 0.493 | 0.537 | **1.090** |

#### 教訓

`meta.bin` の keypoints を参照するときは、**実装が使っているインデックス定数を必ず確認する**。44 点のうち BODY_25 部分と extra19 部分に似た意味の点（MidHip と Pelvis）が両方あり、取り違えても値がそれらしく出るため気づきにくい。

### 棄却: 骨長補正を腕にも広げる（2026-08-19 実装 → 実測で逆効果）

`HumanBoneLengthCorrection` の対象を脚（大腿・下腿）から腕（上腕・前腕）にも広げ、`meta.bin` の keypoints3d から倍率を算出して適用した。**投影サイズが悪化したので採らない。**

#### 実測（TestScene / 16 秒 / 共通 43 フレーム）

`[BONEFIX] thighFactor=1.149 shinFactor=1.187 upperArmFactor=1.008 foreArmFactor=1.462`

| 区間 | `boneRatio` 脚のみ → 脚+腕 | 乖離 |
|---|---|---|
| 全体 | 1.197 → **1.270** | 19.7% → **27.0%（悪化）** |
| 0-3s 立位 | 1.106 → 1.126 | 10.6% → 12.6% |
| **4-6s** | 1.536 → **1.627** | 53.6% → **62.7%（悪化）** |
| 9-12s | 1.243 → 1.270 | 24.3% → 27.0% |

| | 脚のみ | 脚+腕 |
|---|---|---|
| `overlap`（めり込み） | 4 | **4（変化なし）** |
| 手が topBone になるフレーム | 9 / 43 | **13 / 43** |
| `boneTopDelta` median | −47.9 px | **−60.7 px** |

**前腕を 46% 伸ばした結果、手がさらに上へはみ出しただけだった。症状（`overlap`）は 1 回も減っていない。**

#### なぜ逆効果か

**骨長を映像に合わせることと、投影を bbox に収めることは別問題**である。骨長比は確かに映像と一致するようになった（`[BONELEN]` 補正後 upperArm 0.542 / foreArm 0.542 に対し、映像 f=0 は 0.536 / 0.537）。しかし手足の先端が遠くなるぶん**ボーンの投影範囲は広がる**ので、`boneRatio` は悪化する。

上の「根本原因」のとおり投影の膨らみは `bboxWorldH` の単一深度前提から来ており、骨長を正確にしても消えない。むしろ末端が伸びるぶん増える。

#### 未解明の点

`foreArmFactor = 1.462` は Clamp 上限 1.5 に近く極端。実行時の `modelForeArm`（`ResolveBoneDistance(LeftLowerArm, LeftHand)`）から逆算すると補正前の前腕/胴 = 0.371 だが、`BoneHierarchyDump` で prefab の bind pose を直接測った値は 0.493 で**一致しない**。`HumanoidRigCache` が返すボーンの world 位置がどの時点のものかを確認しないと、この倍率が妥当か判断できない。

**脚（`thighFactor` 1.149 / `shinFactor` 1.187）でも同じ疑いがあるが、脚は補正後に映像と 1.009 の精度で一致することが確認できているので実害は出ていない。**

#### 判断

**腕の補正は revert する。** 骨長の正確さより投影サイズの悪化のほうが視覚的な影響が大きく、症状にも効かない。脚のみの補正（既存動作）に戻す。

### 解明: 骨長の「1.33 倍の食い違い」は別モデルを比較していただけ（2026-08-19）

`foreArmFactor = 1.462` の根拠が prefab 実測と 1.33 倍食い違う件を追ったところ、**そもそも別のモデルを測っていた**ことが原因だった。骨長補正の実装に問題はない。

#### 原因

`TestScene` の `trackModelIndices` が `selectedHumanIndex` より優先される。

```
trackModelIndices:
- trackId: 0  modelIndex: 16   ← Human
- trackId: 1  modelIndex: 4    ← Else
```

当方は `selectedHumanIndex: 0` を見て `00_Female_A_01` の骨長を測っていたが、実行時に使われるのは modelIndex 16。ボーン名でも判別できた（prefab 側 `LeftHand` に対し実行時は `hand_l` / `lowerarm_l` / `spine_02` という UE 系命名）。

#### 骨長補正は正しく動いている

| | 前腕 / 胴 |
|---|---|
| 映像（keypoints, Pelvis(39) 基準） | **0.537** |
| モデル補正前 | 0.367 |
| **モデル補正後** | **0.537** |

`foreArmFactor = 1.462` は、このモデルの前腕が映像より 27% 短いことを正しく反映した値だった。Clamp 上限（1.5）に近いのは事実だが、過大な補正ではない。

#### 訂正: 「modelIndex が範囲外」は当方の誤り（2026-08-19 同日）

一度「`modelIndex: 16` は配列範囲外で Clamp されている」「`06_` が欠番」と書いたが**どちらも誤り**。`Assets/Resources/Models/Human/` には **`06_Female_C.vrm`** があり、`Resources.LoadAll<GameObject>` は VRM も返すため **17 件（インデックス 0〜16）**。ファイル名の番号と配列インデックスは完全に一致しており、`modelIndex: 16` は範囲内で `16_Male_Eric` が正しく選ばれている。`HumanIndexPrefixer.Run` も `Renamed 0/17`（変更不要）と正しく判定した。

**原因**: `ls *.prefab` で一覧を作り、`.vrm` を除外したまま数えていた。**`Resources.LoadAll<GameObject>` の対象は prefab だけではない**（VRM など GameObject として読める資産すべて）。モデル一覧を数えるときは拡張子で絞らないこと。

| 配列インデックス | ファイル |
|---|---|
| 5 | `05_Female_B_03.prefab` |
| **6** | **`06_Female_C.vrm`** ← ここを見落としていた |
| 7 | `07_Human_Beta.prefab` |
| 16 | `16_Male_Eric.prefab` |

#### 使用中のモデル（2026-08-19 時点、TestScene）

| track | カテゴリ | modelIndex | 実際に使われる prefab |
|---|---|---|---|
| 0 | Human | 16（→ 15 に Clamp） | **`16_Male_Eric`** |
| 1 | Else | 4 | **`04_Soccer`** |

これ以前の調査記録で「既定モデルは `00_Female_A_01`（女性）」「Else は `00_Baseball`」と書いた箇所があるが**誤り**。実際は男性モデルとサッカーボール。

#### 教訓

**実行時に使われている prefab は `selectedHumanIndex` ではなく `trackModelIndices` で決まる**（track ごとの指定が優先）。モデル依存の実測をする前に、`trackModelIndices` を確認するか、ボーン名で実物を照合すること。

### 症状の正確な測定と、深度では解決できないことの確定（2026-08-19）

#### `[GAP]` の `overlap` は症状を捉えていない

`[GAP]` の `overlap` は `dist < ボール半径` で判定するが、**`dist` はボール中心と最寄り「ボーン」の距離**であり、ボーンは体の内部にある。**体表面へのめり込みは検出されない。**

部位の太さ（ボーンから体表面まで、表示身長比: 胴 0.13 / 大腿・上腕 0.055 / 下腿・前腕 0.040 / 足 0.030 / 手 0.022 / 頭 0.055）を足して測り直すと:

| 区間 | 既存 `overlap` | **実際のめり込み** | めり込み量 median |
|---|---|---|---|
| 全体 | 4/47（9%） | **17/47（36%）** | −4 mm |
| **4-6s** | 2/10 | **7/10（70%）** | **+20 mm** |
| 9-12s | 0/10 | **4/10（40%）** | −1 mm |

```
f140 (4.7s)  dist 18mm / 必要 68mm → +51mm めり込み  spine_02
f150 (5.0s)  dist 19mm / 必要 66mm → +47mm          spine_02
f210 (7.0s)  dist 23mm / 必要 70mm → +47mm          hip
```

**胴体にボールが深く埋まっている。症状は解決していない。**

#### 深度では原理的に届かない

| 区間 | 接触に必要な距離 | 実際の `dist` | うち `depthGap` | `lateralGap` |
|---|---|---|---|---|
| 全体 | 31 mm | 36 mm | −12 mm | 26 mm |
| **4-6s** | **67 mm** | 47 mm | **+5 mm** | 37 mm |
| 9-12s | 31 mm | 31 mm | −23 mm | 17 mm |

4-6s では **必要 67 mm に対し深度方向が 13 mm しか担えていない（5.2 倍不足）**。同じ倍率で広げるには `popoutRangeMeters` を 0.35 → **1.82 m** にする必要があり、`screenDistanceMeters = 1.0` を超えるため配置が破綻する。

**根本的な理由**: 人とボールが接触している場面では、実空間での距離差は「ボール半径 + 体の厚み」= 実寸で約 0.26 m。被写体距離が約 100 m（SMPL `transl.z`）なので **相対 0.26%** にすぎない。**depth map の分解能で捉えられる量ではない。** D-001 で `anchor_z` のドリフトを直しても、この分解能の壁は変わらない。

#### 結論

**深度・スケール・骨長・FK のいずれを直しても、接触場面のめり込みは解消できない。** 以下がすべて実測で否定された。

| 手段 | 結果 |
|---|---|
| `anchor_z` のドリフト修正（D-001） | corr +0.026 → +0.649 に改善したが症状は不変 |
| スケール基準の実測補正（案 A） | `boneRatio` 27% → 19.7%。めり込み総数は不変 |
| `screenDistanceMeters` を上げる（案 B） | `boneRatio` 変化なし、めり込みは 4 → 9 に悪化 |
| 毎フレーム scale 補正 | 0.3 秒で身長 ×0.56 と破綻 |
| 骨長補正を腕へ拡張（案 C） | `boneRatio` 悪化、めり込み不変 |
| `popoutRangeMeters` を上げる | 必要値 1.82 m は `screenDistance` を超え破綻 |

**残る手段は「Other 側を表示 Human の体表面へ寄せる」= 接触補正（`enableHumanOtherContactCorrection`、実装済み・現在 OFF）のみ。** これは体型・投影差で接触が崩れる状況のために実装されたもので、まさに今回の状況に対応する。適用するかは user の判断を仰ぐ。

### 訂正: 「深度では原理的に届かない」は誤り（2026-08-19 同日に撤回）

直前に「人はカメラから約 100 m 先にいるので、接触時の 0.26 m の前後差は depth map の分解能で捉えられない。深度では原理的に解決できない」と結論したが、**二重に誤っていた**ので撤回する。

#### 誤り 1: 実距離を 100 m と読んだ

SMPL の `transl.z ≈ 100` を実距離（メートル）と解釈したが、これは **4D-Human の weak-perspective カメラの値**で、焦点距離 5000 px を仮定した数値。実距離ではない。

実距離は bbox から逆算できる。

```
実距離 = 実身長 × 焦点距離(px) ÷ bboxH
       = 1.602 m × 914 px ÷ 441 px = 3.32 m
```

焦点距離は `fy_norm × eye_h / 2 = 914 px`。視野角でも検算が合う（fovy 38.6°、3.3 m で縦 2.3 m の視野 → 身長 1.6 m が 69% ＝ bboxH 441/640 と一致）。**実距離は median 3.61 m**（2.41〜5.95 m）。

したがって接触時の前後差 0.22 m は相対 **6.1%** であり、0.26% ではない。

#### 誤り 2: 比較対象を取り違えた

「深度が 5.2 倍足りない」は、`[GAP]` の **`depthGap`（ボール中心と最寄り"ボーン"の視線方向距離）** を「接触に必要な前後差」と直接比べた結果だった。**人は root（腰）の位置で配置される**ため、前傾姿勢では胸のボーンが root より手前へ出て `depthGap` は小さくなる。比較すべきは root 基準の配置深度差である。

#### 正しい数値

| | 値 |
|---|---|
| 人とボールの `z01` 差 | median **0.058**（量子化 30 段ぶん） |
| → 配置深度差に換算 | **84 mm** |
| 接触に必要な前後差（実寸 220 mm を表示スケール ×0.242 換算） | **53 mm** |
| **充足率** | **168%（余っている）** |
| popout レンジの実使用幅 | **350 / 350 mm（100%）** |

**深度データにも popout レンジにも十分な情報がある。**

#### では何が不足しているのか

4-6s の実測を 3D 距離で見ると、

| | 値 |
|---|---|
| ボール中心と最寄りボーンの 3D 距離 | 47 mm |
| 接触に必要な距離（部位の太さ + ボール半径） | 67 mm |
| **不足** | **20 mm**（1.4 倍） |

内訳は `depthGap` 13 mm・`lateralGap` 37 mm で、**横方向のずれのほうが大きい**。「深度が 5 倍足りない」のではなく「3D で 1.4 倍足りない」が正しい。

不足の主因は、モデルの投影が bbox より **1.197 倍**大きいこと（`boneRatio`）。体が太いぶん必要距離が増えている。`boneRatio` が 1.0 なら必要距離は 67 → 56 mm になり、不足は 20 → 9 mm に縮む。

**「原理的に不可能」ではない。** 手段の再検討が要る。

### 特定: 4-6s は bundle の `anchor_z` がボールを人より「奥」に置いている（2026-08-19）

「ボールが胸に埋もれる」区間の直接原因。**Unity 側の配置ロジックではなく、bundle のデータが逆を向いている。**

#### ずれの内訳（画面の縦・横・奥行きに分解）

`[GAP]` に `upGap` / `rightGap` を追加して測った（`up` = 画面の上下、`right` = 左右）。

| 区間 | 3D dist | 必要 | 不足 | \|depth\| | **\|up\|** | \|right\| |
|---|---|---|---|---|---|---|
| 全体 | 36 mm | 31 mm | −6 mm | 18 | 14 | 11 |
| **4-6s** | 47 mm | 67 mm | **+19 mm** | 13 | **37** | **5** |
| 9-12s | 31 mm | 31 mm | −1 mm | 23 | 15 | 10 |

**左右（`right` 5 mm）は合っている。** 縦（`up` −37 mm＝ボールがボーンより下）が最大だが、これはモデルが bbox より 1.197 倍大きく下端合わせで胸が上へずれるためで、`boneRatio` に帰着する。

#### 決定的な問題: 深度の符号が逆

| 区間 | `z01` 差（人 − 球） | 配置深度差 | 接触に必要 |
|---|---|---|---|
| 0-3s | +0.088 | **+117 mm**（ボールが手前） | +53 mm |
| **4-6s** | **−0.038** | **−55 mm（ボールが奥）** | +53 mm |
| 9-12s | +0.046 | **+71 mm**（手前） | +53 mm |

```
f140 (4.7s)  人 z01 0.268 / ボール 0.298 → ボールが 43 mm 奥
f150 (5.0s)  人 z01 0.256 / ボール 0.280 → ボールが 33 mm 奥
f170 (5.7s)  人 z01 0.260 / ボール 0.322 → ボールが 90 mm 奥
```

**映像ではボールは顔の横（カメラ側）にあるのに、bundle の `anchor_z` は「人より奥」と記録している。** 必要な +53 mm に対して −55 mm なので **108 mm 逆方向**。この区間で体に埋まるのは当然の帰結。

0-3s と 9-12s では符号が正しく（+117 / +71 mm）、必要量も満たしている。**4-6s 固有の問題。**

#### 位置づけ

D-001（クリップ全体の時間ドリフト）は修正済みで、`corr(transl.z, z01)` は +0.026 → +0.649 に改善した。しかしそれは**人物 track の時系列**の話で、**同一フレーム内の人とボールの前後関係**は別の問題。この区間では前後が逆転している。

Unity 側では対処できない（データが逆なら配置も逆になる）。**bundle 生成側へ D-003 として報告する。**

#### 補足: 症状の切り分け結果

| 区間 | 主因 |
|---|---|
| 4-6s（胸・顔まわり） | **`anchor_z` の前後関係が逆**（bundle 側） |
| 9-12s（足） | 深度は正しい（+71 mm）。`boneRatio` 1.243 による体の太りで不足 −1 mm 前後 |

### 撤回: 「4-6s は anchor_z がボールを人より奥に置いている」（2026-08-19 起票 → 同日棄却）

上の「特定: 4-6s は bundle の `anchor_z` が…」は**当方の誤報**だったので撤回する。**bundle のデータは正しかった。**

#### 何を間違えたか

`bundle_human.svb` の 4-6s で「人 `z01` < ボール `z01`（ボールが奥）」を見つけ、映像でボールが顔の横に写っていることから「手前にあるはずなのにデータが逆」と判断して D-003 を起票した。

**誤りは「映像でボールが顔の横にある = カメラ側にある」という目視の直感**にあった。この区間は人物が深く前傾しており、**骨盤が手前・胸や首が奥**になる。ボールは胸の前で保持されているので、**骨盤より奥にあるのが正しい。**

#### 裏付け

生成側が **depth map とは独立な HMR2 の `pose.keypoints3d`**（root 相対のカメラ空間 3D 関節）で検証した結果:

| f | hip_z（骨盤中点） | neck_z（首） | neck − hip |
|---|---|---|---|
| 100 | 0.031 | 0.036 | +0.004（直立） |
| 140 | −0.002 | 0.302 | **+0.304** |
| 200 | −0.051 | 0.489 | **+0.541** |
| 220 | −0.025 | −0.038 | −0.013（戻る） |

**depth map が「奥」と言っていた区間と完全に一致して、姿勢推定側も「首は骨盤よりずっと奥」と言っている。** user 側でも映像を確認し「hip より後ろにあるので正しい」と確認済み。

#### person のアンカーは骨盤 1 点

生成側の回答で判明した重要な事実。`meta.bin` の person の `anchor_u/v/z` は **左右 hip の中点 1 点**でサンプリングされている。したがって `anchor_z` は「人物全体の代表深度」ではなく**骨盤の深度**であり、体が大きく折れ曲がる姿勢では他 track との単純な前後比較が成立しない。

#### Unity 側の描画方式（生成側への回答）

生成側から「Unity は単一スカラーで前後判定しているのか、メッシュ表面で解決しているのか」と問われ、確認した結果は**後者**。

- Unity 標準の Z バッファ描画。深度ソートや描画順の特別処理はない（`renderQueue` / `ZWrite` を触るのは `showOtherProxyBoxes` の debug 用マテリアルのみ）
- `anchor_z` の用途は **root をどこに置くか**だけ。Human は Hips 相当、Else は球の中心
- 配置後の前後関係は**変形後のメッシュ表面同士**で解決される

したがって **アンカー同士の値が逆転していても描画は破綻しない**。生成側で person のアンカー部位を動的に切り替える対応は不要と回答した。

#### 副産物

生成側が調査中に `build_bundle_svb.py:3053` の実装バグを発見した。`obj["sam2"]["segmentation"]` を見ておらずトップレベルしか参照していないため、**キーポイントを持たない全 track（`other` 全般）が一度もマスクベースのアンカーを使えていなかった**（常に `bbox_center_depth` にフォールバック）。D-003 の症状には無関係だが別 issue として修正提案されている。

#### 教訓

**目視の印象で「データが逆」と判断しない。** 深い前傾・仰向けなど体が折れ曲がる姿勢では、画面上の見た目と奥行きの前後関係が直感と食い違う。`meta.bin` には `pose.keypoints3d`（root 相対の 3D 関節）という**depth map と独立した情報**が入っているので、前後関係を疑うときはまずこれで裏を取る。今回は生成側に depth map の再検証・3 通りのサンプリング比較・チャンク境界の確認まで行わせてしまった。

### 実測: 部位の太さと、めり込みの正確な量（2026-08-19）

これまで「部位の太さ」を身長比のラフな仮定で置いていたが、**胴体を 1.54 倍過大に見積もっていた**ことが判明した。実測し直し、新しい bundle（`bundle_shots_inpaintfix.svb` 相当。inpaint 済み・D-001 修正済み）で症状を測り直した。

#### 部位の太さの実測（`BodyThicknessDump`）

`SkinnedMeshRenderer` を `BakeMesh` し、各頂点を `boneWeights` の最大重みボーンへ割り当て、**そのボーンの軸（親→子の線分）への垂直距離**の分布を取る。中央値がその部位の実効半径。

| 部位 | 実測（身長比 p50） | 実寸（身長 1.7 m） | 従来の仮定 |
|---|---|---|---|
| **Spine（胴）** | **0.0845** | 14.4 cm | **0.13（1.54 倍過大）** |
| Chest | 0.0954 | 16.2 cm | — |
| Hips | 0.0866 | 14.7 cm | — |
| 大腿 | 0.0522 | 8.9 cm | 0.055 |
| 下腿 | 0.0349 | 5.9 cm | 0.040 |
| 上腕 | 0.0316 | 5.4 cm | 0.055 |
| 前腕 | 0.0274 | 4.7 cm | 0.040 |
| 足 | 0.0408 | 6.9 cm | 0.030（過小） |
| 頭 | 0.0554 | 9.4 cm | 0.055 |
| 手 | 0.0199 | 3.4 cm | 0.022 |

**測り方を 2 回間違えた**ので記録しておく。

1. **対象ボーンを左半身 12 個に絞って「最寄りボーンを総当たり」** → 右半身や肩の頂点が Neck 等へ流れ込み、Neck の値が身長比 0.46（首から 79 cm）と破綻
2. **ボーンの「位置」からの距離** → 関節は骨の端点なので、骨に沿って分布する頂点まで拾う。`LeftLowerLeg` が 0.286 m（すねの長さの 7 割）になった

正しくは **`boneWeights` で所属を決め、骨の軸への垂直距離を測る**。

#### 新 bundle でのめり込み実測

| 区間 | n | `dist` | 必要 | 差 | めり込み |
|---|---|---|---|---|---|
| 全体 | 46 | 36 mm | 34 mm | −3 mm | **19/46（41%）** |
| 0-3s | 10 | 32 | 26 | −7 | 2/10（20%） |
| **4-6s** | 10 | 47 | 50 | **+4** | **6/10（60%）** |
| 6-9s | 8 | 48 | 34 | −6 | 4/8（50%） |
| **9-12s** | 10 | 31 | 35 | **+3** | **8/10（80%）** |
| 12-15s | 11 | 38 | 35 | −5 | 2/11（18%） |

深いめり込みのフレーム:

```
f150 (5.0s)  dist 16 / 必要 50 → +34 mm  spine_02
f210 (7.0s)  dist 26 / 必要 54 → +28 mm  hip
f140 (4.7s)  dist 24 / 必要 52 → +27 mm  spine_02
f180 (6.0s)  dist 15 / 必要 39 → +24 mm  head
f320 (10.7s) dist 21 / 必要 34 → +13 mm  foot_r
```

**症状は実在する。** 胸トラップ（4-6s、`spine`）で 60%、足で扱う場面（9-12s、`foot_r`）で 80% のフレームがめり込んでいる。user の申告と一致。

#### ただし中央値では「あと数 mm」

4-6s は +4 mm、9-12s は +3 mm の不足でしかない。**平均的には僅差で、個別フレームで 30 mm 級のめり込みが起きている**という構図。

必要距離は「部位半径 + ボール半径」で、部位半径は表示身長に比例する。したがって `boneRatio` 1.197 を 1.0 に近づければ:

- 必要距離が縮む（4-6s で 50 → 45 mm）
- 同時に `dist` は増える（モデルが小さくなればボーンが体の内側へ寄るため）

**両方がめり込みを減らす方向に働く。** 中央値レベルなら解消する見込みがあり、深いフレーム（f150 の +34 mm）は接触の瞬間そのものなので多少は残る。

### 実測: スケールを振ってめり込み率との関係を測る（2026-08-19）

`RefineLockedScaleFromProjectedBones` の補正目標を `projectedBoneRatioTarget` として可変にし（既定 1.0）、4 段階で実測した。バッチ引数 `-boneRatioTarget` / `-diagLogs` で指定できるので、シーンを触らずに掃引できる。

#### 結果（新 bundle、47 フレーム、部位半径は実測値）

| target | scale | 表示身長 | `boneRatio` median | めり込み | 不足 median |
|---|---|---|---|---|---|
| **1.00**（現行） | 0.2178 | 371 mm | 1.183 | **19/46（41%）** | −3 mm |
| 0.92 | 0.2003 | 341 mm | 1.075 | 20/47（43%） | −4 mm |
| 0.84 | 0.1829 | 311 mm | 0.980 | 15/47（32%） | −9 mm |
| **0.76** | 0.1655 | 282 mm | 0.885 | **8/47（17%）** | −19 mm |

区間別のめり込み率:

| 区間 | t=1.00 | t=0.92 | t=0.84 | **t=0.76** |
|---|---|---|---|---|
| 0-3s | 20% | 55% | 27% | **0%** |
| **4-6s（胸）** | 60% | 60% | 70% | **40%** |
| 6-9s | 50% | 25% | 25% | **12%** |
| **9-12s（足）** | 80% | 50% | 20% | **0%** |
| 12-15s | 18% | 18% | 18% | 27% |

**モデルを小さくするほどめり込みは減る。** target 0.76 で全体 41% → 17%、9-12s（足で扱う場面）は 80% → 0%。ただし 4-6s（胸トラップ）は 40% 残る。

#### トレードオフ

立位区間（0-3s）でモデルが映像に対してどれだけ小さくなるか:

| target | 0-3s の `boneRatio` |
|---|---|
| 1.00 | 1.092（映像とほぼ同じ） |
| 0.92 | **0.995（ぴったり）** |
| 0.84 | 0.906（10% 小さい） |
| 0.76 | **0.816（18% 小さい）** |

**「めり込みを減らす」と「映像とサイズを合わせる」が正面から競合する。** target 0.76 はめり込みが最小だが、立位で人が明らかに小さく見える。

#### 注意点

- **t=0.92 で 0-3s が 20% → 55% に悪化**している。この区間は手（`ring_03` など細い部位）が最寄りで、モデルを縮めると手の半径も縮む一方で `dist` の変化が小さく、判定が反転しやすい。サンプル数も 10 フレームと少ないためノイズの可能性がある。**単調ではないので、中間値を選ぶときは実測で確かめること**
- 4-6s だけはどの target でも 40% 以上残る。この区間は前傾が深く `boneRatio` が 1.5 前後まで上がるため、基準フレーム（立位）で合わせた scale では吸収しきれない

#### 判断が必要

数値だけでは決められない。めり込みの「見た目の不自然さ」と、モデルが小さいことの「見た目の不自然さ」を天秤にかける必要がある。**新しい bundle は inpaint 済みなので、実際にレンダリングして見比べられる状態になっている。**

### 目視比較: スケール 4 段階のレンダリング結果（2026-08-19）

数値だけでは決められないため、**バッチ再生中に実際のレンダリング結果を PNG で保存**して見比べた。

#### キャプチャの方法

`BatchPlaybackLogger` に `-captureFrames` / `-captureDir` を追加。`VideoPlayer.frame` を監視し、指定フレームに達したら `Camera` を一時的な `RenderTexture` へ描画して PNG 保存する。`-nographics` を付けていないので通常どおりレンダリングでき、**人手を介さず見た目を確認できる**。

```
Unity.exe -batchmode -projectPath <proj> -executeMethod BatchPlaybackLogger.Run \
          -scene Assets/Scenes/TestScene.unity -playSeconds 12 \
          -boneRatioTarget 0.76 -captureFrames 150,320 -captureDir <dir> -logFile out.log
```

`-boneRatioTarget` / `-diagLogs` もバッチ引数で渡せるので、**シーンを一切変更せずに掃引できる**。

#### f150（5.0s、胸トラップ）

| target | 見え方 |
|---|---|
| 1.00（現行） | **ボールが完全に体に埋まって見えない** |
| 0.92 | **見えない** |
| 0.84 | **見えない** |
| **0.76** | **ボールが胸の前に見える** |

**t=0.76 でのみボールが視認できる。** めり込み率（4-6s で 60% → 40%）の改善が見た目に直結した。

#### f320（10.7s、足でボールを支える）

| target | 見え方 |
|---|---|
| 1.00〜0.84 | **全パターンでボールが見える**（足先に乗っている） |
| 0.76 | 見えるが、足とボールの間に**隙間が開く** |

**足の場面は元々視覚的に破綻していなかった。** 数値上は 9-12s のめり込みが 80% → 0% と大きく動いたが、`dist` と必要距離の差が小さい（+3 mm）ため、見た目にはほとんど影響していなかった。**数値のめり込み率と体感の不自然さは比例しない。**

#### 所見

- **症状として問題なのは胸トラップ（4-6s）だけ**で、足の場面は現行設定でも許容範囲
- t=0.76 ではモデルが明らかに小さくなる（表示身長 371 → 282 mm）が、背景が inpaint 済みで比較対象の人物が映っていないため、**単独では極端に不自然には見えない**
- ただし他の区間（立位）では映像より 18% 小さくなるため、**bbox との整合を重視するなら別の判断になる**

### 確定: 症状の原因は配置深度が実距離を反映していないこと（2026-08-20）

長い切り分けの末、**モデルが映像より大きく表示されることが唯一の原因**であり、その原因は**配置深度**だと確定した。それ以外（姿勢・骨長・下端合わせ・遠近感）はすべて実測で否定された。

#### 検証の前提: keypoints3d は実寸メートル

実距離の逆算に keypoints を使うため、まず単位を検証した。

| 部位 | 実測 median | 人体の標準値 | 比 | フレーム間の変動係数 |
|---|---|---|---|---|
| 大腿 | 0.388 m | 0.42 m | 0.92 | **3.0%** |
| 下腿 | 0.403 m | 0.40 m | **1.01** | 3.7% |
| 骨盤→首 | 0.474 m | 0.50 m | 0.95 | 3.3% |
| 上腕 | 0.254 m | 0.30 m | 0.85 | 4.8% |
| 前腕 | 0.254 m | 0.26 m | 0.98 | 8.0% |

**すべて人体標準の 0.85〜1.01 倍で、骨長はフレーム間で 3〜5% しか変動しない。** 実寸メートルとして扱ってよい（`manifest.json` の `metrabs_joint_scale: 0.001` = mm → m 変換とも整合）。

なお **Y スパン（0.464〜1.806 m）は身長ではない**。姿勢で縮むため、実距離の指標には骨長のほうが適する。

#### 実距離の求め方

```
keypoints を実寸のまま距離 z に置いて投影し、縦幅が bboxH になる z を二分探索で解く
（各点の root 相対 z を考慮するので、遠近感も織り込まれる）
```

粗い近似（3D Y スパン ÷ bboxH）との差は median 3%。**独立した推定である SMPL `transl.z` との相関は +0.647** で、基準として妥当。

**bbox と keypoints 投影のずれ（髪・靴のぶん）を較正しようとしたが、係数は 1.0000 で補正不要だった。** SAM2 マスクの外接矩形は keypoints の投影範囲とほぼ一致している。

#### 結果

| | 値 |
|---|---|
| 映像から逆算した実距離 | 2.43〜6.03 m（median 3.69 m） |
| Unity の配置深度 | 0.63〜0.98 m |
| **corr(実距離, 配置深度)** | **+0.361** |
| **比（実距離 ÷ 配置深度）の p10〜p90** | **4.12〜5.10 → 1.24 倍のばらつき** |

**この比が一定なら、深度が実距離の何倍ずれていてもモデルの大きさは合う。** ばらつくから、フレームごとにモデルの大きさが狂う。

そして **1.24 倍というばらつきは、実測した `boneRatio` のばらつき（1.24 倍）と一致する**。深度の誤差がそのまま表示サイズの誤差になっていることの裏づけ。

区間別（0-3s を基準にした比）:

| 区間 | 比 | 基準との差 |
|---|---|---|
| 0-3s | 4.53 | 1.00 |
| **4-6s（胸トラップ）** | **5.76** | **1.27 倍** |
| 9-12s | 4.86 | 1.07 倍 |

**4-6s で 27% 大きくなる。** ここが症状の出る区間と一致する。

#### なぜ深度がずれるとモデルが大きくなるのか

`scale` は shot 先頭で固定される。以降のフレームでは、

```
モデルの投影 = 骨格 × scale ÷ 配置深度
```

配置深度だけが手前へずれると、**その分そのまま大きく描画される**。f150 では実距離 4.24 m（遠い）に対し配置深度 0.719 m（手前寄り）で、モデルが 30% 大きくなっていた。

#### 否定された原因（すべて実測）

| 候補 | 実測 | 判定 |
|---|---|---|
| 背骨の曲がりが再現されていない | Hips-Neck-Head の差 median **7.2°** | 否定 |
| 膝・肘の角度 | 差 **0.0°** | 否定 |
| 配置が高い | `boneBottomDelta` **+8.4 px** | 否定 |
| 骨長が違う | 脚は補正後 **1.009** で一致 | 否定 |
| 前後の広がり（遠近感） | 「板」と仮定した投影との差は **2.4%** | 否定 |
| **配置深度** | **corr +0.361 / 比が 1.24 倍ばらつく** | **確定** |

#### 「単一深度前提が原因」という説明は撤回する

2026-08-19 に「`bboxWorldH` の式が被写体を板と仮定しているため、前後に広がった姿勢で投影が膨らむ」と説明したが、**実測すると遠近感の寄与は 2.4% しかなく、誤りだった**。

```
f150:  「板」と仮定した投影  330 px
        実際（部位ごとの深度）338 px   ← 差は 2.4%
        映像の bbox           242 px   ← 1.36 倍のずれはここ
```

板と仮定しても既に 1.36 倍大きいので、遠近感では説明できない。

### 未解決: shot 先頭でスケールをロックする設計が bundle_animal で破綻している（2026-08-17 発見）

「スケールの基準フレームは shot 先頭に固定する（2026-08-07 実装）」は `bundle_human.svb`（1 shot・先頭が立位）では問題にならなかったが、**`bundle_animal.svb` では半数の shot で破綻している**。

原因は②が**⑤の姿勢適用より前**にあり、しかも基準の `modelHeightMeters`（`baseSkeletonHeightMeters`）が `ReplaceableModel.Awake()` で測る **prefab の bind pose（T ポーズ）の Head〜足首**であること。これを `bboxWorldH`（**姿勢込みの映像の投影高さ**）に合わせているので、**両辺が別のものを測っている**。shot 先頭が立位なら T ポーズと近いので誤差が小さいだけで、先頭が座位・前傾・画面端で切れている shot では破綻する。

`bundle_animal.svb` track 0 の実測（shot 先頭の `bboxH` と shot 内 median の比）:

| shot | フレーム | 先頭 bboxH | shot 内 median | median / 先頭 |
|---|---|---|---|---|
| shot1 | f258-338（2.7s） | **73 px** | 191 px | **2.62** |
| shot3 | f427-668（8.0s） | **172 px** | 357 px | **2.08** |
| shot5 | f898-982（2.8s） | 110 px | 150 px | 1.36 |
| shot6 | f982-1117（4.5s） | 546 px | 391 px | 0.72 |

**track 0 は 8 shot 中 4 shot（50%）で先頭が median から 25% 以上外れている。** shot1 では動物が本来の 1/2.6 の大きさのまま 2.7 秒間表示される。track 1 は 7 shot 中 1 shot。`bundle_human.svb` は 0.87 で許容範囲。

**方向性**: shot 先頭の 1 フレームでだけ「姿勢を適用した状態の投影高さ」を bbox 高さに合わせれば、この次元の不一致は消える。**毎フレームやってはいけない**（人体・動物のサイズは不変であるべきで、姿勢のたびに伸縮する）。

### モデルアセット（`Resources/Models/`）

`Assets/Resources/Models/{Human,Animal,Else}/` 配下の各 prefab は、2 桁ゼロ埋めの番号プレフィックス（例: `00_Baseball`）をファイル名先頭に付ける運用。番号は `selected{Human,Animal}Index`（Inspector の生配列インデックス）や実行時 UI のインデックスと一致させるため、モデルを追加・削除したら欠番なく連番になっているか必ず確認・振り直すこと。

- **Human/Animal**: `Assets/Editor/HumanIndexPrefixer.cs` / `AnimalIndexPrefixer.cs`（`Tools > VisionGraft > Prefix All {Human,Animal} Models With Index`）で振り直す。
  - Human は 2026-08-07 に Renderpeople の無償 rigged モデル 3 体（`rp_carla_rigged_001` / `rp_claudia_rigged_002` / `rp_eric_rigged_001`）を追加して **14 体 → 17 体**（`00_Female_A_01`〜`16_Male_Eric`）。**既存の末尾に 14/15/16 として足したので `selectedHumanIndex` や `trackModelIndices`、これまでの実験ログの index はズレていない**（振り直しは不要だった）。生成は `Assets/Editor/RenderpeopleHumanPrefabBuilder.cs`（`Tools > VisionGraft > Build Renderpeople Human Prefabs`）。
  - **Renderpeople 同梱の shader は取り込まないこと**。`RP_Rigged_MasterShader.shader` は Amplify Shader Editor 製の **Built-in RP 用 surface shader**（`CGINCLUDE` + `#pragma surface`）で、URP では動かずマテリアルがマゼンタになる。shader だけ除外して取り込み、マテリアルは URP/Lit（shader guid `933532a4fcc9baf4fa0491de14d08ed7`）で作り直した。`_BaseMap` / `_MainTex` に `*_dif.jpg`、`_BumpMap` に `*_norm.jpg` を割り当て、smoothness は 0.25 固定。**`*_gm.tga`（Renderpeople の gloss/mask）は未使用**（URP/Lit の `_MetallicGlossMap` は R=metallic / A=smoothness という前提でチャンネル構成が一致しないため、貼らずに固定値にした）。
  - FBX 本体とテクスチャは unitypackage 内の `asset` + `asset.meta` をそのまま `Assets/RP_Character/` に配置して GUID を維持している。**FBX の meta が `animationType: 3`（Humanoid）/ `avatarSetup: 1`（CreateFromThisModel）+ 55 ボーンの `humanDescription` を持っている**ので、Avatar 設定の作業は不要（生成後に `isHuman=True isValid=True` を確認済み）。メッシュ実寸は身長 1.73〜1.86 m で既存 Human と同じ実寸スケール。
- **Else**: `Assets/Saritasa/Models/Sport_Balls/` の 6 種（Baseball/Basketball/Football/Golf/Soccer/Tennis）を `Assets/Editor/ElseModelImporter.cs`（`Tools > VisionGraft > Import And Prefix Else Models`）で `Resources/Models/Else/00_Baseball.prefab`〜`05_Tennis.prefab` としてコピー・採番した（2026-07-17）。元の fbx/material は `Assets/Saritasa/Models/Sport_Balls/` に残したまま GUID 参照する構成（Animal の `Sources/` と同じパターン）で、`AssetDatabase.CopyAsset` を使い GUID 衝突を避けている。
  - 追加で Sketchfab 製のディーゼル機関車モデル（`2ТЭ116УД`, 作者 Leafia dev., **CC-BY-4.0**）を `06_DieselLocomotive.prefab` として取り込んだ。元の `.glb` は `Resources/Models/Else/Sources/DieselLocomotive.glb` に保管。スケルトン・アニメーションなしの静的剛体メッシュなので Else に適合。**CC-BY-4.0 のため最終成果物にクレジット表示が必要**（未対応、要フォロー）。
  - ランタイム側の選択ロジックを実装済み（2026-07-30）。`StreamingStereoVideoPlayer.Core.cs` に `selectedElseIndex`（グローバルデフォルト）と `elsePrefabs`（`Resources/Models/Else` から自動ロード）を追加し、`ResolveTrackPrefab`（`StreamingStereoVideoPlayer.Playback.partial.cs`）に `IsCategoryOther` 分岐を追加した。修正前は Animal 以外のカテゴリを無条件に Human 用ロジックへフォールバックしていたため、Else の track にも Human プレハブが割り当てられていた（既知のバグ、修正済み）。
  - track ごとにモデルを個別指定したい場合は `trackModelIndices`（`{trackId, modelIndex}` の Inspector 配列）を使う。優先順位は「VR 内 Change Model パネルでの変更 > `trackModelIndices` > `selectedHumanIndex`/`selectedAnimalIndex`/`selectedElseIndex` のグローバルデフォルト」。ただし VR 内 Change Model パネルは Person/Animal の track のみが対象で、Else の track には現状表示されない（`TryGetRuntimeModelPickerTarget` が Person/Animal のみを候補にしているため）。
- **`.glb` インポーターの競合に注意**: プロジェクトには UniGLTF（VRM 用、`Packages/com.vrmc.gltf`）と glTFast（`com.unity.cloud.gltfast`）の両方が入っており、どちらも `.glb`/`.gltf` の ScriptedImporter を登録する。UniGLTF 側の自動棲み分け（`UniGLTF.Editor.asmdef` の `versionDefines`）は旧パッケージ名 `com.atteneder.gltfast` を見ているため、現行の `com.unity.cloud.gltfast` とは噛み合わず、新規 `.glb` を追加すると両方拒否されて `DefaultImporter` にフォールバックする（既存の `50+ Animated Animals` 内の `.glb` は `.meta` に旧来のインポーター参照が焼き込まれているため影響を受けない）。対処として Scripting Define Symbols に `UNIGLTF_DISABLE_DEFAULT_GLB_IMPORTER` / `UNIGLTF_DISABLE_DEFAULT_GLTF_IMPORTER` を追加済み（`ElseModelImporter.DisableUniGltfDefaultGlbImporter`）。今後 `.glb` を追加する際はこの設定により glTFast 側が自動で使われる。
- いずれのツールも Unity Editor を閉じてバッチモード（`Unity.exe -batchmode -nographics -quit -projectPath <path> -executeMethod <クラス.メソッド> -logFile <path>`）で実行する必要がある（同一プロジェクトの多重起動不可のため）。`Resources.LoadAll<GameObject>` は `Sources/` サブフォルダ内の `.glb`/`.fbx` もメインアセットが `GameObject` である限り拾ってしまう点に注意（`PrefixWithIndex` はパスに `/Sources/` を含むものを除外するよう対応済み）。

- **runtime 側のフィルタも 2026-08-05 に修正済み**。`LoadPrefabsFromResources` は当初「大文字始まりまたは数字始まり」で絞っていたため、`Resources/Models/Else/Sources/DieselLocomotive.glb` が通過して Else が 8 件（本来 7 件）になっていた。現在は **2 桁ゼロ埋め番号 + `_` で始まる名前のみ**を採用する（`IsIndexedPrefabName`）。`Sources/` 配下の素材はこの規則に合わないので自動的に除外される。あわせて `Resources.LoadAll` の戻り順が保証されない問題に対処するため、読み込み後に名前順（= 番号順）へ整列させ、`selectedHumanIndex` / `selectedElseIndex` / `trackModelIndices` の index を安定させている。
  - Animal だけはこの後に `SortByPriority(AnimalModelPriorityOrder)` が掛かるため、**配列の index は番号ではなく優先順位順**になる。`Assets/Editor/AnimalIndexDumper.cs` が実際の index を出力できるので、`selectedAnimalIndex` を指定する際はそちらで確認すること。

---

## 2026-08-20: ボールが Human モデルに埋もれる問題 — 調査と対処

> **結論**: 主因は `anchor_z` の精度ではなく、**姿勢で bbox が縮むのにモデルのスケールが shot 先頭で固定されたままだったこと**。
> ⑧ `RefineDepthFromProjectedBones`（投影高から深度を逆算）とスクリーンの背景描画化で対処し、**実機で大幅な改善を確認した（2026-08-20）**。

### 対処のまとめ

| 対処 | 内容 | 効果（`bundle_human.svb` 全編 2156f） |
|---|---|---|
| **⑧ `RefineDepthFromProjectedBones`** | スケールを固定したまま、投影された骨格高が bbox に一致する深度へ毎フレーム動かす | `boneRatio` median 1.082 → **0.998**、1.3 超 8.8% → **2.0%**、球が人より手前 79.0% → **87.7%** |
| **スクリーンの背景描画化** | 動画スクリーンを Background キュー・`ZWrite Off` で描く | 深度がスクリーンを越えた **8.1% のフレームでモデルが消えていた**のを解消 |

### 効かなかった手（いずれも実証済み・再提案しないこと）

| 手段 | 理由 |
|---|---|
| affine 係数 `a`, `b` の較正 | `ResolvePopoutFraction` は `1/d` の線形写像。`Z = a/(d-b)` を入れても `d` の線形式のままで、**正規化に完全に吸収される** |
| `popoutRangeMeters` の調整 | 深度の実使用幅は 0.18m しかなく、レンジ幅は主因ではない |
| `screenDistanceMeters` の拡大 | `s` が `z0` に比例するため**比が不変**。ばらつきだけが増幅される |

以下は調査の経緯（時系列）。

### 調査ログ 2026-08-20: popout レンジと「ボールが体にめり込む」問題

> **重要**: このセクションの初版には検証コードの誤りが 4 件あり、結論が逆だった。
> 下の「訂正版」が正しい。誤りの内容と原因は「検証コードの誤りと再発防止」に残す。

#### なぜ popout レンジに押し込んでいるのか

`StreamingStereoVideoPlayer.Manifest.partial.cs` の配置式が答えである。

```csharp
float zPlacement = screenDist - eps - popout;
zPlacement = Mathf.Min(zPlacement, screenDist - 0.0001f);   // 必ずスクリーンより手前
```

**スクリーン（動画テクスチャ）が不透明だから。** 奥に置くと隠れて描画されない。実シーン（`TrialScene` / `TestScene`）では `screenDistanceMeters = 1.0`、`popoutRangeMeters = 0.35`、`enableAnchorDepthRangeNormalization = 0`。

#### 訂正版: popout レンジを広げると劇的に改善する

Unity 実装を忠実に移植（`screen=1.0` / 逆数変換 `ResolvePopoutFraction` / `dMin,dMax` は 120 サンプルの 2%・98% / `fx,fy` 別々）して測った表面クリアランス（`bundle_human.svb`、負 = めり込み）:

| popout | 配置後の身長 | 立位 0-3s | 胸トラップ 4-6s | 足上げ 9-12s | 全編 | 全編めり込み率 |
|---|---|---|---|---|---|---|
| **0.70** | 330mm | +130.7 | **+26.0** | **+50.0** | **+73.6** | **12%** |
| 0.50 | 379mm | +50.2 | −22.6 | +2.8 | +19.5 | **27%** |
| **0.35（現行）** | 416mm | +0.0 | **−43.5** | **−21.9** | −1.2 | **52%** |
| 0.25 | 441mm | −7.8 | −35.9 | −26.2 | −12.5 | 71% |
| 0.15 | 466mm | −24.0 | −27.1 | −13.9 | −23.6 | 86% |
| 0.00 | 503mm | −59.3 | −11.0 | +9.6 | −15.5 | 80% |

現行設定の区間別:

| 区間 | クリアランス | めり込み率 |
|---|---|---|
| 0-3s 立位 | +0.0mm | 50% |
| **4-6s 胸トラップ** | **−43.5mm** | **90%** |
| 7-8s | +4.1mm | 37% |
| **9-12s 足上げ** | **−21.9mm** | **100%** |
| 13-30s | −4.9mm | 62% |
| 30-60s | +15.7mm | 32% |

**立位はほぼ完璧（+0.0mm）で、ユーザーが申告した 2 区間だけが突出して悪い。** そして `popoutRangeMeters` を 0.35 → 0.70 に上げるとめり込み率が 52% → 12% に下がる。0.50 でも 27%。

**トレードオフ**: popout を広げると `z = screenDist - popout` が小さくなり、配置後の身長が 416mm → 330mm に縮む。また最前面が `screen - 0.70 = 0.30m` まで来るので、VR で近すぎないか実機確認が要る（`MinDistanceFromHeadMeters = 0.25` の clamp のすぐ手前）。

#### `a`, `b` 較正が効かないことの確認（この結論は変わらず）

`ResolvePopoutFraction` は `farness = (1/d - 1/dMax)/(1/dMin - 1/dMax)` で `1/d` の線形写像。`Z = a/(d-b)` の較正を入れても `1/Z = (d-b)/a` となり、**どちらも `d` の線形式**なので正規化後に一致する。**affine 係数の較正は原理的に効果ゼロ。**

#### 検証コードの誤りと再発防止

初版の結論（「popout をどう振ってもめり込み率 78〜87% で解決しない」）は、次の 4 件の誤りによるものだった。

| # | 誤り | 影響 |
|---|---|---|
| 1 | X 方向の投影に `fy_norm * eye_h / 2` を使った（正しくは `fx_norm` と `eye_w`） | **なし**（正方形ピクセルで `fx*eye_w = fy*eye_h = 1828`、偶然一致） |
| 2 | `dMin`/`dMax` を全フレーム全 track の min/max で算出 | 実装は**120 サンプルに間引いた上で 2%・98% パーセンタイル** |
| 3 | **popout 配分を線形とした** | 実装は**逆数変換**（`ResolvePopoutFraction`）。**結論が逆転した主因** |
| 4 | `screenDistanceMeters = 2.0`（Core.cs の既定値）を使った | 実シーンは **1.0**。身長・クリアランスが全て 2 倍ずれていた |

さらに初版以前の測定でも次の誤りがあった。

| # | 誤り | 影響 |
|---|---|---|
| 5 | ボール半径に実物大 0.0925m を使った | 配置はミニチュアスケール（[[placement-scale-judgment]]）。桁が合わない |
| 6 | 「体の最前面 vs 球の最背面」で判定 | 腕を振ると最前面が手首になり常に破綻。**ボール中心が体の内部にあるか**（骨への垂直距離 vs 太さ）が正しい |
| 7 | 骨リストに足の指（19/22 BigToe・21/24 Heel）を入れ忘れ | 最寄り骨が Ankle 止まりになり足首→つま先の長さが丸ごと乗った |
| 8 | スケールを `H/1.7026`（実寸基準）で計算 | 実装は shot 先頭で投影ボーンを bbox に合わせてロック（案 A）。姿勢が縮むフレームで 1.5 倍ずれる |

**共通の原因**: 配置パイプラインを**実装コードを読まずに記憶と docs から再構成した**こと。特に #3・#4 は「`ResolvePopoutFraction` を読む」「シーンの serialize 値を見る」だけで防げた。

**再発防止**: 配置に関わる数値検証をするときは、必ず先に該当実装を読み、`docs/bundle-shared/` の `Bundle` クラスと組み合わせた**移植コードをファイルとして残してから**測る。今回作った移植（`Placer` クラス: 2%/98% 較正・逆数変換・clamp・`fx`/`fy` 別投影）を今後の検証の起点にする。

#### 結論（訂正版）

- **画面上（2D）ではボールとモデルの接触部位は正しく重なっている**（胸 7.6px / 足 14.0px、球半径 22.8px に対して）。投影・FK は合っている
- 現行設定では**立位はほぼ完璧**（+0.0mm）で、**胸トラップ 90% / 足上げ 100% のめり込み**が症状の実体
- **`popoutRangeMeters` を上げるとめり込み率が 52% → 27%(0.50) → 12%(0.70) に改善する。** まず実機で 0.50 と 0.70 を確認する価値がある
- affine 係数の較正は原理的に効果ゼロ。`anchor_z` 自体の精度は別途 D-004 で bundle 側に確認中

### 調査ログ 2026-08-20 追記: めり込みの実体は「マージン不足」

#### 元動画との照合で分かったこと

`source/pre_removal_stereo_video.mp4` の frame 90 に `meta.bin` の bbox を重ねて確認した結果:

- **bundle のデータは正しい。** 人物 bbox・ボール bbox とも被写体を正確に捉えている
- 元動画では人物が腕を胸の前で交差させ、**ボールは顔の前＝腕より手前**にある
- Unity でも**画面上の位置は合っている**（ボールに最も近い keypoint は Neck で 11.9px、球半径 26.5px）

**ずれているのは深度方向だけ。** しかも順序自体は正しい（全編 81% で「球が人より手前」）。

#### 埋もれる本当の理由: 体積を持つモデル vs 1 点の深度

人の配置深度は骨盤 1 点で決まるが、モデルは FK で姿勢を持つため腕や頭が骨盤より手前に出る。現行設定（`screen=1.0` / `popout=0.35`、身長 416mm）での f90:

| | 骨盤からの手前オフセット |
|---|---|
| ボール中心 | **+107.3mm** |
| 体で最も手前の点（右手首） | **+98.3mm** |
| **差** | **わずか 9mm** |

ボールの world 半径は約 24mm なので、**中心が 9mm 手前でも後ろ半分が体に食い込む**。これが「埋もれる」の実体で、順序の誤りではなく**マージン不足**である。

#### `screenDistanceMeters` 拡大が効く理由

`screen=3.0` / `popout=2.10`（身長 1010mm）での f90:

| | 骨盤からの手前オフセット |
|---|---|
| ボール中心 | **+644.0mm** |
| 体で最も手前の点 | **+238.5mm** |
| **差** | **405mm**（球半径 58mm に対し十分） |

深度差はスケールに比例して拡大するが、**体の前後厚は姿勢由来なので同じ比では増えない**。結果としてマージンが稼げる。実画像でも現行ではボール左半分が体に埋まっていたのが、提案設定では体の前に完全に浮いた（`docs/tmp/placement_current.mp4` / `placement_proposed.mp4` の f90）。

#### 効かない区間

f150（胸トラップ 4-6s）は**深度の順序自体が逆転**している（球が人より 55.9mm 奥）。順序が逆なのでスケールを上げても直らず、むしろ差が拡大する（提案設定で 335.4mm 奥）。この区間は D-004 の depth 精度の問題であり、Unity 側では解決できない。

#### 検証環境の注意

- **`TrialScene` にはカメラが 0 台**。XR ランタイムの無いバッチ環境では描画できない。スクリーンショット取得は `TestScene` を使う
- `BatchPlaybackLogger` に `-quit` を付けてはいけない（`EnterPlaymode()` 直後に落ちる）。自身が `EditorApplication.Exit(0)` を呼ぶ
- **2D スクリーンショットでは現行と提案の差はほとんど見えない。** `ResolveFitSize` が視野角を保つため単眼の見かけが変わらず、差は深度方向にしか出ない。判定にはオクルージョン（ボールが体に隠れるか）を見るか、実機の立体視が要る

### 調査ログ 2026-08-20 追記2: `screenDistanceMeters` 拡大案は取り下げ

#### 実画像による判定

`docs/tmp/cmp_f*.png`（上段=元動画 / 中段=現行 / 下段=提案 screen3.0）で目視確認した結果、**現行の方が良い**とユーザーが判定。数値もこれを裏付けた。

**モデルの投影高 ÷ bbox 高**（1.0 が正しいサイズ、`bundle_human.svb`）:

| 設定 | 全編 median | p10..p90 | f60 | f90 | f150 | 1.0 超の割合 |
|---|---|---|---|---|---|---|
| **現行** screen1.0/popout0.35 | 1.069 | 0.977..1.211 | **1.132** | 1.165 | 1.451 | 86% |
| 提案 screen3.0/popout2.10 | 1.073 | **0.856..1.325** | **1.240** | 1.302 | 1.778 | 70% |

**median はほぼ同じだが、提案設定はばらつきが大きく広がる**（p10-p90 の幅 0.234 → 0.469）。深度レンジを広げた分、`anchor_z` の誤差がそのまま投影サイズの誤差として増幅されるため。接触フレーム（f60/f90/f150）ではいずれも現行より大きく写る。

**この案は取り下げる。** めり込みのマージンは稼げるが、モデルが過大に写る副作用の方が目立つ。

#### 残る問題: 現行でもモデルが 13% 大きい

現行設定でも f60 で **1.132**（bbox の 13% 増）、全編で **86% のフレームが 1.0 超**。

原因は、スケールが shot 先頭（f0、立位・`span0`=1.602m）で固定される一方、**投影高が `span(f) × s × f_px / z(f)` で深度 `z(f)` に反比例する**こと。`anchor_z` がフレームごとに揺れると、同じモデルでも投影サイズが変わる。bbox は実写由来の正しいサイズなので、そこからずれる。

- 毎フレームのスケール再補正は破綻することが確認済み（0.3 秒で身長 ×0.56、f180→190）
- 深度を bbox から逆算すれば投影高は必ず bbox に一致するが、keypoints を持たない `other` には適用できず、人と `other` で深度基準が分かれる

#### パースは原因ではない

「提案設定の方が頭身が高く見える」ことについてパース（遠近感）を疑ったが、**前後厚 ÷ 配置深度は現行 20.7% / 提案 20.3% とほぼ同じ**で、パースの強さは変わらない。見え方の差は上記の投影高の差による。

### 調査ログ 2026-08-20 追記3: モデルは bbox に制限されていない（実画像で確認）

#### 確認方法

Unity のスクリーンショット（`TestScene`, 現行設定）に `meta.bin` の bbox を重ねた。Unity スクショ 1280x720 上でスクリーンが占める領域は実測で `x=388, y=234, w=504, h=254` なので、動画座標 `(u,v)` は

```
x = 388 + u * 504/1280
y = 234 + v * 254/640
```

で変換できる。画像は `docs/tmp/bbox_f60.png` ほか（シアン=人物 bbox、マゼンタ=ボール bbox）。

#### 結果: 頭が bbox 上端から大きくはみ出す

- **下端（足）は bbox 下端とほぼ一致**している。⑦ `FitDisplayedModelToBBox` → `AlignProjectedModelBottomToBBox` が効いている
- **上端（頭）は bbox 上端よりはっきり上に出ている**。f60 では bbox 上端が肩の高さにあり、頭がその上にはみ出す

**⑦は「下端の位置合わせ」しかしない（スケールを変えない）実装**なので、モデルが bbox より大きいとき、**超過分がすべて上方向に伸びる**。これが「身長が高く見える」の直接の原因。

#### 構造的な理由

投影高は次の式で決まる。

```
投影高(f) = span(f) × s × f_px / z(f)
```

- `s` は shot 先頭で固定（案 A: `RefineLockedScaleFromProjectedBones`）
- `span(f)` は姿勢で変わる
- **`z(f)` は `anchor_z` 由来でフレームごとに揺れる**

bbox は実写由来の正しいサイズなので、`z(f)` が揺れると投影高が bbox からずれる。現行設定では **全編の 86% のフレームで bbox 超過**、median 1.069、f60 で 1.132。

#### 対処の候補と既知の問題

| 手段 | 状態 |
|---|---|
| 毎フレームのスケール再補正 | **破綻確認済み**（0.3 秒で身長 ×0.56、f180→190） |
| 深度を bbox から逆算する | 投影高は必ず bbox に一致するが、keypoints を持たない `other` に適用できず、人と `other` で深度基準が分かれる |
| ⑦ を下端合わせでなく中心合わせにする | はみ出しが上下に分散するだけで、サイズずれ自体は残る |
| `anchor_z` の精度改善 | bundle 側（D-004、回答待ち） |

#### 注意: この節の数値は keypoints3d ベース

投影高の数値は `keypoints3d` から計算している。**Unity が実際に姿勢を作るのは SMPL block** なので、両者がずれていればこの数値も実挙動とずれる（[docs/smpl-retargeting.md](smpl-retargeting.md) の 2026-08-20 の節）。ただし**画像で確認した「頭が bbox からはみ出す」という事実そのものは Unity の実レンダリング結果**であり、データ種別に依らない。

### 調査ログ 2026-08-20 追記4: Unity 実測ログで土台を確認、主因は深度ではなく姿勢

#### keypoints3d ベースの計算は実挙動を近似できている

`-diagLogs true` で `logPlacementMeasurement` を有効にし、`[PLACE]` ログの `boneRatio`（モデルの骨投影高 ÷ bbox 高）を実測した。

| f | keypoints3d からの計算値 | **Unity 実測 `boneRatio`** |
|---|---|---|
| 60 | 1.132 | **1.097** |
| 90 | 1.165 | **1.137** |
| 150 | 1.451 | **1.486** |
| 300 | 1.229 | **1.337** |

**差は 2-9%。** SMPL block と keypoints3d を突き合わせる作業は不要と判断してよい。今日の一連の数値検証（[docs/smpl-retargeting.md](smpl-retargeting.md) で懸念した「データ種別の取り違え」）は、結論を覆すほどのずれを含んでいない。

#### 主因は深度ではなく姿勢による bbox の縮小

実測ログから読み取れる決定的な事実:

| f | `boneRatio` | `bboxH` | `depth` |
|---|---|---|---|
| 0 | 0.996 | 441 | 0.798 |
| 60 | 1.097 | 406 | 0.766 |
| 90 | 1.137 | 400 | 0.741 |
| 130 | 1.605 | 274 | 0.767 |
| 170 | **2.002** | **130** | 0.722 |
| 190 | **2.200** | 155 | 0.737 |
| 300 | 1.337 | 237 | 0.837 |

- **`depth` は全編 0.72〜0.90m の範囲しか動いていない**（`popoutRangeMeters = 0.35` に対し実使用は約 0.18m、レンジの半分）
- 一方 **`bboxH` は 441 → 130 と 3.4 倍も変動する**（姿勢が縮こまるため）
- **`boneRatio` は `bboxH` の縮小とほぼ連動して 0.996 → 2.200 まで上がる**

つまり**モデルが過大に写る主因は `anchor_z` の揺れではなく、姿勢で bbox が縮むのにモデルのスケールが shot 先頭で固定されたままであること**。深度の寄与は小さい。

**これは popout レンジや `screenDistanceMeters` をどう振っても解決しない**（追記2 で取り下げた通り）。

#### メッシュ余白の寄与も無視できない

`boneRatio`（骨基準）と `sizeRatio`（メッシュ AABB 基準）には常に差がある。

- f0: `boneRatio` 0.996 / `sizeRatio` 1.094 → **メッシュ余白で 9.4% 増**
- f60: 1.097 / 1.221
- f460: 1.073 / **1.414**

`topDelta` は f0 で −32px、f90 で −92.5px と、**頭が bbox 上端から大きくはみ出す**。⑦が下端合わせしかしないため、超過分がすべて上に出る（追記3 の画像と一致）。

#### 対処の方向

`boneRatio` を 1.0 に保つには、**投影高が bbox に一致するよう深度を決める**しかない。深度を bbox から逆算すれば `boneRatio ≡ 1.0` になり、姿勢がどう変わっても追従する。

- 毎フレームのスケール再補正は破綻確認済み（0.3 秒で身長 ×0.56）だが、**深度側を動かす方法は未検証**
- `other` には keypoints がないため同じ方法が使えず、人と `other` で深度基準が分かれる問題は残る

### 調査ログ 2026-08-20 追記5: bbox 由来の深度が有効（試算）

#### 考え方

投影高は `span(f) × s × f_px / z(f)` なので、**`z` を bbox に合わせて逆算すれば `boneRatio ≡ 1.0` になる**。スケール `s` は shot 先頭で固定したままなので、毎フレームのスケール補正で起きた破綻（0.3 秒で身長 ×0.56）は生じない。

実測ログの `boneRatio` と `depth` から直接逆算できる（投影高 ∝ 1/z なので `z_new = depth × boneRatio`）。

#### 試算結果（`bundle_human.svb`、実測ログ 46 フレーム）

`MinDistanceFromHeadMeters = 0.25` と `zPlacement ≤ screenDist - 0.0001` の clamp を含めた**実効 `boneRatio`**:

| 設定 | median | p10..p90 | max | 1.3 超 | 球が人より手前 |
|---|---|---|---|---|---|
| **現行** | 1.196 | 1.060..1.605 | 2.200 | **35%** | **78%** |
| **bbox 由来 screen=1.0 / k=1.0** | **1.000** | 1.000..1.262 | 1.622 | **7%** | **100%** |
| bbox 由来 screen=1.0 / k=0.9 | 1.000 | 1.000..1.136 | 1.459 | 4% | 89% |
| bbox 由来 screen=1.5 / k=1.3 | 1.000 | 1.000..1.094 | 1.405 | 2% | 87% |
| bbox 由来 screen=2.0 / k=1.6 | 1.000 | 1.000..1.009 | 1.297 | 0% | 65% |

**`screen=1.0` / `k=1.0`（screen は現行のまま、人の深度だけ bbox 由来に変える）が最良。** サイズずれ（1.3 超）が 35% → 7%、前後関係が 78% → 100% と両方改善する。

#### 深度の振れ幅と clamp

- 新しい深度の範囲は **0.750〜1.621m**（現行は 0.720〜0.898m、幅 0.178m）
- `screen=1.0` では **41% のフレームがスクリーンを越え、clamp される**
- ただし clamp されたフレームでも実効 `boneRatio` は最大 1.622 で、**現行の 2.200 より良い**

#### `screenDistanceMeters` を上げても解決しない

`s` は shot 先頭の `bboxWorldH = (2*bboxH/eye_h)*(z0/fy)` から決まり **`z0` すなわち screen に比例する**ため、`z_new` も screen に比例する。**比が不変なので clamp の割合は変わらない。** 代わりにスケール係数 `k` を下げると clamp は消えるが、人だけが手前に寄ってボールとの前後関係が崩壊する（`k=0.6` で球が手前 9%）。

#### 残る制約

- **`other` には keypoints がないため同じ逆算ができない。** ボールの深度は `anchor_z` 由来のまま
- したがって**人と `other` で深度基準が分かれる**。上表の「球が人より手前 100%」はこの非対称を含んだ結果であり、`bundle_train.svb` のような `other` のみの bundle には何の効果もない
- `[PLACE]` ログは 10 フレームおき・f460 までの 46 サンプル。**全編での検証は未実施**

#### 補足: Else は既に bbox に一致している

`[PLACE] f=0 track=1 Other sizeRatio=1.000 topDelta=0.0 bottomDelta=0.0` のとおり、Else は投影が bbox に完全一致している。**サイズずれの問題は Person 固有。**

#### 全編（2156f）での再試算 — 追記5 の 46 サンプルを訂正

`-diagEveryN 1` で全フレームの `[PLACE]` を取得し直した。**追記5 の 46 サンプル（f0-460）は悪い区間に偏っており、現行の性能を過小評価していた。**

| 設定 | median | max | 1.3 超 | 球が人より手前 | clamp |
|---|---|---|---|---|---|
| **現行** | 1.082 | 2.269 | **9%** | **79%** | – |
| bbox 由来 `k`=1.0 | 1.000 | 1.681 | **2%** | 90% | 14% |
| **bbox 由来 `k`=1.1** | 1.000 | 1.850 | **3%** | **97%** | 28% |
| bbox 由来 `k`=1.2 | 1.040 | 2.018 | 8% | 100% | 72% |
| bbox 由来 `k`=1.3 | 1.127 | 2.186 | 14% | 100% | 95% |

（現行の全編値: `boneRatio` median 1.082 / p10-p90 0.985..1.279 / max 2.269、深度 0.636〜1.014m）

**`screen=1.0` / `k=1.1` を採用する。** サイズずれ 9% → 3%、前後関係 79% → 97%、最大 `boneRatio` も 2.269 → 1.850 と全項目で改善する。`k` をさらに上げると前後関係は 100% になるが clamp が急増してサイズずれが悪化する。

`screen` を上げると clamp は減るが、ボールの深度が `screen` に比例して奥に動くため前後関係が崩壊する（`screen=2.0` / `k`=1.0 で球が手前 0%）。**`screen` は現行の 1.0 のままにすること。**

### 実装 2026-08-20: ⑧ `RefineDepthFromProjectedBones`（投影高から深度を逆算）

#### 何をするか

ロック済みスケールを固定したまま、**毎フレーム「投影された骨格の高さが bbox 高に一致する」深度へモデルを動かす**。

投影高は `span(f) × scale × f_px / z(f)` なので、`ratio = 投影高 / bboxH` を求めて `z' = z × ratio × k` とすれば `boneRatio ≡ 1.0` になる。画面上の位置 `(u, v)` を保つため、カメラ空間で z 方向にスケールする。

**スケールは一切変えない**ので、毎フレームのスケール補正で起きた破綻（0.3 秒で身長 ×0.56）は生じない。

#### 実装

- `StreamingStereoVideoPlayer.Playback.partial.cs`: `RefineDepthFromProjectedBones()` を追加。案 A（`RefineLockedScaleFromProjectedBones`）の直後に呼ぶ。深度が動くと ⑦ の下端合わせが崩れるので、動かした場合だけ `FitDisplayedModelToBBox` を掛け直す
- `StreamingStereoVideoPlayer.Core.cs`: `refineDepthFromProjectedBones`（既定 true）と `projectedDepthScaleK`（既定 1.0）を追加
- `Assets/Editor/BatchPlaybackLogger.cs`: `-popoutRange` `-diagEveryN` `-depthK` `-captureWidth` と範囲キャプチャ（`0-400:2`）を追加
- ガードは案 A と同じ `MinProjectedBoneRatioForScaleRefine` / `Max...` を流用。骨格を持たない Else には適用しない

#### 実測結果（`bundle_human.svb` 全編 2156f、`boneRatio` は 1.0 が理想）

| 設定 | median | p10..p90 | max | 1.3 超 | 1.1 超 | 0.9 未満 | 球が人より手前 |
|---|---|---|---|---|---|---|---|
| **OFF（従来）** | 1.082 | 0.985..1.279 | 2.269 | **8.8%** | **37.8%** | 0.6% | **79.0%** |
| ON `k`=0.95 | 1.052 | 1.042..1.075 | 1.698 | 2.0% | 7.0% | 0.0% | 80.6% |
| **ON `k`=1.00（既定）** | **0.998** | **0.986..1.066** | **1.698** | **2.0%** | **6.8%** | **0.0%** | **87.7%** |
| ON `k`=1.10 | 0.907 | 0.892..1.066 | 1.698 | 2.0% | 6.8% | **18.0%** | 94.4% |

区間別 median（OFF → ON `k`=1.1 で測ったときの値）:

| 区間 | OFF | ON |
|---|---|---|
| 0-3s 立位 | 1.099 | 0.902 |
| **4-6s 胸トラップ** | **1.522** | **1.110** |
| 9-12s 足上げ | 1.218 | 1.006 |
| 13-30s | 1.042 | 0.924 |
| 50-72s | 1.081 | 0.904 |

**`k`=1.0 を既定にした。** サイズずれが最小（median 0.998、0.9 未満が 0%）で、前後関係も 79.0% → 87.7% に改善する。`k` を上げると前後関係はさらに良くなるが、モデルが小さく写るフレームが増える（`k`=1.1 で 18%）。

#### 目視確認

`docs/tmp/fix_f60.png` ほか（上=修正前 / 下=修正後、シアン=人物 bbox、マゼンタ=ボール bbox）:

- 修正前は**頭が bbox 上端を大きく突き抜け、ボールがモデルの体に埋まっていた**
- 修正後は**頭が bbox 内に収まり、ボールが体の前に出ている**

#### 残る制約

- **Else には効果がない。** 骨格が無いため逆算できず、深度は `anchor_z` 由来のまま。人と Else で深度基準が分かれる状態は解消していない
- **`bundle_train.svb` のような Else のみの bundle には一切効かない**
- 深度が 0.584〜1.050m と広がり、一部フレームでスクリーン手前の clamp に当たる（`k`=1.0 で `boneRatio` max 1.698 として残る）
- `bundle_animal.svb` では未検証（Animal にも適用される実装なので要確認）

### 実装 2026-08-20: スクリーンを背景描画にする（モデルがスクリーンに埋もれる問題）

#### 症状

⑧ `RefineDepthFromProjectedBones` を入れた結果、深度の範囲が 0.636〜1.020m → **0.515〜1.048m** に広がり、`screenDistanceMeters = 1.0` を越えるフレームが出た。

| | 実装前 | ⑧ 実装後 |
|---|---|---|
| Person 深度 | 0.636〜1.020m | **0.515〜1.048m** |
| スクリーン面に張り付き | 0.5% | **8.1%** |
| 0.90m 以上（スクリーン近傍） | – | **32.3%** |

スクリーンは `RenderType=Opaque`・既定の Geometry キュー・`ZWrite` ON だったため、**モデルの深度がスクリーンより奥になったフレームでモデルがスクリーンに隠れて消えていた**。f315（深度 1.048m）では逆立ちしたモデルの上半身が丸ごと見えなくなっていた。

#### 対処

**スクリーンは「動画という背景」であって遮蔽物ではない。** Background キューで先に描き、深度を書かないようにした。

- `Assets/Shaders/PerEyeStereoVideoURP.shader`: `Tags` に `"RenderType"="Background"` `"Queue"="Background"` を追加、Pass に `ZWrite Off` `ZTest Always` を追加
- `StreamingStereoVideoPlayer.Screens.cs`: `ForceBackgroundDrawOrder()` を追加し、`SetupScreensAndMaterials()` から左右のマテリアルに適用。フォールバックシェーダー（URP/Unlit 等）が使われた場合の保険

これでモデルは深度に関わらず常にスクリーンの手前に描画される。動画は平面の背景なので、これが正しい見え方になる。

#### 確認

`docs/tmp/zfix_f315.png`（上=修正前 / 下=修正後、深度 1.048m のフレーム）:

- 修正前は逆立ちしたモデルの**上半身が床に埋もれて消えていた**
- 修正後は**腕・肩・シャツがはっきり見える**

#### 副次的な効果（未実施）

スクリーンに隠れなくなったので、`RefineDepthFromProjectedBones` の**上限 clamp（`screenDist - 0.0001`）を緩める余地ができた**。現在 8.1% のフレームがこの clamp に当たっており、そこでは `boneRatio` が 1.0 に到達できていない（max 1.698）。clamp を緩めればさらに改善するはずだが、モデルがスクリーンより奥に置かれることになるので、実機での見え方を確認してから判断する。

### 実機確認（2026-08-20）

ユーザーが Quest 実機で `bundle_human.svb` を再生し、**Human について大幅な改善を確認**。⑧ とスクリーン背景描画をこの構成で確定とする。

**この時点で残っている課題（次回以降）:**

1. **`bundle_animal.svb` が未検証。** ⑧ は Animal にも適用される実装なので、効果と副作用の確認が要る
2. **Else には ⑧ が効かない。** 骨格が無いため逆算できず、深度は `anchor_z` 由来のまま。人と Else で深度基準が分かれる状態は解消していない。**`bundle_train.svb`（Else のみ）には一切効果がない**
3. **上限 clamp の緩和が未実施。** スクリーンに隠れなくなったので `zPlacement ≤ screenDist - 0.0001` を緩められる。現在 8.1% のフレームがこの clamp に当たり `boneRatio` が 1.0 に届いていない（max 1.698）。立体視での見え方を確認してから判断する
4. **D-004 が回答待ち。** `anchor_z` が実距離をほとんど再現していない件（決定係数 human 0.143 / animal 0.110-0.330 / train 0.001-0.797）は bundle 側に起票済み


### 副作用 2026-08-20: ⑧ で深度が前後に揺れる

実機で「その場で止まっているはずの Human モデルが前後に大きく動く」と報告があり、実測で確認した。

**⑧ は bbox から深度を逆算するため、bbox のフレーム間ノイズと姿勢変動がそのまま深度の揺れになる。**

| | ⑧ OFF | ⑧ ON（k=1.0） |
|---|---|---|
| 1 フレーム間の深度変化 median | 1.0mm | **3.0mm** |
| 同 p90 | 5.0mm | **20.0mm** |
| 同 max | 22.0mm | **420.0mm** |
| 1 秒間の変化 median | 21.0mm | 34.0mm |
| 同 p90 | 75.0mm | **132.0mm** |

止まっている区間だけを見るとさらに明確:

| 区間 | ⑧ OFF の深度幅 | ⑧ ON の深度幅 |
|---|---|---|
| 0-3s 立位 | 85.0mm | **341.0mm** |
| 13-30s | 293.0mm | **484.0mm** |

**1 フレームで最大 420mm 動く**（配置後の身長が約 420mm なので、身長分を 1/30 秒で移動する計算）。サイズは合うようになったが、位置が暴れる状態。

⑧ 導入時に「スケールを動かさないので毎フレーム補正の破綻は起きない」と書いたが、**破綻の形がサイズから位置に移っただけだった**。次はこの揺れの抑制が課題。

#### 対処: 補正比率を時間平滑化する（実装済み）

深度そのものではなく**補正比率 `ratio`（= 投影高 / bboxH）を平滑化する**。深度には人の実際の移動も含まれるが、`ratio` は純粋な補正量なので、平滑化しても移動は保たれる。

`ratio` 自体のフレーム間変化は median 0.0050 / p90 0.0410 / **max 0.6180** で、外れ値が深度の跳ねを生んでいた。

`StreamingStereoVideoPlayer.Playback.partial.cs` の `SmoothProjectedDepthRatio()` で指数平滑化する。係数は時定数から毎フレーム求める（`alpha = 1 - exp(-dt/tau)`）のでフレームレートに依存しない。shot 境界では `smoothedProjectedDepthRatioByTrack` をクリアして前 shot の値を引きずらせない。

**全編実測（`bundle_human.svb` 2156f）:**

| 設定 | `boneRatio` median | max | 1.3 超 | 深度 1f 変化 median | p90 | max | 0-3s の深度幅 | 13-30s | 球が手前 |
|---|---|---|---|---|---|---|---|---|---|
| **⑧ OFF** | 1.082 | 2.269 | 8.8% | 1.0mm | **5.0mm** | **22.0mm** | **85.0mm** | 293.0mm | 79.0% |
| ⑧ ON 平滑化なし | 0.998 | 1.698 | 2.0% | 3.0mm | 20.0mm | **420.0mm** | 341.0mm | 484.0mm | 87.7% |
| ⑧ ON `tau`=0.65s | 0.998 | 1.698 | 2.1% | 2.0mm | 6.0mm | 64.0mm | 165.0mm | 245.0mm | 89.7% |
| **⑧ ON `tau`=1.2s（既定）** | **0.997** | **1.698** | **2.1%** | 2.0mm | **5.0mm** | **22.0mm** | 148.0mm | **245.0mm** | **91.8%** |
| ⑧ ON `tau`=2.0s | 0.994 | 1.698 | 2.5% | 2.0mm | 5.0mm | 22.0mm | 125.0mm | 240.0mm | 92.8% |

**`tau`=1.2s でフレーム間の揺れが ⑧ OFF と完全に同等（p90 5.0mm / max 22.0mm）に戻り、サイズ精度（median 0.997、1.3 超 2.1%）と前後関係（91.8%）は改善したまま。** 13-30s の深度幅は ⑧ OFF より小さい。

平滑化を強めても `boneRatio` がほとんど悪化しないのは、**外れ値の `ratio` が均されて `max` が抑えられる効果と相殺する**ため。サイズ精度とのトレードオフは実質無い。

0-3s の深度幅は 148mm と ⑧ OFF（85mm）より大きいままだが、これは低周波のゆっくりした動きであり、フレーム間の跳ねは解消している。
