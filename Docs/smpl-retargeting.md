# SMPL → Unity Humanoid リターゲティング 実装ガイド

> **構成:** `発表用まとめ` がゼミ向け概要。`関連ファイル`〜`SMPL ↔ Unity Humanoid 座標系対応` が**実装リファレンス**（FK 公式・座標変換ルール・マッピング表）。`調査ログ` 以降が**なぜこの実装になったかの経緯記録**。

## 発表用まとめ

### Human SMPL → Unity Humanoid Rig

方式は **FK のみ**（IK 禁止・暫定。将来 FK vs IK の比較検証が必要）。

```
tw[j] = parentTW * bindRotLocal[bone] * bodyPose[j]
→ ApplyWorldRotation(bone, tw[j])
```

- Spine/Chest は推定値が過大（34〜63°）なので `SpineBodyPoseScale=0.25` でダウンスケール補正
- 腕・脚は SMPL ジョイント座標も使って AimAt で向きを微調整（1 ボーンずつの forward 補正）
- **なぜ `parent * bind * pose` の順か**: SMPL body_pose は「親ボーンの現在姿勢からの相対回転」という規約。実際に右腕で約 77° のズレが出て発覚した

### Animal SMAL → カスタム Animal Rig

同じ FK 公式を使用。SMAL ネイティブ座標系が Unity と異なるため `SmalDataAxisCorrection = Euler(0,90,90)` を適用（実機キャリブレーション確定値）。一部の関節（脚・首・尻尾の付け根/中間）は rest skeleton との差分から幾何的に補正（詳細 [ADR-0001](adr/0001-animal-smal-fk.md)）。尻尾は2026-07-16に追加対応（下記「調査ログ」参照）。

複数モデル対応（[ADR-0002](adr/0002-animal-rig-generalization.md)）: ボーン名トークンマッチング + 前後脚の実測位置から正面方向を動的に推定。DogRoot・P_GermanShepherd で検証済み。

**Human と異なる理由**: Human は Unity 標準 Humanoid Avatar があるため関節 FK が直接対応するが、Animal はカスタムリグのためジョイント対応・座標補正を自前で構築する必要があった。

---

## 関連ファイル

| ファイル | 役割 |
|---|---|
| `Assets/Scripts/StereoPlayer/StreamingStereoVideoPlayer.HumanSmpl.partial.cs` | 座標変換・FK ループ（メイン） |
| `Assets/Scripts/StereoPlayer/StreamingStereoVideoPlayer.Humanoid.partial.cs` | HumanoidRigCache 構築 |
| `Assets/Scripts/StereoPlayer/HumanoidRigCache.cs` | bindRotLocal / bindRotWorld 定義 |
| `Assets/Scripts/StereoPlayer/StreamingStereoVideoPlayer.PersonSmpl24.partial.cs` | 人物配置 |
| `Assets/Scripts/StereoPlayer/StreamingStereoVideoPlayer.PosePipeline.partial.cs` | パイプライン制御 |
| `Assets/Scripts/StereoPlayer/TransformWriter.cs` | bone rotation 設定ユーティリティ |

---

## 座標変換ルール

バンドルは **globalOrient・body_pose ともに R（通常形式・column convention）で格納**している。

| データ | flipCameraY | transposeMatrix | 備考 |
|---|---|---|---|
| `globalOrient` | true | false | row1 否定 → column 抽出 → D\*R |
| `body_pose` | false | false | column 抽出 = R をそのまま使用 |
| joint positions (meta.bin) | — | — | 変換済みをそのまま使用 |

`TryReadRotationMatrix` のパラメータ:
- `globalOrient` → `flipCameraY: true, transposeMatrix: false`
- `body_pose` → `flipCameraY: false, transposeMatrix: false`

**NG パターン（座標変換）:**
- `D*R*D`（両辺乗算）→ 直立人が上下逆
- body_pose に `transposeMatrix: true`（row 抽出 = R^T）→ LHip が後ろ・LKnee が伸びる ✗（実機確認済み）
- globalOrient に `transposeMatrix: true` → R^T が fk[0] に入り FK 全体がずれる ✗

**なぜ直立テストで body_pose のバグが見えないか:**
body_pose = identity のとき R = R^T = I なので row/column 抽出で差が出ない。歩行など動的ポーズで初めてずれが現れる。

**検証済みデータ（複数フレーム実機確認）:**
- `transposeMatrix:false`（R）: LHip X ≈ -65° → hip flexion（前） ✓、LKnee X ≈ +35° → knee flexion（前） ✓
- `transposeMatrix:true`（R^T）: LHip X ≈ +67° → hip extension（後ろ） ✗、LKnee 直立 ✗

---

## FK 公式

### 正しい公式（2026-08-07 確定・リグ非依存）

```
bodyFk[0] = identity
bodyFk[j] = bodyFk[parent] * bodyPose[j]                        （body_pose の world 累積）
bone.rotation[j] = worldGlobalOrient * bodyFk[j] * bindRotWorld[j]
```

右辺は公開ヘルパー `ResolveHumanSmplTargetWorldRotation(worldGlobalOrient, bodyFk[j], bindRotWorld[j])`
そのもので、Hips に使っている式と同じ形（Hips は `bodyFk = identity` のケース）。

**根拠:**
- SMPL の rest 姿勢では**全 joint のフレームが world 軸と平行**（= rest rotation が identity）。
  したがって body_pose は world 軸で表現された回転であり、親から順に world 空間で積める
- モデル側の bind 回転は最後に一度だけ右から掛ける。**リグの軸規約は bindRotWorld[j] に閉じ込められ、
  body_pose の解釈には影響しない**
- T-pose 検証（bodyPose=identity）: `bodyFk[j] = identity` → `worldGlobalOrient * bindRotWorld[j]` ✓
- リグ非依存性の実測: 同じ body_pose を npc_casual_set と Renderpeople に入れたときの向きの
  食い違いが**全ボーンで 0.0°**（旧公式は平均 58.6°）。[調査ログ 2026-08-07](#2026-08-07-renderpeople-モデル14-16で手足が破綻--fk-公式がbindrotworld--identity-のリグでしか成立していなかった)
- 回帰テスト: `HumanSmplTargetWorldAppliesTheSameWorldRotationRegardlessOfRigBindAxes`

### 旧公式（2026-06-13〜2026-08-07・リグ依存・破棄）

```
tw[j] = parentTW[j] * bindRotLocal[j] * bodyPose[j]
      = worldGlobalOrient * bindRotWorld[j] * bodyPose[j]      （展開形）
```

body_pose を `bindRotWorld` の**右**から掛けており、body_pose をボーンのローカル軸で解釈していた。
**`bindRotWorld[j] ≈ identity` のリグでしか成立しない**。npc_casual_set は胴・肩・腕が 0〜20° と
identity 近傍だったため偶然成立していたが、Renderpeople（14-16）は全ボーンが 90〜120° 乖離しており破綻する。

### FK ループ実装（HumanSmpl.partial.cs）

```csharp
Quaternion worldGlobalOrient = fk[0];  // smoothed globalOrient
// fk 配列を bodyFk（body_pose の world 累積）として再利用する。
// worldGlobalOrient を先に控えてから identity で初期化すること。
Quaternion[] bodyFk = fk;
bodyFk[0] = Quaternion.identity;

for each joint in SmplJointTopologicalOrder (1..21):
    // Spine/Chest は SpineBodyPoseScale でスケールダウン（SMPL 過大推定対策）
    Quaternion fkLocal = (joint == 3 || joint == 6)
        ? Quaternion.Slerp(Quaternion.identity, smplLocal, SpineBodyPoseScale)
        : smplLocal;

    // world 累積は Unity ボーンの有無と無関係に常に積む
    bodyFk[joint] = bodyFk[SmplJointParentArray[joint]] * fkLocal;

    // HumanBone マッピングなし / BONE MISSING (UpperChest 等) → 積算済みなのでスキップしてよい
    if (!SmplJointToHumanBone.TryGetValue(joint, out boneId)) continue;
    if (!cache.bones.TryGetValue(boneId, out bone) || bone == null) continue;
    if (!cache.bindRotWorld.TryGetValue(boneId, out bindW)) continue;

    ApplyWorldRotation(bone, ResolveHumanSmplTargetWorldRotation(worldGlobalOrient, bodyFk[joint], bindW));
```

BONE MISSING に特別扱いが要らないのは、`bodyFk[]` が bindRot を含まないため。
旧公式では `bindRotLocal` が「Unity 親」相対だったので、途中の joint が欠けるとフレームがずれ、
UpperChest の特殊処理が必要だった。

### なぜ ApplyWorldRotation が必要か

`ApplyLocalRotation` では `bone.rotation = bone.parent.rotation * targetLocal`。
UpperChest が hierarchy に存在するが Avatar 未登録の場合、LeftShoulder の `bone.parent.rotation = UpperChest.rotation`（FK 未設定の誤値）になり腕が誤方向になる。
`ApplyWorldRotation` では `bone.rotation = targetWorld`（直接）なので親の rotation 影響を排除できる。

### Hips の設定（FK ループの前）

```csharp
targetHipsWorld = ResolveHumanSmplTargetWorldRotation(fk[0], Quaternion.identity, bindHipsWorld)
               // = fk[0] * bindHipsWorld = worldGlobalOrient * bindHipsWorld
Hips.localRotation = Inv(Hips.parent.rotation) * targetHipsWorld
```

### camRotation

```csharp
camRotation = LookRotation(-screenFront, screen.up)  // TryGetPinholeBasis() で取得
// screen.up ≈ world +Y（VR ヘッドセット up）
```

### NG パターン（FK 公式）

> ⚠️ この表の「現象」は **npc_casual_set 1 リグでの観測**。同リグは胴・肩・腕の bindRotWorld が
> identity 近傍という特殊性があるため、ここでの却下理由が他リグでも成り立つとは限らない。
> **1 行目は 2026-08-07 に撤回した**（下記）。

| パターン | 現象 |
|---|---|
| ~~`globalOrient * fk_body * bindRotWorld`~~ | **撤回（2026-08-07）**: これが正しい公式。当時 `fk_body` を world 累積ではなく親相対のまま作っていたため誤差が出ていた。`fk_body[j] = fk_body[parent] * bodyPose[j]` と world で積めば全リグで正しい |
| `targetWorld = bindRotWorld[j] * globalOrient * body_pose` | 乗算順序が逆。足が後ろ向き |
| `targetWorld = bindRotWorld[j] * fk[j]`（fk[0] リセットなし） | 同上 |
| `correctedLocal = tPoseLocal * smplLocal` + ApplyLocalRotation | UpperChest BONE MISSING 問題で腕が誤方向 |
| ApplyLocalRotation + `Inv(parentTargetWorld) * targetWorld` | UpperChest hierarchy で腕が誤方向（bone.parent.rotation 依存） |
| BONE MISSING で fkLocal スキップ（tw[joint] = parentTW のみ） | UpperChest 回転が肩・腕に伝わらない → 腕がずれる ✗ |
| `globalOrient * bindRotWorld * fk_body` | 乗算順序が逆（bindRot → body_pose の逆）→ 腕方向誤り ✗ |
| Q_Y180 補正（右腕 joints 14,17,19,21）| 旧公式 `globalOrient * fk_body * bindRotWorld` の誤差を補正しようとしたが、約 77° ズレを生じる |
| `globalOrient * fk_body * Inv(globalOrient) * bindRotWorld` | T-pose で = bindRotWorld（globalOrient が四肢に伝わらない）✗ |

---

## SMPL ↔ Unity Humanoid 座標系対応

### T-pose の定義

**SMPL T-pose:**
- body_pose = identity のとき全 joint の world orientation = identity（= pelvis frame）
- body_pose[j] は「pelvis T-pose frame での joint j の回転変化量」

**Unity T-pose:**
- bindRotWorld[j] = Unity T-pose での bone j の world rotation（≠ identity の場合が多い）
- bindRotLocal[j] = bindRotWorld[parent]^-1 * bindRotWorld[j]
- HumanPoseHandler で muscles=0 にして計測（GetOrBuildHumanoidCache で実施）

### UpperChest（joint=9）の特殊ケース

> ⚠️ **2026-08-07: この特殊扱いは不要になった**（以下は旧公式時代の記録）。
> 新公式の `bodyFk[]` は bindRot を含まないので、Unity ボーンが無い joint は
> `bodyFk[j] = bodyFk[parent] * bodyPose[j]` を積むだけでよく、`FindEffectiveParentFk` も削除した。
> なお UpperChest を持つリグは Renderpeople が初ではない（07_Human_Beta / Mixamo が `Spine2` を持つ）。

- Unity Humanoid Avatar に登録されていないが bone hierarchy に transform として存在する場合がある
- `Animator.GetBoneTransform(HumanBodyBones.UpperChest)` = null → **BONE MISSING** 扱い
- 正しい処理: `fk[9] = fk[6] * smplLocal[9]`（body_pose は FK に積算、bone rotation 設定はスキップ）
- NG: `fk[9] = fk[6]`（smplLocal スキップ）→ UpperChest 回転が肩・腕に伝わらない ✗
- LeftShoulder の `bone.parent` が UpperChest transform を指すため ApplyLocalRotation を使うと誤値伝播

### SmplJointParentArray（変更禁止）

```
[-1, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 12, 13, 14, 16, 17, 18, 19]
```

### SmplJointToHumanBone マッピング

| SMPL joint | HumanBodyBones |
|---|---|
| 0 | Hips |
| 1 / 2 | LeftUpperLeg / RightUpperLeg |
| 3 | Spine |
| 4 / 5 | LeftLowerLeg / RightLowerLeg |
| 6 | Chest |
| 7 / 8 | LeftFoot / RightFoot |
| 9 | UpperChest（BONE MISSING の場合あり） |
| 12 | Neck |
| 13 / 14 | LeftShoulder / RightShoulder |
| 15 | Head |
| 16 / 17 | LeftUpperArm / RightUpperArm |
| 18 / 19 | LeftLowerArm / RightLowerArm |
| 20 / 21 | LeftHand / RightHand |

---

## 配置の実測方法（2026-08-06 整備）

推測で修正案を出す前に、**配置したモデルを画面へ再投影して meta.bin の bbox と直接比べる**。`StreamingStereoVideoPlayer.logPlacementMeasurement`（既定 OFF）を ON にすると 2 種類のログが出る。

```
[PLACE] f=300 track=0 Person sizeRatio=1.569 topDelta=-134.9 bottomDelta=0.0
        proj[top=190.1 bot=562.0 h=371.9] bbox[top=325 bot=562 h=237]
        anchorV=525 depth=0.757 scale=0.1753
        boneRatio=1.334 boneTopDelta=-95.9 boneBottomDelta=-16.6
        topBone=RightToes bottomBone=LeftLittleDistal

[BONELEN] thigh=0.0781 shin=0.0729 torso=0.1006 ... | 胴で正規化: leg=1.501 ...
```

| 値 | 意味 |
|---|---|
| `sizeRatio` | `renderer.bounds`（world 軸平行 AABB）の投影高さ ÷ bbox 高さ。1.0 なら映像どおり |
| `boneRatio` | Humanoid の**全ボーン位置**の投影高さ ÷ bbox 高さ。AABB と違い姿勢が傾いても過大評価しない |
| `topBone` / `bottomBone` | 最上端・最下端がどのボーンか。**姿勢によって足にも手の指にもなる** |
| `[BONELEN]` | 表示モデルの骨長。meta.bin の keypoints3d から測った骨長と比べて体型差を見る |

**`sizeRatio` と `boneRatio` の両方を見ること。** AABB は姿勢が傾くと過大に出るので、`sizeRatio` だけが大きい場合は計測アーティファクトの可能性がある。両方大きければモデルが実際に大きい。

### バッチモードでの実行

Unity Editor を閉じた状態で `PlacementMeasurementTests` を実行する（`bundle_human.svb` を実際に再生して 70 秒ぶんのログを取る）。

```
& "C:\Program Files\Unity\Hub\Editor\6000.0.60f1\Editor\Unity.exe" `
  -batchmode -projectPath <project> -runTests -testPlatform EditMode `
  -testFilter "PlacementMeasurementTests" -logFile <log> -testResults <xml>
```

- `-nographics` は付けない（VideoPlayer のデコードに必要）
- `Start-Process` で起動する。`& $unity ...` を PowerShell のバックグラウンド実行から呼ぶとプロセスが立ち上がらないことがあった
- **テストが参照するシーン名に注意**。2026-08-05 に `SampleScene` → `TestScene` へリネームされており、追従を忘れると `Scene file not found` で全テストが失敗する

## 調査ログ

### 2026-08-07: Renderpeople モデル（14-16）で手足が破綻 — FK 公式が「bindRotWorld ≈ identity のリグ」でしか成立していなかった

`16_Male_Eric` を `bundle_human.svb` で再生すると手足が大きく崩れる。14・15（同じ Renderpeople）も同条件。

**検証方法**（実機不要・オフライン）

`bundle_human.svb` の `meta.bin` から実 body_pose を取り出し（frame 300/600/900/1200/1500、track 0）、
2 つのリグに同じ θ を入れて各ボーンの**長軸 world 方向**を比較した。
リグの bind pose は Renderpeople が FBX `.meta` の `skeleton`（T-pose と確認済み）、
npc_casual は prefab の保存姿勢の長軸だけ T-pose に補正し roll（軸規約）は保持したもの。

**結果: 現行公式 `tw[j] = parentTW * bindRotLocal[j] * θ[j]` は展開すると `G * bindRotWorld[j] * θ[j]`**

θ を **bindRotWorld の右**から掛けている＝ θ をボーンのローカル軸で解釈している。
SMPL の body_pose は canonical フレーム（rest で world 軸平行）の回転なので、
正しくは `Θ[j] * bindRotWorld[j]`（Θ = world 累積）と**左**から掛ける。
両者が一致する条件は **bindRotWorld[j] ≈ identity**。

各モデルの bindRotWorld の identity からの乖離 [deg]:

| モデル | Hips | Spine | Chest | UpperChest | Neck | Shoulder | UpperArm | LowerArm | UpperLeg | Foot | Toes |
|---|---|---|---|---|---|---|---|---|---|---|---|
| npc_casual_set (00-05, 08-13) | 0 | 3 | 4 | 無し | 10 | 8 | 20 | 51 | 119 | 160 | 178 |
| 07_Human_Beta (Mixamo) | 1 | 8 | 8 | 7 | 7 | **129** | 120 | 120 | 180 | 180 | 180 |
| 14-16 (Renderpeople) | **120** | **120** | **120** | **120** | **116** | **90** | **90** | **90** | **121** | **120** | **90** |

npc_casual は胴・肩・腕が identity 近傍なので現行公式が**偶然**成立していた。
脚は 119-178° と大きいが、後段の `TryApplySmplArmSegment`（AimAt）が SMPL joint 位置から
方向を上書きするため誤差が隠れる。Renderpeople は全ボーンが 90-120° なので全滅する。

**現行公式の誤差（正しい公式との長軸方向のずれ、5 フレーム平均 [deg]）**

| ボーン | Eric | npc_casual | AimAt の有無 |
|---|---|---|---|
| Spine / Chest | 35.5 / 37.3 | 0.0 / 0.0 | **無し → 見える** |
| UpperChest | 43.7 | （ボーン無し） | **無し → 見える** |
| Neck / Head | 56.6 / 55.0 | 8.0 / - | **無し → 見える** |
| LeftShoulder / RightShoulder | 67.6 / 61.0 | 1.0 / 0.9 | **無し → 見える** |
| LeftToes / RightToes | 31.3 / 35.9 | （子ボーン無しで測定不可） | **無し → 見える** |
| LeftUpperArm / LeftLowerArm | 128.3 / 118.9 | 0.9 / 6.1 | 有り（方向は救われるが **roll は残る**） |
| UpperLeg / LowerLeg / Foot | 26-53 | 29-45 | 有り（同上） |
| LeftHand / RightHand | 110.1 / 71.8 | 6.3 / 17.8 | `TryApplyHandFkAfterAimAt` が world rotation を直接上書き |

**同じ θ を 2 リグに入れたときの食い違い（0 が正解）**

| 公式 | 全ボーン平均 |
|---|---|
| 現行 `parentTW * bindRotLocal * θ` | **58.6°** |
| 左掛け `Θ[j] * bindRotWorld[j]` | **0.0°**（全ボーンで完全一致） |

**結論と対応（実装済み）**

- 手・足が目立つのは、AimAt が四肢の**方向**しか直さないため。roll（ねじれ）と、AimAt を持たない
  Shoulder・Toes・胴の誤差がそのまま残り、末端ほど累積して見える
- **FK 公式を左掛けに修正した**（`HumanSmpl.partial.cs`）。`ApplyWorldRotation` のままで実現でき、
  IK も不要。[FK 公式](#正しい公式2026-08-07-確定リグ非依存)を参照
- BONE MISSING の特殊扱いを削除した（`FindEffectiveParentFk` も未使用のため削除）
- 回帰テストを追加: `HumanSmplTargetWorldAppliesTheSameWorldRotationRegardlessOfRigBindAxes` /
  `HumanSmplTargetWorldReturnsBindPoseWhenBodyPoseIsIdentity`
- **既存 14 体の実機確認が未了**。オフラインでは npc_casual の胴・肩・腕の変化は 0〜6°、脚は
  AimAt が上書きするので小さい見込みだが、07_Human_Beta（Mixamo）は肩の bindRotWorld が 129° 乖離
  しており**旧公式では既にずれていたはず**なので、見た目が変わる

**真因の追加発見（2026-08-07 夜）: body_pose の基底変換が抜けていた**

上記の FK 公式修正のあと `enableKeypointAimAt` を false にして純 FK を確認したところ、**腕が大きく破綻した**。
調べると FK 公式とは別に、**body_pose の座標系変換が最初から抜けていた**。

| 読み込み経路 | 変換 |
|---|---|
| `global_orient` | `flipCameraY: true`（row1 反転）✓ |
| `body_pose` | `flipCameraY: false` → **無変換** ✗ |
| keypoint (`DecodeJointCamFromBundle`) | `return bundleCam`（無変換 = bundle の joint は既に Unity と同じ Y-up）|

body_pose だけ基底が揃っていない。必要なのは基底変換の共役 `S R S⁻¹`（`S = diag(1,-1,-1)`、
右手 Y-down → 左手 Y-up）。クォータニオンでは `(x, y, z, w) → (x, -y, -z, w)`。

**検証**: 座標系変換の候補 16 通り（基底 4 種 × 転置有無 × global_orient 側 4 種）を総当たりし、
SMPL FK した四肢の向きと keypoint の向きの角度差を実測（15 フレーム平均）。

| body_pose の扱い | 平均 | L上腕 | L前腕 | R上腕 | R前腕 | L腿 | L脛 | R腿 | R脛 |
|---|---|---|---|---|---|---|---|---|---|
| **無変換（修正前の実装）** | **78.2°** | 132 | 107 | 128 | 97 | 39 | 28 | 47 | 48 |
| **conj X（採用）** | **8.5°** | 13 | 10 | 12 | 13 | 6 | 5 | 6 | 3 |
| conj Y（= 旧 Q_Y180 補正の形） | 65.2° | 83 | 70 | 103 | 71 | 47 | 68 | 34 | 45 |

ランダムな向きの期待値は 90°。**修正前の腕は 97〜132° で、姿勢として成立していなかった**。
`conj X` が 16 通り中の唯一の最良解で、次点（16.5°）とも明確に差がある。

**これで過去の経緯がすべて説明できる:**

- 2026-06-13「右腕が約 77° ズレる」→ 基底変換漏れ。左右で bindRotWorld が 180°Z 違うため右腕に強く出た
- Q_Y180 補正（x,z 反転 = `conj Y`）を右腕だけに当てた → 上表のとおり `conj Y` は 65.2° で改善が中途半端。
  「複数関節への累積適用で 77° ズレる」という当時の観察と整合する
- AimAt 導入 → keypoint の位置から向きを作り直すので、**body_pose の座標系が壊れていても見た目が合う**。
  つまり AimAt は補助ではなく**四肢の姿勢を作る主役**だった。off にすると破綻するのはこのため
- **`enableKeypointAimAt = false` で腕が壊れたことが、この真因を炙り出した**

**対応**: `ConvertSmplBodyPoseToUnityBasis` を `StoreSmplBlockFromBin` の読み込み時に適用。
回帰テスト 3 本（Y/Z 反転・identity 不変・自己逆元）を追加。

**派生: 手が体に埋もれる問題（frame 38-40 付近、index 0 / 16 の両方）**

keypoint を疑ったが**健全**だった。frame 28-55 で `vis` は全て 1/1/1、値も連続で飛びがなく、
手首の体幹軸からの距離は 0.25〜0.40 m（肩幅 0.33 m）で体に食い込む値ではない。

原因は meta.bin に**独立した推定が 2 つ**入っていて、それを部位ごとに混ぜていること。

| 部位 | 従うソース |
|---|---|
| 肩（Collar, joint 13/14） | SMPL body_pose（AimAt なし） |
| 上腕・前腕 | keypoint（`TryApplySmplArmSegment` が向きを上書き） |
| 手のねじれ | SMPL body_pose |

左肘の曲げ角の実測（keypoint / SMPL）: frame 38 = 107.7° / 85.7°、44 = 94.4° / 64.6°、46 = 84.3° / 54.8°。
**右肘は 0〜8° でよく一致しており、左腕だけ割れている**。付け根が SMPL で内側に入ったところから
keypoint 方向へ腕を伸ばすので、腕全体が内側に平行移動する。

骨長比（体幹長で正規化、keypoint は frame 30-50 平均）:

| | upperArm | foreArm | 腕全長 | 肩幅 | 肩の横オフセット |
|---|---|---|---|---|---|
| keypoint | 0.488 | 0.477 | 0.965 | 0.642 | 0.319 |
| 16_Male_Eric | 109% | **77%** | 93% | **89%** | **−0.036**（内側） |
| 08_Male_A_01 | 104% | 103% | 104% | 99% | +0.001 |
| 07_Human_Beta | 123% | **128%** | **126%** | 102% | — |

**08_Male_A_01 は骨格がほぼ完全に一致している**ので、index 0 でも症状が出る以上、骨長は主因ではない。
Eric は前腕 23% 短・肩 1.8cm 内側という固有の悪化要因が上乗せされる。
なお**体の太さ（服・体型）は keypoint に含まれない**ので、骨格を合わせてもメッシュが太ければ埋もれる。

→ 対応: `enableKeypointAimAt` を追加し、純 FK と現状を切り替えて比較できるようにした（既定は現状維持の true）。

**独立した第 2 の問題: Renderpeople の twist ボーンが動かない**

`upperarm_twist_l/r`・`lowerarm_twist_l/r`・`upperleg_twist_l/r`・`lowerleg_twist_l/r` の 8 本は
`HumanBodyBones` に対応がなく Avatar に登録できないため `HumanoidRigCache.bones` に入らず、
FK でも AimAt でも書き換えられない。DCC 側の constraint は FBX に焼かれていないので静止したまま。
前腕・すねをひねると手首・足首のメッシュが破綻する（candy-wrapper）。
**Animator を有効に戻しても解決しない**（`armTwist: 0.5` は Avatar にマップされた UpperArm/LowerArm 間の
ひねり配分パラメータで、追加 twist ボーンを駆動するものではない）。
npc_casual・Mixamo には twist ボーンが無いため、Renderpeople で初めて出る問題。

**この調査で否定した仮説**

- 「UpperChest がマップされたのが原因」→ 07_Human_Beta（Mixamo）が既に `Spine2` → UpperChest を
  持っており、UpperChest 経路自体は新規ではない。乖離の主因は bindRotWorld であって
  ボーンの有無ではない
- 「Renderpeople が A-pose だから」→ FBX の skeleton から計算した結果、腕は水平・脚は真下の
  完全な T-pose だった

### 2026-08-06: 真因は `SpineBodyPoseScale = 0.25`（脊椎の曲げを 75% 捨てていた）→ 1.0 に修正

前 2 項の「座位・仰向けでモデルが縦に 1.5 倍はみ出す」「股関節だけ曲げが浅い」の**真因**。

`TryApplyHumanSmplRotationOverlay` の FK ループに、Spine1（joint 3）と Spine2（joint 6）の回転を減衰させる係数があった。

```csharp
const float SpineBodyPoseScale = 0.25f;   // 旧値
Quaternion fkLocal = (joint == 3 || joint == 6)
    ? Quaternion.Slerp(Quaternion.identity, smplLocal, SpineBodyPoseScale)
    : smplLocal;
```

「SMPL エスティメーターは Spine・Chest の前傾角を過大推定する傾向がある」という理由で入っていたが、**実測すると曲げの 75% を捨てており**、上半身が起き上がったままになって座位・仰向けで体が折りたためず縦に広がっていた。

**股関節のずれは二次的影響だった**。股の角度は「胴と大腿のなす角」なので、胴（Neck の位置）が起き上がれば股も開く。膝・肘が完全一致していたのは、それらが脊椎の下流ではないため。

**1.0（減衰なし）にした結果**:

| 指標 | 0.25 | **1.0** |
|---|---|---|
| `boneRatio` frame600（座位） | 1.334 | **0.962** |
| `boneRatio` frame1200（座位） | 1.214 | **0.991** |
| `boneRatio` frame300（仰向け） | 1.334 | **1.114** |
| Hips-Neck-Head 角度の期待値との差 | +40〜47° | **-15〜+4°** |

**未確認**: 旧値の根拠だった「前傾の過大推定」が 1.0 で再発しないか、実機での見た目確認が済んでいない。数値上は 1.0 が正しいが、見た目で前傾が強すぎるようなら 0.7〜0.9 での調整を検討すること。

#### 測定上の注意: Unity の `Head` ボーンに対応するのは `head_h36m`（43）

胴の曲がりを `angle(MID_HIP, NECK, X)` で測るとき、**X に `nose`(0) を使うと Unity の `Hips-Neck-Head` と 40° 以上ずれる**。鼻先と頭部中心で基準が違うため。この定義差に気づかず「まだ 30〜40° ずれている」と誤判断しかけた。

| 頭部の基準 | frame 0 | frame 600 |
|---|---|---|
| `nose`(0) | 125.5 | 94.9 |
| **`head_h36m`(43)** | **170.3** | **141.3** |
| `head_top_lsp`(38) | 165.8 | 136.5 |
| Unity `Head` ボーン実測 | 165.1 | 126.1 |

**`head_h36m`(43) を使うこと。**

### 2026-08-06: 股関節だけ曲げが 25〜35° 浅い（膝・肘は完全一致）

前項「Human が姿勢の深いところで縦にはみ出す」の原因を、**関節角度の直接比較**で特定した。角度は座標系・スケールに依存しないので、`meta.bin` の keypoints3d から計算した値と表示モデルの実測値をそのまま突き合わせられる（`[ANGLE]` ログ）。

角度の定義は両側で同じ。`180° = まっすぐ伸展`、小さいほど深く曲げている。

- 膝 = `angle(hip, knee, ankle)`
- 股 = `angle(neck, hip, knee)`
- 肘 = `angle(shoulder, elbow, wrist)`

| frame | 姿勢 | 関節 | 期待(meta.bin) | 実測(Unity) | 差 |
|---|---|---|---|---|---|
| 全 | — | **膝 L/R** | 134.1 / 155.8 … | 134.1 / 155.8 … | **完全一致** |
| 全 | — | **肘 L/R** | 31.7 / 70.1 … | 31.7 / 70.1 … | **完全一致** |
| 0 | 立位 | 股 L/R | 139.4 / 157.7 | 149.3 / 161.9 | +9.9 / +4.2 |
| 300 | 仰向け | 股 L/R | 86.7 / 74.5 | 113.6 / 102.2 | **+26.9 / +27.7** |
| 600 | 座位 | 股 L/R | 48.4 / 55.0 | 83.3 / 89.2 | **+34.9 / +34.2** |
| 1200 | 座位 | 股 L/R | 96.9 / 105.1 | 123.8 / 130.1 | **+26.9 / +25.0** |
| 1500 | 立位 | 股 L/R | 164.4 / 164.8 | 170.8 / 169.7 | +6.4 / +4.9 |

**膝と肘は小数点以下まで完全一致している。FK の実装そのものは正しく動いている。**

**股関節だけが常に「伸展方向」へずれ、しかも深く曲げるほど差が大きい**（立位 +4〜10° → 座位 +25〜35°）。frame 600 では 35° 分の曲げが失われている。股が伸びたままということは上半身と脚が開いたままということで、これが「座位・仰向けでモデルが縦に 1.5 倍はみ出す」の直接の説明になる。

**なぜ膝は一致するのに股だけずれるのか**: 膝の角度は「大腿ベクトルと下腿ベクトルのなす角」なので、**大腿ボーン全体が回転していても角度自体は保たれる**。一方 股の角度は「胴と大腿のなす角」なので、大腿の向きがずれると直接効く。つまり `LowerLeg` は正しく、その親の **`UpperLeg` の向き（bind rotation）だけがずれている**状況。

**調査の方向**（未着手）:

1. `SmplJointToHumanBone` の 1 → `LeftUpperLeg` / 2 → `RightUpperLeg` のマッピング
2. `bindRotLocal` / `bindRotWorld`（T ポーズでの脚の向き）の計算
3. FK 公式 `correctedLocal = Inv(parentFk) * tPoseLocal * parentFk * smplLocal` の pelvis → UpperLeg 部分

Unity Humanoid の T ポーズでは脚が真下を向くが、SMPL の rest pose では脚がやや開いている。この差が bind rotation に一定量のオフセットとして入り、深く曲げるほど相対的に目立つ、という仮説は数値の傾向と整合する。

### 2026-08-06: 配置の実測 — ボールは完璧、Human は姿勢が深いほど縦にはみ出す

`bundle_human.svb` を実際に再生して全区間を計測した結果。接触補正は OFF（素の配置）。

**Other（ボール）は完全に正しい**:

```
sizeRatio  : 全 70 サンプルで 1.000
topDelta   : 0.0 px    bottomDelta: 0.0 px
```

毎フレーム bbox からスケールを計算し直しているため、大きさも位置も 1 ピクセルの誤差もない。**ボール側を疑う必要はない。**

**Human は立位なら正しく、姿勢が深いとずれる**:

| 時刻 | 姿勢 | sizeRatio | boneRatio | topBone |
|---|---|---|---|---|
| 0.0s | 立位 | 1.020 | 0.788 | Head |
| 50.0s | 立位 | 1.045 | 0.821 | LeftMiddleIntermediate |
| 60.0s | 立位 | 1.052 | 0.857 | LeftIndexIntermediate |
| **10.0s** | **仰向け** | **1.569** | **1.334** | **RightToes** |
| **20.0s** | **座位** | **1.537** | 0.973 | LeftLowerLeg |
| **40.0s** | **座位** | **1.582** | 1.214 | Head |

`scale` はユニーク値 1 個（完全固定＝設計どおり）、`bottomDelta` はほぼ全サンプルで 0.0（下端合わせは機能している）。**下端を軸にモデルが上へはみ出している。**

### 2026-08-06: 上記の原因として消去した仮説（同じ道を辿らないこと）

| 仮説 | 検証 | 結果 |
|---|---|---|
| スケールの決め方・基準値が悪い | frame 0 の `sizeRatio` | **否定** 1.020（数式どおり一致） |
| スケール固定が悪い | `scale` のユニーク値 | **否定** 1 個。固定は正常に機能 |
| 下端合わせが効いていない | `bottomDelta` | **否定** 0.0 px |
| `HumanSmplRotationAlpha`(0.65) が姿勢を減衰 | 1.0 にして再計測 | **否定** 小数第3位まで不変。`ShouldUseSmplOnlyPose()` 経路では姿勢の深さに効かない |
| 時間平滑化 `SmplSmoothHalfLifeSec`(0.05) | 0（平滑化なし）で再計測 | **否定** median 1.169→1.174 |
| AABB の過大評価（計測の問題） | ボーン位置ベースで再計測 | **否定** ボーンだけで bbox の 1.334 倍 |
| 最下点が手の指になる（基準点ずれ） | `bottomBone` を確認 | **否定** 映像側の bbox も手が最下点なら正しい。問題は上端 |
| **体型差（骨長比）** | `[BONELEN]` と keypoints3d を比較 | **否定** かつ**逆方向**（下記） |

**体型差の実測**（胴で正規化）:

| 部位 | keypoints3d（映像） | Unity モデル | 差 |
|---|---|---|---|
| 大腿 / 胴 | 0.812 | 0.776 | -4.4% |
| 下腿 / 胴 | 0.843 | 0.725 | -14.0% |
| **脚全体 / 胴** | **1.655** | **1.501** | **-9.3%** |

**Unity モデルの脚はむしろ 9.3% 短い。** 脚が短いのに縦幅が 1.33 倍になるということは、体型差では説明できない。「モデルの脚が長いから足がはみ出す」という仮説は棄却された。

なお SMPL の `betas` から骨長を求めるには SMPL のモデルファイル（shape blend shapes / J_regressor）が必要で bundle に含まれていない。**keypoints3d から直接骨長を測る方が確実**（上表はその方法で測っている）。

**残る原因**: 骨長が 9% 短いのに縦幅が 1.33 倍になる以上、**モデルの姿勢が映像より「開いている」**（関節を折りたたむ角度が浅い）ことになる。FK の計算そのもの（回転の適用式・座標変換・関節マッピング）に踏み込む必要がある。未調査。

### 2026-07-30: jointsWorld にモデルスケールが未適用（`AlignHumanoidFeetYToSmplAnkles` が機能していない）

**症状**: `[FOOT-Y]` ログの offset が異常値。

```
[FOOT-Y] smplAnkleY=-0.816  charFootY=-0.139  offset=-0.677
```

配置身長は 0.15〜0.34 m しかないのに **67.7 cm のズレ**。身長の 2〜4 倍で、脚長差では説明できない。

**原因**: `StreamingStereoVideoPlayer.PosePipeline.partial.cs` の `jointsWorld` 生成でモデルスケールが掛かっていない。

```csharp
jointsWorld[i] = anchorWorld + (camRotation * obj.jointsCam[i]);   // ← スケール未適用
```

`obj.jointsCam` は HMR2 の実寸（`bundle_human.svb` の keypoints3d から測った推定身長 1.53 m、root 相対メートル）。一方 Unity モデルは bbox から決めた配置身長 0.34 m（uniform scale 約 0.22）。つまり `jointsWorld` は**モデルの約 4.5 倍の大きさの骨格**を表している。

数値が一致することで確認できる:

```
モデルの root（Hips）Y ≈ -0.16     （anchor v=525 の逆投影）
実寸骨格の ankle は pelvis から約 0.65 下
  → -0.16 - 0.65 = -0.81           実測 smplAnkleY = -0.816 ✓
モデルの足ボーン → charFootY = -0.139（仰向けで足を上げているため root 付近）
```

**なぜ実害が出ていないか**: `AlignHumanoidFeetYToSmplAnkles` はこの巨大骨格の ankle にキャラの足を合わせようとして root を 67.7 cm 動かすが、直後に `ResolveRootPositionPreservingScreenHeight`（`ShouldPreserveRootScreenHeightAfterHumanSkeletonPlacement() = true`）が「skeleton 適用前の垂直成分」に戻すため、移動が丸ごと打ち消されている。

**NG パターン（重要）**: この打ち消しを「脚長差の補正が効いていないから」という理由で解除してはいけない。補正自体がスケール不整合で壊れているため、解除するとモデルが 67 cm 下に飛ぶ。打ち消しは偶然それを防いでいる。

**影響範囲**: `jointsWorld` を使う処理のうち、`TryApplySmplLegsFromJointPositions` / `TryApplySmplArmsFromJointPositions` などの AimAt 系は**方向（B-A の正規化ベクトル）だけを使う**のでスケール不整合の影響を受けない。絶対位置を使う `AlignHumanoidFeetYToSmplAnkles` のみが壊れている。姿勢そのものは `ShouldUseSmplOnlyPose() = true` により SMPL rotations の FK で決まるため、この件は姿勢の正しさには影響しない。

**状態**: 未修正。修正するなら (1) `jointsWorld` にモデルの uniform scale を適用し、(2) 同時に `ResolveRootPositionPreservingScreenHeight` との関係を整理する、の 2 段階が必要。片方だけ直すと壊れるので注意。

**副次対応（実施済み）**: `[FOOT-Y]` の `Debug.Log` を `logFootBallGap` フラグでガードした。フラグ導入前は 1 回の再生で **87,202 件**出力されており、Editor / 実機ともに負荷になっていた。

### 2026-07-16: 尻尾が追従していなかった問題

**現象:** 「しっぽは追従できているか」と確認したところ、`AnimalSmalFkPolicy.ShouldKeepBindPoseForJoint()` が SMAL joint 25-31（Tail1-7、canonicalの`tail_base`/`tail_mid`/`tail_tip`に対応）を**常にbind pose固定**にしており、body_poseの尻尾データが一切反映されていなかった（親の動きに伴う受動的な動きのみ）。

**根本原因3点:**
1. `ShouldKeepBindPoseForJoint(joint)` が `joint >= 25 && joint <= 31` を無条件でtrueにしていた（tail全体が意図的に未実装のまま放置されていた）。
2. `AnimalSmalFkApplier.SmalRestDirByJoint`（脚・首で使っている「SMAL restポーズでの関節方向」テーブル、`Docs/smal-rest-skeleton.json`から算出）にtailのエントリが1件もなかった。
3. `RegisterAnimalAimPairs`（tailBase→tailMid→tailTipの「どちらを向いて曲げるか」の登録）がtail分を含んでおらず、しかも**`PrimeAnimalBinds`より後に呼ばれていた**ため、他の関節（脚・首）も含めて登録が`bindDirLocal`計算に間に合っていなかった。脚・首はたまたま実際のUnity上の最初の子ボーンが登録先と一致していたため症状が出ていなかったが、tailMid→tailTipの間に未駆動の中間ボーン（例: Buffaloの`Tail2`(mid)→`Tail3`(未駆動)→`Tail4`(tip)）が挟まる種が多く、この順序バグが顕在化する。

**修正:**
- `Docs/smal-rest-skeleton.json`のjoint 25/26（Tail1→Tail2、Tail2→Tail3）の位置差分から方向ベクトルを算出し、`SmalRestDirByJoint`に追加。
- `ShouldKeepBindPoseForJoint`の条件を`joint >= 27`に変更（tailBase/tailMidを解放、tailTipとその先は据え置き — tailTipのSMAL上の子`Tail4`にはUnity側の対応ボーンがなく、脚のpaw関節と同じ「末端は未駆動のまま」という既存方針に合わせた）。
- `RegisterAnimalAimPairs`の呼び出しを`PrimeAnimalBinds`より**前**に移動し、`tailBase→tailMid`・`tailMid→tailTip`のペアを追加。

**副次的に発見・修正した既存ルール違反:** `ShouldKeepBindPoseForJoint`がtrueの分岐で`TransformWriter.ApplyLocalRotation(bone, bindLoc)`を呼んでいた（CLAUDE.mdの絶対ルール「FKループ内ではApplyWorldRotationのみ使用」に反する既存コード）。`parentTW`はこのボーンの実際のUnity親の今フレーム適用済みワールド回転と一致するため、`ApplyWorldRotation(bone, parentTW * bindLoc)`に置き換えても数値的に同じ結果になることを確認した上で修正。

**未検証:** 実際にPlay Modeでbundleを再生し、尻尾の動きが自然に見えるかは未確認。

### 2026-07-17: モデルごとにデフォルトの尻尾姿勢がバラバラな問題

**現象:** 「Lionのしっぽが他のモデルと違って上にそっている」という報告。上記07-16の修正でtail_base/tail_midは駆動されるようになったが、body_poseの推定値がニュートラルに近い間は、モデル固有の"デフォルトの曲がり"がそのまま画面に出続けることが判明。

**根本原因:** `AnimalSmalFkApplier`のFK計算は`restWorldRot = worldFk0 * boneBindWorld`（[AnimalSmalFkApplier.cs:357](../Assets/Scripts/StereoPlayer/AnimalSmalFkApplier.cs#L357)）をベースラインにし、SMALのbody_pose由来の`bendUnity`をその上に乗せるだけの設計。`boneBindWorld`はプレハブ作成時にリガーが作ったbindポーズの向きをそのまま含むため、tail_mid/tail_tipのローカル回転（親からの相対回転）にリガー由来の"カール"が焼き込まれているモデルは、body_poseの寄与が小さい間ずっとそのカールが見え続ける。

**確認方法:**
1. `AnimalSmalFkApplier`にtail joint 25/26の`smalRestDir`と`unityRestDirWorld`のなす角をログ出力する診断（`TAIL-REST-CHECK`）を追加し、`Quaternion.FromToRotation`の軸不定（~180°付近で不安定）が原因かを検証 → Lionでは95〜122°で軸不定域には入っておらず、この経路の破綻ではないと判明。
2. 既存の`joint=25/26/27`ログに`bindLoc`（親からのローカルbind回転）が出ていたので、Dog（基準）とLionを直接比較 → tail_mid（joint 26）のbindLocがDog≒identity、Lion≒48.6°（Euler表記）で明確な差を確認。
3. `Assets/Editor/AnimalTailBindStraightener.cs`の`Report Tail Bind Curl`で、`Quaternion.Angle(identity, localRotation)`により全モデルのtail_mid/tail_tipのカール量を一括計測 → 47モデル中35モデルが15°超（Dogは2.3°/1.7°で基準通り低い）。`40_Mammoth`のtail_tipのみ180.0°という突出した値だった。

**修正:** `AnimalTailBindStraightener.StraightenFlaggedTails`で、tail_mid/tail_tipのローカル回転が15°を超えるモデルのみidentityに矯正しprefabを保存（tail_base自体は種ごとの正当な解剖学的差異とみなし変更しない）。2026-07-17時点で35 prefabに適用済み。適用後にReportを再実行し、対象モデルが全て0.0°になったことを確認（`40_Mammoth`のtail_tip 180.0°→0.0°含む）。

**未検証:** Play Modeでの実際の見た目確認はまだ行っていない。特に`40_Mammoth`は180°という大きな飛びを矯正したため、他モデルより慎重に目視確認が必要。

**2026-07-17追記: tail_mid/tail_tip矯正だけでは直らなかった（Lion）:** 上記の修正をPlay Modeで確認したところ、Lionの尻尾は依然として上を向いていた。ログで`joint=26`（tail_mid）のbindLocが確かに`(0,0,0)`になっていることを確認したため、tail_mid/tail_tipの矯正自体は効いているが、**意図的に触らなかったtail_base自体の向き**が原因と判明。

`Report Tail Base World Direction`（tail_baseからtail_mid/tipへの方向ベクトルと、world upとの内積 `upDot` を比較可能な形で計測 — 各モデルのボーン軸の向き自体はFBXインポート規約でバラバラなので、world Vector3.up基準で揃える）を46モデルに対して実行した結果:

- Dog: upDot=-0.60、他45モデルすべて upDot=-0.07〜-0.99（下向き）
- **Lion: upDot=+0.53 だけが唯一プラス（上向き）**

体型が大きく異なる犬・熊・シカ・馬・ラクーンなど全モデルが一貫して下向きなので、これはLion固有の解剖学的差異ではなく、tail_base自体がリグ作成時点で上向きに作られていた個別の不具合と判断した。`FixLionTailBaseOrientation`でtail_baseの向きをworld upに対して垂直面(XZ平面)でミラーし、upDot=+0.53→-0.53に修正（`Assets/Resources/Models/Animal/04_Lion.prefab`のみ変更、他モデルは分布内に収まっているため未変更）。

**未検証:** Play Modeでの再確認はこれから。

### 2026-06-12: 手の位置ずれ問題

**現象:** SMPL-only FK パスで手の位置がずれる（`手がずれてる`）。

**根本原因:** Spine body_pose X が 34°〜63°（歩行データ）と非常に大きく、FK チェーンを通じて肩・腕全体に伝播している。

```
frame=1:  joint=3(Spine) smplLocal=(62.8, 8.1, 3.9)   after.fwd=(0.25,-0.97,0.05)  ← Y が -0.97（ほぼ真下）
frame=60: joint=3(Spine) smplLocal=(34.4, 6.1, 1.3)   after.fwd=(0.75,-0.58,-0.30) ← Y が -0.58（下向き）
```

T-pose expected: `tpose.fwd = (0.94,-0.24,-0.24)` に対して大きくずれている。

**FK 公式は数学的に正しい**（脚の LHip X ≈ -65° で hip flexion ✓ と実機確認）。問題はデータ（SMPL エスティメーターが脊椎前傾角を過大推定）。

**2026-06-13 対処:** FK ループ内で Spine (joint 3) と Chest (joint 6) の smplLocal を 0.25 倍にスケールダウン。

```csharp
const float SpineBodyPoseScale = 0.25f;
Quaternion fkLocal = (joint == 3 || joint == 6)
    ? Quaternion.Slerp(Quaternion.identity, smplLocal, SpineBodyPoseScale)
    : smplLocal;
fk[joint] = parentFk * fkLocal;
// smoothedSmplLocal にはスケール前の値を保存（時系列スムージングの連続性を保つ）
```

効果: Spine X: 62.8° → 15.7°（歩行で物理的に妥当な範囲）。`SpineBodyPoseScale` を 0.0〜1.0 で調整可能。

**SpineBodyPoseScale 適用後のログ確認:**
```
frame=60 Spine.fwd Y: -0.58 → -0.13  ✓（ほぼ直立）
frame=60 LeftUpperArm.fwd Y: -0.46 → -0.02  ✓（T-pose 方向に近い）
frame=60 RightUpperArm.fwd Y: +0.45 → -0.02  ↑（上向き → 水平、改善）
```

---

### 2026-06-13: 右腕の軸反転問題

**現象:** 右腕 bone.up が全フレームで一貫して -Y（逆さま）。左腕は +Y で正常。

**根本原因:** Unity rig の bindRotWorld Z 成分が右腕で左腕より 180° 大きい。
```
LeftUpperArm  bindWorld Z = 43.7°
RightUpperArm bindWorld Z = 223.8° = 43.7° + 180°   ← 180° 差
LeftLowerArm  bindWorld Z = 353.2°
RightLowerArm bindWorld Z = 173.2° = 353.2° - 180°   ← 180° 差
```

この 180° Z 差により右腕ローカルの Y・Z 軸が反転（Y-up→Y-down、Z-forward→Z-backward）。
SMPL body_pose は Y-up/Z-forward フレームで定義されるため、右腕では回転方向がずれる。

**2026-06-13 対処:** Q_Y180 共役変換（qx→-qx, qz→-qz）を右腕関節 {14, 17, 19, 21} の fkLocal に適用。

```csharp
bool isRightArmJoint = (joint == 14 || joint == 17 || joint == 19 || joint == 21);
if (isRightArmJoint)
    fkLocal = new Quaternion(-fkLocal.x, fkLocal.y, -fkLocal.z, fkLocal.w);
```

T-pose（smplLocal=identity）では fkLocal=identity のまま変化なし。非 T-pose では Y・Z 軸反転を補正。

**期待される効果:**
- 右腕の前後スイングが左腕と対称になる（frame=1 右腕 X: -28.9° → +28.9° 前方スイング）
- bone.up の -Y 問題は T-pose の bindWorld 由来のため、改善しない場合は別途調査

**Q_Y180 補正の限界（2026-06-13 判明）:**

Q_Y180 補正後も視覚的には手の向きが依然ずれていた。分析により：
- Q_Y180 補正自体は数学的に正しい（`Q_Y180 * q * Q_Y180^-1 = (-qx, qy, -qz, qw)`）
- ただし複数関節（joint 14, 17, 19）への累積適用と親 FK との相互作用で
  右上腕が T-pose から約 77° もズレる（歩行では 10〜30° 程度であるべき）
- FK 座標フレーム変換だけでは解決が困難 → 位置ベースの直接整合に切替え

---

### 2026-06-13: 右腕方向の位置ベース補正（AimAt）

> ⚠️ **2026-08-07: ここでいう「FK 座標フレーム変換の限界」の実体は body_pose の基底変換漏れだった**
> （[調査ログ 2026-08-07](#2026-08-07-renderpeople-モデル14-16で手足が破綻--fk-公式がbindrotworld--identity-のリグでしか成立していなかった)）。
> Q_Y180 補正（x,z 反転 = `conj Y`）は必要だった変換 `conj X`（y,z 反転）と別物で、しかも右腕だけに
> 当てていたため中途半端にしか効かなかった。
>
> **したがって AimAt は補助ではなく、四肢の姿勢を作る主役として機能していた**。基底変換が抜けた
> 素の FK は keypoint と平均 78.2°（腕は 97〜132°）ずれており、AimAt が keypoint の位置から向きを
> 作り直すことで見た目が成立していた。`enableKeypointAimAt = false` にすると腕が破綻する。
>
> 基底変換を修正した後は素の FK が平均 8.5° まで一致するので、AimAt は残差を詰める補正の位置づけになる。
> ただし残す場合は **「腕の付け根は SMPL / 腕の向きは keypoint」という混在**になる点に注意。
> keypoint と body_pose は独立した推定で、左肘では 22〜30° 食い違う
> （frame 38: kp 107.7° / smpl 85.7°、frame 46: kp 84.3° / smpl 54.8°）。
> さらに AimAt は**向きしか合わせず位置を合わせない**ので、付け根のずれはそのまま手先に出る。

**問題:** Q_Y180 FK 補正後も右腕方向が ~77° ズレ。FK 座標フレーム変換の限界。

**根本原因のより深い分析:**

bindRotWorld の左右腕の差分 `Q_diff = inv(bindRotWorld[leftArm]) * bindRotWorld[rightArm]` は
Euler 角の差分（ΔY ≈ 132°, ΔZ ≈ 180°）から Q_X180 に近似される。
→ 正しい補正は Q_X180 共役変換 `(qx, -qy, -qz, qw)` のはずだが、
　 複数関節の累積FK + 親フレームの相互作用で単純な共役変換では解決できない。

**2026-06-13 修正: position-based AimAt で右腕方向を直接整合**

FK 回転（`TryApplyHumanSmplRotationOverlay`）の後に、SMPL joint 世界座標位置から
右腕の向きを `ApplyHumanoidBoneToward` で直接整合する。

```csharp
// HumanSmpl.partial.cs TryApplySmplRightArmAimAt()
// RightUpperArm → jointsWorld[SmplRightElbow=19]（肘位置）
ApplyHumanoidBoneToward(rightUpper, rightLower, jointsWorld[19], 1f);
// RightLowerArm → jointsWorld[SmplRightWrist=21]（手首位置）
ApplyHumanoidBoneToward(rightLower, rightHand, jointsWorld[21], 1f);

// PosePipeline.partial.cs (SMPL-only path)
TryApplyHumanSmplRotationOverlay(cache, smplPose);
TryApplySmplRightArmAimAt(cache, pose.jointsWorld, pose.jointVis);
```

**なぜ IK 禁止に抵触しないか:**
- TwoBone IK: 手先目標位置から逆算して肩・肘角度を解く → 禁止
- AimAt (`ApplyHumanoidBoneToward`): 各ボーンを独立して次ジョイント方向へ回転させる
  per-bone の forward rotation → これは禁止範囲外

**残る課題:**
- 手首ツイスト（手の甲/手のひらの向き）: `ShouldApplyHumanSmplTerminalHandRotationInSmplOnlyPose = false`
  のため RightHand ボーンは T-pose 回転のまま（FK 設定なし）
- 左腕はそのまま FK ベース（問題が表面化した場合は同様の AimAt を適用）

---

### 2026-06-13: human 前後移動・スクリーンブレ問題

**現象:**
- human キャラクターが前後に激しく動く
- スクリーンとヘッドセット（カメラ）のブレが激しい

**根本原因（人物位置のジャンプ）:**

SMPL-only path では、キャラクターの3D配置に `pose.rootWorld = anchorWorld` を使用する。

```csharp
// AnchorUvZToWorldPinhole(screen, obj.anchorU, obj.anchorV, obj.anchorZ)
anchorWorld = camOrigin + camRotation * cameraLocalFromPixelDepth(anchorZ)
```

`obj.anchorZ` は bundle の検出データから取得した深度推定値。モノキュラー深度推定は
フレーム間ノイズが大きく（±10〜50cm 変動が一般的）、スムージングなしで使用すると
キャラクターが前後に激しくジャンプする。

SMPL-only path はこれまで `SmoothJointsWorld` を呼ばないため、位置スムージングが
まったく適用されていなかった（回転は `SmplSmoothHalfLifeSec=0.05f` でスムージング済み）。

**2026-06-13 対処:** SMPL-only path に指数スムージングを追加。

```csharp
// HumanSmpl.partial.cs に追加
private Vector3 GetSmoothedSmplRootWorld(HumanoidRigCache cache, Vector3 target)
{
    const float HalfLifeSec = 0.12f;  // 30fps で alpha ≈ 0.21/frame（前フレーム79%保持）
    float alpha = 1f - Mathf.Exp(-Time.deltaTime * 0.693147f / HalfLifeSec);
    if (!humanSmplSmoothedRootInit.Contains(cache))
    {
        humanSmplSmoothedRoot[cache] = target;
        humanSmplSmoothedRootInit.Add(cache);
        return target;
    }
    Vector3 smoothed = Vector3.Lerp(humanSmplSmoothedRoot[cache], target, alpha);
    humanSmplSmoothedRoot[cache] = smoothed;
    return smoothed;
}

// PosePipeline.partial.cs (SMPL-only path)
AlignHumanoidHipsToSmplRoot(instance.transform, cache, GetSmoothedSmplRootWorld(cache, pose.rootWorld));
```

`HalfLifeSec` を大きくすると→より強いスムージング（残像感が増す）。

**スクリーン・カメラブレの根本:**

1. **anchorZ ノイズによる視覚的誤認**: キャラクターが前後ジャンプすると、観察者の脳が
   カメラ動作と誤認する。位置スムージングで大幅改善するはず。
2. **XR トラッキングジッター**: ヘッドセットのトラッキング精度が低い場合、
   固定ワールド位置のスクリーンがヘッドセット視点から揺れて見える。この場合は
   コードでは修正不可能（XR SDK の設定・キャリブレーションの問題）。
3. **映像コンテンツ自体のカメラブレ**: 撮影時のカメラ震動が映像に含まれる場合、
   コードでは修正不可能。

**`DetectRuntimeRecenterFallback` について:**

フレーム間デルタが `0.35m 超 または 35° 超` でスクリーンが再配置される。この閾値は
1フレームあたりの値なので（30fpsで35°/frame = 1050°/sec）、通常の頭の動きでは
トリガーされない。XR トラッキングリセット時にのみ発火。

---

### 2026-06-15: 前後ブレの根本原因判明（bundle 側修正）

**根本原因（bundle 側の調査で判明）:**

HMR2 の `pose.keypoints2d` が full-frame pixel 座標ではなく **normalized crop 座標**だったため、
bundle builder が pixel 座標として使えず、person anchor が **SAM2 mask centroid に fallback** していた。

その結果、人物が実際にはほぼ同じ場所にいても：
- 前傾姿勢 → silhouette 変化 → mask centroid が動く
- mask centroid 変化 → anchorU/V が動く
- anchorZ（anchorUV 位置の単眼 depth）もセットで動く
- Unity 上で人物が前後（+ 左右）に動いて見える

**bundle 側の修正（2026-06-15）:**

`keypoints2d` を `pose.sourceBox.xywh` で元の video pixel 座標に戻し、
left_hip / right_hip 由来の **pelvis/hip anchor を優先**するように変更。

新しい bundle: `shared_volume/sam2_bundle_jobs/20260529-hmr2-rebuild/bundle_pelvis_anchor.svb`

Unity 側の decode 仕様は変更なし（`anchorZq × manifest.quant_pos_scale` は従来通り）。
`anchor_scale_q` = 固定値 65535（未使用）、`rot_q` = identity 固定値（未使用）のまま。

---

### 2026-06-14: anchorZ 深度ノイズの軸別スムージング

**背景（bundle 側確認）:**

- `anchorZ` は SMPL/HMR2 の body-proportion depth ではなく、SAM2 object の anchor_uv 位置で
  元動画 `depth_npz` から取った scene placement depth。
- `SMPL.transl.z` は SMPL block 内の pose/model sidecar であり、Unity world/camera 空間への
  authoritative Z としては使わない（コードは既に anchorZ を使っており正しい）。
- 配置は `anchor_u / anchor_v / anchor_z` + manifest の fov/eye size から camera-space XYZ を
  復元し（`AnchorUvZToWorldPinhole`）、SMPL/skeleton はその anchor に載せる姿勢情報として扱う。

**問題:** `depth_npz` はモノキュラー深度推定のため Z（深度方向）のフレーム間ノイズが XY より
大きい。UV トラッキング（SAM2）は比較的安定。旧 `HalfLifeSec=0.12f` は 3 軸同一半減期のため
深度ノイズを十分に抑制できていなかった。

**2026-06-14 対処:** `GetSmoothedSmplRootWorld` を軸別スムージングに変更。

```csharp
// HumanSmpl.partial.cs GetSmoothedSmplRootWorld(cache, target, cameraForward)
const float HalfLifeSecLateral = 0.12f;  // XY（UV方向）: 従来通り
const float HalfLifeSecDepth   = 0.35f;  // Z（深度方向）: より強いスムージング

Vector3 depthDir    = cameraForward.normalized;
float smoothedDepth = Mathf.Lerp(prevDepth, targetDepth, alphaDepth);      // 0.35s
Vector3 smoothedLateral = Vector3.Lerp(lateralPrev, lateralTarget, alphaLateral); // 0.12s
smoothed = smoothedLateral + depthDir * smoothedDepth;
```

```csharp
// PosePipeline.partial.cs (SMPL-only path)
Vector3 cameraForward = smplPose.camRotation * Vector3.forward;
AlignHumanoidHipsToSmplRoot(instance.transform, cache,
    GetSmoothedSmplRootWorld(cache, pose.rootWorld, cameraForward));
```

**2026-06-14 修正 2 → ray スケーリングに変更（縦横連動ジッター対策）:**

```
worldPos = camOrigin + anchorZ * ray
X = xNdc * Z / fx,  Y = yNdc * Z / fy
```

`anchorZ` が変わると X・Y・Z がすべて連動して変わる。旧実装（世界空間で縦横別々に平滑化）では
横方向が引き続き anchorZ ノイズに汚染されていた。

**正しい修正:** `anchorZ`（スカラー）のみ平滑化し、UV から決まる ray 方向は固定:

```csharp
// HumanSmpl.partial.cs GetSmoothedSmplRootWorld(cache, target, camOrigin, cameraForward)
float rawDepth = Vector3.Dot(target - camOrigin, cameraForward.normalized);
float smoothedDepth = Mathf.Lerp(prevDepth, rawDepth, alpha);  // HalfLifeSecDepth = 0.35s
// UV 方向はそのまま; スカラー深度だけ変更
smoothed = camOrigin + (target - camOrigin) * (smoothedDepth / rawDepth);
```

```csharp
// PosePipeline.partial.cs
Vector3 cameraForward = smplPose.camRotation * Vector3.forward;
AlignHumanoidHipsToSmplRoot(instance.transform, cache,
    GetSmoothedSmplRootWorld(cache, pose.rootWorld, pose.camOrigin, cameraForward));
```

**効果:** 姿勢・スケルトンに一切影響せず前後・左右ブレを同時抑制。
- 30fps・0.35s 半減期: `alpha ≈ 0.064/frame`（前フレームの深度 93.6% 保持）
- SAM2 UV tracking の方向は保存 → X・Y も深度ノイズに汚染されなくなる

---

## Animal SMAL FK（2026-06-15 設計確定）

詳細は [ADR-0001](adr/0001-animal-smal-fk.md) を参照。

### Human SMPL との共通点・相違点

| 項目 | Human SMPL | Animal SMAL |
|---|---|---|
| FK 公式 | `tw[j] = parentTW * bindRotLocal[j] * pose[j]` | 同一 |
| globalOrient 変換 | `flipCameraY=true` | 同一（第一候補、実機確認要） |
| body_pose 変換 | `flipCameraY=false` | 同一 |
| root ボーン | Hips（joint 0 直接） | spine/ボーン（joint 0 直接） |
| 仮想チェーン | なし | joint 1-6（pelvis0〜spine3）BONE MISSING パターン |
| IK との関係 | IK 禁止（全面 FK） | SMAL 有効時は TwoBone IK をスキップ |
| rig キャッシュ | `HumanoidRigCache`（bindRotLocal + bindRotWorld） | `AnimalRigCache` 拡張版（同上を追加） |
| 座標フレーム | Unity Humanoid（HumanBodyBones enum） | カスタム（Transform フィールド直接） |

### SMAL FK 固有の注意点

**仮想脊椎チェーン（joint 1-6）:**
前脚・首（parent=6）の parentTW は `tw[6]`（spine3 先端まで積算した FK 値）。
後脚・しっぽ（parent=0）の parentTW は `tw[0]`（root world）。
犬リグでは全ブランチが `ボーン` から直接出ているが、FK チェーンは SMAL 論理親に従うため
前脚と後脚で異なる parentTW が使われる。これは正常。

**bindRotWorld の必要性:**
Human SMPL では `tw[0] = worldGlobalOrient * bindHipsW` のように root だけ bindRotWorld を使い、
以降は `parentTW * bindRotLocal * pose` で展開する。
SMAL でも同様に `tw[0] = worldGlobalOrient * bindRotWorld[spine]` とする。
`AnimalRigCache.bindRotWorld` は `PrimeAnimalBind` 時に `bone.rotation`（世界空間回転）としてキャプチャ。

**脊椎ボーンが 1 本しかない問題:**
SMAL は joint 0-6 の 6 段脊椎を持つが犬モデルは `ボーン` 1 本。joint 0 が `ボーン` を駆動し
joint 1-6 は FK 積算に使われるのみ（Human の UpperChest BONE MISSING と同じパターン）。
SpineBodyPoseScale 相当のスケールダウンが必要かは実機ログで確認してから判断する。

**チューニング:** `HalfLifeSecDepth` を大きくすると→より安定（意図的な前後移動への追従が遅くなる）。

---

### 2026-06-16: SMAL 向き調査と SmalCanonicalCorrection 分析

**実機ログ（SmalCanonicalCorrection = Euler(90,0,0) 時）:**

```
Frame 1:  camRot=(0,0,0)  rawGO=(276.0, 234.7, 148.3)  worldFk0=(354.9, 22.8, 3.2)
          spine.fwd=(0.387, 0.089, 0.918)  ≈ +Z  spine.up=(-0.085, 0.995, -0.060) ≈ +Y
          bodyPose_maxAngle=31.2° at SMAL_joint=12（前左脚膝）
          leftFront.fwd=(-0.535, -0.285, -0.795)  leftFront.up=(0.332, -0.936, 0.113)
          head.fwd=(0.143, 0.381, 0.913)

Frame 30: worldFk0≈identity  spine.fwd≈+Z  bodyPose_maxAngle=53.5° at joint=25（しっぽ）
Frame 60: worldFk0≈identity  spine.fwd≈+Z  bodyPose_maxAngle=59.3° at joint=25
```

**Prefab 実測から確定したボーン階層：**

```
アーマチュア (parent of ボーン)  localRot = R_x(-90°)  scale = (100,100,100)
└── ボーン (spine / cache.spine)  localRot = R_x(+90°)   → worldRot = identity ✓ (bindSpineW = identity)
    ├── body (body mesh)           localRot = R_x(-90°)   → worldRot = R_x(-90°) at T-pose
    └── ... (other child bones)
```

**T-pose での犬の向き（数学的導出）：**

```
body.worldRot at T-pose = ボーン.worldRot × body.localRot = identity × R_x(-90°) = R_x(-90°)

# R_x(-90°) の作用: (x,y,z) → (x, z, -y)
body.forward = R_x(-90°) × (0,0,1) = (0, 1, 0)  ← +Y（上方向）
body.up      = R_x(-90°) × (0,1,0) = (0, 0,-1)  ← -Z（視聴者方向）
body.right   = R_x(-90°) × (1,0,0) = (1, 0, 0)  ← +X
```

**犬の「鼻」方向（vertex data = mesh local +Y）：**

Blender で dog faces +Y (Blender convention) → FBX export 後の mesh local +Y がそのまま鼻方向。

```
鼻 in world = body.worldRot × (0,1,0)
           = ボーン.worldRot × R_x(-90°) × (0,1,0)
           = tw[0] × (0, 0, -1)   [R_x(-90°)×(0,1,0) = (0,0,-1)]
           = -spine.fwd
```

**結論：鼻方向 = −spine.fwd**

- `SmalCanonicalCorrection = Euler(90,0,0)`（確定値）: spine.fwd ≈ +Z → **鼻 ≈ -Z（視聴者向き） ✓**
- `Euler(90,0,0) × Euler(0,180,0)` を試した結果: spine.fwd ≈ -Z → **鼻 ≈ +Z（スクリーン向き） ✗**

→ **Euler(90,0,0) が正しい。180°Y flip は逆効果だった。**

**NG パターン履歴（SMAL 向き）:**

| 試した値 | spine.fwd | 鼻方向 | 結果 |
|---|---|---|---|
| なし（補正前） | -Y 方向（横倒し） | 横倒し | ✗ spine X ≈ -84° |
| `Euler(90, 0, 0)` | ≈ +Z | ≈ -Z（視聴者向き） | **✓ 正解** |
| `Euler(90,0,0) × Euler(0,180,0)` | ≈ -Z | ≈ +Z（スクリーン向き） | ✗ |

**脚の動きについて:**

SMAL の body_pose で上腿・股関節（joint 7/11/17/21）は <5° と小さく、
膝（joint 12/14/18/22）としっぽ（joint 25）に主な動きがある。
これは SMAL 推定の精度の問題（コードバグではない可能性が高い）。
bindLoc の 180°Y/Z 成分によって bodyPose が逆方向に適用されている可能性は未調査（要確認）。

---

### 2026-06-16 続報: 全フレームログ解析の結果、上記の「確定」は再オープン

**訂正: 上の表で `Euler(90,0,0)` を「確定値」と書いたが、これは frame 1/30/60 の 3 点だけを見て
出した誤った結論だった。** Editor.log 全体（1153 行、frame 1〜2280 を 30 フレーム間隔で
サンプリング）を解析した結果、`worldFk0.Y` は実際には **0°〜360° の全域にわたって変化していた**：

```
frame   worldFk0 (X,Y,Z deg)
1       354.9,  22.8,   3.2
150       1.9, 266.4, 344.6
330      15.3, 157.9,  11.9
510       0.9, 359.9,   6.3
900       6.7, 187.6, 326.6
1080      0.0, 315.1,  16.1
1320    357.8,  51.9, 358.1
```

つまり「常に正面（Y≈0）に固定されている」という旧分析の前提自体が誤りだった
（3 フレームがたまたま Y≈0 近辺に偏っていただけ）。一方で X・Z（ピッチ・ロール）は
ほぼ 0°/360° 付近に収まっており、姿勢としての安定性自体は崩れていない。

**結論：`SmalCanonicalCorrection`（SMAL canonical → Unity world の固定回転）の値そのものは
prefab 階層からの数学的導出であり、机上の検証は通ったが、実際の動画と見比べた
グラウンドトゥース calibration はまだ一度も行われていない。** ボーン階層からの逆算では
「どの軸が SMAL の鼻方向か」という *データ規約* 自体は分からない（これは Python 側の
SMAL 出力規約の話であり、Unity 側の rig 構造からは導出できない）。正しい値は
動画と Unity 画面を見比べて一度だけキャリブレーションする必要がある。

**対応: ハードコードされた定数 → Inspector で調整可能な値に変更**

- `StreamingStereoVideoPlayer.animalSmalCanonicalCorrectionEuler`（デフォルト `(90,0,0)`、
  `StreamingStereoVideoPlayer.Core.cs`）を Play Mode 中にリアルタイムで調整できるようにした。
- `AnimalSmalFkApplier.cs` の `TryApplyAnimalSmalFk` に毎フレーム
  `Debug.DrawRay`（黄色 = 鼻方向 `-spine.forward`、シアン = 上方向 `spine.up`）を追加。
  Scene view で動画のオーバーレイと見比べながら値を調整できる。
- ログサンプリングを `frame==30/60 限定` → `frame % 30 == 0` に変更（動画全体をカバー）。
- 古いスムージング（`SmalSmoothHalfLifeSec = 0.05f`）を `0f` に変更し、生データを直接適用。
  0.05s ハーフライフは歩行周波数（~2Hz）の振幅を約30%減衰させていたため、
  「脚・頭の動きが動画ほど激しくない」という訴えの一因と考えられる。

**キャリブレーション手順（要実機確認）:**

1. Play Mode で動画を再生し、犬の向きが映像から明確に分かるフレーム（真正面 or 真横）で一時停止する。
2. Scene view で黄色い矢印（鼻方向）が映像内の犬の鼻の向きと一致するか確認する。
3. 一致しなければ `animalSmalCanonicalCorrectionEuler` を Inspector で調整し、再度比較する
   （Play Mode 中の変更は即時反映される）。
4. 一度正しい値が見つかれば、これは SMAL データ規約に基づく値であり、
   将来別の犬・別動物 rig を追加しても **同じ値を使い回せる**はずである
   （rig 固有差は `AnimalRigCache` の bindRotLocal/bindRotWorld が自動で吸収する。下記参照）。

**2026-06-16 実機検証で確定: `SmalCanonicalCorrection = Euler(0, 90, 90)`（X=0, Y=90, Z=90）。**

ユーザーが Play Mode 中に `animalSmalCanonicalCorrectionEuler` を Inspector で調整し、
Scene view の鼻方向レイ（黄色）・上方向レイ（シアン）を動画と見比べながら試行錯誤した結果、
向きが一致する値として **X=0, Y=90, Z=90** を発見した。

以前（上の節）prefab 階層から机上で導出した `Euler(90,0,0)` は誤りだった。原因は、
ボーン階層の local rotation だけから「SMAL のどの軸が鼻方向か」という *Python 側のデータ規約*
を逆算しようとしたこと自体に無理があったため。`Euler(90,0,0)` は X 軸（pitch）だけの回転で
Y 軸（yaw）の補正を含んでいなかったが、実際には SMAL canonical → Unity world の変換には
Y・Z 軸の90°回転（合計2軸）が必要だった。机上の階層逆算では bind pose の見た目上の一致と
実際の calibration がたまたま一部の軸でだけ整合していたために、誤った値が「確定」と
誤認されてしまった。**結論として、この種の軸変換はコード上の階層推論だけで確定させず、
必ず実機で動画と見比せて calibrate する必要がある。**

- コードのデフォルト値を `StreamingStereoVideoPlayer.Core.cs` の
  `animalSmalCanonicalCorrectionEuler = new Vector3(0f, 90f, 90f)` に修正済み。
- `AnimalPoseSettingsFactoryTests.cs` のテスト値・アサーションも `(0,90,90)` に合わせて修正済み。
- 将来別の四足 rig を追加する場合、まずこの `(0,90,90)` を流用し、ズレがあれば
  上記のキャリブレーション手順で再調整する。

---

### 将来の rig 多様化への対応方針（Human と異なり Animal に統一 Humanoid rig がない問題）

Human は Unity Humanoid rig（`HumanBodyBones` enum）という業界標準の統一インターフェースがあるため
`HumanoidRigCache` は enum ベースで一意に bone を解決できる。Animal にはこの統一規格が存在しないため、
将来色んな動物・rig 構造に対応する必要がある。現状のアーキテクチャを確認した結果、
**土台は既に rig 非依存（generic）に作られている**ことが分かった：

| 仕組み | 場所 | 汎用性 |
|---|---|---|
| ボーン発見 | `AnimalRigDefinition`（トークン名でのマッチング）+ `FindAnimalBone`/`FindBoneByTokens` | ボーン名が多少違っても "front upper leg" 等のトークンで発見可能 |
| bind pose 記録 | `PrimeAnimalBind`（`bone.localRotation`/`bone.rotation` を汎用的に記録） | どの bone でも同じロジック。rig 固有のハードコードなし |
| モデル向き推定 | `ResolveAnimalModelBasis`（前脚・後脚の bone 位置の中点から forward を算出） | rig 固有の Euler 定数を使わず、実測位置から動的に算出 |
| SMAL joint → bone マッピング | `GetSmalBoneForJoint`（`AnimalRigCache` の汎用フィールドを参照） | rig が変わっても `AnimalRigCache` の同名フィールドが解決されていれば動作 |

**唯一 rig 非依存ではなかったのが `SmalCanonicalCorrection`** （ハードコードされた `Euler(90,0,0)`）。
これは今回 Inspector 設定値に変更した。この値は **SMAL データ規約（Python 側）に紐づく値であり
rig には紐づかない** ため、一度正しく calibrate すれば理論上どの四足 rig にも使い回せる。

**今後新しい動物 rig を追加する場合のチェックリスト:**

1. 新しい prefab のボーン名が `AnimalRigDefinition` のトークンに一致するか確認する
   （一致しなければトークンリストに追加 — これは名前の話なので低コスト）。
2. `ResolveAnimalModelBasis` が前脚・後脚 bone から forward を正しく推定できるか
   実機ログ（`cache.modelForwardLocal`）で確認する。
3. `SmalCanonicalCorrection`（`animalSmalCanonicalCorrectionEuler`）はそのまま使い回せるはずだが、
   FBX インポート時の軸補正規約（-90°X armature 補正など）が DogRoot と異なるツール/設定で
   作られた rig の場合は再キャリブレーションが必要になる可能性がある。
4. 四足ではない動物（鳥・蛇等）は SMAL の 35 joint 階層（`SmalJointParentArray`）自体が
   合わないため、別の joint 階層定義が必要になる（このケースは未対応・将来課題）。

---

### 2026-06-19: anchorZ の奥行き方向が逆だった（Human/Animal/Else 全カテゴリ共通バグ）

**症状:** Animal が映像内で画面に近づいているのに、3D モデルはカメラから遠ざかる方向に動く。

**原因:** `anchorZq * quant_pos_scale` で復元される値は、bundle 側の深度推定の出力そのままで
**0=奥, 1=手前の正規化値**（値が大きいほど近い）。しかし Unity 側はこれを「カメラ空間 forward 方向
の距離（メートル、大きいほど遠い）」として `PinholePlacementSpace.EyePixelDepthToWorld` の
`zMeters` にそのまま渡していた（標準的なカメラ空間 +Z = 前方、というコメントの思い込み）。
値の意味が正反対だったため、近づく物体ほど Unity 上では前方（カメラから遠い側）に大きく
配置されることになっていた。

`AnchorUvZToWorldPinhole` / `EyePixelDepthToWorld` は Human・Animal・Else 全カテゴリ共通の
配置関数のため、この符号反転は本来全カテゴリに影響する。Human で目立たなかったのは別の偶然
（インタラクティブモーション・スムージング等）による隠れであり、実際にはバグそのものは
カテゴリ非依存。

**修正（2026-06-19）:** `StreamingStereoVideoPlayer.Meta.cs` の decode 境界 1 箇所のみで変換。
`StreamingStereoVideoPlayer.Manifest.partial.cs` に **既存だが、どこからも呼ばれていなかった**
`DecodeAnchorDepthMetersFromBundle(float zRaw01)` を発見（引数名が `zRaw01` で、最初から
0=far/1=near を前提に `screenDistanceMeters`/`PopoutRangeMeters`/`MinDistanceFromHeadMeters` で
正しく変換するロジックが実装済みだった）。decode 側をこの関数を呼ぶように接続するだけで直る。

```csharp
// anchorZq * quant_pos_scale decodes to a normalized depth where 0=far, 1=near.
// DecodeAnchorDepthMetersFromBundle converts that into an actual camera-space
// distance (larger = farther) relative to the configured screen/popout range.
float anchorZ = DecodeAnchorDepthMetersFromBundle(anchorZq * GetQuantPosScale());
```

この 1 箇所を直すだけで、`anchorWorld` を経由する全ての配置（Human の `pose.rootWorld`、
Animal の skeleton/SMAL root、Else の anchor）に効く。実際の配置はステレオ動画の画面距離
（`screenDistanceMeters`）を基準に、手前への最大ポップアウト量（`PopoutRangeMeters`）の範囲で
決まる仕組みになる（単純な `1 - normalized` ではなく、VR のポップアウト表現に合わせたスケール）。

**併せて削除した別件（同日）:** Animal パイプラインが `source/animal_control_targets.json`
（debug/検証専用、配置に使用禁止 — `CLAUDE.md` 参照）を今も読み込み、配置・頭/尾/脚姿勢に
使っていたことが判明し削除。Human 側は既に同種の sidecar 読み込みが無効化済みだったが、
Animal 側は未対応のままだった。こちらは本質的な奥行き反転バグとは別件（データソース規約違反）。
削除後も奥行きの逆転症状自体は残っていたため、上記の `anchorZ` 反転が本当の原因だったことが
確定した。
