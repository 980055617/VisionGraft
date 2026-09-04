# D-002: 再ビルドされた bundle の `video.mp4` が inpaint 前（被写体が消えていない）

← [課題一覧に戻る](README.md)

**状態**: **解決**（2026-09-04）　**提起**: [Unity側] 2026-08-17

### 報告 `[Unity側]`

#### 症状

D-001 の対応版として受け取った `bundle_human.svb` / `bundle_train.svb` を再生したところ、**動画内の人物（train では電車）が消えていない**。置き換えモードでは被写体が除去された `video.mp4` の上に 3D モデルを重ねる設計なので、除去されていないと元の被写体とモデルが二重に見える。

**`video.mp4` に inpaint（被写体除去）が適用されていない**と考えられる。

#### 証拠

**1. サイズが「除去前」の動画と完全一致する**

| | サイズ（バイト） |
|---|---|
| 受け取った `bundle_human.svb` の **`video.mp4`** | **61,131,836** |
| 従来版 `bundle_human.svb` の **`source/pre_removal_stereo_video.mp4`**（除去**前**） | **61,131,836** |
| 従来版 `bundle_human.svb` の `video.mp4`（除去**後**） | 59,713,872 |

**2. フレームを抽出して目視確認**

`video.mp4` の frame 0（左目）に人物とボールがそのまま写っている。`bundle_train.svb` の frame 900 でも電車がそのまま写っている。

#### 受け取った bundle の構成

```
bundle_human.svb (64 MB)          bundle_train.svb (55 MB)
├─ video.mp4        61,131,836    ├─ video.mp4        54,924,498
├─ meta.bin          2,629,605    ├─ meta.bin            152,353
├─ manifest.json         5,412    ├─ manifest.json         5,412
└─ source/placement_observations.json
```

従来版と比べて次が同梱されていない。

| 欠けているもの | 影響 |
|---|---|
| **`source/pre_removal_stereo_video.mp4`** | **通常モードが使えない**。このファイルを直接再生する設計（[ADR-0003](../adr/0003-normal-mode-playback-video.md)）で、無い場合はモード切替 UI が自動的に無効化される |
| `source/keypoints3d.json` / `sam2_masks.json` / `other_object_proxies.json` / `animal_control_targets.json` / `pipeline_manifest.json` | runtime 配置には使わないので**再生への影響はない**が、検証・debug で参照している |

#### 直っている点（念のため確認済み）

- **D-001 の深度修正は正しく入っている。** `bundle_depth_check.py` で corr(`transl.z`, `z01`) = **+0.649 OK**、corr(`z01`, 経過時間) = −0.116。「同じ場所に立っているフレームでの `z01` のばらつき」も 0.218 → **0.110** に半減。そちらの検証結果と一致した
- **コーデックは問題ない。** `h264 / avc1 / 2560x640 / 2167 frames`（train は 1830 frames）。Quest の MediaCodec 経路で再生できる形式

#### 依頼

そちらの D-001 回答に「**これは検証用の再ビルドであり、まだ正式な再配布物ではない**（Quest 向け H264 再エンコードや `bundle_consistency.json` の全項目チェックをまだ通していない）」と明記されていたので、**この状態は想定内かもしれない**。そうであれば、正式な再配布ビルドで次の 2 点を満たしてほしい。

1. **`video.mp4` は inpaint 後（被写体除去済み）のものを入れる**
2. **`source/pre_removal_stereo_video.mp4` を同梱する**（通常モードで使う。ADR-0003 のとおり **H264 必須**。以前 mpeg4/mp4v だったときは Quest で黒画面になった）

#### 再発防止の提案

`bundle_consistency.json` のチェック項目に、次を入れると同じ取り違えを自動で弾けると思う。

- **`video.mp4` と `source/pre_removal_stereo_video.mp4` のハッシュが一致しないこと**（一致 = inpaint 未適用）
- `source/pre_removal_stereo_video.mp4` が存在し、`video.mp4` と同じコーデック（h264/avc1）・同じ解像度・同じフレーム数であること

### 回答 `[bundle側]`

#### 結論

**原因はご指摘の通り。`--video_mp4` に inpaint 前の生ステレオ動画を渡して `build_bundle_svb.py` を直接叩いていたため。** `source/pre_removal_stereo_video.mp4` も、そもそも同梱する処理（`scripts/run_sam2_to_bundle.py` 側の `append_bundle_sidecars`）を経由していなかったので欠落していた。

**この不具合は D-001 の正式配布（`bundle_shots_depthdriftfix.svb`）だけでなく、本日配布した `bundle_shots_maskanchorfix.svb` にも同じ形で入っていました。** 確認したところ `bundle_shots_maskanchorfix.svb` の `video.mp4` も 61,131,836 バイト（inpaint 前と同一）で `source/pre_removal_stereo_video.mp4` は同梱されていませんでした。お詫びして訂正します。

#### 原因の詳細

`scripts/run_sam2_to_bundle.py` を読むと、正規のフローでは:

1. `--video_mp4` に渡すのは **rose（inpaint）で被写体除去した動画をステレオ化したもの**（`bundled_video_3d`）。生の撮影動画ではない
2. `--depth_npz` に渡す深度は `--bundle-depth-source`（既定値 `original`）に従い、**inpaint 前の元 depth.npz** を使う（`meta.bin` の anchor は inpaint 前のカメラ距離で置く設計、これは D-001 の修正がそのまま活きる部分）
3. bundle 生成後に `append_bundle_sidecars()` が **別途**、inpaint 前のステレオ動画を Quest 用 H264 に変換したものを `source/pre_removal_stereo_video.mp4` として zip に追記する

`build_bundle_svb.py` 単体では 1 と 3 を知らない（`--video_mp4` に何を渡すかは呼び出し側の責任、sidecar 追記は別関数）。D-001 / D-003 対応時、フル pipeline を通さず `build_bundle_svb.py` を直接叩いていたため、1 で生動画を渡してしまい、3 の sidecar 追記も行わなかった。

#### 修正・検証

各素材の作業ディレクトリに、以前の pipeline 実行で生成済みの rose inpaint 済みステレオ動画（`*_rose_2x2_video_3D_with_audio.mp4`）が既にあったので、これと D-001 で修正済みの depth.npz を組み合わせて作り直した。

1. `--video_mp4` を rose inpaint 済みステレオ動画に変更してビルド（`video.mp4` が正しく inpaint 後になる）
2. inpaint 前のステレオ動画を同じ Quest H264 設定（`build_bundle_svb.py` の transcode と同一設定、`scripts/run_sam2_to_bundle.py:build_transcode_for_quest_cmd` とも同一）で変換し、`source/pre_removal_stereo_video.mp4` として zip に追記

| | human | train |
|---|---|---|
| `video.mp4`（inpaint後） | 59,713,872 バイト（従来版の inpaint 後と一致） | 58,856,611 バイト |
| `source/pre_removal_stereo_video.mp4`（inpaint前・H264） | 61,131,836 バイト | 54,924,498 バイト（従来の誤配布版 `video.mp4` と一致） |
| コーデック | 両方とも h264/yuv420p | 同左 |

frame 140（human）・frame 900（train）を目視確認し、被写体が正しく除去されていることを確認した。`verify_sam2_bundle_consistency.py` は両方とも `ok=True issues=0 warnings=0`（`pre_removal_stereo_video.mp4` 欠落の警告が解消）。depth 側の指標（`bundle_depth_check.py`）は maskanchorfix 版と完全に同一（human: person +0.649 OK / ball -0.770 OK、train: 8 track中 4 track OK 改善など）で、動画の差し替えによる副作用はない。

#### 配布物

- `FINNAL_HUMAN/bundle_shots_inpaintfix.svb`
- `FINNAL_TRAIN/bundle_shots_inpaintfix.svb`

D-001（depth drift fix）・D-003 調査中に見つけた segmentation バグ修正（マスク重心アンカー）・今回の inpaint 修正、**3 つがすべて入った状態**が現時点の最新版。**`bundle_shots_maskanchorfix.svb` は inpaint 前動画を含む不完全な版なので、以後は使わず `bundle_shots_inpaintfix.svb` を使ってください**（ファイル自体は削除せず残しています。既存ファイルを上書き・削除しない運用のため）。

#### 再発防止の提案への回答

`bundle_consistency.json` に **`video.mp4` と `source/pre_removal_stereo_video.mp4` のハッシュ不一致チェック**を入れる提案、賛成です。今回のように `build_bundle_svb.py` を直接叩く手順（フル pipeline を経由しない検証ビルド・緊急修正など）で同じ事故が再発しうるので、`scripts/verify_sam2_bundle_consistency.py` 側に追加することを検討します（未着手）。

### 経過

| 日付 | 誰 | 内容 |
|---|---|---|
| 2026-08-17 | [Unity側] | 起票。`video.mp4` のサイズが従来版の `pre_removal_stereo_video.mp4` と完全一致、フレーム目視でも被写体が残存。D-001 の深度修正自体は入っていることを確認済み |
| 2026-08-19 | [bundle側] | 原因判明。`run_sam2_to_bundle.py` のフル pipeline を経由せず `build_bundle_svb.py` を直接叩いていたため、inpaint 前動画を `video.mp4` に使い、`pre_removal_stereo_video.mp4` の追記もしていなかった。同じ原因で本日配布済みの `bundle_shots_maskanchorfix.svb` にも同一不具合があったことが判明、要修正。rose inpaint 済みステレオ動画 + D-001修正済みdepth.npzで作り直し、`FINNAL_HUMAN`/`FINNAL_TRAIN` に `bundle_shots_inpaintfix.svb` として配布。目視・`verify_sam2_bundle_consistency.py`（ok=True issues=0 warnings=0）で確認済み。`maskanchorfix.svb` は非推奨に変更 |

---

---

### 決着 `[Unity側]` 2026-09-04 — 2 条件とも満たされていることを確認。**解決**

配布済みビルドを手元で機械的に確認した。

| bundle | `source/pre_removal_stereo_video.mp4` | `video.mp4` と別物か | codec |
|---|---|---|---|
| `bundle_train.svb`（= inpaintfix） | あり 54.9MB | 別物（inpaint 適用済み） | avc1 |
| `bundle_human.svb` | あり 61.1MB | 別物 | avc1 |
| `bundle_animal.svb` | あり 39.6MB | 別物 | avc1 |
| `bundle_shots_inpaintfix_zquantfix.svb` | あり 54.9MB | 別物 | avc1 |

判定は D-002 で当方が提案した方法そのまま。`video.mp4` と
`source/pre_removal_stereo_video.mp4` のハッシュが**一致しない**ことをもって
inpaint 適用済みとした（一致 = inpaint 未適用）。

依頼した 2 点はどちらも満たされている:

1. `video.mp4` は inpaint 後 — **満たす**
2. `source/pre_removal_stereo_video.mp4` を同梱・H264 — **満たす**（全 bundle avc1）

**これらのビルドで数週間 runtime を動かしており、通常モード（除去前動画の再生）も
実機で動作している。** 実質的にはとうに解決していたが、この課題ファイルに
記録していなかったため未決のまま残っていた。**解決として archive へ移す。**

再発防止の提案（`bundle_consistency.json` にハッシュ一致チェックを入れる）は
そちらの判断に任せる。こちらからの追加依頼は無い。
