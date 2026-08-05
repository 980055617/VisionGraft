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

`human_smpl_from_sam2.json` の `box_xywh` と `predKeypoints2d` から、各フレームの元映像上の Human 2D keypoint を復元する。
Other に近い身体セグメントを元映像側で選び、セグメント上の接触位置と接触方向を、表示 Humanoid の対応セグメントへ写像する。

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
- `human_smpl_from_sam2.json` に有効な `box_xywh` と `predKeypoints2d` があること
- Human と Other が同じ eye にあり、Other が source 上で身体部位の接触範囲内にあること

source pose sidecar がない場合や接触部位を特定できない場合は、誤った部位へ吸着させず補正を行わない。

体型・骨長そのものを完全再現する処理ではない。細長い物体、複雑な形状、手指単位の接触などは、現在の身体セグメント近似では限界がある。

## 設定

`StreamingStereoVideoPlayer`:

- `enableHumanOtherContactCorrection`
- `humanOtherFullContactRadiusMultiplier`（既定値 1.25）
- `humanOtherReleaseRadiusMultiplier`（既定値 2.0）
- `humanOtherContactSurfacePaddingPixels`（既定値 2.0）

既存シーンへの影響を避けるため機能の既定値は OFF。`SampleScene` では ON。

## bundle_human.svb 回帰テスト

実動画を順次再生し、次を検証する。

- 0:10 付近: 元映像と同じ足側にボールがあり、足表面に配置される。
- 0:20 付近: 足が交差する区間の180連続フレームで、補正が加える最大移動は `19.1 px/frame`（上限 `20 px/frame`）。
- 0:20 と 0:70 付近: source で対応づけた足・右前腕に対する最大表面誤差は `4.9 px`。
- 同区間の対応部位との最大奥行き誤差は `0.018 m`。

接触対象ではない手足が画面上で重なって見えても、奥行きが異なる場合は貫通として扱わない。
