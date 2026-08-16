# Dog Meta-Bone Mapping (categoryId=2)

This table is based on the current in-repo dog asset at `Assets/Prefabs/DogRoot.prefab` and the runtime mapping in `StreamingStereoVideoPlayer.Model.cs`.

Important:
- `Assets/Prefabs/DogRoot.prefab` is a split `MeshRenderer` rig and does not contain `SkinnedMeshRenderer` entries.
- `arm.xxx / foot.xxx / head.001 / neck / body / er.xxx` are mesh node names.
- Runtime should drive their parent rig bones.

## Parent Bone Resolution (from mesh node parent)

| Mesh node | Parent rig bone |
|---|---|
| `body` | `ボーン` |
| `neck` | `ボーン.007` |
| `head.001` | `ボーン.009` |
| `er.L` | `ボーン.009_L.001` |
| `er.R` | `ボーン.009_R.001` |
| `arm.001.L` | `ボーン_L.001` |
| `arm.002.L` | `ボーン_L.002` |
| `arm.003.L` | `ボーン_L.003` |
| `arm.001.R` | `ボーン_R.001` |
| `arm.002.R` | `ボーン_R.002` |
| `arm.003.R` | `ボーン_R.003` |
| `foot.001.L` | `ボーン.001_L.001` |
| `foot.002.L` | `ボーン.001_L.002` |
| `foot.003.L` | `ボーン.001_L.003` |
| `foot.001.R` | `ボーン.001_R.001` |
| `foot.002.R` | `ボーン.001_R.002` |
| `foot.003.R` | `ボーン.001_R.003` |

## Meta Joint (0-19) to Runtime Use

| Meta idx | Semantic (current code interpretation) | Driven parent bone(s) |
|---|---|---|
| 0 | Left eye | Head aim synthesis only (`head`, `neck`) |
| 1 | Right eye | Head aim synthesis only (`head`, `neck`) |
| 2 | (unused in current dog mapping) | - |
| 3 | (unused in current dog mapping) | - |
| 4 | Nose | `ボーン.007` + `ボーン.009` via `5 -> 4` |
| 5 | Throat | `ボーン.007` + `ボーン.009` via `5 -> 4` |
| 6 | Rear hub / tail base (hip side) | `ボーン` via `6 -> 7`; rear leg chains origin |
| 7 | Front hub / withers | `ボーン` via `6 -> 7`; front leg chains origin |
| 8 | Left front upper distal joint | `ボーン_L.001` via `7 -> 8` |
| 9 | Right front upper distal joint | `ボーン_R.001` via `7 -> 9` |
| 10 | Left rear upper distal joint | `ボーン.001_L.001` via `6 -> 10` |
| 11 | Right rear upper distal joint | `ボーン.001_R.001` via `6 -> 11` |
| 12 | Left front lower distal joint | `ボーン_L.002` via `8 -> 12` |
| 13 | Right front lower distal joint | `ボーン_R.002` via `9 -> 13` |
| 14 | Left rear lower distal joint | `ボーン.001_L.002` via `10 -> 14` |
| 15 | Right rear lower distal joint | `ボーン.001_R.002` via `11 -> 15` |
| 16 | Left front paw tip | `ボーン_L.003` via `12 -> 16` |
| 17 | Right front paw tip | `ボーン_R.003` via `13 -> 17` |
| 18 | Left rear paw tip | `ボーン.001_L.003` via `14 -> 18` |
| 19 | Right rear paw tip | `ボーン.001_R.003` via `15 -> 19` |

## Current Risk Notes

- This mapping aligns naming and chain assignment, but does not fix per-bone local axis mismatch by itself.
- If limb twist remains unstable, the next step is DCC-side rig normalization (consistent bone roll/forward axis and deformation-bone separation).

## 調査ログ: DogRoot と P_GermanShepherd のサイズ印象差（2026-06-19）

**症状:** 同じ bundle/同じ検出bboxで再生しても、DogRoot適用時の方が「大きい犬」に見える。

**調査して却下した仮説:**
- FBXのインポートスケール（Scale Factor）の問題 → 誤り。`TrackModelPlacement.ResolveDesiredLocalScale` の
  `scaleH = bboxWorldH / baseBoundsSize.y` は `baseBoundsSize.y` の値に関わらず最終ワールド高さを
  `bboxWorldH` に強制する設計なので、メッシュの素のスケール自体は最終結果に影響しない。実際に
  `dog.fbx` の Scale Factor を変更してみたところ、prefab内で手動オフセットされた各パーツ
  （`arm.003.R` 等）の位置がスケール変更に追従せず分解して見えるだけで、サイズ問題は解決しなかった
  （この変更は不要なので revert 済み）。
- bounds計測に異常な外れ値パーツが混入している → 却下。`[BOUNDS-DBG]` で全Rendererを確認したが、
  DogRootの各パーツ（body/foot.00x/arm.00x/tail/neck/head/er）はいずれも自然な犬の輪郭に沿っており、
  異常な孤立パーツはなかった。

**実測値（2026-06-19、`[BOUNDS-DBG]`/`[SCALE-DBG]` で確認、ログは削除済み）:**

| | baseBoundsSize (W, H, D) | W/H比 | D/H比 |
|---|---|---|---|
| DogRoot | (2.413, 6.689, 8.561) | 0.361 | 1.280 |
| P_GermanShepherd | (0.252, 0.976, 1.217) | 0.258 | 1.247 |

同じ検出bboxを両モデルに適用したときの最終ワールドサイズ予測（`Mathf.Min(scaleW, scaleH)` は両モデルとも
H側が効く＝高さ基準でスケールが決まる）:

| | W | H | D |
|---|---|---|---|
| DogRoot | 0.065m | 0.181m | 0.231m |
| German | 0.046m | 0.180m | 0.224m |

**結論（真因）:** 高さはほぼ一致するが、`Mathf.Min(scaleW, scaleH)` は高さだけをbboxに合わせて固定し、
幅・奥行きはモデル本来のプロポーションのまま追従する。DogRootはGermanよりも「高さに対して幅が広い
（W/H比が約40%大きい）」体型のため、高さを揃えても幅・奥行きがそのぶん大きくなり、体積換算で約1.5倍
「大きい犬」に見える。これはバグではなく、**現在のスケーリング方式（高さのみを基準にした uniform scale）
が、モデル間の体型差を吸収しない**という設計上の特性。

**今後の検討候補（未実装、提案のみ）:**
1. 現状の `Mathf.Min(scaleW, scaleH)` を維持（高さ基準になりやすいが、効く軸がモデルごとに変わる可能性は残る）
2. モデルごとに人間が目視で基準サイズを手動キャリブレーション
   （`ReplaceableModel.referenceHeightMeters` と同様の仕組みを幅・奥行きにも拡張）— 現時点で最有力候補
3. bbox の幅・高さに対して非均一スケール（X/Y/Z個別）— リギング済みモデルでFK姿勢適用時に不自然な伸縮の
   リスクがあるため非推奨
4. 体積（W×H×D）ベースでスケール決定 — bboxからの体積推定が粗くなりがちで複雑化のリスク

### 2026-08-07 追記: 候補 1 は破棄。`Mathf.Min` を撤廃した

上の「効く軸がモデルごとに変わる可能性は残る」という懸念は、実際に起きていた。犬 2 種（W/H 比 0.36 / 0.26）は
どちらも H 側が効くので当時は表面化しなかったが、その後追加した W/H 比が 1 を超えるモデル群
（17_Deer1.0 = 1.82、22_Elk1.0 = 1.84、42_Moose1.0 = 1.57、30_Goose = 1.42、14_BoarV2 = 1.25）では
W 側が効き、`bundle_animal.svb` の 15 shot 中 最大 13 shot で **bbox 高さの 34〜50% まで潰れていた**。

`scaleW` が比べていた「bbox 幅」と「bind pose の X 幅」はそもそも同じものを測っていないため、
`Mathf.Min(scaleW, scaleH)` を `scaleH` のみに変更した。詳細は
[bundle-placement.md](bundle-placement.md) の「NG パターン: Animal のスケールを bbox の『幅』でも制限する」を参照。

**この節の結論（体型差を吸収しない）は変わらない。** 高さを揃えても幅・奥行きはモデル本来のプロポーションの
まま残るので、候補 2（モデルごとの手動キャリブレーション）は依然として有効な選択肢。次にこの件を検討する場合は
別スレッドで。
