# モデルのマテリアル

3D モデル側のマテリアル起因の見え方の問題をここにまとめる。配置・姿勢の話は
[bundle-placement.md](bundle-placement.md) / [smpl-retargeting.md](smpl-retargeting.md) を見ること。

## 棚卸しの道具

`Assets/Editor/ModelMaterialAudit.cs`。`Resources/Models` 以下の prefab が実際に使っている
マテリアルの実値（shader / queue / surface / alphaClip / cutoff / zwrite / cull / texture）を出す。

```
Unity.exe -batchmode -projectPath <proj> -executeMethod ModelMaterialAudit.Run \
          -logFile <log> [-auditFilter Lion]
```

**50+ Animated Animals はマテリアルを FBX 埋め込み（`materialLocation: 1`）で持っている**ので、
`.mat` ファイルがディスク上に無く grep では中身が判らない。推測せずこれで実値を見ること。

## ライオンのたてがみが透ける（2026-09-05）

### 症状

ライオンのたてがみ・頭の回りの毛が透明で、向こう側の映像が透けて見える。
拡大・縮小のどちらでも変わらない。

**当初「モデルピッカーのプレビュー固有」と書いたが誤り。**実配置でも同じように透ける
（`cap_lion_world` のキャプチャで、たてがみ越しに芝生とスクリーンの縁が見えている）。

### 原因: 毛のカードが alpha blend になっている

```
04_Lion / lion_male[0] mat=M_lion       surface=0(Opaque)      queue=2000 zwrite=1 alphaClip=0
04_Lion / lion_male[1] mat=M_lion_mane  surface=1(Transparent) queue=3000 zwrite=0 alphaClip=0
```

体は Opaque、**たてがみだけ Transparent + ZWrite off + alpha clip 無し**。
毛のカードは alpha テクスチャで形を抜くもので、alpha blend で描くと
「テクスチャの alpha がそのまま不透明度になる」ため一面が半透明になる。
正しくは **alpha clip（cutout）** で、alpha がしきい値を超えた画素だけを不透明に描く。

### 同じ状態のモデルが 6 つある

全 198 マテリアル中、Transparent は 6 つだけで、**すべて毛・ヒゲのカード**。

| モデル | マテリアル | テクスチャ | 実体 |
|---|---|---|---|
| 04_Lion | `M_lion_mane` | `lion_mane_common_alpha_dif` | FBX 埋め込み |
| 24_Fox | `M_Fox_Cards` | `red_fox_cards_alpha_dif` | FBX 埋め込み |
| 37_Lioness | `M_Lionesswiskers` | `T_Lioness_alpha` | FBX 埋め込み |
| 48_Puma | `M_whiskers` | `T_puma_cards_alpha` | FBX 埋め込み |
| 50_Racoon | `M_Mexican_Bobcat_Cards` | `T_Raccoon_Fur` | FBX 埋め込み |
| 27_GermanShepherd | `M_GermanShepherd_Transparent_URP` | `T_GermanShepherd_B` | `Assets/RSG_DogsPack/URP/` の `.mat` |

`M_GermanShepherd_Transparent_URP` は `cutoff=0.123` と cutout 用の値が入っているのに
`alphaClip=0` で、**cutout のつもりで設定して alpha clip を立て忘れている**状態。

### 前例: 39_Lynx だけ既に直っていた

修正前の棚卸しで、`39_Lynx` の `M_lynx_cards` だけが
`queue=2450 surface=0 alphaClip=1 cutoff=0.35` と正しい状態だった。
**同じやり方の前例があるので、閾値もそれに合わせて 0.35 にした。**

### 対処: 取り込み時に cutout へ倒す（2026-09-05）

`Assets/Editor/FurCardMaterialPostprocessor.cs`。

- `OnPostprocessModel` で、Transparent かつ毛・ヒゲの語を名前に含むマテリアルを
  alpha clip へ変換する
- **`OnPostprocessMaterial` は使えない。**この pack は `materialImportMode: 2`
  （Import via MaterialDescription）で取り込まれており、その経路では呼ばれない。
  再取り込みしても `surface=1` のまま変わらなかった（実測）。
  `OnPreprocessMaterialDescription` を実装すると既定のマテリアル生成ごと肩代わりする
  ことになるので、生成後に触れる `OnPostprocessModel` を使う
- `.mat` として存在するもの（`M_GermanShepherd_Transparent_URP`）は取り込み時に走らないので
  `FixStandaloneMaterials()` で明示的に直す
- 導入時の一度きりの再取り込みは
  `-executeMethod FurCardMaterialPostprocessor.ReimportAffectedModels`

**URP Lit はプロパティだけ変えても効かない。**キーワード（`_ALPHATEST_ON` を立て、
`_SURFACE_TYPE_TRANSPARENT` と `_ALPHAPREMULTIPLY_ON` を落とす）、ブレンド係数、
`RenderType` タグ、キュー、そして **深度パス（`DepthOnly` / `DepthNormals`）の再有効化**まで
揃える必要がある。深度パスを戻し忘れると深度を書かないままで前後が入れ替わる。

### 対象の絞り方

Transparent なら何でも倒すと、将来ガラスや水のモデルを入れたときに壊れる。次の 2 条件のどちらか:

1. マテリアル名かベーステクスチャ名が毛・ヒゲの語を含む
   （`alpha` / `card` / `fur` / `mane` / `hair` / `wisker` / `eyelash`）
2. `_Cutoff` が既定の 0.5 から動かしてある
   （Transparent では `_Cutoff` は使われないので、動かしてある = cutout を意図している。
   `M_GermanShepherd_Transparent_URP` の 0.123 がこれ）

### 実測

| | 修正前 | 修正後 |
|---|---|---|
| Transparent なマテリアル | 6 | **0** |
| `alphaClip=1` のマテリアル | 35 | 41（+6、対象ぶんのみ）|
| ライオンのたてがみ | 背景の芝生とスクリーンの縁が透けて見える | 不透明。毛筋が出る |

巻き添えは無い。増えた 6 件は対象そのもので、人物モデルの髪・服（元から
`alphaClip=1 cutoff=0.5`）は Opaque なので条件に入らず触っていない。

### NG: プロジェクト全体の `t:Material` を舐める

最初の実装で `AssetDatabase.FindAssets("t:Material")` で全マテリアルを走査したところ、
**無関係な 12 件に差分が出た**（Volleyball / TextMesh Pro の例 / npc の髪・服・顔）。

`LoadAssetAtPath` するだけで Unity がシェーダのプロパティ同期を走らせ
（`_MainTex` に `_BaseMap` を、`_Color` に `_BaseColor` を写す）、`SaveAssets()` で
それが書き出される。alpha 関連は 1 件も変わっていない純粋な正規化差分だが、
触っていないファイルが変更済みになる。

**走査対象は `Resources/Models` の prefab が実際に使っているマテリアルだけにすること。**

## 取りこぼし: Opaque 側にも同じ問題があった（2026-09-05、Hare のヒゲ）

ライオンの件を「Transparent が悪い」と読んで Transparent だけを対象にしたが、
**逆向きの壊れ方が 11 件あった。**

```
33_Hare / mat=M_rabbit_alpha  surface=0(Opaque)  alphaClip=0  tex=eu_rabbit_cards_alpha_dif
```

alpha テクスチャなのに Opaque かつ alpha clip 無しだと **alpha が完全に無視され**、
カードが白い板のまま出る。Hare は口元が白い塊になっていた。

- Transparent + clip 無し → **透ける**（ライオンのたてがみ）
- Opaque + clip 無し → **抜けない**（Hare のヒゲ）

どちらも「alpha clip が立っていない」が本体。判定は
**Surface ではなく `_AlphaClip` が 0 かどうか**で見ること。

### 名前だけでは判定できない。alpha を実測する

「毛のカード（alpha で形を抜く）」と「毛のシェル（alpha を使わない）」は名前で区別できない。
テクスチャの alpha を読んで、**完全透明の画素が 5% を超えるもの**だけを cutout にする。

| テクスチャ | 完全透明 | 判定 |
|---|---:|---|
| `eu_rabbit_cards_alpha_dif` | 99.0% | cutout |
| `Fox_Fur` | 96.0% | cutout |
| `Bear_fur_color` | 84.0% | cutout |
| `foxfur_d` | 78.0% | cutout |
| `T_kangaroo_alph` | 65.6% | cutout |
| `Mammoth_Fur` | 64.2% | cutout |
| `Red_Deer_Fur` | 57.3% | cutout |
| `TX_HorseHair_Albedo` | 56.5% | cutout |
| `Donkey_Hair` | 48.8% | cutout |
| `T_moose_eyelashes_alpha` | 21.9% | cutout |
| `Donkey (6)` | 20.6% | cutout |
| `Donkey (4)`（胴体）| 0.0% | 除外（alpha 無し）|

### 測定でつまずいた点 2 つ

1. **palette（P）形式でも tRNS で透明を持てる。**`TX_HorseHair_Albedo` を
   「mode が RGBA でないから alpha 無し」と判定したのは誤りで、実際は 56.5% が完全透明。
   **必ず `convert("RGBA")` してから数えること**
2. **`Texture2D.LoadImage` は PNG / JPG しか読めない。**`.tga` を落とすと
   10_BearWITHFUR と 26_FoxWITHFUR を取りこぼす（どちらも RLE 32bpp で、
   実測 84% / 78% の完全透明）。alpha だけを読む簡易 TGA デコーダを入れた
   （`TryTgaTransparentFraction`、type 2 と type 10 に対応）

なお TGA ヘッダの alpha bit 数（descriptor の下位 4 bit）は**当てにならない**。
上の 2 ファイルはどちらも 0 だが alpha データは入っている。

### 最終状態

| | 最初 | 最終 |
|---|---:|---:|
| Transparent なマテリアル | 6 | **0** |
| `alphaClip=1` のマテリアル | 35 | **50**（+15）|

`.mat` の差分は意図した 1 件（`M_GermanShepherd_Transparent_URP`）だけ。
比較画像は `docs/tmp/mat_hare_whiskers.png` と `docs/tmp/mat_fur_after.png`。
