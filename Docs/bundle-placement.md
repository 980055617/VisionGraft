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

### モデルアセット（`Resources/Models/`）

`Assets/Resources/Models/{Human,Animal,Else}/` 配下の各 prefab は、2 桁ゼロ埋めの番号プレフィックス（例: `00_Baseball`）をファイル名先頭に付ける運用。番号は `selected{Human,Animal}Index`（Inspector の生配列インデックス）や実行時 UI のインデックスと一致させるため、モデルを追加・削除したら欠番なく連番になっているか必ず確認・振り直すこと。

- **Human/Animal**: `Assets/Editor/HumanIndexPrefixer.cs` / `AnimalIndexPrefixer.cs`（`Tools > VisionGraft > Prefix All {Human,Animal} Models With Index`）で振り直す。
- **Else**: `Assets/Saritasa/Models/Sport_Balls/` の 6 種（Baseball/Basketball/Football/Golf/Soccer/Tennis）を `Assets/Editor/ElseModelImporter.cs`（`Tools > VisionGraft > Import And Prefix Else Models`）で `Resources/Models/Else/00_Baseball.prefab`〜`05_Tennis.prefab` としてコピー・採番した（2026-07-17）。元の fbx/material は `Assets/Saritasa/Models/Sport_Balls/` に残したまま GUID 参照する構成（Animal の `Sources/` と同じパターン）で、`AssetDatabase.CopyAsset` を使い GUID 衝突を避けている。
  - 追加で Sketchfab 製のディーゼル機関車モデル（`2ТЭ116УД`, 作者 Leafia dev., **CC-BY-4.0**）を `06_DieselLocomotive.prefab` として取り込んだ。元の `.glb` は `Resources/Models/Else/Sources/DieselLocomotive.glb` に保管。スケルトン・アニメーションなしの静的剛体メッシュなので Else に適合。**CC-BY-4.0 のため最終成果物にクレジット表示が必要**（未対応、要フォロー）。
  - ランタイム側で `selectedElseIndex` 相当の選択ロジックはまだ実装されていない。
- **`.glb` インポーターの競合に注意**: プロジェクトには UniGLTF（VRM 用、`Packages/com.vrmc.gltf`）と glTFast（`com.unity.cloud.gltfast`）の両方が入っており、どちらも `.glb`/`.gltf` の ScriptedImporter を登録する。UniGLTF 側の自動棲み分け（`UniGLTF.Editor.asmdef` の `versionDefines`）は旧パッケージ名 `com.atteneder.gltfast` を見ているため、現行の `com.unity.cloud.gltfast` とは噛み合わず、新規 `.glb` を追加すると両方拒否されて `DefaultImporter` にフォールバックする（既存の `50+ Animated Animals` 内の `.glb` は `.meta` に旧来のインポーター参照が焼き込まれているため影響を受けない）。対処として Scripting Define Symbols に `UNIGLTF_DISABLE_DEFAULT_GLB_IMPORTER` / `UNIGLTF_DISABLE_DEFAULT_GLTF_IMPORTER` を追加済み（`ElseModelImporter.DisableUniGltfDefaultGlbImporter`）。今後 `.glb` を追加する際はこの設定により glTFast 側が自動で使われる。
- いずれのツールも Unity Editor を閉じてバッチモード（`Unity.exe -batchmode -nographics -quit -projectPath <path> -executeMethod <クラス.メソッド> -logFile <path>`）で実行する必要がある（同一プロジェクトの多重起動不可のため）。`Resources.LoadAll<GameObject>` は `Sources/` サブフォルダ内の `.glb`/`.fbx` もメインアセットが `GameObject` である限り拾ってしまう点に注意（`PrefixWithIndex` はパスに `/Sources/` を含むものを除外するよう対応済み）。
