# ADR-0001: Animal SMAL FK 姿勢追従の実装方針

**Status:** Accepted  
**Date:** 2026-06-15

---

## Context

bundle の `meta.bin` に `FLAG_SMAL = 1 << 2` が追加され、Animal object に SMAL FK block が付くようになった。  
従来の Animal 姿勢追従は関節位置ベースの AimAt + TwoBone IK だった。  
SMAL FK block には 35 個の 3×3 回転行列（global_orient 1 個 + pose 34 個）と betas[41]・transl[3] が含まれる。

---

## SMAL Joint Topology

AniMer 付属 SMAL 実装（`my_smpl_00781_4_all.pkl`、NUM_JOINTS=34）に基づく。

```
index | name        | parent
------+-------------+-------
0     | root        | -1
1     | pelvis0     | 0
2     | spine       | 1
3     | spine0      | 2
4     | spine1      | 3
5     | spine2      | 4
6     | spine3      | 5
7     | LLeg1       | 6   ← 前脚左付け根（shoulder相当）
8     | LLeg2       | 7
9     | LLeg3       | 8
10    | LFoot       | 9
11    | RLeg1       | 6   ← 前脚右付け根
12    | RLeg2       | 11
13    | RLeg3       | 12
14    | RFoot       | 13
15    | Neck        | 6
16    | Head        | 15
17    | LLegBack1   | 0   ← 後脚左付け根（root直下）
18    | LLegBack2   | 17
19    | LLegBack3   | 18
20    | LFootBack   | 19
21    | RLegBack1   | 0   ← 後脚右付け根（root直下）
22    | RLegBack2   | 21
23    | RLegBack3   | 22
24    | RFootBack   | 23
25    | Tail1       | 0   ← しっぽ（root直下）
26    | Tail2       | 25
27    | Tail3       | 26
28-31 | Tail4-7     | ...
32    | Mouth       | 16
33    | LEar        | 16
34    | REar        | 16
```

---

## Dog Rig → SMAL Mapping

犬モデル（`Assets/Prefabs/DogRoot.prefab`）は脊椎ボーンが `ボーン` 1本のみ。

| SMAL joint | SMAL 名      | Unity ボーン         | AnimalRigCache フィールド |
|-----------|-------------|---------------------|--------------------------|
| 0         | root        | ボーン               | spine（root FK 専用）    |
| 1–6       | pelvis0〜spine3 | BONE MISSING    | —（FK チェーン積算のみ）|
| 7         | LLeg1       | ボーン_L.001         | leftFrontUpper           |
| 8         | LLeg2       | ボーン_L.002         | leftFrontLower           |
| 9         | LLeg3       | ボーン_L.003         | leftFrontPaw             |
| 10        | LFoot       | BONE MISSING        | —                        |
| 11        | RLeg1       | ボーン_R.001         | rightFrontUpper          |
| 12        | RLeg2       | ボーン_R.002         | rightFrontLower          |
| 13        | RLeg3       | ボーン_R.003         | rightFrontPaw            |
| 14        | RFoot       | BONE MISSING        | —                        |
| 15        | Neck        | ボーン.007           | neck                     |
| 16        | Head        | ボーン.009           | head                     |
| 17        | LLegBack1   | ボーン.001_L.001     | leftRearUpper            |
| 18        | LLegBack2   | ボーン.001_L.002     | leftRearLower            |
| 19        | LLegBack3   | ボーン.001_L.003     | leftRearPaw              |
| 20        | LFootBack   | ボーン.001_L.004     | leftRearToe（新規）      |
| 21        | RLegBack1   | ボーン.001_R.001     | rightRearUpper           |
| 22        | RLegBack2   | ボーン.001_R.002     | rightRearLower           |
| 23        | RLegBack3   | ボーン.001_R.003     | rightRearPaw             |
| 24        | RFootBack   | ボーン.001_R.004     | rightRearToe（新規）     |
| 25        | Tail1       | ボーン.003           | tailBase                 |
| 26        | Tail2       | ボーン.004           | tailMid（新規）          |
| 27        | Tail3       | ボーン.005           | tailTip（新規）          |
| 28–34     | Tail4-7/Mouth/Ear | BONE MISSING |  —（初版は省略）        |

---

## FK 公式

**2026-06-18 訂正:** 初版は `tw[j] = parentTW * bindRotLocal[j] * pose[j]`（bindLoc が pose より先）だったが、
これは誤り。標準 SMPL/SMAL LBS（`smplx.lbs(..., pose2rot=False)`）の回転合成は

```
Rot(transform_chain[j]) = Rot(transform_chain[parent[j]]) @ R_j
```

であり、rest pose のボーン間の曲がりは **回転ではなく rel_joints（translation offset）のみ**で表現される。
つまり SMAL の世界では「pose[j] をそのボーン固有の bind local 回転の中で解釈する」という概念は存在せず、
pose[j] は常に **親の蓄積フレームに直接重ねる**ものである。

一方 Unity 側の犬リグは Blender で「自然な立ちポーズ」として作られており、各ボーンの bind local 回転
（`bindRotLocal`）はゼロではない（例: leftFrontLower の bindLoc ≈ (18.8°, 0, 0)）。これは SMAL の
rest pose の概念と違うため、composition の中で別に扱う必要がある。

正しい順序は **pose を先、bindLoc を後**:

```
tw[0] = worldGlobalOrient * bindRotWorld[spine]
bone[spine].rotation = tw[0]

tw[j] = tw[parent[j]] * pose[j] * bindRotLocal[bone[j]]   // bone あり（pose が先、bindLoc が後）
tw[j] = tw[parent[j]] * pose[j]                           // BONE MISSING（bindRotLocal=identity なので同じ式）

bone[j].rotation = tw[j]
```

この順序なら pose[j]=identity のとき `tw[j] = tw[parent[j]] * bindRotLocal[bone[j]]` となり、
再帰的に展開すると元の bind pose（自然な立ちポーズ）に一致する（下記 T-pose 検証）。
**pose[j]=identity のときは乗算順序が結果に影響しないため、この誤りは T-pose 検証だけでは検出できない。**
実機で「データ上は大きく回転しているのに脚が曲がって見えない（捻りになっている）」という症状で発覚した。

**もう一つの前提条件（2026-06-18 さらに訂正）:** `pose[j]`（SMAL ローカル回転）は SMAL 側の座標系
（Z-up 系）で読まれた行列から作られているため、Unity ワールド座標系で `parentTW` と直接合成する前に
軸を合わせる必要がある。最初は `C * pose[j] * Inverse(C)` で共役変換したが、これは見た目にほぼ反映され
ない結果になった。正しい変換は次の連鎖で導出できる：

`worldFk0 = camRotation * globalOrient * C` という既存の式を「SMAL ネイティブのチェーン
`Rot_smal[j] = Rot_smal[parent[j]] * pose[j]` を Unity ワールド座標系に変換したもの」とみなして
`U[j] := camRotation * Rot_smal[j] * C` を定義すると、

```
U[j] = camRotation * Rot_smal[parent[j]] * pose[j] * C
     = (camRotation * Rot_smal[parent[j]] * C) * (Inverse(C) * pose[j] * C)
     = U[parent[j]] * (Inverse(C) * pose[j] * C)
```

つまり関節ごとに合成すべき補正後の pose は **`Inverse(C) * pose[j] * C`**（`C * pose[j] * Inverse(C)` ではない、向きが逆）。

さらに、この `U[j]` はまだ「ワールド座標系での軸補正のみ」を反映した値であり、`bone[j]` 固有の bind local
回転（`bindRotLocal`）を合成する前段階では、**bone[j] の実際の Unity 親ボーンの bind world 回転
（`bindRotWorld[bone[j].parent]`）の枠組みに re-express する**必要がある（`Inverse(parentBindWorld) * U補正 * parentBindWorld`）。
これが必要な理由は、SMAL の論理親（`SmalJointParentArray[j]`）と実際の Unity 親ボーンが一致しない関節が
あるため（例: 前脚・首は SMAL 上の親が joint 6 だが、Unity 上の実際の親ボーンは spine 1 本「ボーン」）。
この再表現を省くと、`bindRotWorld`/`bindRotLocal` が前提とする「親ボーンのローカル枠」と異なる枠で
pose 補正が合成され、結局ねじれ（twist）寄りの結果になってしまう。

この式（`worldFramePose`/`localPose` 経由）を実装してテストしたが、**実機ではまだ脚がほとんど動かなかった**。
理由として、`canonicalCorrection`（C）はノーズ方向（forward 1軸）だけで実機キャリブレーションされた値であり、
roll/twist 成分が未確定のまま。この C を関節ローカル補正の共役変換に再利用すると、roll が間違っているために
結果がねじれ寄りになり続けた可能性が高い。

**2026-06-18 三度目の訂正: C を再利用せず、関節ごとに rest skeleton から幾何学的に補正を導出する方式に変更。**
bundle 作成側から `Docs/smal-rest-skeleton.json`（SMAL rest pose の J array, 35×3, SMAL ネイティブ座標系）
を入手できたため、global の C に頼らず関節ごとに以下を行う：

1. `smalRestDir[j]` = SMAL rest skeleton で「joint j → その運動学的子 joint」への方向ベクトル（J array から事前計算、コード内に定数で保持）
2. `smalPosedDir[j] = (pose[j]適用後のsmalRestDir[j]).normalized`（SMAL ネイティブ座標系内のみで計算、C 不要）
3. `bendSmal[j] = Quaternion.FromToRotation(smalRestDir[j], smalPosedDir[j])`（曲げ成分のみ抽出。ひねり成分は捨てる）
4. `unityRestDirWorld[j] = bindRotWorld[bone[j]] * bindDirLocal[bone[j]]`（Unity 犬リグの実際のbind時方向。
   `bindDirLocal` は既存の `RegisterAnimalAimPairs` / `TryGetBoneCenterDirectionWorld` 機構がすでに記録している）
5. `jointFrameMap[j] = Quaternion.FromToRotation(smalRestDir[j], unityRestDirWorld[j])`（SMAL→Unity、関節ごとの実測対応）
6. `bendUnity[j] = jointFrameMap[j] * bendSmal[j] * Inverse(jointFrameMap[j])`（共役変換でUnity空間に転送）
7. `tw[j] = bendUnity[j] * tw[parent[j]] * bindRotLocal[bone[j]]`

この方式は global の C のroll不確実性に依存せず、関節ごとに実測した rest 方向の対応だけで補正を決めるため
頑健。`RegisterAnimalAimPairs` で aim child が登録されている関節（前脚/後脚の upper・lower、neck）のみ対応
（`AnimalSmalFkApplier.cs` の `SmalRestDirByJoint`）。対応がない関節（paw・head・tailBase 等）は従来の
`localPose * bindRotLocal` 式にフォールバックする。

BONE MISSING（仮想 spine chain）の場合は `tw[j] = tw[parent[j]] * worldFramePose[j]`（親ボーン枠がないため再表現なし）。

**T-pose 検証（全 pose = identity）:**  
`tw[j] = worldGlobalOrient * bindRotWorld[bone[j]]` ✓（乗算順序に関わらず成立するため、この検証だけでは
上記の順序ミスを発見できない。実機の動画比較が必須）

**SMAL 構造由来の注意点:**
- 前脚・首（parent=6）は `tw[6]`（spine3 頂点の accumulated FK）を親フレームとして使う
- 後脚・しっぽ（parent=0）は `tw[0]`（root world）を親フレームとして使う
- 犬モデルでは全ての骨が `ボーン` 1本から生えているが、FK チェーンは SMAL の論理親に従う

**参考データ:** `third_party/AniMer/data/smal/my_smpl_00781_4_all.pkl` から抽出した SMAL rest skeleton
（J array 35×3、kintree_parent_by_joint）は bundle 作成側から共有された。軸は推定で +X=root→head,
+Y=モデル左, +Z=上。rest pose の曲がりが pure translation で表現されていることは、この J array で
LLeg1→LLeg2→LLeg3 の offset 方向が segment ごとに変化している（=回転なしで曲がっている）ことから確認できる。

---

## 座標変換

Human SMPL と同一ルール（第一候補・実機ログ確認後に調整）:

| データ    | flipCameraY | 備考 |
|----------|-------------|------|
| global_orient (index 0) | true  | D\*R（row1 否定）|
| pose (index 1-34)       | false | R をそのまま使用 |

---

## AnimalRigCache 拡張

```csharp
// 新規フィールド（Transform）
public Transform leftRearToe;
public Transform rightRearToe;
public Transform tailMid;
public Transform tailTip;

// 新規辞書（FK 計算用 T-pose world rotation）
public readonly Dictionary<Transform, Quaternion> bindRotWorld
    = new Dictionary<Transform, Quaternion>();
```

`PrimeAnimalBind` で `bindRotLocal`（既存）と `bindRotWorld`（新規）を同時にキャプチャする。

---

## アーキテクチャ

- **FLAG_SMAL パース**: `TryReadFrameObjects` に `(flags & 0x04) != 0` 判定を追加し `StoreSmalBlockFromBin` を呼ぶ
- **SMAL データ保管**: `StreamingStereoVideoPlayer` に `animalSmalPosesMetaBin` (`Dictionary<int, Dictionary<uint, AnimalSmalPose>>`) を追加
- **FK 適用**: `AnimalPoseApplier` を拡張。`AnimalPoseRequest` に `hasSmalPose` / `smalPose` を追加
- **パイプライン分岐**: `AnimalPoseApplier.Apply()` で SMAL あり → FK モード（IK をスキップ）、なし → 既存 IK モード
- **FK コード配置**: 新ファイル `Assets/Scripts/StereoPlayer/AnimalSmalPoseApplier.cs`（`AnimalPoseApplier` の partial または 別クラスで `Apply` 内から呼ぶ）

---

## Consequences

- SMAL FK が使える場合、TwoBone IK は一切使わない（Human と同一ポリシー）
- betas（形状パラメータ）は初版では使わない（Unity 側に SMAL mesh deformation なし）
- transl は配置に使わない（anchor_u/v/z が authoritative、CLAUDE.md ルールに準拠）
- 耳ボーン（joint 33/34）は初版スコープ外。必要になれば追加
