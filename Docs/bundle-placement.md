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
