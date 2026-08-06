# Human–Other 接触補正

## 目的

Human の姿勢を固定骨長の Unity Humanoid にリターゲットすると、元映像と表示モデルの体型・骨長の差によって、Other（ボールなど）と身体の接触位置がずれることがある。
この補正は、元映像で接触している身体部位を特定し、表示中 Humanoid の対応部位へ Other を追従させる。

## 原因

Other の `anchorU/V/Z` は元映像を基準にしている一方、表示 Human は別体型の Humanoid へリターゲットされている。そのため、元の Other anchor をそのまま表示しても接触は保たれない。

以前の Human bbox を用いる方式には次の問題があったため、実装から削除した。

- bbox だけでは、Other が足・腕・胸などのどの部位と接触しているか判別できない。
- bbox 内に Other がある場面では接触面の方向を決められない。
- 画面上の X/Y だけを直しても、0:20 付近では足とボールに約 9–10 cm の奥行き差が残る。
- 左右の足が交差する場面で補正対象が切り替わり、大きな1フレーム移動を起こしうる。

## 現在の方式

`meta.bin` の keypoints3d（`jointsCam` = root 相対の camera xyz、44 点の `hmr2_openpose25_extra19`）を、表示中の Human と同じ見た目サイズにスケールしてから eye pixel へ投影し、元映像相当の 2D pose を作る（`TryBuildHumanSourceContactPose`）。
スケールは keypoints3d の骨長（大腿 + 下腿 + 胴、track ごとに一度だけ推定してキャッシュ）から求めた身長を、そのフレームの bbox の world 高さに合わせる形で決める。
Other に近い身体セグメントを元映像側で選び、セグメント上の接触位置と接触方向を、表示 Humanoid の対応セグメントへ写像する。

当初は `source/human_smpl_from_sam2.json` の `box_xywh` / `predKeypoints2d` を読んでいたが、CLAUDE.md の鉄則「配置・姿勢追従には `meta.bin` と `manifest.json` のみを使う」に反するため meta.bin 由来へ移行した（2026-08-05）。sidecar を同梱しない bundle でも動作する。移行による精度差は「既知の制約」を参照。

対応する主な部位:

- 左右の足、すね、太もも
- 左右の前腕、上腕
- 胴体、肩、頭

表示 Other は次の両方を補正する。

- X/Y: 対応する表示セグメントの表面位置
- Z: 対応する表示セグメントのカメラ奥行き

左右の足や腕が近づく場面で部位選択がちらつかないように、直前の接触部位を保持するヒステリシスを設けている。接触点・接触方向・X/Y・Z のフレーム間変化にも上限を設け、部位が切り替わる場面の瞬間移動を抑える。

同じ動画フレームで `DisplayModelTick` が複数回呼ばれても、時間平滑化を重複適用しない。

## 適用範囲

本番コードには bundle 名、フレーム番号、track ID の固定値はない。各フレームの Human/Other と source keypoint を使うため、同じ sidecar 形式を持つ別動画にも適用される。

必要条件:

- Human が Unity Humanoid で、対象部位の bone を持つこと
- `meta.bin` の Human track に keypoints3d（44 点）と有効な bbox があること
- Human と Other が同じ eye にあり、Other が source 上で身体部位の接触範囲内にあること

keypoints3d から source pose を復元できない場合や接触部位を特定できない場合は、誤った部位へ吸着させず補正を行わない。

体型・骨長そのものを完全再現する処理ではない。細長い物体、複雑な形状、手指単位の接触などは、現在の身体セグメント近似では限界がある。

## 設定

`StreamingStereoVideoPlayer`:

- `enableHumanOtherContactCorrection`
- `humanOtherFullContactRadiusMultiplier`（既定値 1.25）
- `humanOtherReleaseRadiusMultiplier`（既定値 2.0）
- `humanOtherContactSurfacePaddingPixels`（既定値 2.0）
- `logHumanOtherContact` / `logHumanOtherContactEveryNFrames`（診断ログ、既定 OFF）

既存シーンへの影響を避けるため機能の既定値は OFF。`SampleScene` では ON。

**診断ログの注意**: `logHumanOtherContact` をシーンに保存していない状態では、コード側の既定値を変えても Unity がシーン側に保持している旧値を優先することがある。ON にしてもログが出ない場合は Inspector で明示的にチェックを入れること（調査時に実際にこれで数回空振りした）。

## 補正量の上限（2026-08-05 追加）

表示 Humanoid と元映像は体型（骨長比）が異なるため、姿勢によっては対応部位の位置が大きく食い違う。実測では仰向け付近で 40〜80 px の乖離が出る。そのまま吸着させると Other が元映像の位置から引き剥がされるため、Other 半径を基準に 2 段の上限を設けている（`ClampHumanOtherDesiredOffset`）。

| 条件 | 挙動 |
|---|---|
| `desired ≤ 半径 × 1.5` | そのまま適用 |
| `半径 × 1.5 < desired ≤ 半径 × 3.75` | `半径 × 1.5` に頭打ち |
| `desired > 半径 × 3.75` | **補正しない**（対応部位の取り違えか体型差が大きすぎる。引きずるより元映像の位置を保つ方が破綻しない） |

あわせて、1 フレームあたりの移動量の上限（`HumanOtherMaximumCorrectionDeltaPerFramePixels` = 18 px）が効かない経路を塞いだ。従来は前フレームの状態が無い場合（初回接触、**および動画再生でフレームがスキップされた場合**）に `desired` がそのまま適用され、Other が瞬間移動していた。現在は前フレームの適用量（無ければ 0）から必ず `MoveTowards` する。

## 既知の制約

**表示モデルと元映像の体型差が主要な誤差要因**。接触判定そのものは元映像側で正しく行われていても、写像先である表示 Humanoid の対応部位が映像とずれていれば、Other はそのずれの分だけ引き剥がされる。

2026-08-05 の実測（`bundle_human.svb`）:

- keypoints3d を Unity のスケールに合わせて投影したときの、sidecar の元映像 2D との差: **median 21.7 px**（仰向け区間 15.0 px、足首 25〜35 px）
- モデルのスケールを固定して深度で投影した場合: **median 43.1 px**

深度は `z01`（disparity 系の正規化値）から復元されるが、変動幅が 1.12 倍しかないのに対し映像側の見た目は姿勢で 2.5 倍変わるため、深度だけでは投影サイズが合わない。このため source pose のスケールは bbox 基準で毎フレーム求めている（物理的にはモデルのサイズは不変であるべきだが、source pose は「元映像の 2D 位置の再現」が目的なので再現精度を優先する）。

この制約は bundle 側では解消できない。根本的に詰めるなら SMPL の `betas`（体型パラメータ）を Unity モデルへ反映する必要があるが、現状 betas は読み込むだけで未使用。

## bundle_human.svb 回帰テスト

実動画を順次再生し、次を検証する。

- 0:10 付近: 元映像と同じ足側にボールがあり、足表面に配置される。
- 0:20 付近: 足が交差する区間の180連続フレームで、補正が加える最大移動は `19.1 px/frame`（上限 `20 px/frame`）。
- 0:20 と 0:70 付近: source で対応づけた足・右前腕に対する最大表面誤差は `4.9 px`。
- 同区間の対応部位との最大奥行き誤差は `0.018 m`。

接触対象ではない手足が画面上で重なって見えても、奥行きが異なる場合は貫通として扱わない。
