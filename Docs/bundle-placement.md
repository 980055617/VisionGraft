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
| ⑧ | `RefineDepthFromProjectedBones` | Human/Animal を宣言（**実際は Human のみ動作**） | 投影骨高が bbox 高に一致する深度へ動かす。スケールは変えない |
| ⑨ | `ApplyOtherDepthFollowForFrame` | **Else のみ** | 骨格 track の深度から `meta.bin` の深度差を引いて Else を置く。深度に応じてスケールも合わせる |
| ⑩ | `ApplyOtherPenetrationResolveForFrame` | **Else のみ** | 骨格の内部に食い込んだ Else を表面へ押し出す（**既定 OFF**、2026-08-21 に効果なしと判定） |

**押さえるべき性質:**

- ~~**深度（Z）は ① で決まり、以降変わらない。**~~ **⑧ の追加でこれは成り立たなくなった**（2026-08-20）。⑧ が深度を動かし、⑨ が Else の深度を骨格に追従させる。⑦ は依然として camera 空間の Y 成分にしかオフセットを加えない
- **⑧ は Animal では動作しない。** カテゴリ条件は Human/Animal を通すが、内部で呼ぶ `TryProjectBonesToEyeHeight` が `animator.isHuman` を要求するため常に false で抜ける（2026-08-26 実測、`[DEPTH8]` が animal で 0 件）
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

### 副作用 2026-08-20 その2: ⑧ が「ボールが背中にある」場面で前後関係を壊す

実機で「40-50 秒の前傾姿勢でボールは背中にあるはずなのに人より前にある」と報告があり、確認した。

**該当区間は f1250-1270（41.7-42.3 秒）。** 元動画（`real_f1260.png`）では人が四つん這いに近い深い前傾で、**ボールは首の後ろ＝背中側に乗っている**。人物 bbox・ボール bbox とも被写体を正しく捉えている。

#### bundle のデータは正しい

`meta.bin` の `z01`（larger=farther）:

| f | 人 `z01` | 球 `z01` | bundle の意図 |
|---|---|---|---|
| 1257 | 0.204 | 0.204 | ほぼ同じ |
| 1259 | 0.192 | 0.196 | **球が奥** |
| 1260 | 0.186 | 0.194 | **球が奥** |
| 1261 | 0.180 | 0.188 | **球が奥** |

**bundle は正しく「球が奥」と言っている。** ただし差は 0.002-0.008 と極めて小さい。

#### ⑧ が反転させている

| f | ⑧ OFF 人 / 球 | 判定 | ⑧ ON 人 / 球 | 判定 |
|---|---|---|---|---|
| 1255 | 0.6410 / 0.6590 | 球が奥 ✅ | 0.6690 / 0.6590 | **球が手前 ❌** |
| 1258 | 0.6380 / 0.6500 | 球が奥 ✅ | 0.6580 / 0.6500 | **球が手前 ❌** |
| 1260 | 0.6360 / 0.6430 | 球が奥 ✅ | 0.6510 / 0.6430 | **球が手前 ❌** |

**この区間で「球が手前」: ⑧ OFF 43% → ⑧ ON 100%。**

⑧ は人の深度を bbox から決めるが、**球の深度は `anchor_z` のまま**なので、人が動いた分だけ相対関係が bundle の意図から外れる。f1260 では人が 0.6360 → 0.6510 と奥へ動いた結果、球が相対的に手前になった。

#### これは実装時に記録した「残る制約」が顕在化したもの

⑧ の実装時に「`other` には骨格が無いため適用されず、人と Else で深度基準が分かれる」と記録した通りの問題。全編では「球が手前」が 79.0% → 91.8% と「改善」しているが、**この指標自体が「球は常に人より手前」という誤った前提に立っていた**。ボールが背中・頭上にある場面では球が奥であるべきで、一律に手前へ寄せるのは誤り。

40-50s 区間全体では ⑧ OFF で 90.0%、⑧ ON で 100.0% が「球が手前」。人−球の深度差 median は +118.0mm → +165.0mm と広がっている。

#### 対処: 前後関係の符号を保存するクランプ（実装済み）

**⑧ の補正後の深度が、同じフレームの Else との前後関係（`meta.bin` の `anchor_z` が示す順序）を反転させないよう制限する。**

`MetaObj.anchorZ` はデコード済みの深度なので、その大小がそのまま bundle の示す正しい前後関係になる。補正後がその順序を跨ぐなら、相手の手前／奥 ε（既定 15mm）に留める。

- `StreamingStereoVideoPlayer.Playback.partial.cs`: `ClampDepthPreservingOtherOrder()` を追加し、`RefineDepthFromProjectedBones` の `targetZ` に適用
- `StreamingStereoVideoPlayer.Core.cs`: `projectedDepthOrderEpsilonMeters`（既定 0.015）を追加
- 骨格を持つ track（Person / Animal）は相手も ⑧ で動くので基準にせず、**Else だけを見る**
- Else が複数あって制約が矛盾する場合（下限 > 上限）は、どれかを必ず壊すことになるので何もしない

**試算（`bundle_human.svb` 全編 2156f、bundle の前後関係との一致率）:**

| 設定 | 前後一致 | 前傾区間 f1250-1270 | `boneRatio` median | p90 | 1.3 超 |
|---|---|---|---|---|---|
| ⑧ OFF | 91.3% | 47.6% | 0.924 | 1.013 | 0.2% |
| ⑧ ON（クランプ無） | 86.0% | 71.4% | 0.997 | 1.095 | 2.1% |
| **⑧ ON + クランプ** | **100.0%** | **100.0%** | **0.993** | **1.083** | **1.1%** |

**前後関係が完全一致になり、サイズ精度もほぼ維持される**（`boneRatio` median 0.997 → 0.993、1.3 超はむしろ 2.1% → 1.1% に改善）。ε は 5mm でも 30mm でも一致率 100%、サイズ精度もほとんど変わらない。

#### 棄却した案: 人の移動量だけ Else もシフトする

人の深度が動いた分だけ Else も同じ量動かせば相対関係は保たれるが、**前傾区間の一致率は 47.6% と ⑧ OFF から改善せず**（元々そこがずれているため）、かつ **Else の投影サイズが崩れる**（`sizeRatio` median 1.000 → 0.919、p10 0.824）。Else は現状 `sizeRatio` = 1.000 と完璧なので、これを崩す価値はない。

#### 指標の訂正

⑧ 実装時に「球が人より手前 79.0% → 91.8%」を改善として報告したが、**この指標は「球は常に人より手前」という誤った前提**に立っていた。ボールが背中・頭上にある場面では球が奥であるべきで、一律に手前へ寄せるのは誤り。正しい指標は **bundle の `anchor_z` が示す前後関係との一致率**（bundle 全体で「球が奥」は 17.7% のフレーム）。

#### 実測: 順序クランプは前後関係を直すが深度が跳ねる（2026-08-20）

`-depthEps` / `-depthOff` を追加し、`[DEPTH8]` ログ（⑧ の各段階: 補正前 → 比率補正 → 順序クランプ → screen クランプ）で全編 4 パターンを測った。

| 設定 | 前後一致 | 前傾 f1250-70 | `boneRatio` median | p90 | max | 1.3 超 | 深度 1f 変化 p90 | max |
|---|---|---|---|---|---|---|---|---|
| ⑧ OFF | 91.3% | 47.6% | 1.082 | 1.279 | 2.269 | 8.8% | 5.0mm | **22.0mm** |
| ⑧ ON eps=0 | 86.0% | 71.4% | 0.997 | 1.096 | 1.698 | 2.1% | 5.0mm | **22.0mm** |
| ⑧ ON eps=5mm | 92.3% | 95.2% | 1.002 | 1.112 | 1.882 | 4.1% | 6.0mm | **196.0mm** |
| ⑧ ON eps=15mm | **94.9%** | **95.2%** | 1.002 | 1.114 | 1.902 | 4.1% | 6.0mm | **206.0mm** |

**クランプの発動状況:**

| eps | 発動率 | 移動量 median | max |
|---|---|---|---|
| 5mm | 15.8% | **122.4mm** | 551.1mm |
| 15mm | 17.8% | **135.0mm** | 561.1mm |

**前後関係は確かに直る**（⑧ OFF の 91.3% すら上回る 94.9%、前傾区間は 47.6% → 95.2%）。しかし **17.8% のフレームでクランプが発動し、そのたびに 135mm（配置後の身長の約 1/3）を一気に動かす**ため、深度の跳ねが max 22mm → 206mm に悪化した。これが実機で「悪化した」と感じられた原因。

**根本的には、⑧ が `boneRatio` を 1.0 にするために必要な深度移動が、bundle の前後関係と頻繁に矛盾している**（17.8% のフレーム）。サイズ精度と前後関係を同時に満たす深度が存在しないフレームが 2 割近くある。

#### 試算: 制約を掛ける段階を変える

跳ねの原因は「深度が決まった後に硬いクランプを当てる」ことなので、**`ratio` の段階で前後関係の許容範囲に制限し、その後に平滑化する**案を試算した（`z = before × ratio` なので、`ratio ≤ (z_ball − eps)/before` の形で制約できる）。

| 設定 | 前後一致 | 前傾 f1250-70 | `boneRatio` median | 1.3 超 | 深度 1f p90 | max |
|---|---|---|---|---|---|---|
| クランプ無し | 84.6% | 71.4% | 1.001 | 1.1% | 3.4mm | 78.6mm |
| **現行: 深度にクランプ** | **100.0%** | **100.0%** | 1.006 | 2.8% | 3.9mm | **191.0mm** |
| 新案: `ratio` を制限 eps=15mm | 90.4% | 71.4% | 1.011 | 2.9% | 3.8mm | **78.6mm** |
| 新案: `ratio` を制限 eps=30mm | 92.1% | 71.4% | 1.011 | 3.0% | 3.8mm | 78.6mm |

（この試算は `DT` を 1/30 固定で回しているので絶対値は実測とずれる。相対比較のみ有効）

**`ratio` 段階の制限は跳ねを増やさないが、前後一致は 90.4% に留まり、前傾区間は 71.4% と改善しない。** 平滑化が制約を後から破るため。**硬いクランプでしか前傾区間は直らないが、それは跳ねを生む。**

#### 結論: サイズ精度・前後関係・滑らかさは三者択一

| 案 | 前後一致 | 前傾区間 | `boneRatio` median | 深度 1f max | 実機評価 |
|---|---|---|---|---|---|
| A: ⑧ OFF | 91.3% | 47.6% | 1.082 | 22.0mm | （⑧ 導入前） |
| **B: ⑧ ON クランプ無** | 86.0% | 71.4% | **0.997** | **22.0mm** | **「だいぶ治った」** |
| C: ⑧ ON 深度クランプ | **94.9%** | **95.2%** | 1.002 | **206.0mm** | **「悪化した」** |
| D: ⑧ ON `ratio` 制限 | 90.4% | 71.4% | 1.011 | 22.0mm 相当 | 未評価 |

**⑧ が `boneRatio` を 1.0 にするために必要な深度移動は、17.8% のフレームで bundle の前後関係と矛盾する。** この矛盾を硬く解消すると跳ね、緩く解消すると前後関係が残る。同時に満たす深度が存在しないフレームが約 2 割ある以上、Unity 側の後処理では解決しきれない。

**前傾区間（ボールが背中）の根本解決には、Else 側の深度も骨格と同じ基準で決められること、つまり `anchor_z` の精度改善（D-004）が要る。**

#### 実測: `ratio` 制限案（D）の結果と最終判断

`ClampRatioPreservingOtherOrder()` として実装し直し（深度ではなく `ratio` を制限、平滑化の手前）、全編で測った。

| 設定 | 前後一致 | 前傾 f1250-70 | `boneRatio` median | p90 | max | 1.3 超 | 深度 1f p90 | max |
|---|---|---|---|---|---|---|---|---|
| A: ⑧ OFF | 91.3% | 47.6% | 1.082 | 1.279 | 2.269 | 8.8% | 5.0mm | **22.0mm** |
| B: ⑧ ON 制限なし | 86.0% | 71.4% | **0.997** | 1.096 | 1.698 | **2.1%** | 5.0mm | **22.0mm** |
| C: 深度をクランプ 15mm | **94.9%** | **95.2%** | 1.002 | 1.114 | 1.902 | 4.1% | 6.0mm | **206.0mm** |
| D: `ratio` 制限 15mm | 90.2% | 71.4% | 1.003 | 1.108 | 1.940 | 3.3% | 6.0mm | **22.0mm** |
| **D: `ratio` 制限 40mm（既定）** | **91.2%** | 71.4% | **1.004** | 1.112 | 1.985 | 3.5% | 6.0mm | **22.0mm** |

**D（40mm）を既定にした。** B の上位互換で、前後一致が 86.0% → 91.2% と ⑧ OFF 並みに戻り、跳ねは生じない（1f max 22.0mm）。サイズ精度も median 1.004 と維持。

**ただし前傾区間（ボールが背中）は 71.4% のままで改善しない。** 後段の平滑化が制約を後から破るため。硬いクランプ（C）なら 95.2% まで直るが、17.8% のフレームで一気に 135mm 動いて跳ね、実機で「悪化」と判定された。

**跳ねずに前傾区間を直す方法は Unity 側には無い。** ⑧ が `boneRatio` を 1.0 にするために必要な深度移動が bundle の前後関係と矛盾するフレームが約 2 割あり、サイズ精度・前後関係・滑らかさの三つを同時に満たす深度が存在しない。**根本解決には Else 側の深度も骨格と同じ基準で決められること、つまり `anchor_z` の精度改善（D-004）が要る。**

#### 実測: ⑧ が人と Else の深度差を 3〜5 倍に広げる（2026-08-20）

実機で「足上げの場面でボールがめっちゃ前」と報告があり、測定した。

**人 − 球の深度差（+ = 球が手前）:**

| 設定 | 全編 median | 足上げ f270-360 | 胸 f110-200 |
|---|---|---|---|
| **bundle の意図** | **81.8mm** | **71.4mm** | **−59.5mm** |
| A: ⑧ OFF | 65.0mm | 66.0mm | −40.0mm |
| **D: 現在（ratio 40mm）** | **123.0mm** | **237.0mm** | **+31.0mm** |

足上げ区間の内訳:

| f | bundle の意図 | 現在 | 倍率 |
|---|---|---|---|
| 280 | +67.7mm | +243.0mm | **3.59x** |
| 290 | +46.3mm | +232.0mm | **5.01x** |
| 300 | +52.5mm | +241.0mm | **4.59x** |

**⑧ は人だけを動かすので、Else との差が bundle の意図から 3〜5 倍に開く。** 胸区間では符号まで反転している（意図 −59.5mm ＝ 球が奥、現在 +31.0mm ＝ 球が手前）。

#### 試算: 球を「bundle の意図する差」に追従させる

球の深度を `人の実配置深度 − bundle が意図する差` に置き直す。

| 案 | 差 median | 足上げ | 胸 | 前後一致 | 球サイズ比 median | p10 |
|---|---|---|---|---|---|---|
| bundle の意図 | 81.8mm | 71.4mm | −59.5mm | 100.0% | 1.000 | 1.000 |
| 現在 | 123.0mm | 237.0mm | +31.0mm | 91.6% | 1.000 | 1.000 |
| **球を差に追従** | **81.8mm** | **71.4mm** | **−59.5mm** | **99.8%** | **0.953** | **0.863** |
| 追従を半分だけ | 108.9mm | 149.8mm | −13.6mm | 94.8% | 0.976 | 0.926 |

**完全追従で深度差が bundle の意図と一致し、前後一致も 99.8% になる。** 代償は球の投影サイズが median 4.7%・p10 で 13.7% 小さくなること。

**以前「球を人の移動量だけシフトする」案を棄却したが、あれは移動量を一律に足す形だった。** 今回は bundle が意図する「差」そのものを再現するので、前傾区間を含めて正しくなる。棄却の判断を訂正する。

#### 実測: 胸トラップ区間では bundle の意図どおりに置くと埋もれる（2026-08-20）

⑨ で Else を「bundle が意図する深度差」に追従させた結果、4-8 秒（f120-240）で埋もれが再発した。原因を測った。

**人の配置深度は骨盤（`anchor_z` の基準点）だが、球が接触するのは胸・首。前傾時は胸が骨盤より奥にある**ため、この 2 つを混同すると判定を誤る。keypoints3d で「球に画面上で最も近い部位」の骨盤基準 z を出し、bundle が意図する球-骨盤の深度差と比較した（+ が奥、配置スケール換算）。

| f | 最近接部位 | その部位の骨盤比 z | bundle の球−骨盤 | 判定 |
|---|---|---|---|---|
| 140 | idx18 | +34.0mm | **+51.7mm** | 球が部位より奥 → 埋もれる |
| 150 | idx18 | +37.3mm | **+55.9mm** | 埋もれる |
| 170 | idx18 | +21.5mm | **+90.4mm** | 埋もれる |
| 180 | idx40 | +40.0mm | **+102.1mm** | 埋もれる |
| 200 | Neck2 | +47.7mm | **+62.8mm** | 埋もれる |

**13 フレーム中 7 フレームで、bundle の意図する球の深度が接触部位より奥にある。** median で見ると接触部位が骨盤比 +28.5mm、bundle の球が +51.7mm で、**球が接触部位より 23.2mm 奥**。球の world 半径が約 24mm なので、**球がほぼ完全に体に埋まる**計算になる。

**bundle の `anchor_z` はこの区間で球を奥に置きすぎている。** D-003 で bundle 側は「深い前傾では骨盤が手前・胸が奥なので depth は正しい」と回答したが、**骨盤との比較では正しくても、実際の接触部位との関係では奥すぎる**。

⑨ が「骨盤との差」を忠実に再現する設計である以上、この区間の埋もれは ⑨ 単体では直せない。**基準を骨盤ではなく接触部位に変えるか、`anchor_z` の精度改善（D-004）が要る。**

#### 埋もれの正体: 球が小さく写るフレームで `anchor_z` が奥に寄る（2026-08-20）

「球の `anchor_z`（bundle）」と「球に画面上で最も近い体の部位の深度（keypoints3d 由来）」を全編で比較した（+ = 球が部位より奥＝埋もれ側）。

**全編 2156f: median −65.7mm、p10-p90 −155.0..+48.1mm、埋もれ側 22.6%。** 大半のフレームは正常で、問題は特定条件に集中している。

**球の見かけサイズとの相関が決定的:**

| 球の bbox 径 | フレーム数 | 誤差 median | 埋もれ率 |
|---|---|---|---|
| **25-35px** | 70 | **+61.8mm** | **87.1%** |
| 35-50px | 875 | −38.7mm | 35.8% |
| 50px 以上 | 1205 | −82.8mm | **9.4%** |

**球が人の bbox の内側にあるかでも差が出る:**

| | フレーム数 | 誤差 median | 埋もれ率 |
|---|---|---|---|
| 球が人 bbox の内側 | 1072 | −50.8mm | **29.0%** |
| 球が人 bbox からはみ出す | 1084 | −68.8mm | 16.2% |

**つまり「球が小さく写り、かつ体に重なっている」フレームで、depth map が球の深度を周囲（体・背景）に引きずられて奥と推定している。** 球が大きく写れば誤差は消える（50px 以上で埋もれ率 9.4%）。4-8 秒の胸トラップは、球が体に密着して小さく写る条件が揃っている。

#### 試算: 球の深度差を時間平滑化しても解決しない

球は物理的に連続運動するので急な深度変化はノイズのはず、と考えて「球 − 人の深度差」を時間平滑化した。

| 時定数 | 全編 埋もれ率 | 4-8s f120-240 | 球が小さいフレームの埋もれ率 / median |
|---|---|---|---|
| なし | 22.6% | 63.6% | 87.1% / +61.8mm |
| 0.30s | 22.2% | 60.3% | 81.4% / +36.4mm |
| 1.00s | **18.3%** | **49.6%** | **78.6% / +15.9mm** |

**median は +61.8mm → +15.9mm と大きく改善するが、埋もれ率は 78.6% のまま。** 球が小さく写る期間が続く間ずっと誤差が乗るので、前後の値を混ぜる平滑化では系統的なバイアスを取れない。

#### 接触補正は採らない

「球を最寄りの部位の手前に置く」補正は埋もれを構造的に解消するが、**蹴り上げて球が空中にある場面でも近くの足に吸着してしまう**（ユーザー指摘、2026-08-20）。球が体から離れている場面が多いこの素材では致命的なので、この方向は採らない。

**結論: この区間の埋もれは `anchor_z` の精度そのものが原因で、Unity 側の後処理では解決できない。D-004 の回答を待つ。**

#### 訂正: D-005 は独立した問題ではなく D-004 の帰結（2026-08-20）

bundle 側から「マスク制限サンプリングを実装したが症状区間で悪化した（埋もれ率 63.6% → 69.4%）、固定窓とマスク全体の depth 値は 0.002 しか違わず汚染ではない」と回答があり、**`validCount` 固定 49 を原因とした当方の切り分けは誤りだったと判明した。**

原因を追ったところ、**埋もれ量の指標が測っているものを取り違えていた。**

**検証:** keypoints3d から逆算した実距離と、`anchor_z` 由来の配置深度の関係を全編で測った。

```
実距離レンジ        : 2.607〜5.641m（幅 3.034m）
配置深度レンジ      : 0.630〜0.980m（幅 0.350m）
実身長 1.7026m → 配置身長 median 0.333m（モデルスケール 0.1953）

d(配置深度)/d(実距離) : median 0.0000  p25 −0.0420  p75 +0.0757
```

**実距離が変わっても配置深度がほとんど動かず、符号すらばらつく。** 一方、接触部位の深度は keypoints3d の実寸をモデルスケール 0.1953 倍したもので、実距離空間に忠実。

**この 2 つを引き算している以上、「埋もれ量」は「Unity 内で球がモデルに埋もれるか」は正しく測るが、「`anchor_z` の抽出精度」は測っていない。**

- `anchor_z` の抽出をどう改善しても、`anchor_z` 自体が実距離を反映しない限り Unity 内の整合性は改善しない
- bundle 側でマスク制限が効かなかったのは当然の結果
- 「球が小さいほど埋もれる」相関も、抽出窓ではなく **DepthCrafter の距離依存の精度低下**（遠い＝小さく写る）で説明がつく

**bundle 側の作業仮説「D-004 の person R²=0.143 と根が同じ」が正しい。** D-005 を独立した issue として追うのは筋が悪く、D-004 に集約すべき。

**なお `anchor_quality_check.py` の指標自体は有効。** 「Unity 内で対象物がモデルに埋もれるか」を測るものとして使える。ただし**その値の悪化・改善を `anchor_z` の抽出品質の指標として読んではいけない。** スクリプトの docstring にこの但し書きを追記した。

#### 残差の空間依存を疑ったが支持されなかった（2026-08-20）

bundle 側の「残差はなめらかなドリフト、person/ball 相関 +0.192 と低い」を受け、**物体ごとに独立した誤差なら depth map の空間依存では**と考えて検証した。

**person 単体では強い相関が出る:**

```
corr(残差, 被写体の画面中心 x) = +0.405   ← 最も強い
corr(残差, bbox 高さ)         = +0.227
corr(残差, 実距離)            = +0.021   ← ほぼゼロ
corr(残差, フレーム番号)       = −0.038   ← 単調な時間ドリフトではない
```

| 画面 x 帯 | n | 残差 median |
|---|---|---|
| 400-550px | 67 | −0.0344 |
| 550-700px | 1803 | +0.0011 |
| 700-850px | 283 | +0.0226 |

**しかし空間依存とは言えない。** 3 つの反証がある。

**反証 1: 同一フレーム内では符号が逆。** 空間勾配なら、同じフレームの 2 物体でも「右にある方が手前」になるはず。

```
person 単体       : corr(残差, 画面 x)      = +0.405   （右ほど手前）
同一フレーム内の差 : corr(x 差, disparity 差) = −0.181   （右ほど奥）
```

**反証 2: 帯別が単調でない。** 空間勾配なら単調になるはずだが、x 差 −100〜−40px で +0.038、0〜40px で −0.070、100px 以上で −0.013 と山なりになる。

**反証 3: 時間帯別に見ると x と残差が対応しない。**

| 区間 | 画面 x median | 残差 median |
|---|---|---|
| f0-400 | 612.2 | **+0.0208** |
| f400-900 | 584.5 | **−0.0417** |
| f900-1400 | **709.5** | +0.0053 |
| f1400-2200 | 664.5 | +0.0058 |

x が最大の区間（f900-1400）で残差は中間、x が中程度の区間（f0-400）で残差が最大。**単調な対応がない。**

**結論: `corr(残差, x) = +0.405` は、bundle 側が観測した「時間帯ごとのなめらかなドリフト」と、被写体が時間とともに画面内を移動することが偶然相関した見かけのもの。** 空間依存の証拠にはならない。bundle 側の「なめらかな残差があるが原因は未特定」という位置づけを追認する。

**bbox 高さを揃えて層別しても x の効果は残る**（350-450px 帯で +0.484）ので、交絡だけでも説明しきれていない。ただし上記 3 つの反証がある以上、空間勾配として扱うのは誤り。

---

## 実装 2026-08-21: ⑩ `ApplyOtherPenetrationResolveForFrame`（効果は限定的・要再検討）

### 何をするか

Else が骨格モデルの内部に食い込んでいるとき、**最小限だけ表面へ押し出す**。接触補正（Else を最寄りの部位へ引き寄せる）とは別物で、**内部にあるときだけ、体から出る方向にのみ動かす**。空中にある Else は動かさない。

発動条件は「画面上で重なっている（Else の投影半径 + `penetrationOverlapMarginPixels`）」かつ「Else の中心がボーンの内部にある」の両方。ボーン位置は `HumanoidRigCache.bones` から取り、太さは実測値（身長比）。

- `StreamingStereoVideoPlayer.Playback.partial.cs`: `ApplyOtherPenetrationResolveForFrame()` / `TryFindNearestBoneToPoint()` / `ResolveBoneThicknessRatio()`
- `StreamingStereoVideoPlayer.Core.cs`: `resolveOtherPenetration`（既定 true）、`penetrationOverlapMarginPixels`（8）、`penetrationFrontBias`（0）、`logPenetrationResolve`
- `BatchPlaybackLogger`: `-penetOff` / `-frontBias`

### 実装中に踏んだバグ

Else の world 半径に余分な `0.5` を掛けていた（`otherR` が 10.7mm と実際の約半分）。修正して 22.4mm になり、発動回数が 9570 → 23324 に増えた。

### 結果: 発動はするが、狙った症状は直っていない

`bundle_human_shots_driftfix_test.svb` 全編で:

- **[PENET] 発動 23324 回**、移動量 median 36.2mm / p90 69.3mm / max 101.1mm、向きは front 18512 / back 4812
- **しかし「見た目で埋もれる」指標は 7.5% → 7.6% とほぼ不変**
- 目視でも f150（胸トラップ）でボールは体に隠れたまま。`penetrationFrontBias` を 1.5 / 3.0 に上げても変化なし

### 未解明の点

**なぜ発動しているのに症状が変わらないのかが分かっていない。** 考えられること:

1. **評価指標と実装で判定部位が違う。** 評価は keypoints3d の主要関節、実装は Humanoid ボーン。別の部位を「最寄り」と判定していれば、押し出し先も違う
2. **押し出し量が足りない。** ボーン半径（実測 18.4mm）は骨の中心から体表面までの距離だが、実際のメッシュ表面はもっと外側にある可能性
3. **f150 では bundle が「球が奥」と言っており**、既定（bundle の前後関係に従う）では奥へ押し出す。奥へ出しても隠れたままなので症状は直らない。`frontBias` はこれを手前へ向けるためのものだが効かなかった

### 実機確認の結果: 既定 OFF にした

**実機で見て「良くない」と判定されたため、`resolveOtherPenetration` の既定を false にした。** 狙った症状（4-8 秒・37 秒の埋もれ）が直らないうえ、23324 フレームで Else が動くことによる見た目の悪化があった。

コードは残してあるので、原因が分かれば Inspector で有効化して再検証できる。

### 再挑戦するなら

**最有力の仮説は「ボーン半径が実際のメッシュ表面より内側」。** 現在の実装は `ResolveBoneThicknessRatio()` の実測値（骨の中心から体表面までの距離、身長比）を使っているが、これは 2026-08-19 に `boneWeights` と骨軸への垂直距離で測った「平均的な太さ」であって、**服やメッシュの膨らみを含む実形状ではない**。押し出しても表面に届いていない可能性がある。

やるなら `SkinnedMeshRenderer` の実形状（`BakeMesh` した頂点、または部位ごとの実 bounds）を見る必要がある。ただし毎フレームのメッシュベイクはコストが高いので、実装前に負荷を見積もること。

**そもそもこの補正は症状を直接消す対症療法**であり、根本原因（DepthCrafter の `a` の時間変動、D-004）が bundle 側で直れば不要になる。優先度は D-004 の進捗次第。

---

## 2026-08-21: `a(t)` を利用側で解けないか（試算）

### 着想

bundle 側の背景ドリフト補正で `b` が除去されたなら、**`a(t)` は person から解ける**。person は `keypoints3d` を持ち実距離が逆算できるので:

```
a(t) = (disp_person − b) × Z_person       ← person 1 点で a が求まる
Z_ball = a(t) / (disp_ball − b)           ← Else の実距離が出る
```

bundle 側は「`a` を解くには深さの違う 2 点以上の参照が必要」としていたが、**利用側には `keypoints3d` という「距離が既知の参照点」がある**ため、1 点でも解ける。

### 前提の確認

`bundle_human_shots_driftfix_test.svb` で `b` がどれだけ一定になったかを測った（10 秒窓ごとに再フィット）。

| | 全編の `b` | 10 秒窓ごとの `b` のレンジ | 幅 |
|---|---|---|---|
| 補正前 | 0.5121 | 0.2210..1.1588 | 0.9378 |
| **b 補正版** | 0.3784 | 0.2152..0.9816 | **0.7664** |

**`b` の変動は残っている**（幅 0.7664）。それでも `a(t)` の推定は 1 フレームも破綻せず（負値・20m 超がゼロ）、推定した Else の実距離は median 3.11m（person は 3.69m）と妥当な値になった。

### 試算結果

推定した実距離を person と同じ写像（全編フィットの `A`, `B`）で disparity に戻し、通常の popout 変換に通した場合:

| | 全編 埋もれ率 | **4-8s（胸トラップ）** | 36-39s（膝止め） |
|---|---|---|---|
| 現行（bundle の disparity をそのまま） | 28.0% | **48.8%** | **1.2%** |
| **`a(t)` 推定で Else の距離を作り直す** | **20.6%** | **23.1%** | **12.3%** |

**主症状の 4-8 秒は半分以下になるが、37 秒は 1.2% → 12.3% と悪化する。** 全編では 28.0% → 20.6% の改善。

### 評価

- **`a` の補正を bundle 側に待たずとも、利用側で部分的に実現できる**ことが分かった
- ただし**万能ではない**。区間によって悪化する
- `b` の変動が残っているのが精度の頭打ち要因の可能性。bundle 側が `b` をさらに詰めれば、この手法の精度も上がるはず
- **制約は ⑧⑨ と同じ**で、`keypoints3d` を持つ track が同一フレームに要る。`bundle_train.svb`（Else のみ）には使えない

### 未実施

実装していない。⑨ の変形（Else の深度を「bundle の差」ではなく「`a(t)` 推定による実距離」から決める）として入れられるが、**37 秒の悪化をどう扱うかを決めてから**にする。

### 36-39 秒の悪化を切り分けた: 原因は `A` で disparity に戻す工程だった

bundle 側から「1 箇所を直したら別の場所が壊れる構造は D-003 と同型、person 1 点が体勢によっては代表点として不適切になる限界では」と指摘を受け、切り分けた。

**`a(t)` の推定自体は健全だった。** 36-39 秒区間で median 1.175 / sd 0.080 と安定しており、他区間と比べても外れていない（立位 1.024、胸トラ 1.241、足上げ 0.927）。person 1 点の代表性の問題ではなかった。

**原因は、推定した実距離を「全編フィットの `A`」で disparity に戻していたこと。**

```
Z_ball = a(t) / (disp_ball − b)        ← ここまでは正しい
disp' = A / Z_ball + B                 ← ここで a(t) ≠ A のフレームがずれる
```

`a(t) > A` の区間では `(disp_ball − b)` が縮み、Else が奥へ寄って埋もれる。36-39 秒は `a(t)` = 1.175 に対し `A` = 1.090 で、まさにこの条件だった。

#### 対処: 実距離を直接 popout に写す

`disparity` に戻さず、person も Else も**実距離のまま** `1/Z` で popout レンジへ正規化する。

| 方式 | 全編 | 4-8s（胸トラ） | 36-39s（膝止め） | 41-42s（前傾） |
|---|---|---|---|---|
| 現行（bundle の disparity） | 28.0% | 48.8% | **1.2%** | 19.0% |
| `a(t)` 推定 → `A` で戻す | 20.6% | **23.1%** | **12.3%** ❌ | 14.3% |
| **`a(t)` 推定 → 実距離を直接 popout へ** | **23.5%** | **40.5%** | **1.2%** ✅ | **14.3%** ✅ |

**実距離を直接使う方式は、どの区間も現行以上で悪化がない。** 4-8 秒の改善幅は `A` 方式（23.1%）に劣る（40.5%）が、副作用がない分こちらが安全。

「1 箇所を直して別を壊す」構造は、**person 1 点の限界ではなく変換工程の設計ミス**だった。bundle 側の懸念は正しい問題提起で、切り分けのきっかけになった。

### 実装した「実距離の比」方式は、目視では改善が確認できなかった（2026-08-21）

⑨ の深度決定を `Z_other / Z_skeleton = (disp_skeleton − b) / (disp_other − b)` に変更して実装した。

- `StreamingStereoVideoPlayer.Playback.partial.cs`: `TryResolveMetricDepthRatio()` / `TryResolveDepthAffineB()` / `EstimateDepthAffineB()` / `EstimateDistanceFromJoints()`
- `StreamingStereoVideoPlayer.Core.cs`: `useMetricRatioForOtherDepth`（既定 true）、`depthAffineB`（0 で自動推定）、`logDepthAffineFit`
- `BatchPlaybackLogger`: `-metricOff`

**動作は確認できた。** `b` の自動推定は成功し（`[AFFINE] samples=121 a=0.9956 b=0.3944`、Python 試算の 0.3784 に近い）、比も妥当な値が出る（`[RATIO] ratio=0.6950`）。ON/OFF でキャプチャ画像のハッシュも変わる。

**しかし f150（胸トラップ）でボールは隠れたままで、目視では改善が分からない。** 試算での改善幅が 48.8% → 40.5% と控えめだったので、体感で差が出るレベルに達していない。

#### 評価方法の問題も判明した

`[PLACE]` ログは各 track の `ApplyMetaTarget` 内で出力されるが、**⑨ は全 track の処理が終わったあとに走る**ため、**同じフレームの ⑨ の効果はログに反映されない。** Other の depth を新旧で比べても 100% 一致してしまい、効果を測れなかった。

**⑨ 系の評価は `[PLACE]` ログではできない。** キャプチャ画像で見るか、⑨ の後に専用ログを出す必要がある。過去の ⑨ の評価値（前後一致 91.6% → 99.8% など）も、この経路で測ったものは同じ問題を含んでいる可能性があるので、**再評価が要る。**

#### 既定 OFF にした

**主症状（5 秒付近の胸トラップでボールが人体を貫通する）が目視でまったく変わらなかったため、既定を false にした。** 動作自体はしているが、試算での改善幅（4-8s で 48.8% → 40.5%）が見た目に出るレベルに達していない。

コードは残してあるので、`b` の精度が上がるなどして効果が見込める状況になれば Inspector で有効化して再検証できる。

### 2026-08-21 時点のまとめ: 5 秒の貫通は Unity 側で直せていない

この日に試した対処と結果:

| 対処 | 結果 |
|---|---|
| ⑧ 投影高から深度を逆算 + 平滑化 + 順序制限 | **有効**（モデルのサイズずれ・揺れ・前後関係は改善、実機確認済み） |
| ⑨ Else を bundle の深度差に追従 | 有効（ただし `[PLACE]` ログでは効果を測れないため要再評価） |
| ⑨ 実距離の比で Else の深度を決める | **効果なし → 既定 OFF** |
| ⑩ Else のめり込みを表面へ押し出す | **効果なし → 既定 OFF** |

**モデル側の問題（サイズ・揺れ・全体的な前後関係）は ⑧ で解決した。** 残る「特定区間でボールが人体を貫通する」症状は、**この日に試した 3 つの後処理すべてで改善できなかった。**

根本原因は D-004（DepthCrafter の `a(t)` の時間変動）で、bundle 側が `a` の補正を実装するのを待つ状態。それまでは、
- ⑩ をメッシュ表面ベース（`SkinnedMeshRenderer.BakeMesh`）で作り直す
- 評価方法を直す（`[PLACE]` ログでは ⑨⑩ を測れない）

のいずれかを詰める必要がある。**どちらも「試算で効くはず」と言って 2 回外しているので、次は実装前に評価方法の妥当性を確かめること。**

### 骨長補正 OFF で「ボールが離れて見える」原因（2026-08-21）

姿勢一致のため `enableHumanBoneLengthCorrection` を OFF にしたところ、実機で「ボールと人がまた離れた」と報告があった。切り分けた。

#### ⑨ は正しく動いている

`[DEPTH9]` ログで確認したところ、⑨ は骨長補正の ON/OFF に関わらず **`gapAfter = intended = 135.7mm`** と、bundle の意図する差を正確に再現していた。

```
[DEPTH9] skelZ=0.7615 otherZ=0.7267 → 0.6258 moved=-100.9mm
         gapBefore=34.8mm gapAfter=135.7mm intended=135.7mm
```

**球の位置は骨長補正の影響を受けていない。**

#### 原因: 人が奥へ動き、相対的に小さくなった

| 設定 | 人 depth | 配置身長 | intended 差 | **差 / 身長** |
|---|---|---|---|---|
| 骨長補正 ON | 0.9175m | 247mm | +78.2mm | **31.7%** |
| **骨長補正 OFF** | **1.0195m** | 269mm | +78.2mm | **29.1%** |

骨長補正を切ると脚が縮み、⑧ が投影高を bbox に合わせるため**人だけが 102mm 奥へ動く**。深度が増えるので配置身長は 247 → 269mm と大きくなるが、**画面上の見かけは bbox に合ったまま**。

`[PLACE]` ログで見た「人−球の差が 124mm → 223mm に広がった」は ⑨ 適用**前**の値で、⑨ 適用後は両方 135.7mm。**実際に広がってはいない。**

#### 本質的な問題: bundle の意図する差が大きすぎる

**差 / 身長 = 29〜32%。** 実世界に換算すると、身長 1.7m の人に対してボールが **50cm 前**にある計算になる。胸トラップや膝止めの場面で 50cm も前にあるのは明らかに過大。

参考値:
- ボールが胸の前 30cm → 差 / 身長 = 17.6%
- ボールが 1m 前 → 58.8%

**⑨ は「bundle の意図する差」を忠実に再現する設計なので、その意図自体が過大だと忠実であるほど離れて見える。** D-004（`a(t)` の時間変動）が解決していない現状では、この差の絶対値は信用できない。

#### 対処の方向

1. **差にスケール係数を掛ける**（例: 0.5 倍にして 15% 前後に収める）。根拠は薄いが見た目は改善するはず
2. **`a(t)` 推定で実距離の比を使う**（`useMetricRatioForOtherDepth`）。一度実装して既定 OFF にしたが、この文脈では意味が変わる。比を使えば「bundle の差の絶対値」に依存しない
3. **bundle 側の `a` 補正を待つ**

**2 を再評価する価値がある。** 前回は「埋もれが直らない」ことを理由に OFF にしたが、今回の問題は「差が過大」であり、比ベースならこの過大さが是正される可能性がある。

#### 試算: 差の決め方を変えても「離れる」と「埋もれる」のトレードオフから出られない

| 方式 | 人−球差 | 差/身長 | 埋もれ率 | 4-8s |
|---|---|---|---|---|
| **現行 ⑨（bundle の差）** | +78.2mm | **29.1%** | **27.1%** | 46.2% |
| ⑨ 実距離の比 | +447.5mm | **150.5%** | **0.0%** | 0.0% |
| ⑨ bundle の差 × 0.7 | +54.8mm | 20.4% | **48.3%** | 61.5% |
| ⑨ bundle の差 × 0.5 | +39.1mm | 14.6% | **50.0%** | 61.5% |
| ⑨ bundle の差 × 0.3 | +23.5mm | 8.7% | 50.0% | 69.2% |

**「離れる」と「埋もれる」は同じ軸の両端。** 差を広げれば埋もれは消えるが不自然に離れ、縮めれば近づくが体に食い込む。**現行の 29.1% が実はバランスの取れた位置だった。**

実距離の比方式は、`b` の推定値が骨長補正 OFF で 0.3944 → 0.5828 に変わった影響で比が過大になっている。**`b` の推定が person の実距離逆算に依存しており、モデル側の設定変更で揺れる**のは設計上の弱点。

#### 骨長補正 OFF で「離れた」と感じた件の整理

数値上は骨長補正 OFF の方が差/身長は小さい（31.7% → 29.1%）。**差そのものは広がっていない。**

変わったのは**人の絶対深度**（0.9175 → 1.0195m、102mm 奥）。⑨ で球も同じだけ奥へ移動するので差は保たれるが、**両方が奥に行くことで立体視での見え方が変わる。** 実機での「離れた」という印象はこれによる可能性が高い（画面上の見かけは bbox に合っているため 2D では差が出ない）。

**したがって骨長補正 OFF の是非は「姿勢一致 vs 絶対深度の変化」のトレードオフ**であり、⑨ の設計とは独立に判断すべき。

#### 訂正: 「離れる」と「埋もれる」はトレードオフではない

前節で「同じ軸の両端」と書いたが、**正確ではなかった。** 各フレームで「球が最寄り部位の表面にちょうど触れる」ために必要な深度差を計算したところ、**区間によって必要な値が 40 倍違う**ことが分かった。

| 区間 | ちょうど接触する差 | 身長比 |
|---|---|---|
| 0-3s 立位 | +68.6mm | 16.5% |
| **4-8s 胸トラップ** | **+2.0mm** | **1.4%** |
| 9-12s 足上げ | +79.2mm | 24.3% |
| 36-39s | +40.5mm | 10.9% |

全編では median +67.4mm（身長比 20.2%）、p10-p90 は **−5.9〜+122.2mm** と 20 倍以上のレンジ。

**現行 ⑨ は一律 +78.2mm（29.1%）を与えている。**

- **胸トラップ（正解 +2.0mm）に 78.2mm を与えている → 40 倍手前**。これが「離れて見える」
- 足上げ（正解 +79.2mm）はほぼ的中
- 立位（正解 +68.6mm）もおおむね妥当

**つまり「一律の差」が胸トラップだけを大きく外している。** 一律値を下げれば胸トラップは合うが、足上げ・立位が今度は近すぎて埋もれる。**これが「トレードオフ」に見えた正体で、本質は「フレームごとに必要な値が違うのに定数を使っている」こと。**

#### ただしこれは接触を前提にした計算

上の「必要な差」は **「球が最寄り部位に接触している」と仮定**して逆算した値。実際には球が空中にあるフレームもあり、そこでは接触させてはいけない（[[contact-correction-off-limits]] の理由）。

画面上で重なっているフレーム（97f）と離れているフレーム（21f）で必要な差を比べても median は +68.6mm と +62.4mm でほとんど変わらず、**この指標だけでは接触の有無を判別できない。**

**したがって「フレームごとに必要な差」を知るには、結局その瞬間の実距離が要る。** 一律値の調整では原理的に届かず、D-004（`a(t)` の補正）に戻る。

---

## 2026-08-24: 実距離配置の試算と、`anchor_z` の誤りの確定

### 実距離配置は現行より悪化する

bundle 側が配布した `a`,`b`（`bundle_shots_depthscale_test.svb` の `depth_scale_calibration`、a=3.8477 / b=−0.2716）で実距離を復元し、正規化せずに線形スケールで配置した場合を試算した。

| 方式 | 全編 埋もれ | 4-8s 埋もれ | 4-8s 人−球差 |
|---|---|---|---|
| 現行（disparity を 2%/98% 正規化） | **21.6%** | **47.1%** | −51.8mm |
| 実距離 × 0.199（人 median → 0.80m） | 47.3% | 71.9% | −30.6mm |
| 実距離 × 0.216 | 47.3% | 71.9% | −33.3mm |

**大幅に悪化する。** 復元した実距離のレンジは person 3.52-4.69m / ball 3.38-5.71m と現実的で、clamp もほぼ発生しない（0.1-1.2%）。**配置手法の問題ではなく、復元された実距離そのものが誤っている。**

### 確定: `anchor_z` はこの区間で球を人より奥に置いている

depth map を一切使わない独立推定と比較した。

- **person の実距離**: `keypoints3d`（HMR2 由来）を投影して bbox 高に一致する距離を逆算
- **ball の実距離**: ボールの既知直径 18.5cm と bbox 径から逆算

| 区間 | 人−球（較正値） | 人−球（**独立推定**） | 食い違い |
|---|---|---|---|
| 0-3s 立位 | +320mm | +425mm | 105mm |
| **4-8s 胸トラップ** | **−119mm** | **+752mm** | **871mm・符号反転** |
| 9-12s 足上げ | +177mm | +548mm | 371mm |
| **36-39s 膝止め** | **−16mm** | **+158mm** | 174mm・符号反転 |
| 41-42s 前傾 | +54mm | +445mm | 391mm |
| 13-30s | +300mm | +329mm | **29mm** |

**独立推定では全区間で球が人より手前**（すべて正）。**較正値だけが胸トラップと膝止めで符号を反転させている。**

そして 13-30s（症状が出ない区間）では食い違いが 29mm しかない。**症状の出る区間と食い違いの大きい区間が一致する。**

### 結論: `a`,`b` の較正では直せない

`a`,`b` は「disparity → 距離」の変換を正すものであって、**disparity そのものの誤りは正せない。** 胸トラップ区間の `anchor_z` は、球を人より奥と推定している時点で誤っており、どんな変換を掛けても符号は戻らない。

これは bundle 側が以前指摘した「`a(t)` が小さい瞬間は実際の距離差が変わっていなくても見かけの差が縮む」という現象の、より強い形（縮むだけでなく反転する）と考えられる。

### 利用側で使える情報源が 1 つ増えた

**ボールの既知直径からの距離逆算**は、depth map に依存しない独立した推定として機能する。上表のとおり person 側（keypoints3d 由来）と組み合わせれば、**depth map を使わずに人−球の前後関係を決められる。**

ただしこれは「対象物の実サイズが既知」であることが前提で、`bundle_train.svb` の電車や未知の Else には使えない。**汎用解にはならないが、Human + 既知サイズの Else という構成では有効な選択肢。**

次はこの方向（既知サイズによる距離推定）を試算する余地がある。

### 試算: 既知サイズによる距離推定（depth map を配置に使わない）

`anchor_z` がこの素材で信用できないことが確定したので、**depth map を一切使わずに距離を決める**方式を試算した。

- **person**: `keypoints3d` を投影して bbox 高に一致する距離を逆算
- **Else**: 既知の実サイズ（サッカーボール直径 18.5cm）と bbox 径から逆算
- 配置は `z = Z × K` の線形スケール（K は人の median が popout 中央に来るよう決定）
- **`anchor_z` は u/v のみ使い、深度には使わない**

| 方式 | 全編 埋もれ | **4-8s** | 36-39s | 4-8s 人−球差 | 深度 1f 変化 p90 / max | clamp |
|---|---|---|---|---|---|---|
| **現行（disparity 正規化）** | **21.6%** | **47.1%** | 1.2% | −51.8mm | 7.78mm / 59.1mm | 0.0% |
| 既知サイズ 平滑化なし | 10.9% | 5.0% | 0.0% | +156.2mm | **23.83mm / 470.2mm** | 9.5% |
| 既知サイズ tau=0.3s | 12.2% | 1.7% | 0.0% | +147.9mm | 6.94mm / 35.6mm | 7.5% |
| **既知サイズ tau=1.2s** | **12.4%** | **0.0%** | **0.0%** | +112.7mm | **2.77mm / 10.2mm** | 6.6% |

**主症状の 4-8 秒が 47.1% → 0.0% になる。** 全編も 21.6% → 12.4%。深度の揺れも平滑化を掛ければ現行より小さい（p90 7.78 → 2.77mm、max 59.1 → 10.2mm）。

#### 距離推定のノイズ源

| 推定 | 1f 変化 median | p90 | max | 元データの 1f 変化 |
|---|---|---|---|---|
| person（keypoints3d + bboxH） | 16.0mm | 130.5mm | 2411mm | bboxH p90 8.0px |
| ball（既知直径 + bbox 径） | 33.5mm | 163.4mm | 7212mm | bbox 径 p90 1.0px |

**bbox のわずかな揺れ（1px 程度）が距離では数十 mm〜数 m に増幅される。** 特に球は径が 25px と小さいので感度が高い。**平滑化は必須**で、tau=1.2s で p90 13.3mm / 12.4mm まで下がる。

#### この方式の制約

- **Else の実サイズを設定する必要がある。** サッカーボールのような既知物体には使えるが、`bundle_train.svb` の電車や未知の Else には使えない
- **person（または `keypoints3d` を持つ track）が必要。** Else のみの bundle では人側の距離基準がない
- **⑧（投影高から深度を逆算）とは排他。** ⑧ は深度を bbox に合わせる補正なので、実距離配置と衝突する
- clamp が 6.6% 発生する（現行 0%）。スケール K を下げれば減らせるが、popout の使用幅が狭くなる

#### 位置づけ

**汎用解ではない。** 「Human + 実サイズが既知の Else」という構成に限った特殊解で、`bundle_train.svb` には適用できない。

ただし**この素材（サッカーボールを扱う人）では主症状が構造的に消える**ので、実装する価値はある。`anchor_z` の精度改善（D-004）を待つ間の実用的な回避策になる。

#### 却下: 既知サイズ方式は「離しただけ」だった

埋もれ率だけを見て「4-8s が 47.1% → 0.0%」と報告したが、**指標が不適切だった。** 球を体から引き離せば埋もれ率は必ずゼロになる。

**球の表面と体の表面のクリアランス**（0 付近 = 接触、負 = めり込み、大きい正 = 離れすぎ）で測り直した。

| 方式 | 区間 | median | p10 | p90 | めり込み | **接触帯（−20〜+40mm）** |
|---|---|---|---|---|---|---|
| **現行** | 全編 | +26.4mm | −9.0 | +120.8 | 17.3% | **57.4%** |
| | **4-8s** | **+5.8mm** | −21.2 | +58.3 | 43.0% | **66.1%** |
| | 36-39s | +24.0mm | +2.3 | +50.9 | 4.9% | 76.5% |
| **既知サイズ tau=1.2s** | 全編 | +57.6mm | −2.8 | +96.7 | 11.1% | **35.7%** |
| | **4-8s** | **+98.3mm** | +53.9 | +173.4 | **0.0%** | **1.7%** |
| | 36-39s | +54.8mm | +5.3 | +86.3 | 8.6% | 45.7% |

**現行は 4-8s で median +5.8mm とほぼ接触しており、接触帯に 66.1% が入る。既知サイズ方式は +98.3mm 離れ、接触帯は 1.7% しかない。**

**埋もれが消えたのは接触が正しくなったからではなく、球を 10cm 引き離したから。** 全編でも接触帯が 57.4% → 35.7% と悪化する。

**この方式は却下する。**

#### 指標の教訓

**「埋もれ率」は片側だけの指標で、離せば必ず改善する。** 今後この種の評価には**クリアランスの分布（median・p10・p90・接触帯の割合）**を使う。めり込みと離れすぎを同時に見ないと、片方を潰してもう片方を悪化させた変更を「改善」と誤認する。

現行方式は 4-8s で「めり込み 43.0%・接触帯 66.1%・median +5.8mm」であり、**接触の中心は正しく捉えているが分散が大きい**（p10 −21.2 / p90 +58.3）という状態。直すべきは中心ではなく**ばらつき**。

### ばらつきの主因は球の深度（2026-08-25 分解）

「中心は合っているがばらつきが大きい」状態の内訳を、4-8s 区間（n=121）で分解した。

**各項の変動量（p10-p90 幅）:**

| 項 | median | sd | **p10-p90 幅** |
|---|---|---|---|
| 人の配置深度 | +746.5mm | 37.7mm | 106.7mm |
| **球の配置深度** | +786.5mm | **75.8mm** | **223.6mm** |
| 人−球の深度差 | −51.8mm | 62.3mm | 159.6mm |
| 接触部位の深度 | +763.9mm | 47.3mm | 127.9mm |
| **部位−球の深度差** | **−14.2mm** | **53.2mm** | **131.0mm** |
| 画面方向の距離 | +10.2mm | 6.7mm | 16.9mm |
| 部位の半径 | +9.3mm | 4.3mm | 11.6mm |
| 球の半径 | +20.1mm | 1.7mm | 4.1mm |
| **クリアランス（結果）** | **+5.8mm** | – | **79.5mm** |

**球の配置深度の幅が 223.6mm と突出**しており、人（106.7mm）の 2 倍以上。画面方向の距離（16.9mm）や半径（11.6 / 4.1mm）の寄与は小さい。**ばらつきはほぼ深度方向、それも球側で作られている。**

#### 深度を固定すると悪化するという逆説

| 条件 | median | p10-p90 幅 | めり込み | 接触帯 |
|---|---|---|---|---|
| ① 現行そのまま | +5.8mm | **79.5mm** | 43.0% | 66.1% |
| ② 人の深度を固定 | +31.0mm | **126.5mm** | 19.8% | 57.9% |
| ③ 球の深度を固定 | −7.9mm | **101.0mm** | 62.0% | 57.0% |
| ④ 両方固定（姿勢・bbox のみ） | +56.3mm | **50.9mm** | 0.8% | 24.8% |

**片方だけ止めるとばらつきが増える。** 人と球の深度は互いに打ち消し合って動いており、両者を独立に平滑化すると相殺が壊れる。**平滑化するなら「差」に対して掛けるべき**で、個別に掛けてはいけない。

④ で幅が 50.9mm まで縮むのは姿勢と bbox 由来の残差だが、median が +56.3mm・接触帯 24.8% と接触自体が壊れるので、実用にはならない。

#### 部位ごとの内訳

4-8s で最寄りになった部位:

| 部位 | 件数 | クリアランス median | 幅 |
|---|---|---|---|
| LEar | 37 | −5.5mm | 46.7mm |
| Nose | 20 | +14.7mm | 48.1mm |
| **Neck** | 15 | **−20.7mm** | **64.2mm** |
| LSho | 8 | +47.6mm | – |

**最寄り部位が顔・首まわりで入れ替わっており、部位が変わるたびに半径（Neck 0.0554 vs Nose 0.0554 vs Shoulder 0.0316）と位置が飛ぶ。** これも幅を作る一因。

#### 次に試すべきこと

**「人−球の深度差」を平滑化する。** 個別の深度ではなく差に掛ければ、相殺構造を壊さずにばらつきだけ減らせる可能性がある。差の p10-p90 幅は 159.6mm で、クリアランスの 79.5mm の 2 倍。ここを詰められればクリアランスも縮む。

### 有効: 「人−球の深度差」を時間平滑化する（2026-08-25 試算）

個別の深度ではなく**差**に平滑化を掛ける案を試算した。相殺構造を壊さないので、ばらつきだけを減らせる。

**接触帯（クリアランス −20〜+40mm）の割合と p10-p90 幅:**

| 方式 | 全編 | 4-8s | 36-39s | 球の 1f 変化 p90 |
|---|---|---|---|---|
| **現行** | 57.4% / 129.7mm | 66.1% / 79.5mm | 76.5% / 48.6mm | 5.80mm |
| 人−球差 tau=0.3s | 59.8% / 122.9 | 76.0% / 70.0 | 84.0% / 47.4 | 7.35mm |
| **人−球差 tau=0.6s** | **63.7% / 115.2** | **84.3% / 55.2** | **85.2% / 40.0** | **7.60mm** |
| 人−球差 tau=1.2s | 65.0% / 109.6 | 72.7% / 67.9 | 80.2% / 50.7 | 7.59mm |
| 部位−球差 tau=0.6s | 65.4% / 112.3 | 90.1% / 45.9 | 85.2% / **69.4** | **18.44mm** |
| 部位−球差 tau=1.2s | 67.6% / 105.9 | **97.5% / 41.8** | **75.3% / 89.7** | **18.42mm** |

**`人−球差 tau=0.6s` を推奨。** 全区間で改善し、副作用がない。

- 全編 接触帯 57.4% → **63.7%**、幅 129.7 → **115.2mm**
- 4-8s（主症状）接触帯 66.1% → **84.3%**、幅 79.5 → **55.2mm**、めり込み 43.0% → 39.7%
- 36-39s 接触帯 76.5% → **85.2%**、**めり込み 4.9% → 0.0%**
- 球の 1f 変化は 5.80 → 7.60mm と微増するが許容範囲

**「部位−球差」を平滑化する案は 4-8s では優秀（97.5%）だが採らない。** 36-39s が悪化し（幅 48.6 → 89.7mm）、球の 1f 変化が 3 倍（5.80 → 18.4mm）になる。接触部位が LEar → Nose → Neck と入れ替わるたびに基準が飛ぶため。

#### なぜ差に掛けると効くのか

人と球の深度は互いに打ち消し合って動いている（片方だけ固定すると幅が 79.5 → 101〜127mm に悪化する）。**個別に平滑化すると相殺が壊れるが、差に掛ければ相殺を保ったままノイズだけ落ちる。**

現行の ⑧ は person 側だけを平滑化しているので、この観点では設計が不十分。**⑨（Else の深度決定）に差の平滑化を入れるのが正しい置き場所。**

#### 実装するなら

`ApplyOtherDepthFollowForFrame` の中で、`skeletonCam.z − targetZ`（＝人−球の深度差）を track ごとに指数平滑化する。`smoothedProjectedDepthRatioByTrack` と同じパターンで、shot 境界でクリアする。時定数は 0.6s を既定とし Inspector で調整可能にする。

### 指標の再設計: 独立推定を正解とする（2026-08-25）

「接触帯」指標には**接触すべきフレームかを区別していない**という欠陥が残っていた。空中にあるボールも「接触帯にいない」と減点される。

**depth map に依存しない独立推定を正解として、そこからの誤差で測る形に改めた。**

- **正解の person 距離**: `keypoints3d`（HMR2 由来）を投影して bbox 高に一致する距離を逆算
- **正解の ball 距離**: 既知直径 18.5cm と bbox 径から逆算
- **評価**: 配置後の「人−球の深度差」をスケール合わせして、正解の「人−球の実距離差」と比較

接触の有無に関係なく全フレームを評価できる。

#### 結果: 実態はこれまでの見立てより悪い

| 方式 | 区間 | 誤差 median | RMS | p10-p90 | 符号一致 |
|---|---|---|---|---|---|
| **現行** | 全編 | +65.6mm | 648.6mm | 1205.9mm | 83.1% |
| | **4-8s** | **−893.1mm** | **1122.6mm** | 2110.7mm | **39.7%** |
| | 36-39s | −206.9mm | 314.9mm | 509.2mm | 48.1% |
| **差平滑化 tau=0.6s** | 全編 | +71.6mm | 656.1mm | 1231.1mm | **86.9%** |
| | 4-8s | −824.6mm | 1093.6mm | 1962.8mm | **46.3%** |
| | 36-39s | −123.5mm | 252.3mm | 605.9mm | **60.5%** |
| **差平滑化 tau=1.2s** | 全編 | +97.3mm | 657.6mm | 1200.9mm | **88.9%** |
| | **4-8s** | **−667.7mm** | **1023.3mm** | 1678.9mm | **53.7%** |
| | **36-39s** | **+86.0mm** | **292.8mm** | 776.0mm | **96.3%** |

（符号一致 = 配置の前後関係が正解と同じ向きになっている割合）

**4-8s は現行で符号一致 39.7%。前後関係が 6 割で逆になっている。** 誤差 median も −893mm と大きい。「接触帯 66.1%」という以前の評価は、接触すべきかを問わない指標だったため実態を過小評価していた。

差の平滑化で改善はする（39.7% → 53.7%）が、**まだ半分程度**。全編・36-39s では大きく改善する（36-39s は 48.1% → 96.3%）。

#### 評価

**差の平滑化は有効だが、4-8s の根本解決にはならない。** この区間は `anchor_z` 自体が球を人より奥と誤推定しており、平滑化はノイズを均すだけで系統誤差は消せない。

ただし **tau=1.2s は全編 88.9%・36-39s 96.3% と他区間で大きく効く**ので、入れる価値はある。4-8s だけが残る、という状態になる。

#### 指標についての教訓

同じ対象を 3 つの指標で測って、それぞれ違う結論が出た。

| 指標 | 4-8s の評価 | 見落としていたもの |
|---|---|---|
| 埋もれ率 | 47.1%（既知サイズ方式で 0.0%） | 離せば必ず改善する片側指標 |
| 接触帯（クリアランス） | 66.1% | 接触すべきフレームかを区別していない |
| **独立推定との誤差** | **符号一致 39.7%** | （現時点で最も厳しく、実態に近い） |

**片側だけの指標、正解を持たない指標は、改善を過大評価する。** depth map に依存しない独立推定が使える場面では、それを正解として誤差を測るのが最も信頼できる。

### 実装 2026-08-25: ⑨ の深度差を時間平滑化する

`ApplyOtherDepthFollowForFrame` で決めた「骨格 track と Else の深度差」を指数平滑化する。

```csharp
targetZ = skeletonCam.z - SmoothOtherDepthGap(other.trackId, skeletonCam.z - targetZ);
```

- `StreamingStereoVideoPlayer.Playback.partial.cs`: `SmoothOtherDepthGap()`
- `StreamingStereoVideoPlayer.Core.cs`: `otherDepthGapSmoothingSeconds`（既定 **1.2**）
- `StreamingStereoVideoPlayer.Model.cs`: `otherDepthGapByTrack`
- `ShotBoundary.partial.cs`: shot 境界でクリア

**個別の深度ではなく差に掛けるのが要点。** 人と Else の深度は互いに打ち消し合って動いており、片方だけ平滑化すると相殺が壊れる（クリアランスの p10-p90 幅が 79.5mm → 人だけ固定 126.5mm / 球だけ固定 101.0mm に悪化）。

#### 実測結果（全編、独立推定を正解とした前後関係の再現）

| 設定 | 区間 | 誤差 median | RMS | 符号一致 |
|---|---|---|---|---|
| **平滑化なし** | 全編 | +95.1mm | 752.2mm | 83.9% |
| | 4-8s | −349.5mm | 799.4mm | 92.3% |
| | 36-39s | +85.4mm | 132.4mm | 88.9% |
| **差平滑化 1.2s** | 全編 | **+31.1mm** | **621.1mm** | **89.5%** |
| | 4-8s | −316.0mm | 834.1mm | 87.6% |
| | 36-39s | +125.6mm | 144.3mm | **96.3%** |

**全編で改善**（符号一致 83.9% → 89.5%、RMS 752 → 621mm、誤差 median 95 → 31mm）。**36-39s も 88.9% → 96.3%。**

4-8s は 92.3% → 87.6% とわずかに下がるが、この区間の RMS は元から 800mm 前後と大きく、**誤差 median は −349.5 → −316.0mm と改善している**。符号一致の低下は境界付近のフレームが入れ替わったもので、実質的な悪化ではない。

#### 試算との差異

試算では「平滑化なし 4-8s 39.7% → 1.2s で 53.7%」と出ていたが、実測は 92.3% → 87.6%。**大きく食い違う。**

原因は評価対象の違い。試算は Python で ⑨ を再現した値、実測は `[PLACE]` ログの値だが、**`[PLACE]` は ⑨ の適用前に出力される**（各 track の `ApplyMetaTarget` 内で出るが ⑨ は全 track 処理後に走る）。したがって実測側の数字は ⑨ の効果を部分的にしか含んでいない。

**⑨ 系の正確な評価には、⑨ の後に出るログが要る。** 現状は `[DEPTH9]` が発動時のみ・先頭 8 件しか出さないので、全編評価には使えない。**次に ⑨ 系を触るときは先にこのログを整備すること。**

### 2026-08-25: A/B が成立していなかった（バッチハーネスのバグ）

⑨ の差平滑化を `[DEPTH9]` ログで評価したところ、tau=0 と tau=1.2 が**ほぼ完全に同一**だった。

| 設定 | 全編 誤差med | RMS | 符号一致 | 4-8s | 36-39s |
|---|---|---|---|---|---|
| tau=0 と称した run | +51.4mm | 645.2 | 88.5% | 52.9% | 96.3% |
| tau=1.2 の run | +51.1mm | 645.0 | 88.5% | 52.9% | 96.3% |

（後述のとおり**両方 tau=1.2 だった**。tau=1.2 の正しい値がこの行と一致している）

`gapAfter` は 82.6% のフレームでビット一致、最大差 3.0mm。

**原因は平滑化ではなくバッチハーネスだった。** `BatchPlaybackLogger` のパラメータ適用ブロックに

```csharp
if (depthEps >= 0f || depthOff || penetOff || frontBias >= 0f || metricOff || !aimAt || armLen || !boneLen)
```

というガードがあり、**`gapSmooth` がこの条件に入っていなかった**。`-gapSmooth 0` だけを渡すと条件が false になりブロックごとスキップされ、フラグは黙って無視される。両 run とも既定の tau=1.2 で走っていた。3.0mm の差はフレームタイミングの非決定性。

**気付いた手がかり:** 適用ログ `[BATCH] depthEps=... applied to N` が出力に存在しなかった。**設定を変えた A/B では、まず「設定が適用されたログ」の存在を確認すること。**

対処: ガード条件を撤廃し無条件で走らせ、各フラグが自分で「指定されたか」を判定する形にした。適用ログにも全フラグの値を出すようにした。フラグを増やすたびに条件へ追加する設計そのものが誤り。

### 平滑化の実装は正しかった（同 A/B の副産物として確認）

上記の調査中に、`SmoothProjectedDepthRatio` / `SmoothOtherDepthGap` が **1 メタフレームにつき約 31 回走る**ことが分かった（`[PLACE]` 68254 件 ÷ 2167 フレーム。`DisplayModelTick` は毎 `Update` 呼ばれるが meta フレームは fps に従うため）。

一時これを「1 フレーム内で収束してしまい平滑化が効いていない」と判断してフレーム単位に間引く修正を入れたが、**誤りだったので revert した。** `Time.deltaTime` は tick ごとに小さくなり、31 tick の合計が 1 メタフレームぶんになるので、平滑化の総進行量は tau どおりになる。tick ごとの実測でも確認できる:

```
f1 track=1 ticks=30 intended=+135.7mm
  gapAfter: +9.8 → +10.0 → +10.1 → ... → +13.1     1 フレームで +3.3mm
```

目標 135.7mm・現在 9.8mm に対し 1 フレームで 3.3mm 進む = α 0.033/frame。理論値 `1-exp(-(1/30)/1.2)` = 0.027 とほぼ一致する。

**さらに ⑧ では間引いてはいけない。** `ratio` は毎 tick「現在の姿勢で再投影した骨高」から計算されるので、この 31 回の反復は `z ← z·ratio(z)` の**不動点反復**（投影骨高 == bbox 高への収束）も兼ねている。フレーム単位に間引くと収束が失われ、boneRatio median 0.998 という実測値が悪化する。コードにコメントで明記した。

### 実行ハーネスの落とし穴 2 件（2026-08-25）

**1. `& $Unity ...` は Unity を待たずに戻る。** Unity は自分をデタッチするので、PowerShell の `&` は即座に制御を返す。`foreach` でスイープを回すと 3 つが同時起動し、プロジェクトロックを奪い合って先行分は `HandleProjectAlreadyOpenInAnotherInstance` でクラッシュする。**ログファイルすら残らない**ので「Unity が起動していない」ように見える。必ず `Start-Process -Wait` を使う。

**2. `.ps1` に日本語コメントを書かない。** Windows PowerShell 5.1 はスクリプトを ANSI として読むため、BOM なし UTF-8 の日本語コメントが壊れて `Unexpected token '}'` のようなパースエラーになる。スクリプトのコメントは ASCII にし、説明は docs 側に書く。

**3. ガード撤廃の副作用: bool フラグがシーン値を踏み潰した。** `aimAt` / `armLen` / `boneLen` は既定値付きの `bool` で宣言されていたため、ブロックを無条件実行にした途端、**指定しなくても既定値がシーンに書き込まれる**ようになった。`boneLen` の既定 `true` が、既定 OFF にしたはずのシーン設定を上書きしていた。`bool?` に変えて「指定されたときだけ適用」にし、ログにも `scene` と出すようにした。

### ⑨ の差平滑化: 正しい A/B の結果（2026-08-25）

ハーネス修正後、`-gapSmooth` を 0 / 1.2 / 3.0 で振った。健全性チェックとして `|gapAfter - intended|` を確認（tau=0 で 0.00mm = 確かに素通し）。

| 設定 | 全編 符号一致 | 全編 誤差med | 全編 RMS | 1f変化 p90 | 4-8s | 36-39s |
|---|---|---|---|---|---|---|
| tau=0 | 83.0% | +38.2mm | 646.5 | 9.50mm | 38.8% | 48.1% |
| **tau=1.2（既定）** | **88.5%** | +51.6mm | 645.2 | **4.10mm** | 52.9% | **96.3%** |
| tau=3.0 | 87.5% | +90.5mm | 650.7 | 4.20mm | **60.3%** | 96.3% |

**⑨ の差平滑化は有効。** tau=1.2 で符号一致が 83.0% → 88.5%、フレーム間の揺れが 9.50 → 4.10mm と半減し、36-39 秒（前傾でボールが背中にある区間）は 48.1% → 96.3% と劇的に改善する。

tau=3.0 は 4-8 秒だけ更に良い（60.3%）が、全編符号一致（87.5% < 88.5%）と揺れ（4.20 > 4.10mm）でわずかに劣る。**tau=1.2 を既定として維持する。**

**判断根拠は符号一致と 1f 変化 p90 の 2 列に置くこと。** 誤差 median と RMS は run ごとに再フィットする係数 `k`（独立推定と配置深度の median 比）で正規化しているため、**行をまたいで比較できない**。tau=3.0 の「誤差 median +90.5mm」は `k` 依存の数字なので単独では悪化の証拠にならない。符号一致と揺れは `k` 不変。

### この表は ⑩ の影響を受けていない（確認済み）

`[DEPTH9]` の `gapAfter` は `smoothed(intended)` であり、入力の `intended` は `skeleton.anchorZ - other.anchorZ` という **bundle データそのもの**。`otherCam.z` を読まないので、**⑩ の出力に対して構造的に不感**である。⑩ はこのログの次の行で走り、シーンは `resolveOtherPenetration: 1` になっている。したがって「⑨ を測ったつもりが最終描画位置ではない」という危険があった（`[PLACE]` が ⑨ より先に出る問題の 1 段後ろでの再発）。

`[PENET]` の発動回数を数えて確認した:

| 設定 | ⑩ の発動 | 移動量 |
|---|---|---|
| tau=0 | 767 / 68905 tick（1.1%） | median 48.5mm / max 61.6mm |
| **tau=1.2** | **0 回** | — |
| tau=3.0 | 0 回 | — |

**推奨設定 tau=1.2 では ⑩ が一度も発動しない**ので、`[DEPTH9]` の値が最終描画位置そのものであり、上の表は end-to-end で有効。

副産物として **⑩ が「効果なし」だった理由が説明できた**: ⑨ の差平滑化が入っていると、球がめり込むほど深度がずれる状況自体が起きないため ⑩ の発動条件を満たさない。⑩ が発動するのは平滑化を切ったときだけ。

なお tau=0 の行だけは ⑩ が 1.1% の tick で介入しているので、実際の描画はこの表よりわずかに良い可能性がある。ただし最大でも 1.1% ぶんなので、tau=1.2 が優る結論は変わらない。

**この結果は 2026-08-25 の試算とほぼ一致した**（試算: 全編 83.1% → 88.9%、4-8s 39.7% → 53.7%、36-39s 48.1% → 96.3%。実測: 83.0% → 88.5%、38.8% → 52.9%、48.1% → 96.3%）。試算手法そのものは正しく、問題は試算ではなく**測定側のハーネス**にあった。

なお 4-8 秒は tau=1.2 でも 52.9% にとどまる。ここは `anchor_z` 自体が球を人より奥と誤推定している区間で、⑨ の平滑化で埋められる範囲を超えている（D-004）。

### 2026-08-25: 指標が実機の見え方を捉えていなかった（3 回目）

実機確認で **「二つが全然離れている、4-8 秒だけでなく全体的に」** という報告を受けた。⑨ の符号一致 88.5% という数字と食い違うので `[GAP]` ログ（球と最近傍ボーンの 3D 距離）で測り直したところ、**報告が正しかった。**

| tau=1.2（現行） | 値 |
|---|---|
| 球中心 → 最近傍ボーン | median 176.0mm |
| 球の半径 | 21.2mm |
| 球の表面 → ボーンの隙間 | median 155.3mm |
| **球が人より手前にあるフレーム** | **100.0%** |
| 隙間が球の直径以内 | 1.0% |
| ずれの内訳 | 深度 167.4mm / 横 49.4mm |

**全編 2156 フレームで一度も球が人より奥にならない。** 系統的なバイアスであり、ノイズではない。bundle 側では球は 93.2% のフレームで人の bbox と画面上で重なっているので、ずれは完全に深度方向。

**なぜ符号一致 88.5% で見逃したか。** あの指標は「人ルート深度 − 球深度」の符号を独立推定と比べていた。独立推定でも球は人より手前が多数なので、**「常に手前」でも符号は当たる。** 大きさを一切見ていなかった。

これは 2026-08-25 に一度直したはずの欠陥の再発（埋もれ率 → 接触帯 → 独立推定との誤差）。誤差を `k`（median 比）で正規化した時点で**大きさの情報を捨てていた**ことに気付いていなかった。

### 内訳の分解

| 量 | median | 意味 |
|---|---|---|
| ⑧ が人を奥へ動かす量 | **+173.1mm（100% のフレームで奥へ）** | ratio median 1.2942 = モデルの骨投影高が bbox より 29% 高い |
| 最近傍ボーン深度 − ルート深度 | **+93.8mm** | ボーンはルートより奥にある |
| ルート深度 − 球深度 | +67.7mm | ⑨ が bundle の `intended`（median +73.9mm）どおりに置いた結果 |

93.8 + 67.7 = 161.5mm ≈ 実測の 167.4mm。**支配項は「最近傍ボーンがルートより 93.8mm 奥」**で、⑨ が置いた 67.7mm ではない。

⑨ は `targetZ = skelZ - intended` として**人のルート深度**から `intended` を引いている。一方 bundle の `anchor_z` は depth map をアンカー画素でサンプルした値、すなわち**被写体の可視表面**の深度。ルートが体の前方 93.8mm にあるなら、この 2 つは同じ基準点ではない。

**未確認:** なぜルートがボーン群より 93.8mm 手前にあるのか。⑧ は視線方向の平行移動なのでルートとボーンの相対深度は変えない。したがってモデルの root transform の位置か、④⑥ の配置段階に起因する。Unity 上で root の位置を直接見る必要がある（Editor 使用中のため未実施）。

**次にやること:** 対処より先に、上の「未確認」を確定させる。⑨ の基準点を変える案は、原因が確定するまで出さない。

### 原因確定 2026-08-25: モデルの transform 原点が体の外にある

`[ROOTDIAG]` を新設し、ルート・ボーン群・メッシュの位置関係を視線方向とモデルローカルの両方で測った。

| 量（視線方向・世界スケール） | median | p10..p90 |
|---|---|---|
| 最も手前のボーン − ルート | **+87.9mm** | +50.2 .. +143.3 |
| ボーン平均 − ルート | **+184.5mm** | +146.4 .. +237.3 |
| メッシュ中心 − ルート | **+193.6mm** | +161.1 .. +228.3 |
| メッシュ半深度 | 124.0mm | — |
| 球 − ルート | −68.1mm | −120.4 .. −0.7 |
| 球 − 最も手前のボーン | **−164.3mm** | −210.3 .. −78.4 |

**ルートはメッシュの外側、前方 70mm にある**（193.6 − 124.0）。球が体の前後幅の内側に入るのは 1.6% だけで、98.4% は体より完全に手前。

**モデルローカルで測ると原因が確定する:**

| ローカル位置 | median | **変動幅 (p10..p90)** |
|---|---|---|
| `localHips.x` | −0.0100 | **0.0000** |
| `localHips.y` | +0.9600 | **0.0000** |
| **`localHips.z`** | **+0.8600** | **0.0000** |
| `localMeshC.z` | +0.7700 | 0.2700（姿勢で動く） |

**Hips ボーンはモデルローカルで z = +0.86m に固定されており、全 2156 フレームで 1mm も動かない。** FK や SMPL `transl` 由来ではなく、**プレハブの transform 原点と armature が 0.86m ずれている**。表示スケール 0.2502 を掛けると 0.215m ≒ 実測の +184.5mm と一致する。

### なぜこれが「球が常に手前」になるのか

⑧ と ⑨ はどちらも `instance.transform.position` を「人がいる深度」として使う。

- ⑧ `RefineDepthFromProjectedBones`: `camLocal.z * ratio` でルートの深度を比率倍する。体はルートより 184mm 奥にあるので、体を ratio 倍したことにならない
- ⑨ `ApplyOtherDepthFollowForFrame`: `targetZ = skelZ - intended` と**ルートの深度**から bundle の深度差を引く。bundle の `anchor_z` は depth map を**被写体の可視表面**でサンプルした値なので、基準点が 184mm ずれる

結果、球は体より 164mm 手前に置かれ続ける。**2D 投影は正しい**（キャプチャで球は胸に乗っている）ため、モノラルでは気付けずステレオでのみ「離れて見える」。

**注意: これは接触補正の話ではない。** 基準点そのものが体の外にあるという座標の誤りで、補正を足すのではなく参照点を体に戻す問題。

### オフセットは FBX の bind pose に焼き込まれている

track 0 が使うのは `16_Male_Eric.prefab`（`trackModelIndices` で `modelIndex: 16`、キャプチャの背広の男性と一致）。prefab 自身は root の `m_LocalPosition` を 0 に設定しており、実体は `Assets/RP_Character/rp_eric_rigged_001/rp_eric_rigged_001_u3d.fbx` を参照している。**0.86m のオフセットは FBX の armature 側にある。**

Renderpeople のスキャンモデルはスキャン時の位置が焼き込まれたまま書き出されることがあり、これに該当する。**モデルごとに値が違う**ので、プレハブを個別に直すのではなく **配置パイプライン側で参照点を体に戻す**のが正しい対処。

### 対処方針: ⑨ の参照点を選べるようにして実測で決める

⑨ は `skeletonCam = inv * (skeletonInstance.transform.position - camOrigin)` と **root** を使っている。ここを体基準の点に差し替える。候補と、現在の実測値から予想される「球 − 最も手前のボーン」:

| 参照点 | 予想 | 根拠 |
|---|---|---|
| Root（現行） | −164.3mm | 実測値 |
| Hips | 約 −68mm | root より 184.5mm 奥、球は参照点の 68.1mm 手前 |
| メッシュ中心 | 約 −28mm | メッシュ中心は root より 193.6mm 奥 |
| メッシュ前面 | 約 +68mm | 前面は root より 69.6mm 奥（193.6 − 124.0） |

bundle の `anchor_z` は depth map を**可視表面**でサンプルした値なので、理屈ではメッシュ前面が最も近い。ただし **予想は予想として実測で決める**（この 3 週間で「試算では効くはず」が 3 回外れている）。フラグで切り替えて `[GAP]` の「球表面 → 最近傍ボーン」で比較する。

**⑧ は今回触らない。** ⑧ も root の深度を ratio 倍しており同じ基準点の問題を抱えるが、`[PLACE]` を見るかぎり投影は bbox に合っており目的は達成できている。1 度に 1 つだけ変えて効果を測る。

### 実測結果 2026-08-26: 参照点は Hips が最良。予想は外れた

`-depthRef` で 4 種を全編実測した（`bundle_human_shots_driftfix_test.svb`）。

| 参照点 | 球表面→最近傍ボーン | 符号付き深度 | 球が手前の割合 | 隙間が球の直径以内 |
|---|---|---|---|---|
| **Root（従来）** | **155.2mm** | −167.4mm | **100.0%** | 1.0% |
| **Hips（採用）** | **22.1mm** | −11.5mm | 68.0% | **79.8%** |
| MeshCenter | 22.8mm | −9.1mm | 62.6% | 76.4% |
| MeshFront | 88.8mm | −95.9mm | 99.4% | 7.0% |

**「anchor_z は可視表面の depth だから MeshFront が対応するはず」という事前の読みは外れた。** `intended` は popout 圧縮空間での depth 差であって、実距離での表面間距離ではないため。理屈で参照点を決めていたら 88.8mm で止まっていた。**実測してよかった 4 例目。**

Hips と MeshCenter はほぼ同点。**Hips を採用**した理由は安定性:

| 参照点 | 参照点の 1f 変化 median | 球の 1f 変化 p90 |
|---|---|---|
| Hips | **1.00mm** | **3.00mm** |
| MeshCenter | 1.60mm | 4.70mm |
| Root | 0.90mm | 4.00mm |

MeshCenter は腕・脚の振りで bounds が動くため参照点が揺れる（`localMeshC.z` の変動幅 0.27 に対し `localHips.z` は 0.0000）。

### 「ただ近づけただけ」ではないことの確認

ユーザーから「埋もれが消えただけなら離すだけでいい」「空中のボールを近くの足で接触補正されたくない」という指摘を受けているので、**ボールが実際に空中にある区間で離れたままか**を確認した。

| 区間 | Root | **Hips** |
|---|---|---|
| 全編 | 155.2mm | **22.1mm** |
| 4-8s（胸トラップ） | 94.6mm | **26.8mm** |
| **9-12s（蹴り上げ・空中）** | 192.1mm | **81.3mm** |
| 36-39s | 170.7mm | **22.6mm** |
| 40-50s（前傾） | 180.5mm | **20.2mm** |

**9-12s だけ 81.3mm と離れたまま**（全編の 3.7 倍）。接触すべき区間では 20〜27mm まで近づき、空中の区間では離れている。**一律に近づけているのではない。**

これは補正を足したのではなく、`transform.position` という**体の外にある点**を Hips に戻しただけであることの帰結。接触判定も距離しきい値も入れていない。

### 残っている同じ問題: ⑧

⑧ `RefineDepthFromProjectedBones` も `camLocal.z`（root の深度）を ratio 倍しており、同じ基準点の誤りを持つ。体は root より 184.5mm 奥にあるので、体を ratio 倍したことにならず 184.5 × (ratio−1) ≒ 54mm 分だけ補正不足になる。ただし `[PLACE]` を見るかぎり投影は bbox に合っているため、今回は触っていない。**次に検証する候補。**

### Hips 採用の副作用 2 件と、より根本的な問題

⑨ の参照点を Hips にすると球は体に付くが、球は 215mm 奥へ動く（724.0 → 939.1mm）。副作用が 2 つ出る。

| 副作用 | Root | Hips |
|---|---|---|
| 球の見かけの大きさ | 1.000 | **0.772 倍** |
| 画面際にクランプされたフレーム | 0.0% | **21.7%** |
| 球が使った深度幅 | 463.1mm | 254.9mm |

⑨ は `TrackPlacementCommand.PositionOnly` で位置だけ動かしスケールを変えないため、深度が変わると投影サイズが変わる。移動量が Root では median 43.1mm だったのが Hips では 163.7mm になり、サイズ誤差が 6% → 23% に拡大した。

**より根本的な問題: 人の体そのものが画面に張り付いている。**

| 位置（視線方向） | median |
|---|---|
| ルート | 765.6mm |
| 最も手前の骨 | 864.9mm |
| **骨の平均（実際に見える体）** | **937.0mm** |
| 画面 | 1000mm |
| popout レンジ | 650〜1000mm |

**体はレンジの奥 18% に張り付いており、popout をほとんど使えていない。** ⑨ を Hips にすると球も体に合わせて奥へ行くので、クランプが起きる。

この構造は次のようになっている。`ComputeTargetHeightMeters(bboxH, anchorZ)` は「深度 anchorZ で bbox 高を張る」ようにモデルを拡縮するが、実際の体はルートより 171mm 奥にあるため、その深度では bbox より小さく写る。⑧ はそれを見て深度を調整するが、**スケールは変えられないので体を更に奥へ動かして辻褄を合わせる**（ratio median 1.2942、100% のフレームで奥へ）。

**したがって「体が奥すぎる」と「モデルのスケールが大きすぎる」は同じ現象の別表現**であり、どちらも原点オフセットに起因する。

### 本来の対処: 配置段階でオフセットを打ち消す

`ApplyReplaceableModelTransform` はルートを `anchorWorld` に置く。ここで **Hips が `anchorWorld` に来るようにルートをずらす**のが筋。そうすれば

- 体が anchorZ に来る → popout レンジを正しく使える
- スケール計算の前提（深度 anchorZ で bbox を張る）が成立する → ⑧ の ratio が 1 に近づく
- ⑨ は Root 参照のままでよい（root ≒ 体になるため）
- 球の移動量が小さくなる → サイズ誤差もクランプも起きない

**⑨ の Hips 参照は対症療法で、これは原因療法。** ただし ⑦ の下端合わせ・⑧ の深度補正と相互作用するため、影響範囲が広い。今回は ⑨ の参照点変更までで止め、実機確認の結果を見てから配置段階に入る。

### 実機確認 2026-08-26: 改善を確認

ユーザーが実機で確認し **「だいぶ良くなった」**。⑨ の参照点を Root → Hips にした変更を採用として確定する。球の 23% 縮小については言及なし。

**この修正で確定したこと**: `instance.transform.position` は「人がいる場所」ではない。モデルによっては体の外にある。深度を扱うコードがこれを人の位置として使ってはいけない。

### 球の縮小を解消: ⑨ で深度に合わせてスケールも動かす（2026-08-26）

Hips 参照にすると ⑨ の移動量が median 43.1mm → 163.7mm に増え、球が bbox より小さく写るようになった。配置パイプラインは「投影が bbox に一致する」前提で組まれているので、深度を動かしたらスケールも追従させる。

```csharp
float depthFactor = targetZ / otherCam.z;
Vector3 moved = otherCam * depthFactor;
Vector3 scale = matchOtherScaleToFollowedDepth
    ? otherInstance.transform.localScale * depthFactor
    : otherInstance.transform.localScale;
```

| | 補正なし | **補正あり** |
|---|---|---|
| **投影半径 / bundle の bbox 半径** | 0.820 | **1.001**（p10..p90 0.937..1.045） |
| 球表面 → 最近傍ボーン | 21.1mm | **16.2mm** |
| 隙間が球の直径以内 | 80.9% | **88.1%** |
| 球が手前の割合 | 66.0% | 66.0% |
| 世界半径の 1f 変化 p90 | 1.30mm | 1.60mm（暴走なし） |

**画面上の大きさが動画のボールと一致する（1.001）。** 接触指標も改善するのは、球が本来の大きさに戻って表面がボーンに近づくため。`matchOtherScaleToFollowedDepth` を既定 true にした。

**累積しない理由**: `ApplyMetaTarget` が毎 tick 位置とスケールの両方を貼り直す（1 メタフレーム内の `otherZ` / `scaleIn` の実測幅はどちらも 0.000）。毎 tick の `localScale` は「anchor 深度で bbox を張る `desiredScale`」なので、`depthFactor` を掛けるのは代入と同じ意味になる。

### この実装で踏んだ罠 2 件

**1. `lockedModelLocalScaleByTrack` は Else を持たない。** 最初この辞書からロック値を取る実装にしたが、`lockScale = IsCategoryPerson(...) || IsCategoryAnimal(...)` で **Else は対象外**なので `TryGetValue` が常に false になり、A/B が完全に同値になった。

**2. 同じコメント行が ⑨ と ⑩ の両方にあり、編集が ⑩ に入った。**

```
// 画面上の位置 (u, v) を保ったまま深度だけ変える。
Vector3 moved = otherCam * (targetZ / otherCam.z);
TrackPlacementWriter.Apply( ... otherInstance.transform.localScale));
```

このブロックは `ApplyOtherPenetrationResolveForFrame`（⑩）と `ApplyOtherDepthFollowForFrame`（⑨）に**文字単位で同一**の形で存在する。ファイル上は ⑩ が先にあるため、`replace(old, new, 1)` が ⑩ を書き換えていた。⑩ は既定 OFF なので何も起こらず、フラグは届いている（`matchScale=True` をログで確認）のに結果が変わらないという状態になった。

**教訓: 一意でない文字列で置換しない。** `assert old in s` は「存在する」ことしか保証せず、「一意である」ことは保証しない。**`s.count(old) == 1` を確認するか、一意なアンカー（ここでは `[DEPTH9]` ログ）からの相対位置で場所を決めること。**

気付けたのは、⑨ に `factor=` `scaleIn=` `matchScale=` をログに足して**書き込み側の値を直接見た**から。指標だけ見ていると「効果がない」で終わっていた。

### 棄却 2026-08-26: 「体を anchorWorld に置けば ⑧ が不要になる」は誤り

`alignModelBodyToAnchorDepth`（姿勢適用後に Hips が root 位置に来るようモデル全体を平行移動）を実装して A/B した。

| 指標 | OFF | ON |
|---|---|---|
| **⑧ の ratio (median)** | 1.2968 | **1.4972** |
| ⑧ が奥へ動かした割合 | 100.0% | 100.0% |
| ⑧ の移動量 | 172.9mm | 258.0mm |
| ルート深度 | 765.4mm | 743.8mm |
| 見える体の深度 | 937.2mm | 872.5mm |
| 球の深度 | 935.8mm | 843.8mm |
| 球のクランプ率 | 20.4% | **5.8%** |
| **球表面 → 最近傍ボーン** | **16.2mm** | **52.9mm** |
| 投影半径 / bbox | 1.001 | 1.001 |

平行移動量は median 236.5mm だったが、**体は 64.7mm しか手前に来なかった**（937.2 → 872.5）。⑧ が押し戻したためで、⑧ の移動量は 172.9 → 258.0mm に増えている。

**なぜ外れたか。** 体を手前に動かすと投影は大きくなるので、ratio（投影高 ÷ bbox 高）は 1 から**遠ざかる**。⑧ はそれを見て更に奥へ押す。私は「② がスケールを anchorZ 前提で決めているから、体を anchorZ に置けば ratio が 1 になる」と考えたが、実際にはスケールは ② のあと `RefineLockedScaleFromProjectedBones` が投影実測で決め直しており、② の式は最終的なスケールを決めていない。

### そこから分かったこと: 体の深度は自由変数ではない

投影が bbox に一致するという条件は、**モデルの世界サイズと bbox の見込み角で深度を一意に決めてしまう**。root をどこに置いても ⑧ がその深度へ引き戻す。

したがって「体が画面に張り付いている」は、ルートの位置の問題ではなく **モデルの世界サイズが popout レンジに対して大きい**ことの言い換えである。体を手前に出すにはモデルを小さくするしかないが、それでは bbox に合わなくなる。**popout レンジ側を動かす以外に自由度がない。**

`alignModelBodyToAnchorDepth` は既定 false のまま残す（`useMetricRatioForOtherDepth` や `resolveOtherPenetration` と同じ扱い）。球のクランプ率だけは 20.4% → 5.8% と改善するので、レンジを触るときに再評価する価値はある。

**次の候補は `screenDistanceMeters` / `popoutRangeMeters`。** 2026-08-20 の調査で「affine 較正は無効だが popout 拡大は有効」と出ている（[[depth-range-fixes-ineffective]]）。ただしそのときは球の埋もれを指標にしていたので、**今の指標（球表面→最近傍ボーン、体の深度、クランプ率）で測り直す**必要がある。

### 2026-08-26: popout レンジのスイープ。現在値は球が「近すぎ」だった

`screenDistanceMeters` / `popoutRangeMeters` を振り、現在の指標で測り直した。

| screen / popout | 体の深度 | 画面までの余裕 | 球↔ボーン | クランプ | 投影/bbox | ⑧ratio | **独立推定比** |
|---|---|---|---|---|---|---|---|
| **1.0 / 0.35（現在）** | 937.0mm | 63.0mm | **16.3mm** | 20.5% | 1.001 | 1.2929 | **0.41** |
| **1.0 / 0.60** | 812.9mm | **187.1mm** | 27.9mm | **6.2%** | 1.001 | 1.2887 | **0.84** |
| 1.5 / 0.60 | 1476.0mm | 24.0mm | 49.4mm | 34.8% | 1.000 | 1.4979 | 0.49 |
| 2.0 / 0.90 | 2059.1mm | **−59.1mm** | 120.0mm | 50.1% | 1.000 | 1.7712 | 0.57 |

「独立推定比」= Unity の相対距離（人までの距離に対する人−球の距離）÷ 独立推定の同量。1.00 が物理的に正しい。独立推定の median は 14.78%。

**`screenDistanceMeters` を上げても改善しない。** 体の深度がほぼ比例して伸びる（937 → 1476 → 2059mm）ため、画面までの余裕はむしろ減り、2.0 では体が**画面より奥**に出る（−59.1mm）。⑧ の ratio も 1.29 → 1.77 と悪化する。

**`popoutRangeMeters` だけを広げるのが効く。** 1.0 / 0.60 で体が 124mm 手前に来て popout の余裕が 63 → 187mm、クランプが 20.5 → 6.2% に減る。

### 接触指標だけで判断すると逆の結論になる

球↔ボーンは 16.3 → 27.9mm と「悪化」する。しかし**独立推定比が 0.41 → 0.84 と大きく改善している** ── つまり現在の 0.35 は球が**物理的に近すぎ**で、16.3mm という数字はその副産物だった。

ユーザーから受けた指摘「単純に埋もれが消えたなら離すだけでいい」がそのまま当てはまる形で、**接触指標は片側にしか効かない**。近すぎるほうの誤りを検出できない。**判定は独立推定比で行う。**

なお ⑧ の ratio は popout を変えてもほぼ動かない（1.2929 → 1.2887）。⑧ が押し戻す量はモデルの世界サイズと bbox で決まっており、レンジとは独立という前項の結論と整合する。

### 訂正 2026-08-26: 独立推定の球直径 18.5cm が誤り。popout は現状維持が正しい

前項で「現在の popout 0.35 は球が近すぎ（比 0.41）、0.70 が最適（比 1.07）」と書いたが、**これは独立推定の前提が誤っていた。**

独立推定は球の距離を「既知の実直径 ÷ bbox 半径」で逆算する。その直径を 18.5cm としていたが、この値だと**胸トラップ中（4-8s）に球が人の 0.763m 手前**になる。接触しているはずの場面で 76cm 離れているのは物理的にありえず、しかも**空中で蹴り上げている 9-12s より胸トラップのほうが離れている**という順序の逆転まで起きていた。

直径を振って、胸トラップ中の球の位置で較正した。

| 仮定直径 | 4-8s の球の位置 | 全編 相対距離 | **popout 0.35 の比** | popout 0.70 の比 |
|---|---|---|---|---|
| 18.5cm | +0.763m（不可能） | 14.78% | 0.41 | 1.07 |
| 20.5cm | +0.360m | 7.13% | 0.85 | 2.21 |
| **21.0cm** | **+0.264m** | 5.96% | **1.01** | 2.64 |
| 22.0cm | +0.073m（体に埋まる） | 5.54% | 1.09 | 2.85 |

胸トラップ中の正解は「胸表面 ≒ 0.12m ＋ 球半径 ≒ 0.105m ＝ 約 0.22m」。**21cm が最も整合し、そのとき現在の popout 0.35 は比 1.01 でほぼ正しい。**

**結論: `popoutRangeMeters` は 0.35 のまま変更しない。** 0.70 にしていたら球を 2.6 倍に離し、直前に「だいぶ良くなった」と確認されたばかりの状態を壊していた。

### 影響を受ける過去の記述

独立推定（18.5cm 前提）を使った箇所は数値を割り引いて読むこと。

| 記述 | 影響 |
|---|---|
| 「Unity は独立推定の 0.55 倍しか離していない」（⑨ Hips 採用前の分析） | **誤り。** 21cm なら現状はほぼ正しい |
| ⑨ の tau スイープの「符号一致率」 | 符号のみなので影響は小さいが、絶対量の議論には使えない |
| 参照点 4 種の比較（Root/Hips/MeshCenter/MeshFront） | `[GAP]` の接触指標ベースなので**影響なし**。Hips 採用の判断は有効 |
| 「体が画面に張り付いている（937mm / 余裕 63mm）」 | 独立推定と無関係の実測なので有効。ただし**直すべき問題かどうかは別途判断が要る** |

**教訓: 独立推定にも前提がある。** 「実測で決める」と言っても、その実測が仮定を含むなら仮定の妥当性を先に検証しなければならない。今回は**接触しているはずの場面で接触と整合するか**という内部整合性チェックで捕まえた。同じチェックを最初にしていれば、参照点スイープの解釈も一度で済んでいた。

### 再訂正 2026-08-26: 球の直径 18.5cm は bundle 側が実物から特定した値だった

前項で「18.5cm は誤り、21cm が正しい」と書いたが、**一次資料を確認せずに書いた。撤回する。**

`docs/bundle-shared/README.md` に bundle 側の記述がある:

> ball は実サイズ既知（**ハンドボール男子3号、直径 0.185m**、D-001 追加報告 #4 参照）

映像の会場も "Kieler Nachrichten" の看板が写るハンドボール会場（THW Kiel）で、競技用ハンドボール男子3号は円周 58-60cm = 直径 18.5-19.1cm。**18.5cm は妥当**であり、私の「胸トラップ時に接触しているはずだから 21cm」という較正は、実物の同定という直接証拠に対して弱い。

**したがって popout の推奨も確定しない。**

| 直径 | popout 0.35 の比 | popout 0.70 の比 |
|---|---|---|
| 18.5cm（bundle 側の同定） | 0.41 | **1.07** |
| 21cm（当方の較正・撤回） | **1.01** | 2.64 |

**それでも `popoutRangeMeters` は変更しない。** 推奨が前提次第で正反対に振れる状態で実機の設定を触るべきではない。

### 未解決の異常: 胸トラップ中に球が人の 0.763m 手前になる

18.5cm を採ると、接触しているはずの 4-8s で球が人の **0.763m 手前**になる。しかも**空中で蹴り上げている 9-12s（0.478m）より離れている**という順序の逆転が起きる。物理的にありえない。

この独立推定は depth map を一切使わず、`meta.bin` の keypoints3d（人）と bbox 径（球）だけを使う。したがって異常の所在は次のいずれか:

1. **keypoints3d のスケール**（SMPL betas）が過大 → 人の距離 Dp が過大
2. **球の bbox が過大**（モーションブラー等を含む）→ 球の距離 Db が過小
3. 直径 18.5cm が違う（bundle 側の同定が誤り。可能性は低い）

**これは bundle 側に渡すべき新しい手がかり。** 手法は bundle 側と一致確認済み（README 855 行、決定係数 0.143 を小数点以下まで再現）なので、実装差ではなく入力データの問題を示している。

### D-003 / D-004 の状態を取り違えていた

当方が「4-8s の `anchor_z` 誤りは bundle 側 D-004 待ち」と説明していたのは**二重に誤り**。

- **4-8s の前後関係逆転は D-003 として起票し、2026-08-19 に当方の誤報として棄却済み。** bundle 側が抽出コードを 3 通り検証し、データは正しいと確認。症状の原因は Unity 側（表示モデルの投影が bbox の 1.197 倍）と当方が認めている。これは ⑧ で boneRatio 1.082 → 0.998 に直した
- **D-004 は「`anchor_z` が実距離を再現しない」という全 bundle 共通の一般課題**で、状態は「対応中」、最終更新 2026-08-22。直近のやり取りは 2026-08-24 に当方が「較正値は正しいが配置への効果ゼロ、配布ベースが driftfix でない」と報告したところで、**ボールは bundle 側にある**

今回測り直した「4-8s で bundle は球を奥（z01 差 −0.036）、独立推定は手前、一致率 40%」という結果は、D-003 の再提起ではなく **D-004（低 R²）の一事例**として扱うのが正しい。

## 2026-08-26: 実距離配置の前提を検証。`b` 相殺の主張は誤りだった

bundle 側から `a_metric ≈ 3.507` を受領し、実距離配置の設計に入る前に前提を確認した。

### 訂正: `b` は正規化で相殺されていない

当方が 2026-08-24 に「`disparity − b` は popout の 2%/98% 正規化で完全に吸収される」と報告し、bundle 側も 2026-08-26 に同意したが、**この主張は現在の構成では成り立たない。**

`TestScene.unity` の実際の設定は **`enableAnchorDepthRangeNormalization: 0`** ── **線形正規化は OFF**。`NormalizeAnchorZ01` は恒等写像になる。相殺の代数

```
(d − b − (dMin − b)) / ((dMax − b) − (dMin − b)) = (d − dMin) / (dMax − dMin)
```

はこの線形正規化についての式であり、**OFF なら適用されない。**

実際に効いているのは `ResolvePopoutFraction` の**逆数変換**:

```
farness = (1/d − 1/dMax) / (1/dMin − 1/dMax)
```

これは非線形なので `b` は消えない。`dMin <= AnchorDisparityMinimum(0.01)` なら線形にフォールバックするが、実測 **dMin = 0.588（depthscale）/ 0.566（driftfix）** で条件を満たさず、**逆数変換が有効**。

実装どおりに計算した `b` の効果:

| 量 | `b` を引いたときの変化 |
|---|---|
| 人の配置深度 | median **7.72mm**（p90 8.13 / max 8.14） |
| 球の配置深度 | median **6.14mm**（p90 8.09 / max 8.14） |
| **人−球の差** | median **2.19mm**（p90 6.30 / max 8.14） |
| 変化が 0.05mm 未満のフレーム | **3.1%** |

**相殺されるなら全フレームで 0.00mm のはず。** 実測では 96.9% のフレームで動いている。

「埋もれ 47.1% → 47.1% で不変」という実測は正しいが、その理由は**相殺ではなく効果が小さいこと**（人−球の差で 2mm）。埋もれ率という粗い指標では見えなかった。

**2026-08-24 の当方の報告と、それに同意した bundle 側の 2026-08-26 の回答は、両方とも訂正が要る。**

### `1 − z01` は較正の disparity と同じ単位だった（検証済み）

`meta.bin` の `anchor_z` は「反転済み・larger=farther」で disparity とは別量と明記されているため、較正を適用してよいか不明だった。検証したところ**同じ単位**である。

```
Z = a_metric / ((1 − z01) − b) = 3.507 / (1 − z01 + 0.2716)
```

| 量 | 較正から復元 | 独立推定 | 比 |
|---|---|---|---|
| 人の距離 median | **3.685m** | 3.694m | **0.998** |
| 人 予測/真値（孤立フレーム 332f） | — | — | **1.0209** |

**0.2% の一致。** `a_metric` は person 参照で作られているので人が合うのは当然だが、`1 − z01` を disparity として扱ってよいことの確認になる。復元レンジは人 3.21〜4.28m / 球 3.08〜5.21m と現実的。

### 球の残差 11.6% は「隣接ピクセル混入」では説明しきれない

bundle 側は「4-8s / 40-50s とも球が体に隣接しており、隣接ピクセル（髪・手・脚の影）の混入で 21.2cm が説明できる可能性が高い」とした。これを**球と人の bbox が重ならないフレームだけ**（重なり 5% 未満、332 フレーム）に絞って検証した。

| 参照 | 球 予測/真値（孤立フレームのみ） |
|---|---|
| `a_metric` = 3.507 | **1.1164** |
| 配布値 a = 3.8477 | 1.2249 |

**孤立フレームでも 11.6% 残る。** 混入で説明できるなら孤立フレームでは 1.00 に戻るはず。

含意直径に直すと 18.5 × 1.1164 = **20.7cm**。これは接触幾何から出した **21.2cm** と独立な経路でありながら近い。**2 つの独立な方法が 20.7〜21.2cm を指している。**

隣接混入は寄与している（全編 14.6% → 孤立 11.6% と減る）が、主因ではない可能性が高い。

### 結論 2026-08-26: 実距離配置は実装しない。順序は変わらず、大きさはむしろ悪化する

`a_metric = 3.507` と `b = −0.2716` が揃ったので、実距離配置 `Z = a/((1−z01) − b)` を現行 popout と比較した。判定は独立推定（球の bbox には bundle 側が Hough 円検出で実測した **4.8% 過大**の補正を適用）。

| 全編 2156f | 符号一致 | 相関 | \|誤差\| med | 大きさ比 |
|---|---|---|---|---|
| 現行 popout | 79.9% | +0.427 | 6.20pt | **0.97** |
| 実距離配置 | 80.8% | +0.446 | 5.98pt | **0.55** |

| 孤立のみ 332f | 符号一致 | 相関 | \|誤差\| med | 大きさ比 |
|---|---|---|---|---|
| 現行 popout | 94.0% | +0.077 | **3.80pt** | **0.73** |
| 実距離配置 | 94.0% | +0.082 | 6.39pt | 0.41 |

**両者は順序をほとんど変えない。** どちらも `d = 1 − z01` の単調関数なので、前後関係は原理的に同じになる。符号一致のわずかな差（79.9 対 80.8%）は現行側の clamp（`dd` を `[dMin, dMax]` に丸める）で順序が潰れるぶんだけ。

現行の変換を整理すると

```
z = S − P·(1 − farness) = (S − P) + P·(1/d − 1/dMax)/(1/dMin − 1/dMax)
```

で **`1/d` のアフィン関数**。実距離配置は `Z = a/(d − b)` で **`1/(d−b)` の純粋な比例**。したがって違いは (1) `b` のシフト、(2) 加法オフセット `(S − P)` の有無だけで、**単調性は共通**。

**差が出るのは深度差の「大きさ」だけで、そこは現行が優る**（全編 0.97 対 0.55、孤立 0.73 対 0.41）。実距離配置は独立推定の半分しか離さない。加法オフセットが相対差を拡大しており、結果的に正しい大きさに近くなっている。

**2026-08-24 の試算（埋もれ 21.6% → 47.3% と悪化）と結論が一致した。** 当時は `a` が無く指標も粗かったが、方向は同じ。

### なぜ実距離配置が「正しい」のに合わないのか

相関が全編 +0.43 / 孤立 +0.08 と低い。**変換の問題ではなく、`anchor_z`（disparity）自体が真の距離をほとんど反映していない**（D-004: person R² 0.143→0.249）。実距離配置は「弱い信号を忠実に実距離へ写す」ので、誤差もそのまま実距離のスケールで出る。現行の圧縮された popout レンジは、その誤差も一緒に圧縮している。

**したがって D-004 が解決するまで実距離配置に利点はない。** 唯一の実利は clamp の解消（現行は球の 20.5% が画面際に張り付く）だが、それだけのために順序が同じで大きさが悪い方式へ移る理由にならない。

### `b` の効き方は変換によって 3 桁違う

| 変換 | `b` を落としたときの人の距離の差 |
|---|---|
| 現行 popout（逆数変換） | **2.19mm**（人−球の差） |
| 実距離配置 | **1.472m**（p10 1.328 / p90 1.723） |

bundle 側の念押しのとおり。現行での 2mm を根拠に「`b` は要らない」と判断してはいけない。

### 球の直径: bundle 側の立場を支持する

Hough 円検出による bbox 4.8% 過大の実測を反映すると、球の残差は **11.6% → 6.5%**、含意直径は **20.7cm → 19.7cm** になる。3 号球の規定上限 19.1cm に対して +3% で、当初の 21.2cm（+11%）より遥かに近い。

**「直径の同定が違う」より「測定側に 6% の未解明誤差が残っている」という bundle 側の読みを支持する。** 当方の 2 経路（接触幾何 21.2cm / 較正 20.7cm）はどちらも bbox 過大を含んでいたため、同じバイアスを 2 回見ていた可能性が高い。

## 2026-08-26: `bundle_animal.svb` の下調べ（再生前のデータ側の確認）

Unity で再生する前に、bundle 側のデータだけで素性を確認した。

### 構成

| 項目 | 値 |
|---|---|
| frames / fps | 2120 / 30（71 秒） |
| shots | **15**（human の 1 shot と対照的） |
| カテゴリ | **`animal` のみ**（`person` / `other` なし、track 0 と 1） |
| joints | **26 点** × 全フレーム |
| `depth_policy` | あり（larger = farther） |
| `generated_at` | 2026-08-06（human 系より古い） |

**`person` / `other` が無いので、今日直した ⑨（Else の深度追従）と Hips 参照は animal では走らない。** `ResolveHumanDepthReferencePoint` は Humanoid でない track では Root にフォールバックするので、挙動は従来どおり。

コード側は `AnimalPoseApplier`（1338 行）が `jointCount >= 20` を要求しており、26 点と整合する。

### depth 品質は human より良好

`bundle_depth_check.py`（共有ツール）の結果:

| track | フレーム | `corr(transl.z, z01)` | 判定 |
|---|---|---|---|
| 0 | 1146f（0〜39s） | **+0.550** | OK |
| 1 | 974f（38〜71s） | **+0.624** | OK |

human の person track は当初 +0.026 で NG、D-004 対応後も R² 0.143→0.249 だったのに対し、**animal は最初から OK 判定**。2 track は時間的に分かれており（0〜39s と 38〜71s）、shot が 15 あることと合わせて「カットごとにアフィン合わせが実質リセットされる」という bundle 側の説明と整合する。

### 体格と距離

| track | 体の高さ | 体の長さ | bbox | 推定距離 |
|---|---|---|---|---|
| 0 | 0.813m | 1.197m | 368×495px（縦長 0.74） | median **1.62m** |
| 1 | 0.998m | 1.856m | 584×603px（ほぼ正方 0.97） | median **1.53m** |

推定距離は「keypoints の体高が bbox 高に一致する距離」。**human の 3.69m に対して 1.5〜1.6m と近い**ので、popout レンジの使われ方が human とは違うはず。

track 0 の bbox が縦長（幅より高さが大きい）なのは四足動物の側面像としては不自然で、立ち上がっているか正面向きの可能性がある。**要目視確認。**

### 未確認

- 実際に再生したときの配置・姿勢・スケール（Unity 未実行）
- どの動物なのか（シーンの `selectedAnimalIndex: 0` = `00_Dog.prefab`）
- D-004 で bundle 側が指摘した「animal の低 R²（0.110 / 0.330）は別原因の可能性、未調査」との関係。**今回の測定（+0.550 / +0.624）は別指標なので直接は比較できない**

## 2026-08-26: animal を初めて再生した。⑧ とスケール再ロックが animal では走っていない

`bundle_animal.svb` をバッチ再生し、`[PLACE]` を全編（2117 件）測定した。

### 症状: モデルの投影が bbox より大幅に小さい

| track | モデル | sizeRatio（投影高÷bbox高） | 上端ずれ | 下端ずれ | ±15% を外れる |
|---|---|---|---|---|---|
| 0（0〜39s） | `16_Deer1`（modelH 2.208m） | **0.826** | **+87.1px** | −1.0px | **69.8%** |
| 1（38〜71s） | `04_Lion`（modelH 4.090m） | **0.5635** | **+267.8px** | −1.0px | **96.5%** |

**下端は合っている（−1.0px）が上端が大きく足りない。** つまりモデルが bbox に対して低すぎる。human では ⑧ が深度を調整して sizeRatio を 1.0 付近に追い込んでいるが、animal ではそれが起きていない。

### 原因: `TryProjectBonesToEyeHeight` が Humanoid 専用

```csharp
Animator animator = instance.GetComponentInChildren<Animator>(true);
if (animator == null || !animator.isHuman)
{
    return false;      // ← animal は Generic リグなのでここで抜ける
}
```

⑧ `RefineDepthFromProjectedBones` はカテゴリ条件で **Animal を対象に含めている**

```csharp
// 骨格を持つカテゴリだけ。Else は投影が既に bbox と一致している（実測 sizeRatio = 1.000）。
if (!IsCategoryPerson(obj.categoryId) && !IsCategoryAnimal(obj.categoryId))
```

にもかかわらず、その直後に呼ぶ投影関数が Humanoid 専用なので **animal では常に false で抜け、何もせず・ログも出さない**。実測でも `[DEPTH8]` が **0 件**（human では 2000 件超）。

**同じ関数に依存する箇所が 4 つある。**

| 呼び出し元 | animal での状態 |
|---|---|
| ⑧ `RefineDepthFromProjectedBones` | **動かない** |
| `RefineLockedScaleFromProjectedBones` | **動かない** |
| ⑦ `FitDisplayedModelToBBox` | 別経路のフォールバックで下端合わせは効いている（bottomDelta −1.0px） |
| `TryFindNearestBoneToPoint`（⑩ が使用） | 動かない（⑩ 自体が既定 OFF なので実害なし） |

**human で「モデルが bbox の 1.197 倍」を直した ⑧ の恩恵を、animal は一度も受けていない。**

### その他の観察

- **深度レンジは健全に使えている**: 配置深度 0.665〜1.134m、画面際に張り付くのは 3.1% のみ（human の球は 20.5%）。animal の `corr(transl.z, z01)` が +0.550 / +0.624 と良好なことと整合
- **shot 境界は正しく検出**（15 shot、境界越え 15 回）
- `[GAP]` / `[ROOTDIAG]` / `[DEPTH9]` は 0 件。`person` / `other` が無いので当然で、異常ではない

### 検証時の設定ミス（記録）

シーンの `trackModelIndices` は human 用（track 0 → index 16、track 1 → index 4）のまま。**この設定はカテゴリを見ずにインデックスだけ適用する**ため、animal bundle では `16_Deer1` と `04_Lion` が選ばれた。意図した `selectedAnimalIndex: 0`（Dog）ではない。

**bundle を切り替えると使われるモデルが黙って変わる**ので、animal を評価するときは `trackModelIndices` をクリアするか animal 用の値に直す必要がある。sizeRatio の絶対値はモデル依存なので、Dog での再測定が要る。ただし **⑧ が動かないという構造上の欠陥はモデルに依存しない。**

### 正しいモデル（Dog / Lynx）で測り直した（2026-08-26）

`trackModelIndices` を track 0 → `00_Dog`、track 1 → `39_Lynx` に修正して再測定した。猫のプレハブは存在しないため、最も猫に近い Lynx を使用（候補は他に `48_Puma` / `37_Lioness`）。

| track | モデル | modelH | sizeRatio（p10〜p90） | 上端ずれ | 下端ずれ | ±15% 外 |
|---|---|---|---|---|---|---|
| 0 | `00_Dog` | 6.689m | **0.969**（0.507〜1.215） | +12.6px | −1.0px | **57.6%** |
| 1 | `39_Lynx` | 1.707m | **0.647**（0.491〜0.860） | +210.5px | −1.0px | **89.4%** |

Deer / Lion のときより Dog は改善（0.826 → 0.969）したが、**ばらつきは残る**。中央値が合っていても p10〜p90 が 0.507〜1.215 と 2.4 倍の幅があり、**57.6% のフレームが ±15% を外れる。**

Lynx は中央値 0.647 と系統的に低い。猫（立ち上がり気味・尻尾を含む縦長 bbox 584×603）に対して、四足で低く長いリンクスの体型が合っていない可能性がある。**モデル選択の問題と配置の問題が混在しているので、これだけでは切り分けられない。**

### `00_Dog` にマテリアルが当たっていない

キャプチャ（f600）で `00_Dog` が**テクスチャなしのベージュ単色**でレンダリングされている。関節の分割が見えており、マテリアルが失われているか未設定。同じシーンの `39_Lynx` は毛皮テクスチャが正しく出ているので、**モデル個別の問題**。

### まとめ: animal の問題は 3 つ

| # | 問題 | 種類 |
|---|---|---|
| 1 | **⑧ 深度補正とスケール再ロックが走らない**（`TryProjectBonesToEyeHeight` が `animator.isHuman` を要求） | **構造的・モデル非依存** |
| 2 | sizeRatio が不安定（Dog 57.6% / Lynx 89.4% が ±15% 外） | 1 の帰結 + モデル体型の不一致が混在 |
| 3 | `00_Dog` のマテリアル欠落 | アセット |

深度レンジの使われ方は健全（配置深度 median 0.751m、画面際 0.3%）で、shot 境界も 15 回すべて検出できている。**姿勢も見た目には破綻していない。**

**1 が最優先。** human で ⑧ を入れたときは boneRatio が 1.082 → 0.998 に収束し、ばらつきも大きく減った。animal でも同じ効果が期待できるが、`TryProjectBonesToEyeHeight` に Generic リグ用の経路（`SkinnedMeshRenderer.bones` の総当たり。`TryResolveNearestHumanBone` が既に同じ方式を使っている）を足す必要がある。

## 2026-08-26: 被写体が画面から見切れるとき何が起きているか（animal）

「犬や猫の一部しか映っていないとき、keypoint や bbox はどうなるのか」という問いを、`meta.bin` を直接読んで測った。`bundle_depth_check.py` は可視フラグを読み飛ばしている（`pos += kp  # visibility`）ので、パーサを拡張して読んだ。

### 1. keypoint は「画面外に出る」のではなく「不可視フラグが立つ」

`meta.bin` は joint ごとに 1 バイトの可視フラグを持つ（値は 0 / 1）。

| | animal（26 点） | human（44 点） |
|---|---|---|
| 1 フレームの可視点数 median | **25 / 26** | 44 / 44 |
| 全点可視のフレーム | **42.6%** | ほぼ全部 |
| **可視 20 点未満** | **18.1%** | 0% |
| 最小 | **14 / 26** | 44 |

**animal では日常的に点が落ちる。** 座標自体は全点ぶん格納されており、画面外の座標が入るのではなく**フラグで不可視を示す**設計。

`AnimalPoseApplier` の入口は `pose.jointCount >= 20` を見ているが、これは**配列長**（常に 26）であって可視点数ではない。したがって**可視 14 点のフレームでも弾かれず**、各セグメントの適用時に `vis` を見て個別にスキップしている。

### 2. bbox は頻繁に画面端で切れる

| 接する辺 | animal | human |
|---|---|---|
| 下端 | **73.9%** | 1.5% |
| 左端 | 21.5% | 0.0% |
| 上端 | 12.9% | 0.1% |
| 右端 | 8.7% | 0.0% |
| **いずれか** | **80.4%** | **1.5%** |

**animal は 8 割のフレームで見切れている。** human（1.5%）とは前提がまるで違う。

利用側は bbox を「被写体の見かけの大きさ」として使うので、**切れた bbox に対してモデルを合わせている**。⑦ の下端合わせは切れた bbox の下端（＝画面の端）に合わせており、実測でも `bottomDelta` は一貫して −1.0px。つまり**足が画面外にある場面では、モデルの足を画面の端に合わせている。**

### 3. anchor が bbox の外に出る

| | animal | human |
|---|---|---|
| anchor が bbox 外 | **10.4%**（track0 Dog では 18.8%） | 0.1% |
| はみ出す向き | **縦のみ**（横は 0px） | 縦横とも |
| はみ出し量 | median **73px**、max **308px** | median 84px |
| うち bbox より下 | **81%** | — |
| anchor が画面外 | **0 件** | 0 件 |

anchor 自体は必ず画面内にあるが、**bbox の外に、しかも下側に出る**ことがある。

**直感に反する点**: anchor が外に出るのは bbox が下端で切れている場面ではなく、**切れていない場面の方が多い**（切れていないフレームの 30.4% 対 切れているフレームの 1.0%）。つまり「見切れているから anchor がずれる」のではない。四足動物の 3D root の投影が、見えている体の下に落ちているように見える。

### 4. 配置への影響（track を分けて集計）

sizeRatio は track によって傾向が逆なので、まとめて集計すると交絡する（最初にそれをやって誤読しかけた）。

**track 0（Dog）**

| 条件 | 件数 | sizeRatio med | p10〜p90 | ±15% 外 |
|---|---|---|---|---|
| 端に接しない | 415 | 0.592 | 0.317〜0.940 | 87.0% |
| 2 辺以上が接する | 405 | **1.021** | 0.936〜1.119 | **6.2%** |
| **anchor が bbox 外** | 215 | 0.704 | **0.368〜1.344** | **78.6%** |
| bbox 小（h≤352） | 290 | 0.632 | 0.307〜1.304 | 81.4% |
| bbox 大（h≥545） | 303 | 1.069 | 0.938〜1.215 | 39.6% |

**track 1（Lynx）**

| 条件 | 件数 | sizeRatio med | p10〜p90 | ±15% 外 |
|---|---|---|---|---|
| 1 辺が接する | 759 | 0.620 | 0.465〜0.892 | 86.4% |
| 2 辺以上 | 214 | 0.674 | 0.568〜0.723 | 100.0% |
| bbox 小（h≤542） | 249 | 0.793 | 0.531〜0.992 | 58.6% |
| bbox 大（h≥638） | 286 | **0.575** | 0.527〜0.720 | **100.0%** |

**Dog と Lynx で傾向が逆。** Dog は bbox が大きいほど合う（0.632 → 1.069）、Lynx は大きいほど外れる（0.793 → 0.575）。Dog は「距離が離れる（bbox 小）と合わなくなる」= 深度の問題、Lynx は「被写体が大きく写るほど合わない」= モデル体型の不一致、と読める。

**`anchor が bbox 外` は Dog の最悪カテゴリ**（p10〜p90 が 0.368〜1.344 と 3.6 倍の幅）。見切れそのものより、この anchor のずれの方が配置を壊している。

### 分かっていないこと

- 見切れているとき bbox が「見えている部分だけ」なのか「推定された全体」なのかは、bundle 側の仕様を確認していない。⑦ が画面端に合わせている以上、**前者だと想定して実装されている**が確認が要る
- anchor が bbox の下に出る理由（root の定義か、投影か、それとも意図的か）
- 見切れフレームで sizeRatio を評価する意味があるか（切れた bbox に合わせること自体が正しいのかどうか）

**いずれも bundle 側の設計意図に依存するので照会する。**

### bundle 側の回答（2026-08-26）と、渡した該当フレーム

**Q1 の答え: `bbox` は可視部分の外接矩形のみ。** SAM2 のセグメンテーションは画面内のピクセルにしか値を持てないため、構造上「見切れた分を含めた推定」は不可能。person / animal / other で共通。**利用側の想定どおりで ⑦ の実装は変更不要。**

**Q2 の答え: `anchor` は bbox と無関係に決まる。** animal の anchor は `animal_camera_root_anchor()` が **AniMer 26 関節の関節 7 と関節 18 の中点**を 3D の root として構成し、それを再投影した点。チェックしているのは**画面内かどうかだけ**で（画面外なら anchor 自体を無効化）、bbox に収まっているかは一切見ていない。**bbox の外に出るのは想定内の挙動でバグではない。**

bundle 側は正直に「関節 7・18 が解剖学的に何を指すかは断定できない」（AniMer の公開デモが安定した意味名を出さないため、コード上も番号のみを使う方針と明記されている）、「下方向に偏る理由は未特定」とし、**該当フレームを特定すれば深掘りできる**と申し出た。

#### 渡したフレーム: 898〜907（track 0）

| frame | anchor が bbox 下へ | anchor(u,v) | bbox(x,y,w,h) | 可視 | j7/j18 | 下端切れ | sizeRatio |
|---|---|---|---|---|---|---|---|
| 902 | **308px** | (768, 529) | (732, 122, 81, 99) | 26/26 | 1/1 | no | 0.941 |
| 901 | 307px | (774, 526) | (739, 125, 79, 94) | 26/26 | 1/1 | no | 1.057 |
| 899 | 305px | (780, 528) | (746, 129, 75, 94) | 26/26 | 1/1 | no | 1.139 |
| 900 | 305px | (780, 528) | (746, 129, 75, 94) | 26/26 | 1/1 | no | 1.088 |
| 904 | 305px | (753, 527) | (715, 119, 92, 103) | 26/26 | 1/1 | no | 0.879 |

**可視性は原因ではない。** root を構成する関節 7 / 18 の可視率は track 0 で 99.7% / 100%、上位フレームはすべて 26/26 可視・見切れなし。

共通する特徴は **bbox が小さく（75〜92 × 94〜110px）画面上部（y≈120）にある**こと ── つまり被写体が遠方かつ上方にいる場面。それに対し anchor は画面下部（v≈525〜529）に落ちている。**root の 3D 深度が過小（近すぎ）に出ていると、再投影が画面下方へ流れる**という説明と整合する。

#### 利用側への含意

- ⑦ の下端合わせは**変更不要**と確定した
- anchor が bbox 外に出ること自体は仕様なので、**クランプするような「修正」を入れてはいけない**
- ただし該当フレームでは anchor と bbox が別々の場所を指しており、**位置は anchor・大きさは bbox という現行の使い分けが破綻している**。sizeRatio 自体は 0.88〜1.14 と悪くないので、壊れているのは大きさではなく**位置**

### 訂正 2026-08-26: 見切れは常態。`sizeRatio` は見切れフレームでは指標にならない

「結論としては見切れないのか」という問いを受けて厳密に測り直した。**見切れは常態である。** 先の説明で anchor の話を強調したため紛らわしくなっていた。

| 切れている辺の数 | 割合 |
|---|---|
| **0 辺（完全に画面内）** | **19.6%**（416f） |
| 1 辺 | 52.3%（1108f） |
| 2 辺 | 20.9%（443f） |
| 3 辺 | 7.2%（153f） |

| track | 左 | 右 | 上 | 下 | いずれか |
|---|---|---|---|---|---|
| 0（Dog） | 36.0% | 14.0% | 0.1% | 56.9% | 63.7% |
| 1（Lynx = 猫） | 4.4% | 2.5% | 26.3% | **92.9%** | **100.0%** |

**track 1 は全 974 フレームで見切れている。完全に画面内のフレームが 1 枚もない。**

### これが指標を壊していた

bundle 側の回答で **bbox は可視部分だけの外接矩形**と確定した。一方モデルは動物全体を描く。したがって

**見切れているフレームでは、正しく配置されたモデルの投影は bbox を超えるのが正しい。`sizeRatio`（投影高 ÷ bbox 高）の正解は 1.0 ではなく 1.0 より大きい。**

実測もそうなっている（track 0）:

| 切れ辺 | 件数 | sizeRatio median | p10〜p90 |
|---|---|---|---|
| **0 辺** | 416 | **0.592** | 0.317〜0.940 |
| 1 辺 | 332 | 1.204 | 0.853〜1.241 |
| 2 辺 | 300 | 0.970 | 0.935〜1.091 |
| 3 辺 | 97 | 1.082 | 1.060〜1.129 |

**これまで「sizeRatio が 1 から外れる」と報告していた集計は、正解が 1 でないフレームを 8 割含んでいた。無効。**

**評価できるのは track 0 の「0 辺」416 フレームだけ。そこでの sizeRatio は 0.592 ── モデルが 41% 小さい。** これが唯一まともに測れた欠陥。track 1 は評価可能なフレームが 0 枚なので、**この指標では判定できない。**

### ⑧ を animal に入れる計画は、このままでは有害

⑧ `RefineDepthFromProjectedBones` は **「投影骨高が bbox 高に一致する」深度へモデルを動かす**。見切れフレームでこれをやると、**動物全体を切れた bbox に押し込むことになり、モデルが不当に小さくなる。**

human では見切れが 1.5% しかないので問題にならなかったが、**animal では 80.4% で発動する。そのまま入れれば 8 割のフレームを悪化させる。**

**したがって ⑧ を animal で動かす前に、見切れフレームの扱いを決める必要がある。** 選択肢:

1. 見切れフレームでは ⑧ を発動させない（0 辺のフレームだけ補正し、あとは保持）
2. bbox の切れていない辺だけを使って合わせる（下端が切れているなら上端と左右で合わせる）
3. 見切れ量を推定して bbox を外挿する（bundle 側は「構造上不可能」としているので利用側でやるしかない）

**1 が最も安全。** ただし track 1 は 0 辺のフレームが存在しないため、**track 1 では ⑧ が一度も発動しない**ことになる。実質「animal の一部では ⑧ を諦める」という判断になる。

`FitDisplayedModelToBBox`（⑦ 下端合わせ）も同じ問題を持つ。下端が切れているフレーム（track 1 で 92.9%）でモデルの足を画面端に合わせるのは誤りで、**足はもっと下（画面外）にあるべき**。実測の `bottomDelta` が一貫して −1.0px なのは、⑦ が切れた bbox の下端に忠実に合わせてしまっている証拠。

### 再訂正 2026-08-26: 「8 割見切れ」は過大。確実に見切れているのは 34.6%

「どうやって見切れを出しているのか、8 割は多いかも」という指摘を受けて判定方法を検証した。**指摘が正しく、8 割は過大だった。**

#### 判定方法の妥当性（これは確認できた）

「bbox が画面境界に接する」を見切れとしていた。この判定自体は妥当:

- **bbox は画面内にクランプされている**。`x+w` は最大 1280（=W）で W を一度も超えず、`y+h` も最大 640（=H）
- **境界からの距離が二極化している**。下端は 0px が 73.4%、1px が 0.5%、2px が 0.1%、3px 以上が 25.9%。**1〜2px がほぼ皆無**で 0px に鋭いピークが立つのは、ランダムな配置ではなく**切り詰めの痕跡**

#### ただし下端接触は構図でも起きる

接地した動物の足が画面下端に来る構図では、切れていなくても bbox は下端に接する。分けて数え直した:

| 判定 | 割合 | 件数 |
|---|---|---|
| A. どの辺にも接しない | **19.6%** | 416f |
| B. **下端のみ**接する（曖昧） | **45.8%** | 971f |
| C. 左 / 右 / 上に接する（確実） | **34.6%** | 733f |

| track | 接しない | 下端のみ | 左右上あり |
|---|---|---|---|
| 0（犬） | 36.3% | 22.2% | **41.5%** |
| 1（猫） | **0.0%** | 73.6% | 26.4% |

#### 元映像で目視確認した

`source/pre_removal_stereo_video.mp4`（2560x640 の左右並置、片目 1280x640）から ffmpeg でフレームを抜き、bbox と anchor を重ねた。

| フレーム | 判定 | 目視結果 |
|---|---|---|
| **f1882**（猫） | side | **確実に見切れ。** 背中が画面上端で切れ、尻尾が右端に達している |
| **f676**（犬） | bottomonly | **曖昧。** 座った犬の前足が画面下端ちょうどにあり、わずかに切れている程度 |
| **f1504**（猫） | bottomonly | **別の問題を発見。** bbox が猫より明らかに広く、右隣の六角形の猫ベッドまで含んでいる |

**結論: 確実に見切れているのは 34.6%。** ただし track 1（猫）は「接しない」フレームが 1 枚も無く、常に下端に接しているので、track 1 に限れば見切れ前提で考える必要がある。

#### 副産物 2 件

- **被写体は犬（ラブラドール）と猫。** `36_LabradorDog` が prefab にあるので、`00_Dog` より適切な可能性がある
- **bbox が被写体より広いフレームがある**（f1504 で隣の猫ベッドを含む）。human の球で bundle 側が指摘した「隣接ピクセルの巻き込み」と同種の現象が animal にもある

#### 指標への影響（前項の訂正を再訂正）

前項で「sizeRatio の集計は 8 割が無効」と書いたが、**正しくは 34.6% が確実に無効、45.8% が判定不能**。評価に使えるのは A の 19.6% で、そこでの track 0 の sizeRatio 0.592 という数字は変わらない。track 1 が評価不能なことも変わらない。

### 見切れの「程度」を測る（2026-08-26）: 接触の有無ではなく欠損率で見る

「見切れの判定がきつすぎる。尻尾だけなら問題ない、半身欠けると配置できない」という指摘を受けて、**程度**を測る方法を作った。

#### 手法

`transl` は使えない（Z のスケールが約 6 倍ずれており、投影スパン ÷ bbox が 0.17 になる。bundle 側の説明どおり anchor の深度は depth map 由来で `transl` とは別物）。

そこで**切れていない辺で較正する**:

```
推定全高 = keypoint 縦スパン × (bbox幅 ÷ keypoint 横スパン) × 1.034
欠損率  = 1 − 見えている bbox 高 ÷ 推定全高
```

係数 1.034 は見切れなしフレーム 416 枚での高さ係数 ÷ 幅係数（317.6 / 307.2 px/m）。左右が切れているフレームは較正できないので**測定不能**として除外する。

**検証**: 見切れなしフレームでの欠損率は median 1.6%、p90 14.5%。0 付近になるので手法は妥当。**ノイズ床は 15% 程度**なので、それ以下は「切れていない」とみなす。

#### ショット別の結果

| shot | 時間 | 被写体 | 欠損率 med | p90 | <15% | 15-30% | >30% | 測定不能 |
|---|---|---|---|---|---|---|---|---|
| **0** | **0.0〜8.6s** | 犬 | **49.8%** | 52.2% | 4% | 3% | **93%** | **60%** |
| 1 | 8.6〜11.3s | 犬 | 0.0% | 19.1% | 86% | 9% | 5% | 0% |
| **2** | **11.3〜14.2s** | 犬 | — | — | — | — | — | **100%** |
| 3 | 14.2〜22.3s | 犬 | 2.1% | 12.4% | 94% | 4% | 2% | 5% |
| 4 | 22.3〜29.9s | 犬 | 15.3% | 19.2% | 47% | 53% | 0% | 42% |
| 5 | 29.9〜32.7s | 犬 | 6.2% | 21.4% | 78% | 18% | 4% | 6% |
| **6** | **32.7〜37.2s** | 犬 | 0.0% | 19.0% | 81% | 19% | 0% | **88%** |
| 7 | 37.2〜38.1s | 犬 | 0.0% | 6.9% | 100% | 0% | 0% | 0% |
| **8** | **38.1〜47.8s** | 猫 | **42.4%** | 44.4% | 0% | 30% | **70%** | 0% |
| 9 | 47.8〜53.8s | 猫 | 0.0% | 21.6% | 84% | 13% | 3% | 0% |
| 10 | 53.8〜57.2s | 猫 | 2.2% | 18.6% | 83% | 17% | 0% | 0% |
| 11 | 57.2〜60.3s | 猫 | 16.6% | 24.4% | 31% | 69% | 0% | 0% |
| 12 | 60.3〜65.2s | 猫 | 1.8% | 8.4% | 100% | 0% | 0% | 16% |
| 13 | 65.2〜68.8s | 猫 | 0.0% | 0.0% | 100% | 0% | 0% | 0% |
| 14 | 68.8〜70.7s | 猫 | 20.2% | 22.7% | 0% | 100% | 0% | **77%** |

（shot 8 に犬 track も 2 フレームだけ残っているが無視してよい）

#### 読み方

**深刻に欠けている（半身級）** ── 配置が破綻する可能性が高い:

| shot | 時間 | 欠損率 |
|---|---|---|
| **0** | **0.0〜8.6s**（犬） | median 49.8%、93% のフレームが 30% 超 |
| **8** | **38.1〜47.8s**（猫） | median 42.4%、70% のフレームが 30% 超 |

**横方向にも切れていて測定できない** ── 同程度かそれ以上に深刻な可能性:

| shot | 時間 | 測定不能 |
|---|---|---|
| 2 | 11.3〜14.2s（犬） | 100% |
| 6 | 32.7〜37.2s（犬） | 88% |
| 14 | 68.8〜70.7s（猫） | 77% |

**ほぼ問題ない** ── shot 1 / 3 / 5 / 7 / 9 / 10 / 12 / 13。合計すると全 71 秒のうち **約 35 秒**（半分）は欠損 15% 未満で、**尻尾や足先が少し切れる程度**。

#### 結論

**「8 割見切れ」は誤り。程度で見ると、配置に影響しうるレベルで欠けているのは shot 0 と 8（計 18.3 秒 = 全体の 26%）、それに測定不能の shot 2 / 6 / 14（計 10.4 秒 = 15%）。** 残りの約 6 割は問題にならない量。

したがって ⑧ を animal に入れるときの見切れ対応は、**全フレームを対象にした複雑な仕組みではなく、欠損率が大きいショットで発動を抑える程度で足りる**可能性が高い。

### 発見 2026-08-26: `manifest.shots` がカットを 6 箇所取りこぼしている

「カットシーンはもっとあるはず」という指摘を受けて、元映像から独立にシーン検出して突き合わせた。

```
ffmpeg -i pre_removal_stereo_video.mp4 -filter:v "select='gt(scene,0.25)',showinfo" -f null -
```

| 閾値 | 検出数 |
|---|---|
| 0.40 | 8 |
| **0.25** | **20** |
| 0.15 | 29 |
| 0.10 | 54 |

閾値 0.25 の検出結果と `manifest.shots` の境界（14 箇所）を比較:

| 検出（秒） | manifest |
|---|---|
| **1.6 / 5.0 / 17.8 / 26.7 / 34.5 / 44.9** | **無し（★取りこぼし）** |
| 8.6 / 11.3 / 14.2 / 22.3 / 29.9 / 32.7 / 36.8 / 38.2 / 44.9 / 47.8 / 53.8 / 57.2 / 60.3 / 65.2 / 68.8 | 一致 |

**manifest にだけある境界は 0 件。manifest は検出結果の真部分集合。** 誤検出ではなく取りこぼし。

#### 利用側への影響: スケールがカットをまたいで持ち越される

スケールは shot 先頭でロックされ、`ResetPerShotTrackState` が shot 境界でクリアする。未検出カットではこれが働かない。

| shot | 未検出カット | 前 scale / sizeRatio | 後 scale / sizeRatio |
|---|---|---|---|
| 0 | 1.6s | 0.0622 / 1.063 | 0.0622 / 1.013 |
| **0** | **5.0s** | 0.0622 / **1.176** | 0.0622 / **0.948** |
| 3 | 17.8s | 0.0195 / 0.572 | 0.0195 / 0.592 |
| 4 | 26.7s | 0.0629 / 1.207 | 0.0629 / 1.082 |
| **6** | **34.5s** | 0.0598 / **0.939** | 0.0598 / **1.193** |
| 8 | 44.9s | 0.2581 / 0.550 | 0.2581 / 0.529 |

**6 箇所すべてでスケールが据え置き。** 一方 manifest の正しい境界では大きく切り替わっている（shot 1→2 で 791%、0→1 で 86.8% など）。

**取りこぼしは最悪の 2 ショットに集中している。** 欠損率 49.8% の shot 0 に 2 箇所（1.6s, 5.0s）、42.4% の shot 8 に 1 箇所（44.9s）。ただし shot 3（欠損率 2.1% と良好）にも 17.8s の取りこぼしがあるので、**未検出カット = 必ず配置が壊れる、ではない。**

#### 副次的な観察: shot ごとのスケールが 9 倍振れる

track 0 のスケールは shot ごとに 0.0082〜0.0731 と **9 倍**の幅がある。modelH 6.689m に対し 0.0082 は表示高 5.5cm で、明らかに小さすぎる（shot 1、8.6〜11.3s）。**取りこぼしとは別の問題**として要調査。

### bundle 側の回答（2026-08-26 その2）: shots.json が古いだけと判明

#### shot 取りこぼしの原因: 配布物の `shots.json` が stale

bundle 側がコードを確認した結果:

- shot 検出は **片目の work 動画（1280x720）** に対して実行される。**ステレオ結合フレーム（2560x640）は使わない**。当方が疑った「結合フレームで感度が落ちる」は外れ
- デフォルト閾値は **0.15**
- 現在のコードで同じ動画に再実行すると **28 shots（閾値 0.15）/ 21 shots（0.25）**。配布物の `shots.json` は **15 shots**（mtime 2026-07-31）
- 配布物の 15 境界は再実行結果の**完全な部分集合**。当方が報告した未検出 6 箇所（1.6/5.0/17.8/26.7/34.5/44.9 秒）は、**再実行では 1 フレーム以内の誤差ですべて検出された**

**したがって閾値でも結合フレームでもなく、配布済み `shots.json` が単純に古い。** 生成時期（7/31）以降に動画か検出ロジックが更新されたが `shots.json` は再生成されずに使い回された、という説明。

**次に animal を再ビルドするときに shot 検出を再実行すれば解消する見込み。** bundle 側は「必要になったタイミングで教えてほしい」として待っている。

#### 該当フレームの追加観測: `z01` が半減している

bundle 側が f898〜907 を直接確認した結果:

```
f897 (shot 4→5 の境界直前): bbox=(0,115,1280,525)  ← フルフレーム
f898: bbox=(760,113,69,110) anchor=(792,525) z01=0.458
f899: bbox=(746,129,75,94)  anchor=(780,528) z01=0.376
f900: bbox=(746,129,75,94)  anchor=(780,528) z01=0.324
f901: bbox=(739,125,79,94)  anchor=(774,526) z01=0.288
f907: bbox=(692,125,85,101) anchor=(736,527) z01=0.232
```

**`z01` が 0.458 → 0.232 とほぼ半減する一方、bbox のサイズ・位置はほぼ一定。** 見た目の大きさが変わらないのに深度だけ大きく動いており、当方の「root の 3D 深度が過小に出ると再投影が下方へ流れる」という仮説と整合する観測。

ただし bundle 側は「深度推定の誤りか、実際に犬が素早く接近しているのか、この観測だけでは切り分けられない（bbox が本当に一定なら後者は考えにくいが断定しない）」として、関節 7/18 単体の 3D 軌跡を見るのが次の手だが急ぎでないので停止、としている。

**注目すべき点: f897 は shot 4→5 の境界で、bbox がフルフレーム（0,115,1280,525）。** 境界直後の f898 から急に小さい bbox に変わっており、**この区間は shot 切り替わり直後**。当方が見つけた「shot 5 の先頭付近で anchor が破綻する」現象と、shot 境界が関係している可能性がある。

## 2026-08-27: animal 再ビルド完了。D-001 が animal に入っていなかった

`FINNAL_ANIMAL/bundle_shots_depthdriftfix_shotsfix.svb`（109,600,867 bytes）として配布された。既存ファイルは無変更で並置。

### D-001（チャンク間ドリフト修正後の `depth.npz`）は反映されていなかった

しかも **D-002 と同種の「一度は反映されたが後の再ビルドで失われた」事故**だった。

| ビルド | frame0 `rawAnchor.z` |
|---|---|
| `bundle_shots_h264fix.svb`（配布中） | 0.48974609375（修正**前**） |
| `20260805-animal-depth-chunkfix/bundle.svb`（未配布） | 0.42724609375（修正**後**） |
| **今回の配布物** | **0.42724609375**（修正後と一致） |

`u`/`v`/`bbox` は 3 者とも完全一致で、変わったのは depth 由来の `z` だけ。配布中ビルドの `pipeline_manifest.json` の `depthPolicy.originalDepth.path` が消滅済みの旧ジョブディレクトリを指していたことも物証。

経緯: 08-05 に修正版 depth でビルド（未配布）→ 08-06 版はサイドカー欠損で不完全 → **08-07 版がサイドカー欠損を直す際に 07-31 の修正前系列から派生してしまい、修正済み depth が失われた。**

**当方が「depth 品質は OK 判定（+0.550 / +0.624）だから既に入っている可能性が高い」と推測したのは外れ。** 指標が OK でも修正が入っているとは限らなかった。

### 副次的発見: anchor のライブ計算率が 77.4% → 99.5% に改善

| `usedAnchor.source` | 配布中（修正前 depth） | 今回（修正後 depth） |
|---|---|---|
| `animal_camera_root`（ライブ計算） | 1641/2120 = **77.4%** | 2109/2120 = **99.5%** |
| `held_previous_high_conf`（直前値保持） | 479/2120 = **22.6%** | 11/2120 = **0.5%** |

修正前は チャンク境界のドリフトで深度が不安定になり、ゲーティングに落ちて hold に回るフレームが多かった。**当方が測っていた「anchor が bbox の下に落ちる」現象も、22.6% が hold だったことと関係する可能性がある。**

### その他の確認結果

| 確認依頼 | 結果 |
|---|---|
| 08-19 の segmentation 修正が animal に効くか | **無関係。** `usedAnchor.source` は `animal_camera_root` と `held_previous_high_conf` の 2 種のみで、`mask_centroid` / `bbox_center` 経由は **0 件** |
| `background_drift_correction` / `depth_scale_calibration` | **不要で正しい。** 前者は animal にとって「オフ」ではなく**そもそも通らないコードパス**（背景 window サンプリング経由の補正で、animal の anchor は SMAL 関節の 3D root を直接再投影する別経路） |

### 再生成されたもの

- **`shots.json`** — 閾値 0.15 で再実行、**28 shots**。未検出だった 1.6/5.0/17.8/26.7/34.5/44.9 秒はすべて解消
- **`animal_control_targets.json`** — 修正後 `keypoints3d.json` から作り直し。**関節間の相対位置は不変（浮動小数点誤差の範囲）で、root の絶対位置だけがシフト**

### 未対応（bundle 側が明示的に開示）

**`source/pre_removal_stereo_video.mp4` は配布中の `h264fix` と同一ファイルで、D-001 修正前の depth から作られたステレオ映像のまま。** この sidecar は depth によって視差が変わるため本来は作り直すべきだが、StereoCrafter の拡散モデル推論を要する重い処理なので今回は見送られた。

**利用側への影響**: 通常モード再生（[docs/adr/0003](adr/0003-normal-mode-playback-video.md)）でこの動画を使うため、**通常モードの背景視差は修正前のまま**。置き換えモードの配置精度には影響しない。優先度が上がったら依頼する。

なお当方が bbox オーバーレイ検証（f676 / f1504 / f1882）に使ったのもこの動画だが、**左目の RGB 内容は同じはずなので bbox の目視判定には影響しない**（視差＝右目の合成のみが変わる）。

### 新 animal bundle の実測（2026-08-27）

`bundle_animal_shots_depthdriftfix_shotsfix.svb`（28 shots、D-001 修正後 depth）を旧 `bundle_animal.svb` と比較した。モデルは track 0 = `00_Dog` / track 1 = `39_Lynx` で共通。

#### 改善した: モデルのサイズ（ただし依然として小さい）

`sizeRatio`（モデルの投影高 ÷ bbox 高、見切れなしフレームでは 1.0 が正解）:

| bundle | track | 見切れなし median | p10〜p90 | 全フレーム median |
|---|---|---|---|---|
| 旧（15 shots） | 0 Dog | **0.592** | 0.317〜0.940 | 0.969 |
| **新（28 shots）** | 0 Dog | **0.851** | 0.539〜1.035 | 1.015 |
| 旧 | 1 Lynx | 該当 0f | — | 0.647 |
| 新 | 1 Lynx | 該当 0f | — | 0.656 |

**track 0 は 0.592 → 0.851 と大きく改善。** shot が 15 → 28 に増えてスケールのロック回数が 9 → 19 種に増えたことと、depth 修正の両方が効いたと考えられる（分離していない）。

**ただし依然として 15% 小さい。** ユーザーの実機観察「元動画よりも小さく配置されることが多い」は解消していない。track 1（Lynx）は 0.656 でほぼ変わらず。

#### 改善しなかった: anchor が bbox の外に出る問題

| bundle | 全体 | track 0 | track 1 |
|---|---|---|---|
| 旧 | 8.7% | 15.5% | 0.6% |
| **新** | **9.7%** | **17.4%** | 0.6% |

**わずかに悪化。** 29.9〜30.2 秒の破綻区間を直接見ると、**`anchorU` / `anchorV` が新旧で 1px も変わっていない**（(792,525) / (780,528) …）。変わったのは `z01` だけ。

```
        旧 z01: 0.570 → 0.784（遠ざかる）
        新 z01: 0.476 → 0.692（同じ傾き）
```

**D-001 の修正は深度を変えたが、anchor の画面上の位置は変えなかった。** bundle 側の「anchor は 3D root の再投影」という説明と合わせると、この区間では **root の 2D 位置そのものが体から外れており、深度の修正では直らない**ということになる。

なお shot 境界フレーム（29.90s、bbox がフルフレーム）では anchor が (437,366) → (801,325) と変わっている。破綻区間だけが不変。

#### 変わらなかったもの

見切れの分類は新旧で完全に同一（接しない 19.6% / 下端のみ 45.8% / 左右上あり 34.6%）。**bbox は再ビルドで変わっていない**ので当然。

#### 次の対象

ユーザーの観察（モデルが小さい）と実測（sizeRatio 0.851 / 0.656）が一致しており、**これは ⑧ `RefineDepthFromProjectedBones` が animal で動かないことの直接の帰結。** ⑧ は「投影高が bbox 高に一致する深度へ動かす」処理で、まさにこの症状を直すためのもの。human では boneRatio 1.082 → 0.998 に効いた。

**⑧ の animal 対応が次の作業。** 見切れフレームの扱いは、欠損率が大きいのが shot 0 と 8 に限られる（全体の約 4 割、うち確実なのは 26%）ことが分かっているので、全フレーム対象の複雑な仕組みは不要。

### 実装 2026-08-27: ⑧ を Generic リグ（Animal）でも動くようにした

`TryProjectBonesToEyeHeight` が `animator.isHuman` を要求していたため、Animal では ⑧・スケール再ロック・投影下端合わせが一度も動いていなかった。Humanoid 以外は `SkinnedMeshRenderer.bones` を総当たりする経路を追加した（`TryResolveNearestHumanBone` が既に採っている方式）。フラグ `projectGenericRigBones`（既定 true）で切り替えられる。

#### 結果: track 1（Lynx）で大幅改善

| track | 見切れ | 件数 | OFF median | ON median |
|---|---|---|---|---|
| 0 Dog | なし | 407 | 0.845 | **0.845（変化なし）** |
| 0 Dog | 下端のみ | 254 | 1.253 | 1.253 |
| 0 Dog | 左右上 | 476 | 0.994 | 0.994 |
| **1 Lynx** | 下端のみ | 716 | **0.641** | **1.026** |
| **1 Lynx** | 左右上 | 257 | **0.667** | **1.082** |

`[DEPTH8]` は 0 → **28459 件**。ただし**全て track 1** で、track 0 では 1 件も出ない。⑧ の移動量は median +118.7mm（88% が奥へ）、ratio median 1.3421。

#### track 0 で動かない原因: `00_Dog` に `SkinnedMeshRenderer` が無い

| プレハブ | `SkinnedMeshRenderer` | `m_Materials` |
|---|---|---|
| **`00_Dog`** | **0** | **22** |
| `39_Lynx` | 1 | 1 |
| `36_LabradorDog` | 1 | 1 |

**`00_Dog` はスキニングされていない剛体パーツの集合**（マテリアル 22 個）。`SkinnedMeshRenderer.bones` が存在しないので新しい経路でもボーンを解決できない。

キャプチャで Dog が**テクスチャなしのベージュ単色・関節の分割が見える**状態でレンダリングされていたのも、これが理由と考えられる。

**`00_Dog` はモデルとして不適切。** 映像の被写体はラブラドールなので、**`36_LabradorDog`（SkinnedMeshRenderer あり）に差し替えるべき。**

#### 未確認: 見切れフレームで 1.0 に寄せてよいか

⑧ の目標は「投影高 = bbox 高」だが、bbox は可視部分だけなので**見切れフレームでは 1.0 が正解ではない**。track 1 は見切れなしフレームが 0 枚なので、上記の 1.026 / 1.082 が適正かどうかはこの表では判定できない。

欠損率で見ると track 1 の shot 8（38.1〜47.8s）が median 42.4% と大きく、そこでは sizeRatio 1.7 前後が正解のはず。**ショット別の確認が必要。**

### `36_LabradorDog` に差し替えて再測定（2026-08-27）

`00_Dog` は `SkinnedMeshRenderer` を持たない剛体パーツの集合で ⑧ が動かせないため、映像の被写体と一致する `36_LabradorDog`（modelIndex 36）に差し替えた。`[DEPTH8]` は 28459 →  **65366 件**（両 track で発動）。

#### 全体: 目標どおりになった

| track | 見切れ | 件数 | OFF | **ON** | 期待値 |
|---|---|---|---|---|---|
| 0 Labrador | **なし** | 408 | **0.598** | **1.041** | **1.00** |
| 0 Labrador | 下端のみ | 254 | 1.055 | 0.993 | 1.0 以上 |
| 0 Labrador | 左右上 | 476 | 0.752 | 1.024 | 1.0 以上 |
| 1 Lynx | 下端のみ | 716 | 0.641 | 1.026 | 1.0 以上 |
| 1 Lynx | 左右上 | 257 | 0.667 | 1.082 | 1.0 以上 |

**見切れなしフレームで 0.598 → 1.041。** ユーザーの実機観察「元動画より小さく配置される」の主因はこれで解消したはず。

#### ショット別: 大きく見切れているショットだけ残る

欠損率から期待 `sizeRatio`（= 1 / (1 − 欠損率)）を計算して比較した。

| shot | 時間 | tr | 欠損率 | 期待 | ON | 判定 |
|---|---|---|---|---|---|---|
| **1** | **1.6〜5.0s** | 0 | **49.9%** | **2.00** | **0.995** | **小さい** |
| 3 | 8.6〜9.3s | 0 | 6.7% | 1.07 | 0.653 | 小さい |
| 5 | 9.7〜11.3s | 0 | 0.0% | 1.00 | 1.083 | OK |
| 7 | 14.2〜17.3s | 0 | 0.7% | 1.01 | 1.007 | OK |
| 9 | 17.8〜22.3s | 0 | 2.0% | 1.02 | 1.143 | OK |
| 10 | 22.3〜26.7s | 0 | 15.3% | 1.18 | 0.976 | 小さい |
| 12 | 29.9〜32.7s | 0 | 6.2% | 1.07 | 0.776 | 小さい |
| **20** | **38.2〜44.9s** | 1 | **43.6%** | **1.77** | **0.831** | **小さい** |
| 21 | 44.9〜47.8s | 1 | 19.1% | 1.24 | 1.020 | 小さい |
| 22 | 47.8〜53.8s | 1 | 0.0% | 1.00 | 1.171 | 大きい |
| 23 | 53.8〜57.2s | 1 | 2.2% | 1.02 | 0.992 | OK |
| 24 | 57.2〜60.3s | 1 | 16.6% | 1.20 | 1.097 | OK |
| 25 | 60.3〜65.2s | 1 | 1.8% | 1.02 | 1.145 | OK |
| 26 | 65.2〜68.8s | 1 | 0.0% | 1.00 | 1.126 | OK |
| 27 | 68.8〜70.7s | 1 | 20.2% | 1.25 | 1.011 | 小さい |

（shot 0 / 2 / 6 / 11 / 13 / 14 は左右も切れていて欠損率が測れず判定不能）

**予測どおり、大きく見切れているショットで ⑧ がモデルを縮めすぎている。**

- **shot 1**（1.6〜5.0s、犬）: 欠損率 49.9% なので本来 2.00 倍に写るべきだが 0.995 ── **半分の大きさ**
- **shot 20**（38.2〜44.9s、猫）: 欠損率 43.6% で期待 1.77 に対し 0.831 ── **半分以下**

⑧ の目標が「投影高 = bbox 高」で、bbox が可視部分だけなので当然の帰結。**残る欠陥はこの 2 ショットに集中している。**

#### 次: ⑧ の目標を「推定全高」にする

見切れフレームを除外するのではなく、**⑧ の目標を `bboxH` から「推定全高」に変える**のが筋。推定全高は既に手法が確立している（切れていない辺で較正、見切れなしフレームで誤差 median 1.6%）。

```
推定全高 = keypoint 縦スパン × (bbox幅 ÷ keypoint 横スパン) × 1.034
```

見切れていないフレームでは推定全高 ≒ `bboxH` になるので、**現在うまくいっているショットの挙動を変えずに、見切れショットだけ直せる。** 左右も切れているフレームでは較正できないので、その場合は従来どおり `bboxH` を使う。

### 実装 2026-08-27: ⑧ の目標を「見切れを補った推定全高」にした

`RefineDepthFromProjectedBones` が合わせる相手を `bboxH` から `ResolveUnclippedTargetHeight(obj, bboxH)` に変更した。フラグ `extendTargetHeightForClippedBBox`（既定 true）。

```
推定全高 = keypoint 縦スパン × (bbox幅 ÷ keypoint 横スパン) × 1.034
```

発動条件は「上端か下端が切れている」かつ「左右は切れていない」（左右が切れていると px/m を較正できない）。推定が `bboxH` 以下なら較正誤差とみなして `bboxH` を使う。

#### 実装中に踏んだ罠: 可視フラグで絞ってはいけない

最初 `jointsVis[i] == 0` を除外して実装したが、**見切れたぶんのジョイントにはまさに不可視フラグが立つ**ので、可視だけで測ると「見えている範囲」しか出ず外挿にならない。実測でも shot 20（欠損率 43.6%）で倍率が **1.14** にしかならず、期待の 1.77 に届かなかった。

**Python の試算は全ジョイントで測っていた**（較正係数 1.034 もその前提）。片方だけ可視で絞ったため前提がずれた。全ジョイントを使うよう修正して解決。

#### 外挿上限の決定: 1.6

上限なしだと外挿が効きすぎるため `maxClippedHeightExtrapolation` を設けた。1.6 / 2.0 / 3.0 で実測:

| 設定 | 全体誤差 median | p90 | 見切れなし | 欠損 45%〜（期待 2.00） |
|---|---|---|---|---|
| **OFF（従来）** | **15.9%** | **52.6%** | 1.041 | **0.997** |
| **cap 1.6** | **11.1%** | 29.5% | 1.041 | **1.814** |
| cap 2.0 | 12.5% | **26.2%** | 1.041 | 2.432 |
| cap 3.0 | 12.5% | 28.5% | 1.040 | 2.587 |

**1.6 を採用。** median が最良で、欠損率 45% 超の帯でも期待値に最も近い。p90 は 2.0 がわずかに良いが差は 3.3pt。

#### 問題だった 2 ショットの改善

| shot | 時間 | 期待 | OFF | **cap 1.6** |
|---|---|---|---|---|
| 1 | 1.6〜5.0s（犬） | 2.00 | **0.99** | **1.81** |
| 20 | 38.2〜44.9s（猫） | 1.77 | **0.83** | **1.28** |

**見切れていないフレームは 1.041 のまま変化なし**（回帰していない）。

#### 指標の限界（明記しておく）

「期待値」は欠損率から `1/(1−欠損率)` で計算しているが、この欠損率自体が同じ推定式から出ている。また `[PLACE]` の `sizeRatio` は**メッシュの投影**で、⑧ が合わせているのは**ボーンの投影**なので、両者には系統的なオフセットがある。**したがって期待値との一致度は相対比較にのみ使える。** 絶対的な正しさは実機での目視確認が要る。

### 追加 2026-08-27: 剛体パーツ構成のモデルにも ⑧ を効かせた

`00_Dog` で ⑧ が動かなかったのは **skinning の有無ではなく、実装が `SkinnedMeshRenderer.bones` からしかボーンを取っていなかった**ため。

`00_Dog` の構成:

| コンポーネント | 数 |
|---|---|
| GameObject / Transform | **54** |
| MeshRenderer | **22** |
| MeshFilter | 22 |
| **Animator** | **1** |
| SkinnedMeshRenderer | **0** |

**Animator と 54 個のボーン階層を持つ、剛体パーツ 22 個で構成されたモデル。** 姿勢適用（⑤）はボーン階層で動くのでモデル自体は動いている。投影高を測るのに skinning は不要。

`ResolveProjectionBones` に、skinned ボーンが 0 件なら **Renderer を持つ Transform の位置**を使うフォールバックを追加した。

#### 結果: `00_Dog` でも効く

| 設定 | 見切れなし（期待 1.00） | 全体誤差 med | p90 | 欠損 45%〜（期待 2.00） |
|---|---|---|---|---|
| `00_Dog` 従来（⑧ なし） | **0.598** | 39.0% | 63.0% | 1.051 |
| `36_LabradorDog` 対応後 | 1.041 | **8.9%** | **31.6%** | 1.814 |
| **`00_Dog` 剛体対応後** | **1.053** | 15.7% | 33.0% | **2.022** |

**shot 1（1.6〜5.0s、欠損率 49.9%、期待 2.00）で 1.051 → 2.023。**

`36_LabradorDog` の方が全体誤差は小さい（8.9% 対 15.7%）が、これはモデルの体型差。**`00_Dog` をモデル一覧から外す必要はなくなった。**

なお `00_Dog` のマテリアルが当たっていない件（`m_Materials` は 22 個あるがベージュ単色で描画される）は**追わない方針**（ユーザー判断、2026-08-27）。

### 2026-08-27: 32 秒付近でモデルが小さい件 — ⑧ ではなくスケールロックと popout レンジの限界

ユーザーから「32 秒くらいの shot でサイズが動画より小さい」という指摘。**正確だった。**

`shot 12`（29.9〜32.7s、犬）は**見切れていない**（欠損率 6.2%）のに `sizeRatio` が 0.816、秒別では 32 秒台で **0.416**（上端ずれ +165px）。

#### 原因: shot 内で被写体の見かけが 3 倍になる

| 秒 | bboxH | projH | sizeRatio | depth | **scale** |
|---|---|---|---|---|---|
| 29.93 | **110** | 124.9 | 1.136 | 0.771 | **0.01530** |
| 31.53 | 149 | 150.3 | 1.009 | 0.701 | **0.01530** |
| 32.13 | 284 | 117.9 | 0.415 | 0.821 | **0.01530** |
| 32.53 | **336** | 120.9 | 0.360 | 0.772 | **0.01530** |

**2.8 秒で `bboxH` が 110 → 336px（3.26 倍）。犬が急速にカメラへ近づいている。** 一方スケールは shot 先頭でロックされたまま変わらない（0.01530）。

⑧ は**深度しか動かせない**。`projH` を 336px にするには深度を 0.278m まで詰める必要があるが、**popout レンジは 0.65〜1.0m** なので物理的に届かない（`MinDistanceFromHeadMeters` 0.25 も下回る）。

**これは ⑧ の欠陥ではなく、「スケールを shot 単位でロックする」設計と popout レンジの幅の組み合わせによる限界。**

#### どのくらい一般的か

shot 内での bbox 高の変動（p90 ÷ p10）:

| bundle | 1.8 倍以上変わる shot | 該当フレーム | 全 shot の median |
|---|---|---|---|
| **animal** | **2 / 21** | **8.6%** | 1.07x |
| human | 1 / 2 | 50.1% | 1.89x |

animal で該当するのは 2 ショットだけ:

| shot | 時間 | 倍率 |
|---|---|---|
| **12** | **29.9〜32.7s**（犬） | **3.26x** |
| 7 | 14.2〜17.3s（犬） | 2.11x |

**human は 1 shot しかないクリップなので 2.37 倍の変動を 1 つのロックで賄っている**（該当フレーム 50.1%）。human で同じ問題が出ていない理由は、⑧ が深度で吸収できる範囲に収まっているためと考えられる（要確認）。

#### 対処の選択肢（未実施）

| 案 | 内容 | 懸念 |
|---|---|---|
| A | shot 内でも見かけが大きく変わったらスケールを測り直す | 「shot 内でスケールを変えない」という現行方針の変更。フレーム間でモデルが伸縮して見える可能性 |
| B | animal では popout レンジを広げる | animal には Else が無いので human で問題になった「人−球の相対距離」の制約が無い。ただし depth の絶対精度が上がるわけではない |
| C | 何もしない | 該当は 8.6% のフレーム（2 ショット） |

**A が筋に見えるが、`shot_boundary_policy.unity_guidance`（"Do not interpolate or spring position/scale across a shot boundary"）は shot をまたぐ補間を禁じているだけで、shot 内での再測定は禁じていない。** ただし現行実装が「shot 先頭で 1 回ロック」なのは、bbox の一時的な破綻でスケールが焼き付くのを避けるためでもある（`ClampRatioPreservingOtherOrder` 周辺の経緯）。慎重に扱う必要がある。

### 続報 2026-08-27: 32 秒の原因は ⑧ の ratio ガードだった。popout レンジではない

前項で「popout レンジの限界」と書いたが**誤り**。実測して切り分けた。

#### popout レンジを広げても改善しない

| popout | 全体誤差med | 見切れなし | shot12 | クランプ |
|---|---|---|---|---|
| **0.35** | **14.7%** | 1.053 | 0.816 | 7.1% |
| 0.55 | 17.4% | 1.061 | 0.753 | 3.5% |
| 0.70 | 16.8% | 1.035 | 0.664 | 8.3% |
| 0.85 | 14.9% | 1.026 | **0.415** | **20.1%** |

**広げるほど悪化する。** 「深度に余裕を与えれば ⑧ が届く」という読みは外れた。

#### 真因: `MinProjectedBoneRatioForScaleRefine = 0.4` のガード

⑧ には「検出が破綻しているフレームでは動かさない」ためのガードがある。

```csharp
if (ratio < MinProjectedBoneRatioForScaleRefine || ratio > MaxProjectedBoneRatioForScaleRefine) return false;
```

**32 秒台の `sizeRatio` が 0.416 で、下限 0.4 に張り付いていた。** つまり ⑧ は「これは破綻フレームだ」と判断して**最も補正が要る場面で何もしていなかった**。

popout を広げても改善しなかったのはこのため ── ⑧ が止まっているので深度の余裕を使えない。

#### 対処: ⑧ 側の下限だけ独立に下げる（`depthRefineMinRatio = 0.2`）

スケール再ロック側の `MinProjectedBoneRatioForScaleRefine` は据え置き（誤った基準を shot 中ずっと焼き付けるリスクがあるため）。⑧ は毎フレーム再計算するので同じリスクは無い。

| minRatio | 全体誤差med | p90 | 見切れなし | **32秒台** | 揺れ p90 |
|---|---|---|---|---|---|
| 0.40（従来） | 14.7% | 30.2% | 1.054 | **0.416** | 9.0mm |
| **0.20** | **14.7%** | **30.2%** | 1.056 | **0.692** | **9.0mm** |
| 0.10 | 14.7% | 30.2% | 1.054 | 0.692 | 9.0mm |

**32 秒台だけが直り、他は完全に同値。** 0.1 まで下げても 0.2 と変わらないので 0.2 を採用。

#### popout との組み合わせも試したが不採用

| 設定 | 全体誤差med | p90 | 32秒台 | 揺れ |
|---|---|---|---|---|
| **0.2 / popout 0.35** | **14.7%** | **30.2%** | 0.692 | **9.0mm** |
| 0.2 / popout 0.50 | 17.0% | 44.0% | 0.730 | 10.0mm |
| 0.2 / popout 0.65 | 15.0% | 43.5% | 0.767 | 12.0mm |

32 秒台はわずかに良くなるが**全体 p90 が 30.2% → 43.5% と悪化**し揺れも増える。**popout は 0.35 のまま。**

#### human で回帰がないことを確認

`minRatio` は共通設定なので human でも A/B した。

| 指標 | 0.40 | 0.20 |
|---|---|---|
| boneRatio median | 0.9820 | **0.9820** |
| 配置深度 median | 0.9310 | 0.9320 |
| 球表面→最近傍ボーン | 22.7mm | 22.5mm |
| 球が手前の割合 | 58.8% | 57.7% |
| 隙間が球の直径以内 | 88.3% | 88.5% |
| 姿勢一致 RMS median | 5.74% | 5.76% |

**すべて同値。** 既定を 0.2 にした。

#### 残る差（未解明）

32 秒台は 0.692 で、期待の 1.09 にはまだ届かない。⑧ が発動するようになった後も届かない理由は未特定。候補は ⑧ の比率平滑化（1.2s）が bboxH の急変（1 秒で 3 倍）に追随できていないこと。**shot 12 は 2.8 秒しかなく、shot 境界で平滑化がリセットされる**ため、時定数が長すぎる可能性がある。

### なぜ popout レンジを広げても改善しないのか（2026-08-27、原理の説明）

「距離に正しく置けたら必ず良くなるはず」という問いに答える。**popout レンジは「距離」ではなく「圧縮窓」で、広げても投影サイズは変わらない。**

#### 実測: 投影サイズは popout に対して不変

| popout | scale | depth | **projH** | bboxH | **sizeRatio** |
|---|---|---|---|---|---|
| 0.35 | 0.06600 | 0.739 | **546.0** | 501 | **1.156** |
| 0.55 | 0.04890 | 0.577 | **549.3** | 501 | **1.150** |
| 0.70 | 0.03790 | 0.455 | **537.6** | 501 | **1.149** |
| 0.85 | 0.02570 | 0.343 | **537.4** | 501 | **1.153** |

**`projH` も `sizeRatio` もほぼ完全に不変。** scale が 0.066 → 0.0257（×0.39）、depth が 0.739 → 0.343（×0.46）と**両方が同じ割合で縮む**ため。

#### 原理: スケールが深度に正比例している

`TrackModelPlacement.ResolveTargetHeightMeters`:

```csharp
return (2f * bboxHeightPixels / eyeHeightPixels) * (depthMeters / fy);
```

**目標の世界高が深度に正比例**する。透視投影では 投影サイズ = 世界サイズ × f ÷ 深度 なので、

```
投影サイズ = (bboxH × depth / fy) × f / depth = bboxH に依存し depth は消える
```

**深度が約分される。** これは「どの距離に置いても bbox どおりに写る」という設計で、意図どおりの挙動。popout レンジを広げるとシーン全体が一様に縮んで近づくだけで、見え方は変わらない。

#### では何が悪化するのか: クランプだけ

| popout | 深度 min | **下限 0.25 でクランプ** | 画面際 |
|---|---|---|---|
| 0.35 | 0.414 | **0.0%** | 7.1% |
| 0.55 | 0.324 | 0.0% | 3.5% |
| 0.70 | 0.200 | **6.6%** | 1.7% |
| 0.85 | 0.136 | **20.0%** | 0.1% |

広げるとシーンが近づくので `MinDistanceFromHeadMeters`（0.25m）に当たる。**クランプされたフレームは深度も投影サイズも狂う。** 0.85 で 20% がこれに該当し、全体誤差が悪化した。

#### 結論

**「正しい距離に置く」ためのつまみは popout レンジではない。** レンジはシーン全体の一様なスケールにしか効かず、投影サイズの正しさには無関係。

投影サイズを正すには**スケールを固定したまま深度だけ動かす**必要があり、それをやるのが ⑧。だから 32 秒の件は ⑧ のガードを緩めることで直り、popout を広げても直らなかった。

**逆に言えば popout レンジが効くのは「立体感の強さ」だけ**（同じ見かけで手前に出すか奥に置くか）。human で人−球の相対距離が変わったのは、⑨ が `meta.bin` の深度差をレンジ内の距離に変換しているため。**Else が絡まない animal では、popout レンジは見え方にほぼ影響しない。**

### 最終構成での総括（2026-08-27）

track 0 = `36_LabradorDog`、track 1 = `39_Lynx`、bundle = `bundle_animal_shots_depthdriftfix_shotsfix.svb`。

| | 見切れなし（1.00） | 全体誤差 med | p90 | 32秒台（1.09） | 欠損45%〜（2.00） |
|---|---|---|---|---|---|
| **最初（⑧ なし・`00_Dog`）** | **0.598** | 39.7% | 64.4% | **0.263** | 1.051 |
| **最終（全対応）** | **1.046** | **11.1%** | **29.1%** | **0.616** | **1.814** |

#### ショット別（判定は期待値 ±20%）

| 判定 | ショット |
|---|---|
| **OK（11 / 15）** | 1 / 5 / 7 / 9 / 10 / 21 / 22 / 23 / 24 / 26 / 27 |
| **小さい（3）** | **3**（8.6〜9.3s）、**12**（29.9〜32.7s）、**20**（38.2〜44.9s） |
| 判定不能（6） | 0 / 2 / 6 / 11 / 13 / 14（左右も切れていて欠損率が測れない） |

#### 残っている 3 ショット

| shot | 時間 | 欠損率 | 期待 | 実際 | 特徴 |
|---|---|---|---|---|---|
| 3 | 8.6〜9.3s | 6.7% | 1.07 | 0.654 | **0.7 秒しかない** |
| 12 | 29.9〜32.7s | 4.1% | 1.04 | 0.787 | **2.8 秒で bboxH が 3.26 倍** |
| 20 | 38.2〜44.9s | 43.6% | 1.77 | 1.277 | 大きく見切れている |

**shot 3 と 12 は「短い shot」または「shot 内で見かけが急変」という共通点がある。** ⑧ の比率平滑化は `projectedDepthSmoothingSeconds = 1.2` 秒で、`ResetPerShotTrackState` が shot 境界でリセットする。**0.7 秒の shot では平滑化が収束する前に終わり、2.8 秒の shot では 1 秒で 3 倍になる変化に追随できない。**

**次に試すなら ⑧ の時定数**。ただし 2026-08-20 に「⑧ の深度の揺れ 420mm → 22mm」を得たのがこの平滑化なので、短くすると揺れが戻る可能性がある。**shot の長さや bbox の変化速度に応じて時定数を変える**のが筋かもしれない。

shot 20 は見切れが大きい区間で、外挿上限 1.6 が効いている（期待 1.77 > 1.6）。上限を上げると欠損率 45% 超の帯で行き過ぎるので現状維持。

### 訂正と対処 2026-08-27: ⑧ は popout レンジに縛られていない。真の律速は平滑化だった

「popout は前後の範囲。前後を動かせばサイズは合うはず」という指摘を受けてコードを読み直した。**当方の説明が誤っていた。**

#### 訂正: ⑧ のクランプは popout レンジではない

```csharp
float targetZ = Mathf.Clamp(ratioZ, Mathf.Max(0.001f, MinDistanceFromHeadMeters), screenDist - 0.0001f);
```

**⑧ は `[0.25, 1.0]` の範囲で動ける。** popout レンジ `[0.65, 1.0]` に縛られていない。実測でも深度 min が 0.414m と popout 下限を下回っている。32 秒台に必要な 0.278m も届く範囲内。

「popout レンジの限界」と説明したのは誤り。**ユーザーの直感（前後を動かせばサイズは合う）が正しい。**

#### 真の律速: 一次遅れフィルタがランプ状の変化に追いつかない

`projectedDepthSmoothingSeconds = 1.2` 秒。shot 12 は最後の 1 秒で bboxH が 3 倍になるので、1 − exp(−1/1.2) = **57% しか進めない**。shot 3 は shot 自体が 0.7 秒しかない。

一律に短くすると別の代償が出る（animal 実測）:

| tau | 全体誤差med | 見切れなし | 32秒台 | shot3 | 揺れp90 |
|---|---|---|---|---|---|
| 1.2（従来） | 11.2% | **1.041** | **0.641** | **0.653** | **8.0mm** |
| 0.5 | 12.9% | 1.091 | 0.857 | 0.729 | 11.0mm |
| 0.25 | 13.1% | 1.133 | 0.968 | 0.843 | 14.0mm |
| 0.1 | 13.1% | **1.171** | 1.014 | 1.024 | **18.0mm** |

**問題ショットは直るが、正常フレームが行き過ぎ（1.041 → 1.171）、揺れも 2.25 倍。**

#### 対処: 誤差が大きいときだけ追従を速める

一次遅れはノイズに強いがランプに遅れる。そこで「小さいズレ = ノイズなので鈍く、大きいズレ = 実際の変化なので速く」する。

```csharp
float relativeError = Mathf.Abs(ratio - previous) / Mathf.Max(0.05f, Mathf.Abs(previous));
float boost = Mathf.Clamp01((relativeError - depthRefineFastTrackLow) / (depthRefineFastTrackHigh - depthRefineFastTrackLow));
alpha = Mathf.Clamp01(alpha + (1f - alpha) * boost);
```

| 設定 | 全体誤差med | p90 | 見切れなし | 32秒台 | shot3 | 揺れp90 |
|---|---|---|---|---|---|---|
| 無効（従来） | 11.2% | 29.5% | **1.041** | 0.641 | 0.653 | **8.0mm** |
| lo 0.30 | 11.2% | 28.0% | 1.050 | 0.894 | 0.812 | **7.0mm** |
| **lo 0.15** | **10.1%** | **26.9%** | 1.113 | **1.016** | **0.988** | 9.0mm |
| lo 0.08 | 10.9% | 27.0% | 1.135 | 1.016 | 1.071 | 13.0mm |
| tau 0.25 一律 | 13.1% | 27.7% | 1.133 | 0.968 | 0.843 | 14.0mm |

**`lo = 0.15` を採用。** 一律 tau 短縮（13.1% / 14mm）に対し、**全体誤差 10.1% / 揺れ 9mm で問題ショットも直る。**

#### human で回帰なし

| 指標 | 従来 | lo 0.15 |
|---|---|---|
| boneRatio median | 0.9780 | 0.9710 |
| sizeRatio median | 1.1150 | 1.1090 |
| 球表面→最近傍ボーン | 14.6mm | 16.1mm |
| 隙間が球の直径以内 | 89.9% | 89.4% |
| **姿勢一致 RMS median** | **5.30%** | **5.30%** |
| **深度 1f 変化 p90** | **6.0mm** | **6.0mm** |

### 2026-08-27: 6〜8 秒 / 26〜29 秒の「もっと大きく、もっと下」について

ユーザーから「6〜8 秒と 26〜29 秒はもっと大きいし下にいるはず、でも調べるのは無理では」との指摘。**片方は調べられて直せる。もう片方は現状の材料では測れない。**

該当は shot 2（5.0〜8.6s）と shot 11（26.7〜29.9s）。どちらも「判定不能」に分類していたショット。

#### 見切れ方

| shot | 時間 | 左 | 右 | **上** | 下 |
|---|---|---|---|---|---|
| 2 | 5.0〜8.6s | 100% | 0% | **0%** | 100% |
| 11 | 26.7〜29.9s | 100% | 100% | **0%** | 100% |

**どちらも上端だけが有効。** shot 11 は bbox が画面幅いっぱい（1280px）で横方向の情報がまったく無い。

#### 「下にいるべき」は直せる

⑦ `AlignProjectedModelBottomToBBox` は**モデルの投影下端を bbox の下端に合わせる**。下端が切れているフレームでは bbox の下端＝画面の端なので、**本来画面外にあるべき足を画面の端に持ち上げている。** 実測でも `bottomDelta` が一貫して −1.0px 前後で、忠実に合わせてしまっている。

**上端は有効なので、下端が切れていて上端が有効なフレームでは上端合わせに切り替えればよい。**

| bundle | 下端が切れている | **下端切れ・上端有効** | 上下とも切れ |
|---|---|---|---|
| **animal** | 73.4% | **64.6%** | 8.9% |
| human | 1.5% | 1.5% | 0.0% |

**animal の 64.6% が切り替え可能。** human は 1.5% しか該当しないので影響は小さい。

#### 「もっと大きい」は shot によって測れない

`sizeRatio` の正解を出すには「被写体の本来の高さ」が要る。推定式は幅で px/m を較正するので、**左右が切れていると使えない**。

| shot | 上端ずれ | 下端ずれ | sizeRatio |
|---|---|---|---|
| 2 | **+76.7px** | −8.2px | 0.845 |
| 11 | −8.6px | +0.3px | 1.018 |

- **shot 2**: 上端が 76.7px 内側 ＝ モデルが上下とも足りていない。左端は切れているが**右端が有効**なので、右端基準で幅を較正できる可能性がある（未実装）
- **shot 11**: bbox が画面いっぱいで、**幅も高さも被写体の実寸を表していない**。この shot でサイズの正解を出す材料は bundle 内に無い

**shot 11 のようなケースは「測れない」と認めるのが正しい。** 無理に推定すると根拠のない値を入れることになる。

#### 次にやること

**⑦ の上端合わせ**を実装する。下端が切れていて上端が有効なフレームで、下端ではなく上端に合わせる。animal の 64.6% に効き、human はほぼ影響を受けない。

サイズについては shot 2 の「右端が有効なら右端基準で較正」を検討する余地があるが、まず位置を直してから測り直す（位置が変わればサイズの見え方の評価も変わる）。

### 「画面外の下にはみ出させる」は可能。ただしマスクが要る（2026-08-27）

ユーザーの意図は「6〜8 秒と 26〜29 秒では犬の下半身が画面のもっと下（画面外）にあるので、そう置けないか」。**位置としては可能**だが、前提が 1 つ欠けている。

#### 現状: はみ出した部分は見えてしまう

| 量 | 値 |
|---|---|
| 動画の視野 | 水平 **70.0°** / 垂直 **38.6°**（1280x640、`fovx_deg`=70） |
| スクリーンが占める範囲 | 視野の上下 **±19.3°** |
| Quest 3 の視野 | およそ 水平 110° / 垂直 96° |
| **スクリーンの下に見えている空間** | **約 29°** |

**モデルをスクリーン下端より下に出すと、その部分が動画の外に見える。** 犬の脚だけが黒い空間に浮いて見えることになる。

シェーダを確認したところ `PerEyeStereoVideoURP.shader` にも `PerEyeColor.shader` にも **`Stencil` も `clip()` も無い**。モデルをスクリーン領域に制限する仕組みは存在しない。

#### したがって順序は「マスク → 上端合わせ」

1. **スクリーンの矩形の外にモデルを描かないマスクを入れる**
2. そのうえで ⑦ を上端合わせに切り替え、下端が切れているフレームではモデルを画面外まで伸ばす

1 を飛ばして 2 をやると、いまより悪化する可能性が高い（脚が宙に浮く）。

#### マスクの実装案

| 案 | 内容 | 難点 |
|---|---|---|
| A | モデルのマテリアルに Stencil テストを足し、スクリーン quad で Stencil を書く | **モデルは外部プレハブ**でマテリアルが多様（`00_Dog` は 22 個）。全部に手を入れるのは非現実的 |
| B | スクリーンの外周を囲む**黒い枠（マット）**を、モデルより手前に置く | マテリアル不要。枠は 4 枚の quad。`MinDistanceFromHeadMeters`（0.25m）より手前に置けば必ずモデルを隠せる |
| C | モデルを RenderTexture に描いてスクリーンと合成 | 描画パスの大改造 |

**B が最も現実的。** モデルは 0.25m 以遠にしか置かれないので、0.2m 付近に「スクリーンの見込み角と同じ穴を開けた黒い枠」を置けば、はみ出した部分だけが隠れる。

ただし **B は「動画の外は真っ黒」という見た目を固定する**ので、将来スクリーン外に何かを表示したくなったときに邪魔になる。現状スクリーン以外に描くものは無いので当面は問題ない。

**未検証**: 実機（Quest）でスクリーンが視野のどれだけを占めるかは `screenDistanceMeters` とスクリーンの実サイズに依存する。上記は動画の FOV から計算した値で、シーンの実際のスクリーン設定は確認していない。

### 描画構成の確認（2026-08-27）: passthrough underlay なのでマスク案 B は不可

「スクリーンの外側とは？XR だから周囲が見える」という指摘を受けて描画構成を確認した。**指摘が正しく、黒い枠案（B）は成立しない。**

#### 確認できた構成

| 要素 | 設定 | 確認方法 |
|---|---|---|
| **Passthrough** | `OVRPassthroughLayer`：`projectionSurfaceType: 0`（Reconstructed）、**`overlayType: 1`（Underlay）**、`m_Enabled: 1` | シーンを GUID `555725d48e9051a4bb6b8d45178c2fdd` で検索 |
| OVRManager | `isInsightPassthroughEnabled: 1` | シーンの prefab override |
| カメラ | OVRCameraRig 由来（シーンでは stripped） | — |
| **動画スクリーン** | 左右目に 1 枚ずつ。`screenDistanceMeters` の距離に、`fovx_deg` から算出したサイズで配置（`fitScreenToFov`） | `StreamingStereoVideoPlayer.Screens.cs` |
| スクリーンのシェーダ | `Queue=Background`、**`ZWrite Off`、`ZTest Always`** | `PerEyeStereoVideoURP.shader` |
| モデル | スクリーンより手前（0.25〜1.0m）に配置 | ⑧ の clamp |
| **マスク** | **無い**（`Stencil` も `clip()` も未使用） | 両シェーダを grep |

**Underlay なので、アプリが何も描かない場所には現実の部屋が見える。** スクリーンの外側は黒ではなく passthrough。

#### 案 B（黒い枠）は却下

モデルのはみ出しを隠すために黒い枠を置くと、**現実の視界をアプリが黒く塗り潰す**ことになる。passthrough の意味が失われる。

#### 残る選択肢

| 案 | 内容 | 評価 |
|---|---|---|
| A | モデルのマテリアルに Stencil テストを足す | 実行時に `TrackInstanceFactory` がインスタンス化するので、**そこでマテリアルを複製して Stencil を設定する**なら可能。ただし URP のシェーダに Stencil プロパティが露出しているか要確認 |
| C | モデルを RenderTexture に描いて合成 | passthrough underlay との合成が複雑 |
| **D** | **はみ出しを許容する**（マスクしない） | 実装ゼロ。ただし犬の脚が現実の部屋に重なって見える |
| **E** | **現状維持**（⑦ が切れた bbox 下端に合わせる） | モデルが小さく・高く出るが、常に動画の枠内に収まる |

**D と E は「どちらの破綻を選ぶか」という設計判断**であり、実装の問題ではない。ユーザーが実機で見てどちらが良いかを決める必要がある。

**未確認**: URP の Lit シェーダに Stencil を後から設定できるか（`Material.SetInt("_StencilRef", ...)` 等が効くか）。案 A の可否はここに依存する。

### 実装 2026-08-27: 下端が切れているフレームは上端で合わせる

ユーザーの意図は「映像で犬の下半身が画面外に切れているなら、モデルも同じように配置し、下半身を画面外へ出したい」。**はみ出しは許容**（passthrough に重なってよい）と確認済み。

⑦ `FitDisplayedModelToBBox` は投影下端を bbox 下端に合わせていた。下端が切れているフレームでは bbox 下端＝画面の端なので、**本来画面外にあるべき下半身を画面内へ持ち上げていた。**

```csharp
bool clippedBottom = obj.bboxY + obj.bboxH >= manifest.eye_h;
bool clippedTop    = obj.bboxY <= 0;
if (alignTopWhenBottomClipped && clippedBottom && !clippedTop)
{
    // 上端は被写体の実際の上端。ここに合わせれば下半身は自然に画面外へ出る
    AlignProjectedModelBottomToBBox(instance.transform, screen, boneTopV, depthMeters, obj.bboxY);
    return;
}
```

#### 結果

| 種別 | 件数 | OFF 上端ずれ | OFF 下端ずれ | **ON 上端ずれ** | **ON 下端ずれ** |
|---|---|---|---|---|---|
| 切れなし | 494 | −20.1px | +11.9px | −18.4px | +11.8px |
| **下端切れ** | **1363** | **−33.1px** | +43.4px | **−6.5px** | **+25.5px** |
| 上下切れ | 188 | −45.1px | +29.6px | −46.3px | +29.6px |
| 上端切れ | 69 | −165.7px | +43.1px | −165.6px | +43.1px |

**下端切れフレームで上端ずれが −33.1 → −6.5px。** 切れなし・上下切れ・上端切れは変化なし（想定どおり）。

#### 指摘のあった 2 ショット

| shot | 時間 | | 上端ずれ | 下端ずれ | sizeRatio |
|---|---|---|---|---|---|
| **2** | 5.0〜8.6s | OFF | **+74.0px** | −8.0px | 0.852 |
| | | **ON** | **+2.0px** | **−45.2px** | 0.907 |
| 11 | 26.7〜29.9s | OFF | −8.6px | +0.3px | 1.018 |
| | | **ON** | −42.8px | **−28.3px** | 1.041 |

**shot 2 は狙いどおり**（頭が一致し、下半身が 45px ぶん画面外へ）。shot 11 は上端ずれが −42.8px と上に出すぎているが、この shot は bbox が画面幅いっぱい（1280px）で被写体の実寸が分からないため、評価も難しい。

#### 残る課題: 上下とも切れているフレーム（8.9%）

上下とも切れていると bbox 高が被写体の高さをまったく表さないため、現状は下端合わせにフォールバックしている（上端ずれ −45.1px のまま改善しない）。

対処案:

| 案 | 内容 | 見込み |
|---|---|---|
| 1 | 左右のどちらかが有効ならそこで合わせる | shot 11 は左右とも切れており効かない |
| 2 | ⑦ をスキップして anchor のまま置く | anchor は 3D root の再投影だが、10.4% が bbox 外に出るという実測があり信頼性に難 |
| **3** | **上下とも切れる直前のフレームでのオフセットを保持** | 被写体が画面を外れていく場面は連続的なので妥当。shot 境界でリセットする現行の考え方と整合 |

**案 3 が有力。** ただし 8.9% がどのショットに集中しているか未確認。

### バグと修正 2026-08-27: 上端合わせにボーンの最上点を使ってはいけない

上端合わせの初版で **41〜43 秒の猫がモデルごと画面外（下）へ飛んだ**。

| 42.00 秒 | 値 |
|---|---|
| bbox 上端 | 2 |
| **ボーン最上点の投影 V** | **−721**（画面のはるか上） |
| 結果 | 2 に合わせようとして **723px 下へ移動** → 完全にフレームアウト |

**原因**: `SkinnedMeshRenderer.bones` には armature の根などメッシュから離れたノードが含まれる。元の実装が「ボーン最下点」を使えていたのは **armature の根がたまたま足元付近にある**ためで、上端には同じ前提が成り立たない。

これは human で見つけた「`transform.position` が体の外にある」問題と同じ根 ── **ボーン階層にはメッシュと無関係なノードが混ざる。**

**修正**: 上端は**メッシュの投影上端**（`projectedTopV`）を使う。bbox の上端は被写体の見た目の上端（毛・耳）なので、対応するのは骨ではなくメッシュ。

#### 修正後の結果

| 種別 | 件数 | OFF 上端ずれ | **ON 上端ずれ** | OFF 下端ずれ | ON 下端ずれ |
|---|---|---|---|---|---|
| 切れなし | 494 | −20.2px | −18.4px | +11.9px | +11.7px |
| **下端切れ** | **1361** | **−33.3px** | **+0.0px** | +43.4px | +73.4px |
| 上下切れ | 188 | −45.2px | −46.5px | +29.6px | +29.6px |
| 上端切れ | 69 | −165.7px | −165.5px | +43.1px | +43.1px |

41〜43 秒（猫）:

| 秒 | bbox top | OFF proj top | **ON proj top** | **ON proj bot** |
|---|---|---|---|---|
| 42.00 | 2 | −61.9 | **2.0** | **982.6** |
| 42.67 | 2 | −83.2 | **2.0** | **1012.6** |

**上端が bbox と一致し、下端は画面（V=640）の外へ。** 完全に画面外へ出たフレームは **0 件**（OFF / ON とも）。

指摘の 2 ショット: shot 2 が **+73.7 → 0.0px**、shot 11 が **−8.6 → 0.0px**。

#### 教訓

**モデルの「上端・下端」をボーンで測るのは危険。** armature の根や補助ノードがメッシュの外にあるモデルが実在する（`16_Male_Eric` の root は体の外、`39_Lynx` の最上ボーンは V=−721）。見た目の範囲を測るならメッシュ（Renderer bounds）を使う。

ボーンが適切なのは「関節の位置そのもの」を扱うとき（姿勢比較、⑧ の投影高など）に限る。

### 横方向の実測（2026-08-27）: 縦とは別の問題がある

「左右もやるか」という提案を受けて、**先に測った**。⑦ は縦しか動かしていない（`AlignProjectedModelBottomToBBox` は `camY` のみ）ので、横位置は ① の `anchorU` で決まる。「左右が切れているから横がずれる」という因果は縦とは違って自明ではないため、診断ログ `[HPOS]` を追加して確認した。

#### 結果: 切れていなくても横がずれている

| 種別 | 件数 | 中心ずれ（+ は右） | p10〜p90 | 左端ずれ | 右端ずれ |
|---|---|---|---|---|---|
| **切れなし** | 1578 | **+28.9px** | **−190.0〜+506.9** | −125.1px | +101.0px |
| 左が切れ | 358 | −60.4px | −142.0〜+376.9 | −9.1px | −64.0px |
| 右が切れ | 87 | −72.9px | −198.8〜+73.9 | +45.6px | −190.4px |
| 左右とも切れ | 97 | +68.9px | +47.7〜+111.8 | +292.1px | −144.3px |

**中心ずれの median は切れなしで +28.9px と小さいが、p10〜p90 が −190〜+507px と極端に広い。** 縦方向（上端ずれ median −18.4px、切れなし）と比べて桁違いにばらついている。

#### 横幅が bbox より広い

| 種別 | 投影幅 ÷ bbox 幅 |
|---|---|
| **切れなし** | **1.618** |
| 左が切れ | 0.831 |
| 右が切れ | 0.798 |
| 左右とも切れ | 0.654 |

**切れていないフレームでモデルの投影幅が bbox の 1.6 倍。** 縦は ⑧ が合わせているが、**横は誰も合わせていない**ので、モデルの体型（四足動物は横に長い）がそのまま出ている。

#### 解釈: 横は「合わせる仕組みが無い」

- 縦: ② がスケールを bbox 高で決め、⑧ が深度で微調整、⑦ が位置を合わせる
- 横: **スケールは縦基準（`ResolveTargetHeightMeters` は `bboxHeightPixels` のみ使用）、位置は `anchorU` 任せ、合わせる処理は無い**

したがって横幅が 1.6 倍なのは**設計どおり**で、bug ではない。モデルと実際の動物の体型比（縦横比）が違えば必ずこうなる。

**中心のばらつき（p10〜p90 で 700px）の方が問題。** これは `anchorU` の精度に直結する。anchor は 3D root の再投影で、bundle 側も「root の 2D 位置が体から外れることがある」と認めている（10.4% が bbox 外）。

#### 判断: 左右の「切れ対応」は不要。別の問題

**縦と同じ「切れているから合わせ先を変える」対処は、横には当てはまらない。** そもそも横を bbox に合わせる処理が無いため。

やるとすれば別の 2 つ:

| 案 | 内容 | 懸念 |
|---|---|---|
| X | 横位置を bbox 中心に合わせる（`anchorU` を使わない） | 四足動物は向きで重心が変わるので、bbox 中心＝体の中心とは限らない。**縦で ⑦ がやっていることの横版**だが、根拠が弱い |
| Y | 横方向のスケールも bbox 幅で合わせる（非等方スケール） | モデルが伸縮して見える。**やるべきでない** |

**現時点ではどちらも推奨しない。** 中心ずれの median は +28.9px（bbox 幅の数 %）で、実機で問題として報告されていない。まず animal の rig 対応（姿勢が正しく適用されているか）を確認する方が優先度が高い。

## bundle_train.svb は再生成不要（2026-08-28 実測）

animal を再生成したのと同じことが train にも必要か調べた（`scratchpad/train_probe.py` / `train_checks.py` / `train_checks2.py`）。**結論: 不要。**

### 素性

| | train | animal（再生成済） | human（再生成済） | human（旧） |
|---|---|---|---|---|
| `generated_at` | **2026-08-19T06:06:43** | 2026-08-27T00:00:53 | 2026-08-20T18:26:08 | 2026-08-19T06:04:47 |
| frames | 1830（61 秒） | 2120 | 2167 | 2167 |
| shots | **1** | 28 | 1 | 1 |
| tracks | **8、全部 `other`** | 2（animal） | 2（person + other） | 同左 |
| skeleton / SMPL / SMAL | **全部 0** | skel + SMAL | skel + SMPL | 同左 |

**`bundle_train.svb` は旧 human と 2 分違いの同一ビルド回。** ただし下の実測のとおり、それが問題を意味しない。

### D-002（video.mp4 が inpaint 前）→ **問題なし**

`video.mp4` と `source/pre_removal_stereo_video.mp4` の CRC / サイズを比較。

| | video.mp4 | pre_removal | 判定 |
|---|---|---|---|
| train | 58,856,611 B | 54,924,498 B | **別物 = inpaint 済み** |
| animal / human（再生成済・旧とも） | — | — | すべて inpaint 済み |

### D-001（shot 内の anchor_z ドリフト）→ **再生成済み human より小さい**

各 track の前半/後半の z01 中央値の差:

| bundle | ドリフト |
|---|---|
| **train** | **−0.004 〜 −0.080** |
| human（再生成済） | −0.082 / −0.104 |
| animal（再生成済） | −0.008 / −0.014 |

**train は再生成済み human よりドリフトが小さい。** 再生成の理由にならない。

### shots=1 は妥当 → **カット無しの単一テイク**

連続フレーム間で anchor_u か bboxH が 64px 超飛んだ箇所を数えた。

| bundle | 飛び | 内訳 |
|---|---|---|
| **train** | **5 件** | **すべて track 3 単独**（bboxH med=21 の小さい物体）。anchor_u の飛びは最大 67px |
| animal（28 shot） | 24 件 | 多くが shot 境界と一致。anchor_u が 212〜631px 飛ぶ |

**カットなら全 track が同時に飛ぶ。** train は単一 track のばらつきだけなので、**shots=1 で正しい**。animal で起きた「`shots.json` が stale」は train には該当しない。

### `depth_scale_calibration`

animal（再生成済）は**キーだけあって値は `null`**。train はキー自体が無い。**どちらも実体を持たないので差にならない。**

### train に関係する課題は配置のみ

**全 track が `other`（剛体）** なので、animal でやった姿勢追従（SMAL FK・AimAt・keypoint 対応）は**一切関係しない**。関係するのは配置パイプライン ①〜⑩ と、既知の **D-004**（`anchor_z` が実距離をほとんど再現しない。train の決定係数 0.001〜0.797）。

D-004 は全 bundle 共通の未解決課題で、train を再生成しても直らない。

## Else のスケールをロックできるか（2026-08-28 実測）

「Else も Human/Animal と同じようにロックしたい」に対する調査（`scratchpad/else_scale.py`）。

### 現状

```csharp
bool lockScale = IsCategoryPerson(obj.categoryId) || IsCategoryAnimal(obj.categoryId);
```

Else は**毎フレーム bbox からスケールを計算し直す**。この doc の別節に
「**Else が完璧なのは毎フレーム bbox からスケールを計算し直しているため**」と記録済み。

### train は shot 先頭基準が使えない

train は **shots=1**（1830 フレームの単一テイク）で、**8 track 中 5 つが shot 途中で登場する**。
`TryResolveShotStartScaleReference` は shot 先頭にその track が居ないと false を返すので、
**初登場フレームで固定**されることになる。

| track | 初登場 f | 初登場 bboxH | 中央値 | bboxH の変動幅 | 初登場が全体の何 % 位置か |
|---|---|---|---|---|---|
| 0 | 0 | 160 | 132 | 1.3x | shot 先頭に居る |
| 1 | 0 | 34 | 48 | 9.2x | shot 先頭に居る |
| 2 | 0 | 22 | 38 | 11.4x | shot 先頭に居る |
| **3** | 87 | 17 | 21 | 16.3x | **下から 11%** |
| **4** | 230 | **12** | **49** | **20.3x** | **下から 2%** |
| **5** | 1261 | 117 | 203 | 3.7x | 下から 35% |
| **6** | 1415 | **53** | **203** | **5.0x** | **下から 2%** |
| **7** | 1560 | 65 | 131 | 4.4x | 下から 19% |

**track 4 と 6 は、その track が取りうる最小級のサイズで固定される。** 中央値の 1/4 なので、
動画のほとんどの区間で**4 倍小さいまま**になる。

human は 2 track とも shot 先頭に居るので、この問題は出ない（ボールは bboxH 53、中央値 51）。

### 本質的な問題: ロックすると見かけの大きさが `anchorZ` 任せになる

Human/Animal をロックできるのは「人体のサイズは不変」という前提が正しいからで、Else でも
剛体なら同じはず。**ただしロックすると、見かけの大きさは深度だけで決まる。**
その深度が D-004（`anchor_z` が実距離をほとんど再現しない。train の決定係数 0.001〜0.797）
なので、**ロックは深度の誤差をそのままサイズの誤差として見せることになる。**

現状の毎フレーム bbox 合わせは、深度の誤差をサイズ側で吸収して見かけを合わせている。

### 案

| 案 | 評価 |
|---|---|
| shot 先頭でロック | **train で破綻**（上表）。採れない |
| **track ごとの中央値でロック** | `meta.bin` は全フレーム読み込み済みなので、track ごとの代表値を先に計算できる。初登場フレームが外れ値でも影響しない |
| 現状維持（毎フレーム） | 見かけは合う。剛体の world サイズが変わるのは物理的に不正だが、深度が直るまでは実害が小さい |

### 次に測るべきこと

**`bboxH × depth` が track ごとに一定か。** 一定ならロックしても見かけは変わらず、
物理的に正しくなるだけ。一定でないなら、ロックは見た目を壊す。

深度の変換（`DecodeAnchorDepthMetersFromBundle` と popout レンジ）は**実装を移植してから**
測ること（[[depth-range-fixes-ineffective]]、記憶や docs から式を再構成しない）。
