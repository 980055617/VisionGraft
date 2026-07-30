# Bundle 読み込み・配置

## 発表用まとめ

`.svb` ファイル（ZIP アーカイブ）を開き、`meta.bin` と `manifest.json` だけを使って配置・姿勢追従を行う。

```
.svb (ZIP)
├─ video.mp4        必須・Runtime 再生
├─ manifest.json    必須・解像度/fps/FOV/座標系
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
- **スケール**: `bboxWorldH` 基準の uniform scale（`TrackModelPlacement.ResolveDesiredLocalScale`）。モデル間の体型差は吸収しない（既知の制限、[DogMetaBoneMapping.md](DogMetaBoneMapping.md) 参照）
- **形状（betas）**: 読み込むが現状未使用

### 関連ファイル

| ファイル | 役割 |
|---|---|
| `StreamingStereoVideoPlayer.Bundle.cs` | ZIP 展開・エントリ読み込み |
| `StreamingStereoVideoPlayer.Meta.cs` | `meta.bin` パース・フレームデータ格納 |
| `StreamingStereoVideoPlayer.Manifest.partial.cs` | `manifest.json` パース・座標系設定 |
| `PinholePlacementSpace.cs` | UV + 深度 → world 座標変換 |
| `TrackModelPlacement.cs` | bbox ベースのスケール決定 |

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

**残課題（未修正）**: `anchorZ` 経路に統一しても Human とボールの深度差は **中央値 1.5 cm**（範囲 -5.0〜+5.9 cm）しかない。`PopoutRangeMeters = 0.35f` の固定レンジに 0..1 の正規化深度全体を押し込んでおり、実際に使われる `z01` は 0.58〜0.90 の範囲だけなので有効レンジは 0.35 × 0.32 ≈ 11 cm。Human モデルの胴体の厚み（20〜30 cm）に対して不足しており、ボールがメッシュに埋まる/背面に抜ける可能性が残る。根本には「`depth_npz` はシーン全体（背景含む）で正規化された相対深度なので、絶対距離を復元する情報が bundle に無い」という構造的制約がある。対応候補:

1. `PopoutRangeMeters` の拡大、または `z01` → 実距離マップの見直し
2. 人物の身長や SMPL `transl` を使った depth スケールの校正（`bundle_human.svb` の SMPL `transl.z` は 78〜132 という HMR2 独自スケールなので、校正係数を出せるか要検証）
3. bundle 側で metric depth または depth のスケール・シフト係数を `manifest.json` に載せる

### モデルアセット（`Resources/Models/`）

`Assets/Resources/Models/{Human,Animal,Else}/` 配下の各 prefab は、2 桁ゼロ埋めの番号プレフィックス（例: `00_Baseball`）をファイル名先頭に付ける運用。番号は `selected{Human,Animal}Index`（Inspector の生配列インデックス）や実行時 UI のインデックスと一致させるため、モデルを追加・削除したら欠番なく連番になっているか必ず確認・振り直すこと。

- **Human/Animal**: `Assets/Editor/HumanIndexPrefixer.cs` / `AnimalIndexPrefixer.cs`（`Tools > VisionGraft > Prefix All {Human,Animal} Models With Index`）で振り直す。
- **Else**: `Assets/Saritasa/Models/Sport_Balls/` の 6 種（Baseball/Basketball/Football/Golf/Soccer/Tennis）を `Assets/Editor/ElseModelImporter.cs`（`Tools > VisionGraft > Import And Prefix Else Models`）で `Resources/Models/Else/00_Baseball.prefab`〜`05_Tennis.prefab` としてコピー・採番した（2026-07-17）。元の fbx/material は `Assets/Saritasa/Models/Sport_Balls/` に残したまま GUID 参照する構成（Animal の `Sources/` と同じパターン）で、`AssetDatabase.CopyAsset` を使い GUID 衝突を避けている。
  - 追加で Sketchfab 製のディーゼル機関車モデル（`2ТЭ116УД`, 作者 Leafia dev., **CC-BY-4.0**）を `06_DieselLocomotive.prefab` として取り込んだ。元の `.glb` は `Resources/Models/Else/Sources/DieselLocomotive.glb` に保管。スケルトン・アニメーションなしの静的剛体メッシュなので Else に適合。**CC-BY-4.0 のため最終成果物にクレジット表示が必要**（未対応、要フォロー）。
  - ランタイム側の選択ロジックを実装済み（2026-07-30）。`StreamingStereoVideoPlayer.Core.cs` に `selectedElseIndex`（グローバルデフォルト）と `elsePrefabs`（`Resources/Models/Else` から自動ロード）を追加し、`ResolveTrackPrefab`（`StreamingStereoVideoPlayer.Playback.partial.cs`）に `IsCategoryOther` 分岐を追加した。修正前は Animal 以外のカテゴリを無条件に Human 用ロジックへフォールバックしていたため、Else の track にも Human プレハブが割り当てられていた（既知のバグ、修正済み）。
  - track ごとにモデルを個別指定したい場合は `trackModelIndices`（`{trackId, modelIndex}` の Inspector 配列）を使う。優先順位は「VR 内 Change Model パネルでの変更 > `trackModelIndices` > `selectedHumanIndex`/`selectedAnimalIndex`/`selectedElseIndex` のグローバルデフォルト」。ただし VR 内 Change Model パネルは Person/Animal の track のみが対象で、Else の track には現状表示されない（`TryGetRuntimeModelPickerTarget` が Person/Animal のみを候補にしているため）。
- **`.glb` インポーターの競合に注意**: プロジェクトには UniGLTF（VRM 用、`Packages/com.vrmc.gltf`）と glTFast（`com.unity.cloud.gltfast`）の両方が入っており、どちらも `.glb`/`.gltf` の ScriptedImporter を登録する。UniGLTF 側の自動棲み分け（`UniGLTF.Editor.asmdef` の `versionDefines`）は旧パッケージ名 `com.atteneder.gltfast` を見ているため、現行の `com.unity.cloud.gltfast` とは噛み合わず、新規 `.glb` を追加すると両方拒否されて `DefaultImporter` にフォールバックする（既存の `50+ Animated Animals` 内の `.glb` は `.meta` に旧来のインポーター参照が焼き込まれているため影響を受けない）。対処として Scripting Define Symbols に `UNIGLTF_DISABLE_DEFAULT_GLB_IMPORTER` / `UNIGLTF_DISABLE_DEFAULT_GLTF_IMPORTER` を追加済み（`ElseModelImporter.DisableUniGltfDefaultGlbImporter`）。今後 `.glb` を追加する際はこの設定により glTFast 側が自動で使われる。
- いずれのツールも Unity Editor を閉じてバッチモード（`Unity.exe -batchmode -nographics -quit -projectPath <path> -executeMethod <クラス.メソッド> -logFile <path>`）で実行する必要がある（同一プロジェクトの多重起動不可のため）。`Resources.LoadAll<GameObject>` は `Sources/` サブフォルダ内の `.glb`/`.fbx` もメインアセットが `GameObject` である限り拾ってしまう点に注意（`PrefixWithIndex` はパスに `/Sources/` を含むものを除外するよう対応済み）。
