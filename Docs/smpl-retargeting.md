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

### 全 Human モデルのリグ一覧（2026-08-07 実測・保守の基礎資料）

bindRotWorld の identity からの乖離 [deg]。**旧公式はこの値が 0 に近いリグでしか成立しない**。
prefab が PrefabInstance のものは参照先 FBX の `.meta` の `skeleton`、VRM は GLB の JSON チャンクから算出。

| モデル | リグ | Hips | Spine | Chest | UpprChst | Neck | Shldr | UpArm | LoArm | Hand | UpLeg | Foot | Toes | twist |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 00-05, 08-13 | npc_casual_set | 0 | 3 | 4 | **無** | 10 | 8 | 7 | 7 | 16 | 119 | 160 | 178 | 0 |
| 06_Female_C | VRM (VRoid) | **0** | **0** | **0** | **0** | **0** | **0** | **0** | **0** | **0** | **0** | **0** | **0** | 0 |
| 07_Human_Beta | Mixamo | 1 | 8 | 8 | 7 | 7 | **129** | 120 | 120 | 120 | 180 | 180 | 180 | 0 |
| 14/15/16 | Renderpeople | **120** | **120** | **120** | **120** | **114-116** | **90** | **90** | **90** | **180** | **119-121** | **120** | **90** | **8** |

**旧公式でどのモデルが壊れていたか**がこの表で読める:

- **06 (VRM) は全ボーン 0°** — 旧公式でも新公式でも結果が同じ。完全に無事だった
- **npc_casual は胴・肩・腕が 0〜16°** — ほぼ無事。脚は 119〜178° と大きいが AimAt が上書きするので隠れていた
- **07 (Mixamo) は胴が 1〜8° と良好だが肩が 129°** — **肩は AimAt の対象外なので、旧公式では肩がずれていた**
- **14/15/16 (Renderpeople) は全ボーン 90〜180°** — 全滅

twist ボーン（`HumanBodyBones` に対応がなく Avatar に登録できない＝FK でも AimAt でも動かせない）は
**Renderpeople 3 体のみ 8 本**。npc_casual / VRM / Mixamo は 0 本。

骨長比（体幹 Hips→Neck = 1、keypoint 実測は upperArm 0.488 / foreArm 0.477 / shoulderW 0.642）:

| モデル | upperArm | foreArm | 腕全長 | shoulderW |
|---|---|---|---|---|
| npc_casual_set | 104% | 103% | **103%** | 99% |
| 06_Female_C (VRM) | 111% | 111% | 111% | **83%** |
| 07_Human_Beta | 123% | **128%** | **126%** | 102% |
| 14_Female_Carla | 105% | **127%** | 116% | 106% |
| 15_Female_Claudia | 114% | 110% | 112% | 109% |
| 16_Male_Eric | 109% | **77%** | **93%** | **89%** |

**npc_casual だけが keypoint とほぼ一致（103%）**。他は 111〜126% と腕が長く、Eric だけ 93% と短い。
AimAt は向きしか合わせないので、この差はそのまま手先の位置ずれになる。

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

**AimAt は削除できない: FK の累積誤差を各セグメントで断ち切っている（2026-08-07 確定）**

基底変換を入れた後も `enableKeypointAimAt = false` では手と足が破綻した。
「FK 公式が直ったから AimAt は不要」という判断を 2 回出し、2 回とも実機で否定されている。

理由は誤差の伝わり方が構造的に違うこと。**FK は親の誤差が末端まで累積するが、AimAt は各セグメントを
keypoint 方向へ独立に合わせるので毎回リセットされる**（`FromToRotation` が毎回 keypoint 基準で取り直す）。

FK 段数と向き誤差の実測:

| ボーン | Hips からの FK 段数 | 誤差 |
|---|---|---|
| LeftUpperLeg（大腿） | 1 | 6.2° |
| LeftFoot（足首→つま先） | 3 | **20.5°** |
| RightFoot | 3 | **21.9°** |
| LeftUpperArm | 5 | 15.3° |

位置で見ると、肩を起点に FK の向き × モデル骨長で積んだ場合:

| | 位置ずれ |
|---|---|
| 左肘 | 平均 7.3 cm（最大 16.5）|
| 左手首（AimAt off） | 平均 **9.6 cm**（最大 16.8）|
| 左手首（AimAt on 相当・骨長差のみ）| 平均 6.5 cm |

**手が特に悪い追加理由**: 素の SMPL は手をほぼ推定しない（4D-Human/HMR2 は body only）。
body_pose の回転量は肘 84.9°・上腕 51.2° に対し、**手首はわずか 13.5°**。
情報がほとんど無いところに累積誤差だけが乗るので、FK で手を決めると破綻する。
AimAt 有効時の `TryApplyHandFkAfterAimAt` は前腕方向（keypoint 由来）を主軸にするため、この問題を回避している。

⚠️ **したがって AimAt の削除・無効化を提案しないこと。** FK 側の改善は AimAt が触らない
肩・胴・首・頭・つま先にのみ効くと考える。`enableKeypointAimAt` は切り分け用に残してあるが既定は true。

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

**修正後の効果測定（2026-08-07・AimAt がカバーしない部位）**

AimAt は上腕・前腕・大腿・下腿・足首を keypoint で上書きするので、今日の修正が実際に効くのは
それ以外の部位。keypoint で答え合わせできるものを測った（37 フレーム）:

| 部位 | AimAt | 基底変換なし（修正前） | 基底変換あり（現在） | 改善 |
|---|---|---|---|---|
| LeftShoulder (collar) | なし | 40.2° | **25.2°** | +15.0 |
| RightShoulder (collar) | なし | 45.9° | **26.9°** | +19.0 |
| Spine | なし | 8.5° | 8.1° | +0.4 |
| Chest | なし | 10.5° | 8.5° | +2.0 |
| UpperChest | なし | 10.0° | 7.5° | +2.4 |
| LeftUpperArm [参考] | あり | 132.7° | 14.6° | +118.0 |
| LeftUpperLeg [参考] | あり | 44.1° | 5.9° | +38.2 |

**肩に残る 25〜27° は回転誤差ではなく定義差**。同じ joint の body_pose を identity にしても
誤差は 21〜25° とほとんど変わらない（右肩はむしろ回転を与えたほうが悪化する）。
Body25 の `LShoulder` は肩峰（体表の点）で、Collar ボーンの軸（鎖骨方向）とは別のベクトルなので、
この残差は測定基準の違いであって破綻ではない。SMPL の Collar 回転が肩の向きにほとんど寄与していない
ことも同時に分かる。

**検証できなかった部位:**

- **Toes**: Body25 には `LBigToe` までしかなく、Toes ボーン（つま先の先）に対応する点が無い
- **Neck / Head**: `Neck→Nose` は首の軸（上方向）ではなく前方を向くベクトルなので比較にならない
  （この参照で測ると 47.8° になるが、測定方法の問題）

**独立した第 2 の問題: Renderpeople の twist ボーンが動かない**

`upperarm_twist_l/r`・`lowerarm_twist_l/r`・`upperleg_twist_l/r`・`lowerleg_twist_l/r` の 8 本は
`HumanBodyBones` に対応がなく Avatar に登録できないため `HumanoidRigCache.bones` に入らず、
FK でも AimAt でも書き換えられない。DCC 側の constraint は FBX に焼かれていないので静止したまま。
前腕・すねをひねると手首・足首のメッシュが破綻する（candy-wrapper）。
**Animator を有効に戻しても解決しない**（`armTwist: 0.5` は Avatar にマップされた UpperArm/LowerArm 間の
ひねり配分パラメータで、追加 twist ボーンを駆動するものではない）。
npc_casual・VRM・Mixamo には twist ボーンが無いため、Renderpeople で初めて出る問題。

**ただし優先度は低い**（2026-08-07 実測）。この bundle で実際に生じるひねり量は:

| セグメント | 平均 | 最大 | 30° 超のフレーム |
|---|---|---|---|
| 前腕（左 / 右） | 12.6° / 7.7° | 37.9° / 34.4° | 2 / 1（37 フレーム中）|
| 下腿（左 / 右） | 14.3° / 22.0° | 18.7° / 35.7° | 0 / 1 |

平均 8〜22° では candy-wrapper はほとんど目立たない。**手足の見た目の主因は twist ボーンではなく
FK の累積誤差**（下記）だったので、当初この項目を主因候補に挙げたのは誤りだった。

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

---

## 調査ログ 2026-08-20: f150 の姿勢比較（当初の「姿勢が逆」判定は誤り・撤回）

### 撤回した内容

当初、frame 150（**5.0 秒**、30fps）について「元動画は深く前傾、Unity は立って後屈しており前後が逆」と記録したが、**これは元動画フレームの読み間違いだった。撤回する。**

`real_f150.png`（元動画 f150 を横から見た絵）を「深い前傾」と読んだが、実際は**立ったまま上体を後ろに反らせた後屈**であり、**Unity 側の姿勢と一致している**。ユーザーの指摘により判明。

### 教訓

**目視での姿勢判定は誤りやすい。** 特に上体を大きく倒した姿勢は、頭と腰の位置関係だけでは前屈・後屈を取り違える。姿勢の一致を判定するときは、印象で述べず、**keypoints を元動画に重ねて描画するか、Unity 側のボーン投影位置と数値で比較する**こと。

### 確認できていること（この部分は有効）

- **`meta.bin` の keypoints3d は元動画と合っている。** f90 で keypoints を bbox に合わせて投影し元動画に重ねたところ、首・肩・肘・手首・腰・膝・足首がいずれも被写体と一致した。右手首はボール bbox の内側にあり、腕の交差も再現されていた
- **bbox も正しい。** 人物 bbox・ボール bbox とも元動画の被写体を正確に捉えている

### 未解決: 検証に使うデータの取り違え

2026-08-20 の一連の配置検証（[docs/bundle-placement.md](bundle-placement.md)）は**すべて keypoints3d ベース**で行った。しかし **Unity が実際に姿勢を作るのは `meta.bin` の SMPL block（rotations・betas・transl）** であり（CLAUDE.md「runtime の唯一の SMPL データソース」）、keypoints3d は検証用である。

**この 2 つがずれている場合、keypoints3d ベースの数値検証は Unity の実挙動を表さない。** 姿勢の一致が確認できた以上、大きくはずれていない可能性が高いが、**未検証**である。配置の数値を根拠に判断する前に、SMPL block を FK した関節位置と keypoints3d を突き合わせること。

---

## 調査ログ 2026-08-21: 実ボーンと keypoints3d の投影位置がずれている

### きっかけ

⑨⑩（Else の深度補正）が「keypoints ベースの試算では効くのに、実ボーンで実装すると効かない」という状態が続いた。ユーザーから **「実モデルが keypoint ほど曲がっていないのが原因では」** という指摘があり、初めて両者を直接比較した。**これまで一度も測っていなかった量。**

### 測定方法

`[BONEKP]` ログを追加（`logBoneVsKeypoint`）。同じフレームで

- `meta.bin` の keypoints3d を bbox 高さに合わせて投影した位置
- Unity の実ボーン（`HumanoidRigCache.bones`）の world 位置を投影した位置

の差を主要 13 部位について px 単位で出す。

### 結果: ずれている。しかも部位ごとに向きが違う

`bundle_human_shots_driftfix_test.svb`、89 フレーム（f0-880、10 フレームおき）:

| 部位 | du median | dv median | dv / bboxH |
|---|---|---|---|
| Neck | −2.0 | −4.0 | −1.8% |
| RSho | 3.0 | −7.0 | −2.9% |
| **LWri** | **−13.0** | **−14.0** | **−6.2%** |
| RHip | −13.0 | 3.0 | 1.7% |
| RKnee | −18.0 | 2.0 | 1.1% |
| **RAnk** | −14.0 | **17.0** | **8.9%** |
| LAnk | 1.0 | 9.0 | 4.1% |

**モデル全体の上下オフセットを除いた「形の違い」（各フレームで全部位の dv 平均を引いた値）:**

| 部位 | 相対 dv median | 解釈 |
|---|---|---|
| **RAnk** | **+22.0** | **モデルが keypoint より下** |
| **LAnk** | **+12.1** | **モデルが keypoint より下** |
| RKnee | +4.8 | モデルが下 |
| Neck | +1.2 | ほぼ一致 |
| RElb | −2.1 | ほぼ一致 |
| LElb | −3.8 | モデルが上 |
| **LWri** | **−10.7** | **モデルが keypoint より上** |
| RWri | −8.0 | モデルが上 |

**足首がモデルの方が下（+22px、+12px）、手首がモデルの方が上（−10.7px、−8.0px）。** これは **「モデルの四肢が keypoints ほど曲がっておらず、伸びている」** ことを意味する。膝が曲がっていれば足首は上に来るはずで、下にあるのは脚が伸びているから。

### 姿勢が崩れる区間ほど差が大きい

各フレームの「相対 dv の p2p 幅」（形の崩れの大きさ）:

| 区間 | 相対 dv の p2p 幅 |
|---|---|
| 0-3s 立位 | 32.0px |
| **4-8s 胸トラップ** | **64.0px** |
| 9-12s 足上げ | 35.0px |
| 13-30s | 39.0px |

**主症状が出る胸トラップ区間で、形のずれが立位の 2 倍**になる。bboxH がこの区間で 200-300px なので、64px は**身長の 20-30% に相当する形の食い違い**。

### これが ⑨⑩ が効かなかった理由

- **試算は keypoints ベース**で「ボールが胸の前にある」「押し出せば表面に出る」と判定していた
- **実装は実ボーンベース**で、その胸や手足は keypoints と最大 64px ずれた場所にある
- したがって**押し出し先が間違っていた**し、そもそも「埋もれているか」の判定も keypoints と実ボーンで食い違う

**⑩ の失敗（23324 回発動しても症状が変わらない）はこれで説明がつく。**

### 原因の候補（未検証）

1. **AimAt の影響** — `enableKeypointAimAt = true` で四肢の向きを keypoint に合わせているが、向きだけで位置は合わせないため、骨長差が手先・足先に累積する（[docs/bundle-placement.md](bundle-placement.md) の 2026-08-07 の記録と同じ現象）
2. **骨長補正が脚のみ** — `enableHumanArmLengthCorrection = false` で腕の骨長を合わせていない。手首のずれ（−10.7px）はこれで説明できるかもしれない
3. **モデル固有の可動域** — Humanoid Rig の制限で SMPL の角度を再現しきれていない

### 次にやること

**この差を減らすのが先。** Else の深度をどう補正しても、モデル自身が keypoints と違う形をしている限り、keypoints ベースの試算は当たらない。

まず 1（AimAt）を疑うのが筋。`enableKeypointAimAt = false` で `[BONEKP]` を測り直せば、AimAt が差を作っているのか減らしているのかが分かる。**ただし AimAt は過去 2 回、実機で「削除すると破綻する」と判定されている**（[[aimat-is-not-removable]]）ので、切るのは診断目的に限ること。

### AimAt / 腕骨長補正が姿勢一致度に与える影響（2026-08-21 実測）

「モデルの体勢を keypoints3d に極力一致させる」を最優先目標に据え、まず既存スイッチの効果を測った。

**指標**: 各フレームで全部位の投影位置の平均（＝モデル全体の上下左右オフセット）を引いたうえで、部位ごとのずれの RMS を取る。`bboxH` で正規化して % で表す。**小さいほど keypoints に近い形**。

| 設定 | RMS median | p90 | RMS px |
|---|---|---|---|
| **AimAt ON / 腕骨長 OFF（現行）** | **6.36%** | 11.78% | 14.2px |
| AimAt OFF / 腕骨長 OFF | **24.79%** | 36.34% | 60.9px |
| **AimAt ON / 腕骨長 ON** | **5.86%** | 12.70% | 14.9px |
| AimAt OFF / 腕骨長 ON | 28.15% | 40.56% | 67.8px |

**AimAt を切ると姿勢一致度が 4 倍悪化する（6.36% → 24.79%）。** [[aimat-is-not-removable]] のとおり AimAt は削除できない機構であることが、姿勢一致度の観点からも裏付けられた。

部位別に見ると、AimAt OFF では肘が +22〜25px 下、手首が −50〜60px 上と大きく崩れる。**FK だけでは四肢が keypoints の形にならない。**

#### 腕骨長補正は median を下げるが p90 を上げる

`enableHumanArmLengthCorrection` を ON にすると:

- RMS median 6.36% → **5.86%**（改善）
- RMS p90 11.78% → **12.70%**（悪化）
- 手首の相対 dv: RWri −4.9 → **+11.4**、LWri −7.8 → **+2.7**（符号が反転、行き過ぎている）

**手首のずれ（AimAt ON で −4.9 / −7.8px）は腕骨長補正で消えるどころか反対側へ +11.4px 行き過ぎる。** 2026-08-19 に「実測で逆効果」として OFF にした判断と整合する。median だけ見ると改善に見えるので注意。

#### 残っている主なずれ（AimAt ON / 腕 OFF）

| 部位 | 相対 dv |
|---|---|
| **RAnk** | **+20.4px**（モデルが下） |
| **LAnk** | **+14.4px**（モデルが下） |
| LWri | −7.8px（モデルが上） |
| LHip | −6.5px |
| RSho | −5.2px |

**最大の残差は足首**で、モデルの足が keypoints より 14〜20px 下にある。膝（RKnee +3.1 / LKnee +0.2）はほぼ合っているので、**膝から下の向きか長さが合っていない**。

#### 次に調べること

1. **足首のずれの原因** — 最大の残差。膝が合っていて足首がずれるので、下腿の骨長か向きの問題。`TryResolveLimbBoneLengthFactors` は脚に対して有効なはずだが、実際に効いているか確認する
2. AimAt は四肢の**向き**しか合わせないので、骨長差が末端に累積する構造的な問題がある。足首はその累積先
3. 腕骨長補正は現状 OFF が妥当（p90 が悪化し、手首が行き過ぎる）

### 脚の骨長補正が足首のずれを作っていた（2026-08-21 実測）

最大の残差（足首 +20.4 / +14.4px）の原因を追うため、`enableHumanBoneLengthCorrection` の ON/OFF を測った。

| 設定 | RMS median | p90 |
|---|---|---|
| 骨長補正 ON（現行） | 6.36% | **11.78%** |
| **骨長補正 OFF** | **6.31%** | **9.43%** |

**全体指標では OFF の方がわずかに良い**（median ほぼ同じ、p90 は 11.78% → 9.43% と明確に改善）。

**部位別に見ると、補正が足首を悪化させている:**

| 部位 | 補正 ON | 補正 OFF | 差 |
|---|---|---|---|
| RKnee | 3.1 | 7.8 | −4.8 ← 補正で改善 |
| **RAnk** | **20.5** | **13.6** | **+6.8 ← 補正で悪化** |
| LKnee | 0.2 | 3.1 | −2.9 ← 補正で改善 |
| **LAnk** | **14.4** | **6.5** | **+7.9 ← 補正で悪化** |
| RWri | −4.9 | −1.6 | −3.3 ← 補正で悪化 |
| LWri | −7.8 | −1.9 | −5.8 ← 補正で悪化 |

**膝は改善するが、足首はその倍以上悪化する。** 補正 OFF なら足首のずれは RAnk 13.6 / LAnk 6.5px まで下がる（ON では 20.5 / 14.4px）。

手首も悪化している（−1.6 → −4.9、−1.9 → −7.8）。脚の補正が腕に影響するのは、`TryApplyHumanBoneLengthCorrection` がインスタンス全体のスケールや親ボーンを触っている可能性がある。**要コード確認。**

#### 評価

**「膝を合わせるために下腿を伸ばし、その分だけ足首が下へ行き過ぎている」**という構造に見える。骨長補正は骨の長さを keypoints に合わせる処理なので、膝の位置は合うが、そこから先の足首は伸ばした長さの分だけずれる。

`enableHumanArmLengthCorrection`（腕版）を 2026-08-19 に「実測で逆効果」として OFF にしたが、**脚版も同じ性質を持っている**ことになる。

#### 注意: これは既定値の変更を意味しない

`enableHumanBoneLengthCorrection` は既定 true で、実機で評価された状態。**姿勢一致度の指標だけで OFF にするのは早計。** この補正は本来「モデル固有のプロポーションを映像の人物に合わせる」ためのもので、姿勢一致度とは別の目的がある。切ると身長やシルエットが変わる可能性があるため、**実機で見てから判断する。**

#### 次に確認すること

1. `TryApplyHumanBoneLengthCorrection` が何をどう変えているか（手首まで影響が出る理由）
2. 補正 OFF で実機の見た目がどう変わるか
3. 足首だけを対象から外す（下腿は補正するが足は触らない）ことが可能か

### コード確認と ⑧ とのクロス検証（2026-08-21）

「設計がおかしいのに、たまたま噛み合って見た目が成立している可能性がある」という指摘を受けてコードを読み、⑧ とのクロスを測った。

#### コード確認: 腕には触っていない（当初の疑いは外れ）

`HumanBoneLengthCorrection.Apply()` は `LeftLowerArm` / `Hand` にも `ApplyToBone` を呼んでいるが、`TryResolveLimbBoneLengthFactors` は `enableHumanArmLengthCorrection` が false のとき **`upperArmFactor` / `foreArmFactor` を 1.0 のまま早期 return する**。倍率 1.0 を掛けるだけなので腕の骨長は変わらない。**「脚の補正が腕に影響している」という疑いは外れ。**

手首のずれが骨長補正で変わる（−1.6 → −4.9px）のは、**脚の長さが変わる → 投影身長が変わる → ⑧ が深度を変える → 遠近感が変わる** という連鎖によるもの。設計上の間接的な結合。

#### ⑧ × 骨長補正のクロス

| 設定 | RMS median | p90 | RAnk | LAnk | RKnee | LKnee |
|---|---|---|---|---|---|---|
| **⑧ON 骨長ON（現行）** | 6.36% | 11.80% | **20.5** | **14.4** | 3.1 | 0.2 |
| **⑧ON 骨長OFF** | **6.31%** | **9.43%** | **13.6** | **6.5** | 7.8 | 3.1 |
| ⑧OFF 骨長ON | 6.97% | 15.53% | 27.2 | 19.7 | 1.4 | −3.6 |
| ⑧OFF 骨長OFF | 6.52% | 15.15% | 24.2 | 18.0 | 6.5 | 0.5 |

（RAnk/LAnk/RKnee/LKnee は相対 dv median、+ = モデルが keypoint より下）

**読み取れること:**

1. **⑧ は姿勢一致度を改善している**（p90 が 15.5% → 11.8%、15.2% → 9.4%）。深度を bbox に合わせることで遠近感が正しくなり、投影位置が keypoints に近づく
2. **骨長補正は膝を改善し足首を悪化させる。** これは ⑧ の ON/OFF に関わらず一貫（⑧ON: 膝 7.8→3.1 / 足首 13.6→20.5、⑧OFF: 膝 6.5→1.4 / 足首 24.2→27.2）
3. **どの組み合わせでも足首が最大の残差**（+13.6 〜 +27.2px、常にモデルが下）

#### コメントと現状の食い違い

`HumanBoneLengthCorrection.cs` の冒頭コメント:

> 実測（2026-08-06）で、既定の Human モデルは胴で正規化した脚が映像より 8.3% 短く（大腿 3.5% / 下腿 15.1%）、その結果 **足首が bbox 高さの約 10% 上**にずれていた

**現在は補正 OFF でも足首は「下」にずれている（+13.6px）。符号が逆。** 2026-08-06 時点の前提（足首が上にずれる）はもう成立していない。

この間に入った変更（⑧ の深度補正、AimAt 周りの調整、bundle 側の depth 修正）のどれかで状況が変わったが、**骨長補正だけが当時の前提のまま残っている。** ユーザーの言う「たまたま噛み合っている」状態の実体はこれ。

#### 判断

**姿勢一致度だけを見れば `enableHumanBoneLengthCorrection` は OFF が良い**（p90 11.80% → 9.43%、足首 20.5 → 13.6px）。ただしこの補正の本来の目的は「モデル固有のプロポーションを映像の人物に合わせる」ことで、姿勢一致度とは別軸。**切ると身長・シルエットが変わるので実機確認が要る。**

**次にやるべきは足首の残差そのものの解明。** どの設定でも +13.6px 以上残っており、これが最大の誤差源。膝が合っていて足首がずれるので、下腿の向きか、足首から先（Toes）の扱いを見る必要がある。

### 足首の残差の正体: Unity の Foot ボーンは Ankle ではなく Heel の位置にある（2026-08-21 確定）

最大の残差だった足首のずれを、keypoints の複数の点と突き合わせて切り分けた。

**Foot ボーンを Ankle / BigToe / Heel それぞれと比較した相対 dv（骨長補正 OFF、+ = モデルが下）:**

| 比較 | 相対 dv median | sd |
|---|---|---|
| RightFoot ボーン vs **Ankle(11)** | **+13.6** | 15.1 |
| RightFoot ボーン vs BigToe(22) | +36.2 | 36.7 |
| **RightFoot ボーン vs Heel(24)** | **−2.5** | **16.1** |
| LeftFoot ボーン vs **Ankle(14)** | **+6.5** | 13.8 |
| LeftFoot ボーン vs BigToe(19) | +18.2 | 39.0 |
| **LeftFoot ボーン vs Heel(21)** | **−3.5** | **11.9** |

**Unity Humanoid の Foot ボーンは、OpenPose の Ankle ではなく Heel とほぼ一致する（−2.5 / −3.5px）。**

これまで「足首が +13.6 / +6.5px ずれている」と測っていたのは、**Ankle と Heel という別々の点を比べていたため**。Foot ボーンを正しく Heel と比べれば、ずれは 3px 以下でほぼ一致している。

**Toes ボーンも BigToe とは一致しない**（RToes +21.5 / LToes +11.8px）。Toes ボーンは足指の付け根、BigToe は親指の先を指すので、これも別の点。

#### これが意味すること

**姿勢一致度の指標（RMS）が、足首の項目で系統的に過大評価されていた。** `[BONEKP]` の比較ペアで `LeftFoot ↔ Ankle(14)` / `RightFoot ↔ Ankle(11)` を使っていたのが誤り。

正しいペアで測り直せば、**モデルの姿勢は当初の測定より keypoints に近い**ことになる。「四肢が伸びている」という当初の読みも、足首については **Heel と Ankle の距離を「伸び」と誤認していた**分を差し引く必要がある。

ただし手首側のずれ（LWri −7.8px など）は別の話で、こちらは Hand ボーンと Wrist(7) の対応なので定義ずれは小さいはず。**上半身の残差は実在する可能性が高い。**

#### 骨長補正の評価も変わる

`enableHumanBoneLengthCorrection` は「下腿を伸ばして足首を下げる」補正だが、**Foot ボーンが Heel 相当である以上、Ankle と比べて調整するのは基準がずれている。**

補正 ON で Foot vs Heel が +5.3 / +2.0px、OFF で −2.5 / −3.5px。**OFF の方が Heel との一致は良い**が、どちらも数 px の差で、当初見えていた「20.5px の悪化」ほどの差ではない。

#### 次にやること

1. **`[BONEKP]` の比較ペアを修正する**（Foot ↔ Heel、Toes は BigToe との対応を再考）
2. **修正したペアで姿勢一致度を測り直す** — 現在の RMS 6.31〜6.36% は足首の誤ペア分を含んでいる
3. **`TryResolveLimbBoneLengthFactors` の下腿の定義を確認** — `ResolveBoneDistance(LowerLeg, Foot)` は「膝から踵」を測っているが、keypoints 側の `sourceShin` が「膝から足首」なら、ここでも同じ取り違えが起きている

### 定義を揃えた結果: 骨長補正 OFF なら足も手もほぼ一致する（2026-08-21）

`sourceShin` の定義を「膝→足首」から「膝→踵」に変更し（モデル側 `ResolveBoneDistance(LowerLeg, Foot)` と定義を揃えた）、`[BONEKP]` の比較ペアも Foot↔Heel に修正して測り直した。

| 設定 | RMS median | p90 |
|---|---|---|
| 【修正前】Foot↔Ankle 骨長ON | 6.36% | 11.78% |
| 【修正前】Foot↔Ankle 骨長OFF | 6.31% | 9.43% |
| 【修正後】Foot↔Heel 骨長ON | 6.35% | 11.37% |
| **【修正後】Foot↔Heel 骨長OFF** | **5.85%** | **9.47%** |

**部位別（相対 dv median、+ = モデルが下）:**

| 部位 | 骨長 ON | **骨長 OFF** |
|---|---|---|
| Neck | 1.5 | 1.0 |
| RSho | −3.9 | −2.5 |
| RElb | −0.5 | **1.1** |
| **RWri** | **−5.7** | **−0.6** |
| LElb | −1.5 | **1.0** |
| **LWri** | **−8.3** | **−0.9** |
| RKnee | 3.7 | 10.3 |
| **RFoot** | **17.5** | **0.0** |
| LKnee | 0.0 | 5.1 |
| **LFoot** | **11.2** | **−1.9** |

**骨長補正を切ると、足は完全に一致し（RFoot 0.0 / LFoot −1.9px）、手首も 1px 以下になる（−0.6 / −0.9px）。** 残るのは膝（+10.3 / +5.1px）と肩・腰（−2.5 / −3.7px）だけ。

**骨長補正 ON では足が 17.5 / 11.2px 下、手首が −5.7 / −8.3px 上にずれる。** 補正が姿勢を崩している。

#### 結論: 骨長補正は姿勢一致を悪化させている

当初の「四肢が伸びている」という読みは半分正しく半分誤っていた。

- **足首のずれ** … 大半は Foot↔Ankle の定義取り違えによる測定側の誤り。定義を揃えれば骨長補正 OFF でほぼゼロ
- **手首のずれ** … 骨長補正が引き起こしていた。OFF で 1px 以下になる
- **膝のずれ** … 骨長補正 OFF の方が大きい（10.3 / 5.1px）。ここは補正が効いている唯一の部位

**トレードオフは「膝を 10px 合わせるために、足を 17px・手首を 8px ずらす」という割の合わないもの。**

#### 未確定: 実機の見た目

姿勢一致度では OFF が明確に良いが、この補正の本来の目的は「モデルのプロポーションを映像の人物に合わせる」ことで、**切ると身長・シルエットが変わる。** 既定値の変更は実機確認が要る。

なお `sourceShin` の定義修正（膝→踵）自体は、モデル側と定義を揃える正しい修正なので、**骨長補正を使う限りは入れておくべき。** 修正後の ON（RMS p90 11.37%）は修正前の ON（11.78%）よりわずかに良い。

### 決定: `enableHumanBoneLengthCorrection` を既定 OFF にした（2026-08-21）

姿勢一致を最優先目標に据えた結果、この補正は既定 OFF とした。実機でも「（keypoints に）近づいていそう」と確認済み。

**根拠:**

| 部位 | ON | **OFF** |
|---|---|---|
| 右足 | +17.5px | **0.0px** |
| 左足 | +11.2px | **−1.9px** |
| 右手首 | −5.7px | **−0.6px** |
| 左手首 | −8.3px | **−0.9px** |
| 右膝 | +3.7px | +10.3px ← 膝だけ ON が良い |
| RMS median | 6.35% | **5.85%** |

**「膝を 10px 合わせるために足を 17px・手首を 8px ずらす」**割の合わないトレードオフだった。

2026-08-06 当時の前提（足首が bbox 高さの 10% *上* にずれる）は既に成立していない。⑧ の深度補正で遠近感が正しくなったこと、bundle 側の depth 修正が進んだことで状況が変わり、**補正だけが当時の前提のまま残っていた。**

コードは残してあるので、モデルのプロポーションを合わせたい場面では Inspector で有効化できる。`sourceShin` の定義修正（膝→踵）は入れたままにする。

### 2026-08-21 時点の姿勢一致度まとめ

| 設定 | RMS median | p90 |
|---|---|---|
| AimAt ON / 骨長 OFF（**現在**） | **5.85%** | 9.47% |
| AimAt ON / 骨長 ON | 6.35% | 11.37% |
| AimAt OFF / 骨長 OFF | 24.79% | 36.34% |

**AimAt は必須**（切ると 4 倍悪化）。残る主な残差は膝（+10.3 / +5.1px）と肩・腰（−2.5 / −3.7px）で、いずれも 10px 以下。

**足首・手首の残差はほぼ解消した。** 次に姿勢一致度を上げるなら膝が対象になるが、現在の残差水準（RMS 5.85%、bboxH 比）で十分かどうかは、配置側の要求精度と合わせて判断する。

## 姿勢一致の現状（2026-08-26 実測、driftfix bundle・全 2167 フレーム）

`[BONEKP]` は `(実ボーンの投影) − (keypoints3d の投影)` を px で出す。v は下向き正なので **dy が負ならボーンが keypoint より画面上で上**にある。

| 関節 | 横ずれ med | 縦ずれ med | 距離 med | bboxH 比 |
|---|---|---|---|---|
| Neck | +2.0 | +2.0 | 16.3px | 5.32% |
| RSho | +7.0 | −2.0 | 17.0px | 5.23% |
| RElb | +4.0 | +3.0 | 17.5px | 5.07% |
| RWri | +4.0 | −1.0 | 17.5px | 5.06% |
| LSho | −4.0 | −1.0 | 14.9px | 5.23% |
| LElb | −2.0 | +2.0 | 14.1px | 4.75% |
| LWri | −2.0 | −2.0 | 18.9px | 5.83% |
| RHip | −7.0 | −4.0 | 18.0px | 5.78% |
| RKnee | −8.0 | −1.0 | 17.7px | 5.42% |
| **RFoot** | −1.0 | **−16.0** | **24.1px** | **7.14%** |
| LHip | −1.0 | −6.0 | 14.3px | 5.21% |
| LKnee | +2.0 | −4.0 | 15.0px | 4.34% |
| **LFoot** | +0.0 | **−17.0** | **21.0px** | **6.21%** |
| 全体 | — | — | — | median **5.39%** / p90 16.35% |

**最大の残差は足の縦方向（−16 / −17px）で、両足に同符号で出ている。** モデルの足が keypoint より 16px 上にある＝脚が短いか、モデル全体が浮いている。左右対称なので個別の関節角ではなく系統的なずれ。

膝は横 −8px / +2px で、2026-08-28 週次に書いた「膝の残差 +10.3px」より小さく、**いまの最優先対象は膝ではなく足**。

なお `RFoot` は keypoint 24（右踵）と比較している（`sourceShin` 修正時に合わせた）。`RFoot_vsToe` / `RToes` も同時に出しているので、基準点の取り違えではないことは確認できる。

全体の median 5.39% は 2026-08-21 時点の 5.85% よりわずかに良い。bundle が driftfix に変わったことによる差と考えられるが、切り分けはしていない。

### 骨長補正の再評価（2026-08-26）: 足のずれは骨長補正では直らない

足の縦 16px ずれ（脛がモデル側で 15px 短い）に対して、対処機構である `enableHumanBoneLengthCorrection` を A/B した。`sourceShin` を「膝→踵」に直した後・driftfix bundle での再評価。

| 関節 | OFF 距離 / 縦 | ON 距離 / 縦 | 差 |
|---|---|---|---|
| Neck | 16.3px / +2 | 17.1px / −11 | +0.8 |
| RSho | 17.0 / −2 | 23.0 / −19 | **+6.0** |
| RElb | 17.5 / +3 | 20.6 / −13 | +3.1 |
| RWri | 17.5 / −1 | 21.3 / −15 | +3.8 |
| LSho | 14.9 / −1 | 18.9 / −15 | +4.0 |
| LElb | 14.1 / +2 | 17.8 / −14 | +3.7 |
| LWri | 18.9 / −2 | 22.5 / −15 | +3.6 |
| RHip | 18.0 / −4 | 31.3 / −30 | **+13.2** |
| RKnee | 17.7 / −1 | 25.5 / −21 | **+7.8** |
| **RFoot** | 24.1 / −16 | 22.1 / −14 | **−1.9（唯一の改善）** |
| LHip | 14.3 / −6 | 31.8 / −31 | **+17.4** |
| LKnee | 15.1 / −4 | 24.0 / −23 | **+8.9** |
| LFoot | 21.0 / −17 | 21.9 / −17 | +0.9 |
| **全関節 bboxH 比** | **5.39%** | **7.41%** | 悪化 |

**改善 1 関節 / 悪化 10 関節。** 2026-08-21 に見た「膝を合わせるために足と手首をずらす」トレードオフは、`sourceShin` の定義を直したあとでも解消していない。しかも今回は**膝も股関節も悪化**しており、当時より状況が悪い。

**決定的なのは、狙った足すら 24.1 → 22.1px（縦 −16 → −14）としか動かないこと。** 足のずれは骨長補正で説明・是正できる量ではない。

ON にすると全関節が縦に −11〜−31px 動く（モデル全体が画面上で上へ寄る）。骨長を変えたぶんルートの位置合わせが崩れているように見えるが、原因は追っていない。**既定 OFF を維持する。**

### 足のずれは当面放置する

- ユーザーは実機で違和感を感じていない（2026-08-26 確認）
- ⑦ `FitDisplayedModelToBBox` が投影下端を bbox に合わせるのでシルエットは合う
- 唯一の対処機構である骨長補正は上記のとおり全体を悪化させる
- 他の関節は縦の偏りが ±1〜6px で、足だけが −16 / −17px と系統的

**別の機構を新設してまで直す価値は現時点でない。** 再検討するなら、まず「なぜ脛だけ短いのか」（モデルの体型か、SMPL betas か、FK の脛セグメントの扱いか）を切り分けてから。

## 2026-08-27: Animal の姿勢一致を初めて測った（`[ANIMALKP]`）

human の `[BONEKP]` に相当する診断を animal 向けに新設した。`AnimalRigCache` が解決したボーンと `meta.bin` の keypoints3d を同じ式で投影し、その差を px で出す。

### ボーンと keypoint の対応（実装から抽出）

| 部位 | ボーン | keypoint |
|---|---|---|
| 首・頭 | `neck` → `head` | **24 → 2** |
| 左前脚 | `leftFrontUpper` → `Lower` → `Paw` | **18 → 13 → 9 → 15** |
| 右前脚 | `rightFront…` | **18 → 12 → 8 → 14** |
| 左後脚 | `leftRear…` | **7 → 11 → 17 → 6** |
| 右後脚 | `rightRear…` | **7 → 10 → 16 → 5** |

`AnimalPoseJointChains` と `ApplyAnimalHeadPose` の実装に合わせている。root は 7 と 18 の中点（bundle 側の説明と一致）。

### 結果: human とは桁が違う

| 部位 | 件数 | 横ずれ med | 縦ずれ med | 距離 med | **bboxH 比** |
|---|---|---|---|---|---|
| **Neck** | 1690 | −636px | −126px | 1489px | **378.3%** |
| **Head** | 1805 | −490px | −346px | 1221px | **281.5%** |
| LFPaw | 1579 | +166px | −596px | 694px | 142.0% |
| RFPaw | 1626 | −158px | −494px | 662px | 139.6% |
| LRPaw | 1197 | +94px | −427px | 468px | 94.2% |
| RRPaw | 1420 | −89px | −402px | 458px | 95.0% |
| LFUp | 2120 | +226px | +115px | 353px | 81.9% |
| RFUp | 2120 | −250px | +109px | 389px | 77.0% |
| LRUp | 2040 | +234px | −238px | 351px | 66.1% |
| RRUp | 2019 | −122px | −199px | 284px | 60.7% |
| TailBase | 2117 | +63px | −98px | 222px | 43.5% |
| **全体** | — | — | — | — | **median 85.6% / p90 344.8%** |

**human の同指標は median 5.30%。animal は 85.6% で 16 倍。**

### 読み取れること

**1. ボーンの解決は成功している。** 15/15 本が全フレームで解決（`resolvedBones=15/15` が 100%）。ボーン名の対応表自体は機能している。

**2. 首と頭が突出して悪い**（378% / 281%）。前脚・後脚の 60〜95% と比べても桁が違う。`ApplyAnimalHeadPose` が使う keypoint 24 → 2 の対応、または `cache.neck` / `cache.head` の解決先が疑わしい。

**3. 左右で符号が反転している。** LFUp +226px / RFUp −250px、LRUp +234px / RRUp −122px。**モデルの左右幅が keypoint より広い**ことを示す（横に開いている）。これは体型差として説明できる。

**4. 遠位ほど悪い。** Up（60〜82%）< Lo（67〜87%）< Paw（94〜142%）。FK の累積誤差の典型的な形。

**5. 可視フラグの欠落が多い。** `LRPaw` は 27664 件が `novis`、`RRPaw` は 20981 件。後脚の足先は 4 割前後のフレームで不可視。

### 注意: この数値は「姿勢の誤り」だけを表していない

`[BONEKP]`（human）と違い、animal では**モデルの体型と実際の動物の体型が大きく違う**。犬モデルと映像の犬、リンクスと猫では骨格の比率が異なるので、**完全に一致することはあり得ない**。

したがって 85.6% という絶対値を「これだけ間違っている」と読むのは誤り。**使い方は (a) 部位間の比較、(b) 変更前後の比較、(c) 極端な値（Neck 378%）の検出**に限る。

### 次に調べるべきこと

**首と頭の 378% / 281%。** 他部位（43〜142%）と桁が違うので、体型差では説明しにくい。候補:

1. `cache.neck` / `cache.head` が別のボーン（armature の補助ノード等）に解決されている
2. keypoint 24 / 2 の意味が想定と違う（bundle 側も「関節番号の意味は断定できない」と明言）
3. `ApplyAnimalHeadPose` が `alpha * 0.35f` と弱い係数で適用しており、そもそも合わせきっていない

**まず 1 を確認する**（解決されたボーン名をログに出す）のが最も安く、切り分けになる。

### 訂正: 位置ではなく角度で測る（2026-08-27）

前項の `[ANIMALKP]`（位置の差）は**測る対象を間違えていた**。適用側 `ApplyAnimalBoneFromPoints` は

```csharp
TransformWriter.ApplyWorldRotation(bone, Quaternion.Slerp(bone.rotation, targetWorld, alpha));
```

と **回転だけを書いており、位置は一切動かさない**。したがって「ボーンの位置と keypoint の位置の差」を測っても、適用の良し悪しを表さない。Neck 378% という数字はこの誤りによるもので、**撤回する**。

さらにペアの取り方も間違っていた。チェーン `[18, 13, 9, 15]` で upper は `18→13` の向きを使うので、upper に対応するのは 18 と 13 の**組**であって 13 単体ではない。

**正しい指標は「ボーンが向いている方向」と「keypoint のペアが示す方向」の角度差。** ボーンの向きは適用側と同じ `TryGetBoneCenterDirectionWorld` を使う（`TryGetBoneDirectionForDiag` として公開）。

### 結果（角度差、度）

| 部位 | 件数 | median | p10 | p90 | 30度未満 | 90度超 |
|---|---|---|---|---|---|---|
| **Neck** | 2120 | **146°** | 104° | 171° | **0%** | **97%** |
| Head | 0 | 測れず（後述） | | | | |
| LFUp | 2120 | 77° | 37° | 102° | 0% | 32% |
| RFUp | 2120 | 83° | 56° | 108° | 0% | 26% |
| LRUp | 2037 | 73° | 60° | 88° | 0% | 8% |
| RRUp | 2016 | 81° | 63° | 94° | 0% | 13% |
| LFLo | 2120 | 37° | 24° | 55° | 28% | 2% |
| RFLo | 2120 | 43° | 27° | 64° | 19% | 3% |
| LRLo | 1736 | 49° | 30° | 70° | 10% | 0% |
| **RRLo** | 1733 | **24°** | 8° | 42° | **68%** | 0% |
| LFPaw | 1557 | 53° | 22° | 88° | 18% | 7% |
| RFPaw | 1604 | 59° | 22° | 91° | 17% | 10% |
| **LRPaw** | 1197 | **20°** | 10° | 36° | **71%** | 0% |
| **RRPaw** | 1420 | **24°** | 12° | 45° | **64%** | 0% |
| **全体** | 23900 | **57°** | 21° | 107° | 19% | 17% |

**ランダムな向き同士なら期待値は 90 度。** human は FK 基底変換の修正後で平均 8.5 度（AimAt 適用前）。

### 読み取れること

**1. Neck が 146 度 ── ほぼ逆を向いている。** 97% のフレームで 90 度を超える。**偶然（90 度）より悪い**ので、単なる誤差ではなく**向きの定義が反転しているか、対応そのものが誤っている**。

**2. Head は一度も測れなかった**（`nodir` が 60509 件 = 全フレーム）。`TryGetBoneCenterDirectionWorld` が `head` ボーンの向きを返せていない。**head は末端ボーンで子が無いため方向が定義できない**と推測される。つまり **`ApplyAnimalBoneFromPoints(cache.head, ...)` も同じ理由で何もしていない可能性が高い。**

**3. Upper が悪く（73〜83 度）、Lower / Paw が良い（20〜59 度）。** FK の累積誤差なら遠位ほど悪くなるはずで、**逆の傾向**。上腕・大腿の向きの取り方に問題がある。

**4. 後脚の遠位が最も良い**（LRPaw 20°、RRLo 24°、RRPaw 24°）。ここは正しく効いている。

### 注意: 体型差では説明できない

human と違い animal はモデルと実際の動物の体型が違うが、**角度差は体型の縦横比に影響されにくい**。四つ足の脚が前を向いているか後ろを向いているかは体型に依らない。**146 度や 77〜83 度は体型差の範囲を超えている。**

### 次に調べる順序

1. **Head が `nodir` になる理由**（末端ボーンで向きが定義できていないなら、頭の姿勢適用は最初から効いていない）
2. **Neck の 146 度**（向きの反転か、24→2 という対応が誤りか）
3. Upper の 73〜83 度（`ShouldUseAnimalAimChildPivotDirection` の分岐が Upper で不利に働いていないか）

### 確定 2026-08-28: `head` ボーンの姿勢適用は一度も効いていない

`[ANIMALKP]` で `Head` が全 60509 フレーム `nodir` だった件を追った。**コード上の欠陥として確定した。**

#### 経路

`ApplyAnimalBoneFromPoints` は、まず `TryGetBoneCenterDirectionWorld` で「ボーンが今向いている方向」を取り、そこから目標方向への回転を作る。取れなければ何もしない。

```csharp
bool hasRegisteredAimChild = cache.aimChildByBone.TryGetValue(bone, out ...);
if (centerTarget != null && ShouldUseAnimalAimChildPivotDirection(hasRegisteredAimChild, IsAnimalLimbBone(cache, bone)))
{
    // 子ボーンへの向きを使う
}
// 落ちたら↓
if (!TryGetTransformCenterWorld(centerTarget, out Vector3 centerWorld)) { return false; }
```

`ShouldUseChildPivotDirection = hasRegisteredAimChild || isLimbBone`。

| ボーン | isLimbBone | aim child 登録 | 結果 |
|---|---|---|---|
| 四肢 12 本 | **true** | あり | 子への向きが取れる |
| `neck` | false | **あり**（`neck → head`） | 取れる |
| **`head`** | **false** | **無い** | **取れない** |
| `spine` | false | あり（`spine → neck`） | 取れる |

登録リストは `leftFrontUpper→leftFrontLower`、…、**`neck→head`**、`spine→neck`、`tailBase→tailMid`、`tailMid→tailTip`。**`head → ?` が存在しない。** `head` は末端ボーンなので当然だが、その場合のフォールバックが機能しない。

#### フォールバックが必ず失敗する理由

```csharp
private static bool TryGetTransformCenterWorld(Transform target, out Vector3 centerWorld)
{
    SkinnedMeshRenderer smr = target.GetComponent<SkinnedMeshRenderer>();   // ← GetComponent
    MeshFilter mf = target.GetComponent<MeshFilter>();
    Renderer renderer = target.GetComponent<Renderer>();
    return false;   // どれも無ければ false
}
```

`GetComponent`（`GetComponentInChildren` ではない）なので、**Transform に直接 Renderer が付いている必要がある**。リグのボーンは純粋な Transform なので、**このフォールバックは構造上必ず失敗する。**

したがって `head` は方向を取れず、`ApplyAnimalBoneFromPoints` が早期 return する。**頭の姿勢は bind pose のまま固定されている。**

#### 影響範囲

- `ApplyAnimalHeadPose` が `cache.head` に対して行う適用（`24→2` の向き、control 経路の `headRoot→headTip`）が**すべて無効**
- 同じ理由で `leftRearToe` / `rightRearToe` / `tailTip` など**末端ボーンはすべて同じ状態**の可能性が高い（未確認）

#### 対処案

| 案 | 内容 | 懸念 |
|---|---|---|
| **A** | `head` の aim child を**子 Transform から自動で拾う**（`ResolveAnimalAimChild` が既にやっているはず。なぜ null なのか要確認） | `head` に子が本当に無いモデルでは効かない |
| **B** | 末端ボーンは**親からの向き**（`bone.position - parent.position`）を現在方向とする | 末端ボーンの向きとしては妥当。実装が単純 |
| C | `TryGetTransformCenterWorld` を `GetComponentInChildren` にする | ボーン配下にメッシュがある構造でしか効かず、汎用性が低い |

**まず `ResolveAnimalAimChild(cache, head)` が何を返しているかを確認する**（`head` に子 Transform があるのに拾えていないなら A、本当に末端なら B）。

### 実測で確定（2026-08-28）: `head` は末端ではない。子を拾っているのに捨てている

前項で「`head` は末端ボーンだから方向が取れない」と推測したが、**外れ**。実測した。

| ボーン | 子の数 | 最初の子 | 方向取得 |
|---|---|---|---|
| `neck` | 1 | `head` | **1** |
| **`head`** | **36** | `head_attach` | **0** |
| 四肢 12 本 | 1 | 次のボーン | **1** |
| `spine` | 4 | — | 1 |
| `tailBase` / `tailMid` | 1 | — | 1 |
| `tailTip` | 0 | — | 0（真の末端） |
| **`rear_l_toe`** | **4** | — | **0** |
| **`rear_r_toe`** | **4** | — | **0** |

**`head` は子を 36 個持っている。** `rear_l_toe` / `rear_r_toe` も 4 個ずつ。それでも方向が取れていない。

#### 原因: 子を拾っているのに使わずに捨てている

```csharp
private Transform ResolveAnimalAimChild(AnimalRigCache cache, Transform bone)
{
    Transform registeredAimChild = ...;                                   // head では null
    Transform fallbackFirstChild = bone.childCount > 0 ? bone.GetChild(0) : null;  // head_attach を得る
    return AnimalAimChildSelector.Select(registeredAimChild, fallbackFirstChild);  // head_attach を返す
}
```

`ResolveAnimalAimChild` は正しく `head_attach` を返している。問題はその先:

```csharp
Transform centerTarget = ResolveAnimalAimChild(cache, bone);              // head_attach
if (centerTarget != null && ShouldUseAnimalAimChildPivotDirection(hasRegisteredAimChild, IsAnimalLimbBone(cache, bone)))
{
    // ここに入れば centerTarget への向きが使える
}
// ↓ head は入れないので、必ず失敗するフォールバックへ落ちる
if (!TryGetTransformCenterWorld(centerTarget, out Vector3 centerWorld)) { return false; }
```

`ShouldUseChildPivotDirection = hasRegisteredAimChild || isLimbBone`。**`head` は登録なし・四肢でないので false。** せっかく取得した `head_attach` を使わず、Transform に直接 Renderer が必要な `TryGetTransformCenterWorld` に落ちて失敗する。

**`rear_l_toe` / `rear_r_toe` も同じ**（`IsAnimalLimbBone` は upper / lower / paw のみを四肢と判定し、toe を含まない）。

#### 影響を受けるボーン

| ボーン | 状態 |
|---|---|
| `head` | **姿勢適用が一度も効いていない**（子 36 個あるのに） |
| `leftRearToe` / `rightRearToe` | 同上（子 4 個あるのに） |
| `tailTip` | 子が無いので原理的に取れない（別問題） |

#### 対処案

| 案 | 内容 | 懸念 |
|---|---|---|
| **A** | `ShouldUseChildPivotDirection` に「子がある非四肢ボーン」も通す | **なぜ登録済みか四肢に限っていたのか**が不明。`AnimalAimDirectionPolicy` として切り出されているので意図がある可能性が高い |
| B | `head` / `toe` を `RegisterAnimalAimPairs` に足す | 明示的で安全。ただしモデルごとに子の名前が違うと機能しない |
| C | フォールバックで「子があれば子への向き」を使う | A とほぼ同義だが、分岐の意図を壊さずに済む |

**`AnimalAimDirectionPolicy` が独立クラスとして切り出されている**のは、過去に何か踏んだ痕跡に見える。**変更する前に、なぜ「登録済み or 四肢」に限っているのかを調べる必要がある。**

### 訂正 2026-08-28: `head` が動かないのはバグではなく意図的な未実装

`AnimalAimDirectionPolicy` が独立クラスになっている理由を調べたところ、**ADR-0002 に明記されていた。**

> **Joints without a registered aim-child** to derive a real per-joint correction from yet (**paws, head, tail**) fall back to carrying the rest pose through (`tw[j] = parentTW * bindLoc`, no body_pose contribution) rather than reusing any constant — **an honest "not yet implemented" rather than a guess.**
> （`docs/adr/0002-animal-rig-generalization.md` 202 行目）

**「登録済み aim-child が無い関節（paws / head / tail）は rest pose のまま通す」は設計判断。** 当てずっぽうの補正を入れるより、動かさない方が正直だという理由。

さらに同 ADR は `TryGetBoneCenterDirectionWorld` について:

> it has its own heuristics (centering on a child's pivot, preferring non-renderer objects, etc.) that can disagree with the raw geometry for some rigs
> （161 行目）

とし、**素の幾何（`child.position - bone.position`）に置き換えたら別のモデルで退行した**経緯も残っている（149〜172 行目、3 通りの bind-time 幾何がすべて失敗）。

#### したがって「`ShouldUseChildPivotDirection` を緩める」は危険

`hasRegisteredAimChild || isLimbBone` を「子があれば通す」に広げると、**ADR-0002 が意図的に避けた「当てずっぽうの向き」を head / toe に入れることになる。** `head` の子は `head_attach`（36 個の子の先頭）で、これがアタッチ用の空ノードなら向きは無意味になる。

#### 正しい対処は「登録する」方

ADR の書きぶりは「**登録済み aim-child が無いから**未実装」であって「原理的にできない」ではない。**`RegisterAnimalAimPairs` に head の正しい aim child を足せば、設計どおりの経路で動くようになる。**

必要なのは「`head` に対して解剖学的に妥当な子ボーン」の特定。`head_attach` が何なのかを確認する必要がある（アタッチ用の空ノードか、実際に顔の前方にあるボーンか）。

#### 現状の位置づけ

| 部位 | 状態 | 種別 |
|---|---|---|
| 四肢 upper/lower/paw | 動く（角度差 20〜83°） | 実装済み |
| `neck` | 動く（146°、要調査） | 実装済み |
| **`head`** | **rest pose のまま** | **意図的な未実装（ADR-0002）** |
| `leftRearToe` / `rightRearToe` | rest pose のまま | 同上（paws に含まれる） |
| `tailTip` | rest pose のまま | 同上（tail） |

**`[ANIMALKP]` の `Head=nodir` は、この未実装を正しく検出していた。** 指標としては機能している。

### 確定 2026-08-28: `neck` の 146 度は keypoint 対応の誤り

`ApplyAnimalHeadPose` は `neck` と `head` の両方に **`24→2` の向き**を適用している。

```csharp
ApplyAnimalBonesFromSegment(cache, cache.neck, cache.head, jointsWorld, vis, 24, 2, alpha * 0.35f, alpha * 0.35f);
```

この `24→2` が何を指すのかを、keypoints3d の空間配置から測った（track 0 = 犬、382 サンプル、root 相対の median）。

| keypoint | x | y | z（− が前方） |
|---|---|---|---|
| kp7（後 root） | −0.218 | −0.080 | +0.298 |
| kp18（前 root） | +0.218 | +0.080 | −0.298 |
| **kp24** | +0.314 | +0.030 | **−0.510** |
| **kp2** | +0.314 | −0.038 | **−0.479** |
| kp20 | +0.268 | +0.200 | −0.302 |
| kp21 | +0.130 | +0.204 | −0.378 |

**kp24 と kp2 はどちらも最前方（z ≈ −0.5）で、8.5cm しか離れていない。**

| セグメント | ベクトル | 長さ |
|---|---|---|
| **`24→2`（実装が使用）** | (−0.008, **−0.078**, +0.032) | **0.085m** |
| `7→18`（体の前後軸） | (+0.436, +0.160, −0.596) | 0.756m |
| `18→24`（前 root → 顔） | (+0.104, −0.028, −0.242) | 0.265m |

**`24→2` は体長 0.756m に対し 11% の長さしかなく、向きはほぼ真下（y −0.078 が支配的）。** 顔の中の 2 点（鼻先と顎など）の差分で、「首がどちらを向いているか」とは無関係。

角度で見ても:

| 比較 | 角度 |
|---|---|
| `24→2` と `7→18`（体軸） | **123 度** |
| `24→2` と `18→24` | **107 度** |

**首として使うべき前方向きと、ほぼ直交〜逆向き。** `[ANIMALKP]` が出した 146 度はこれを正しく検出していた。

#### 対処

**首の向きは `18→24`（前 root → 顔）であるべき。** kp18 は肩／き甲にあたる位置（体の前端）、kp24 は顔の最前方なので、その差分が首の向きになる。

ただし変更前に確認が要る:

1. **`head` にも同じ `24→2` が渡されている。** head は現状 rest pose のまま（ADR-0002 の未実装）なので実害は無いが、head を実装するときは別の対応が要る
2. **`alpha * 0.35f` と弱い係数**で適用されている。誤った向きを弱く当てていたので、正しい向きにすると効き方が変わる
3. bundle 側は「関節番号の解剖学的意味は断定できない」としている。**上記は空間配置からの推定**であり、bundle 側に確認する価値がある

#### 副産物: 他の keypoint の推定

| kp | 位置の特徴 | 推定 |
|---|---|---|
| 20 / 21 | y +0.20 で最も高い、z −0.30〜−0.38 | **耳（左右）** |
| 0 / 1 | y +0.116、z −0.47〜−0.51 | 目または耳の付け根 |
| 22 / 23 | y ≈ 0、z −0.48〜−0.51 | 目 |
| 24 | 最前方 z −0.510 | **鼻先** |
| 2 | z −0.479、y −0.038 | **顎／口** |
| 19 | z +0.804 で最後方 | **尻尾の先** |
| 25 | z +0.590 | 尻尾の中間 |

### A/B の結果 2026-08-28: 首の対応変更は測定上は無変化。しかも 146 度が再現しない

`24→2` を `18→24` に変える実装を入れて A/B した。**同一ビルドでフラグだけを切り替えた比較。**

| 部位 | OFF median | ON median | OFF 90超 | ON 90超 |
|---|---|---|---|---|
| **Neck** | **76°** | **76°** | 35% | 35% |
| LFUp / RFUp | 78° / 83° | 77° / 83° | 32% / 27% | 32% / 26% |
| LRUp / RRUp | 73° / 81° | 73° / 81° | 9% / 13% | 8% / 13% |
| その他 | 20〜59° | 20〜59° | 変化なし | 変化なし |
| **全体** | **57°** | **57°** | 12% | 11% |

**首も含めて何も変わらなかった。**

#### さらに問題: 以前の 146 度が再現しない

| 実行 | Neck median | p90 | 90度超 | Head |
|---|---|---|---|---|
| **以前（`animalang.log`）** | **146°** | 171° | **97%** | nodir 全件 |
| 今回 OFF | **76°** | 115° | 35% | nodir 全件 |
| 今回 ON | 76° | 118° | 35% | nodir 全件 |

**四肢の値は以前と完全に一致している**（LFUp 77/78°、LFLo 37°、LRPaw 20° など）。**Neck だけが 146 → 76 に変わった。**

間に入れた変更は (1) `ApplyAnimalBonesFromSegment` の呼び出しを 2 回の `ApplyAnimalBoneFromJoints` に分割、(2) `[ANIMALRIG]` に子ボーン数の出力を追加、(3) `AnimalHeadKeypoints` の定数化。**(1) は展開すると同一の呼び出しになるはずで、挙動が変わる理由が説明できていない。**

#### したがって「首が逆を向いている」は撤回する

146 度という値を根拠に「偶然より悪い＝向きの定義が誤り」と結論したが、**再現しない値を根拠にはできない。** 現在の 76 度は四肢の Upper（73〜83°）と同程度で、**Neck は外れ値ではない。**

`24→2` が幾何的に首の向きでないこと（8.5cm、ほぼ真下、体軸と 123 度）は keypoints3d の実測なので有効。ただし**それが実際の描画に影響しているという証拠は今のところ無い。**

#### 現在の設定

`animalNeckUsesBodyToHeadSegment` を **既定 true** にした。理由は測定上の改善ではなく、**control 経路が既に使っている `withersWorld → headRootWorld` と意味が揃う**こと。測定上は無害（全指標で同値）。

**測定上の利益が無いことを明記しておく。** 「直った」と誤解しないこと。

#### 未解明として残す

- なぜ Neck だけ 146 → 76 に変わったのか
- 首の適用が実際の見た目にどう効いているのか（角度差 76 度は「合っていない」が、体型差の寄与が分離できていない）

### 原因判明 2026-08-28: Animal は SMAL FK 経路で動いており、現行 bundle では keypoint 経路が走らない

Neck の A/B が「完全に無変化」だった理由と、146 度が再現しなかった理由が同じところにあった。

#### `ApplyAnimalHeadPose` は一度も実行されていない

`AnimalPoseApplier.ApplyAnimalPose` の冒頭:

```csharp
if (request.hasSmalPose && IsAnimalRigReadyForSmalFk(cache))
{
    AlignAnimalRootToSkeleton(...);
    TryApplyAnimalSmalFk(...);   // AnimalSmalFkApplier.cs
    ApplyGestureOverlay(cache, request);
    return;                       // ← ここで返る
}

// 以下は SMAL が無いときだけ走る keypoint 経路
TryApplyAnimalRootOrientation(...);
ApplyAnimalHeadPose(...);
ApplyAnimalTailPose(...);
ApplyAnimalLimbPose(...);
```

ログの実測:

```
[SMAL-PIPE] hasSmalPose=true camRot=(0.0, 0.0, 0.0)   × 1817 行
[SMAL-PIPE] hasSmalPose=false                          × 0 行
```

`IsAnimalRigReadyForSmalFk` の条件（spine + 四肢 Upper 4 本が非 null）も `[ANIMALRIG]` の実測で全部そろっている（`spine` / `front_l_upper` / `front_r_upper` / `rear_l_upper` / `rear_r_upper`）。

**つまり `bundle_animal_shots_depthdriftfix_shotsfix.svb` では SMAL FK 経路が走り、`ApplyAnimalHeadPose` / `ApplyAnimalTailPose` / `ApplyAnimalLimbPose` は一度も呼ばれない。**

（`[SMAL-PIPE]` は `frame % 30 == 0` のときだけ出るので、これは 1/30 サンプルでの全件一致。SMAL block を持たない古い bundle や、極端に短い shot は未確認。）

CLAUDE.md の表に「Animal の姿勢データ = AniMer + SMAL 予定」と書いてあるが、**実際にはもう SMAL block が bundle に入っていて、そちらが使われている。** 表の「未実装」は現状と合っていない。

#### したがって

- **`animalNeckUsesBodyToHeadSegment` はデッドコードを書き換えただけ。** A/B が全指標で同値だったのは当然で、「効果がない」のではなく「実行されていない」。
- **`24→2` が首の向きとして無関係、という指摘自体は正しいが、描画には影響していない。**
- 146 度 → 76 度の食い違いも、**どちらも「SMAL FK が出した姿勢」を測っていて、変わったのは診断側のペアだけ**（角度版 `[ANIMALKP]` は丸ごと未コミット差分で、2 回の実行の間に書き換えている）。実装は一切変わっていない。

#### `[ANIMALKP]` が実際に測っているもの

診断は「SMAL FK が出したボーン方向」対「AniMer keypoints3d が示す方向」。**別ソース同士の比較**であって、「適用がターゲットに収束しているか」ではない。ただし最優先目標が「モデル体勢を keypoints3d に一致させること」なので、**指標としては有効**。読み替えると:

| 部位 | 比較した keypoint ペア | median | 意味 |
|---|---|---|---|
| Neck | 24→2（この実行時）※現在は 18→24 に固定 | 76° | SMAL FK の首と AniMer の首方向が 76 度ずれている |
| 四肢 Upper | chain[0]→chain[1]（18 または 7 起点） | 72〜83° | 同上。**最も大きい** |
| 四肢 Lower | chain[1]→chain[2] | 37〜59° | |
| 四肢 Paw | chain[2]→chain[3] | 18〜20° | **最も小さい** |

**数値は必ず「どのペアで測ったか」とセットで扱う。** ペア定義を変えたら過去の値と混ぜない。

近位ほど悪く遠位ほど良い。当初これを「AimAt が遠位を引き戻している」と書いたが**誤り**で、`AnimalSmalFkApplier` に AimAt は無い（`enableKeypointAimAt` は `HumanSmpl.partial.cs` だけが読む Human 専用）。実際の理由は下の駆動範囲の表にある。

#### SMAL FK が実際に回しているボーン（`AnimalSmalFkApplier.cs`）

`GetSmalBoneForJoint` + `SmalRestDirByJoint` + `AnimalSmalFkPolicy.ShouldKeepBindPoseForJoint` の 3 つで決まる。

| SMAL joint | ボーン | body_pose | 備考 |
|---|---|---|---|
| 0 | `spine` | globalOrient | `tw[0] = worldFk0 * bindRotWorld[spine]` |
| 7 / 8 | LF upper / lower | **駆動** | |
| 9 | LF paw | **なし** | rest dir 未登録 → `parentTW * bindLoc` |
| 11 / 12 | RF upper / lower | **駆動** | |
| 13 | RF paw | **なし** | |
| 15 | `neck` | **駆動** | |
| 16 | `head` | **なし** | rest dir 未登録。ADR-0002 の「未実装」の実体 |
| 17 / 18 | LR upper / lower | **駆動** | |
| 19 / 20 | LR paw / toe | **なし** | |
| 21 / 22 | RR upper / lower | **駆動** | |
| 23 / 24 | RR paw / toe | **なし** | |
| 25 / 26 | `tailBase` / `tailMid` | **駆動（0.5 倍に減衰）** | `TailBodyPoseScale = 0.5f` |
| 27 | `tailTip` | **なし** | `ShouldKeepBindPoseForJoint(27..31)` |

**paw 4 本・toe 2 本・head・tailTip は body_pose をまったく受け取らず、親に追従するだけ。** これは「まだ検証済みの幾何補正が無いから、当て推量するより rest pose を通す」という意図的な設計（`else` 節のコメント）。

したがって `[ANIMALKP]` の「Paw が一番良い（18〜20°）」は**姿勢が合っているからではなく、駆動されていないボーンがたまたま親の向きで keypoint に近い**という話。逆に Upper / Neck の 72〜83° は**実際に body_pose を当てた結果ずれている**。

ただし **Upper の数値には指標由来の下駄が前肢で約 25° 乗っている**（次節「`[ANIMALKP]` の Upper には指標由来の下駄が乗っている」）。額面どおり扱わないこと。

なお `jointsWorld` / `jointVis` は `TryApplyAnimalSmalFk` の中で**初回の 180 度ヨー反転判定（`rootYawFixDecided`）にしか使われない**。それ以外に keypoint は姿勢へ influence しない。

#### 副次的に判明したこと

- `LoadAnimalControlTargetsSidecar` は**意図的な空実装**（`source/animal_control_targets.json` は runtime 使用禁止のため）。ログでも `animalControlFrames=0`。
  → **`hasControl` は常に false。** `ApplyAnimalTailPose` は `!hasControl` で即 return するので、**keypoint 経路に落ちたときは尻尾がまったく動かない。**（現行 bundle は SMAL 経路なので尻尾は joint 25/26 で駆動されている。動かないのは SMAL block の無い bundle のとき。）
- keypoint 経路には spine を回す処理が無い（`cache.spine` は basis 参照のみ）。SMAL 経路では joint 0 が spine を回す。

#### 次にやるべきこと

首・四肢の対応を直したいなら **`AnimalSmalFkApplier.cs` を見る**。`AnimalPoseApplier` の keypoint 経路をいじっても何も起きない。

### `[ANIMALKP]` の Upper には指標由来の下駄が乗っている（2026-08-28 実測）

「Upper が 72〜83° で一番悪い」を課題として報告する前に、指標の定義を疑って実測した。

`AnimalPoseJointChains` の chain[0] は **左右で同じ点**:

```
LeftFront  = { 18, 13, 9, 15 }     RightFront = { 18, 12, 8, 14 }
LeftRear   = {  7, 11, 17,  6 }    RightRear  = {  7, 10, 16,  5 }
```

つまり Upper の目標は左右とも `18→肘` / `7→膝` で、**同じ起点から出ている**。Lower / Paw は左右で別の点しか使わない。

#### 左右の目標がなす角（splay）— `scratchpad/splay.py`、全 2120 フレーム

| 部位 | 左右 splay median | p10 | p90 |
|---|---|---|---|
| **Upper 前（18→13 vs 18→12）** | **64.8°** | 62.4° | 66.0° |
| Lower 前（13→9 vs 12→8） | 15.8° | 11.0° | 23.4° |
| Paw 前（9→15 vs 8→14） | 18.6° | 9.4° | 35.0° |
| **Upper 後（7→11 vs 7→10）** | **52.7°** | 50.0° | 59.3° |
| Lower 後（11→17 vs 10→16） | 35.7° | 27.5° | 43.4° |
| Paw 後（17→6 vs 16→5） | 21.2° | 12.4° | 28.0° |

**Upper の目標だけ左右に 4 倍広がっている。** しかも front は p10 62.4 / p90 66.0 とほぼ一定で、姿勢に依らない**構造的な**広がり。

kp18 は kp13/kp12 の中点から 0.19m（体長 0.898m の 21%）、kp7 は kp11/kp10 の中点から 0.30m（33%）離れており、**正中のハブ**（き甲・腰）と考えるのが自然。

#### したがって Upper には避けられない下駄がある

実際の四肢の upper ボーンは左右でほぼ平行に動く（Lower の splay 16° がその目安）。**平行なボーン 2 本が、65° 開いた目標 2 つに同時に合うことはできない。** 最良でも半分ずつ外れる。

| | 目標 splay | 実際の四肢の splay 目安 | 下駄の見積もり |
|---|---|---|---|
| 前肢 | 64.8° | ~16° | **約 25°** |
| 後肢 | 52.7° | ~36° | **約 9°** |

**Upper 前 78〜83° のうち約 25° は指標の定義が作っている。** 残り 50〜58° が実際のずれ。

#### 結論の修正

- Upper はやはり**一番悪い**が、数値は前肢で約 25° 過大。「78°」を額面どおり扱わない。
- Neck 76° にはこの下駄は無い（`18→24` = き甲→鼻先で、SMAL joint 15 の rest dir が Neck→Head なのと同じ意味づけ）。
- **Paw / toe / head の値が小さいのは、そもそも body_pose を受け取っていないから**であって、姿勢が合っているからではない。

#### 指標を直すなら

Upper の目標を「肩→肘」にしたいが、**AniMer 26 関節に肩に相当する点があるか未確認**。無ければ「左右の目標の平均方向」と比べるか、Upper だけ左右の合成（例: `18 → (13+12)/2` に対する左右ボーンの平均方向）で見るしかない。指標を変えたら**過去の数値と混ぜて比較しない**こと。

### Human と Animal の実装差（「人みたいにやりたい」への回答、2026-08-28）

ユーザーの「animal の rig についてちゃんと対応しているのか人みたいにやりたい」に対する、コードで確認した差分。**2 点だけ。**

#### 差 1: Animal には AimAt が無い

| | FK | keypoint による上書き |
|---|---|---|
| **Human** | SMPL FK | **`enableKeypointAimAt = true`。FK のあと keypoint で四肢の向きを上書きする** |
| **Animal** | SMAL FK | **無し。** `jointsWorld` / `jointVis` は初回の 180° ヨー反転判定にしか使われない |

`enableKeypointAimAt` を読むのは `StreamingStereoVideoPlayer.HumanSmpl.partial.cs` だけで、`AnimalSmalFkApplier` は AimAt に相当する処理を持たない。

Human 側のコメントに残っている実測: **「修正する前は素の FK が keypoint と平均 78.2° ずれており、AimAt は補助ではなく」**。Animal の Upper が 72〜83°（うち約 25° は指標の下駄）というのは、**Human の AimAt 導入前とほぼ同じ水準**。

AimAt は Human で 2 回、実機で「削除するとダメ」と確認されている（[[aimat-is-not-removable]]）。Animal にはその機構が無い。

#### 差 2: paw / toe / head / tailTip が body_pose を受け取っていない

`SmalRestDirByJoint` に rest dir が登録されていない joint は `parentTW * bindLoc`（rest pose を親に追従させるだけ）になる。該当は **paw ×4・toe ×2・`head`・`tailTip`**。

これは「検証済みの幾何補正が無いのに当て推量しない」という意図的な判断（`else` 節のコメント、ADR-0001/0002）。Human 側は手・足まで FK が通っている。

#### 注意

上のどちらも「直せば良くなる」と確認したわけではない。**AimAt を Animal に入れる案は、入れる前に Human で何が効いたのかを読み直すこと。** Animal の keypoint（AniMer 26 関節）は Human の SMPL 関節とは意味づけも精度も違う。特に Upper の目標は正中ハブ起点で左右 65° 開いており、そのまま AimAt の目標には使えない。

### Animal の姿勢誤差は「動きの問題」ではなく「静止姿勢の問題」（2026-08-28 実測）

`[ANIMALKP]` の Upper が「ほぼ一定の 77°」（sd 8〜14°）だったので、動的な追従の問題か静的なオフセットかを切り分けた。

#### 1. SMAL FK 後のボーンはほとんど動いていない

既存ログ `[SMAL-FK-DBG] trueDeltaDegSincePrevSample`（約 1 メタフレームぶんの実回転量）を集計（`scratchpad/smal_motion.py`）。

| track | joint | median | p90 |
|---|---|---|---|
| Track_0（Dog） | 7 LFUp / 8 LFLo / 15 Neck / 17 LRUp | 0.6 / 1.0 / 0.8 / 0.8° | 6〜8° |
| Track_1（Lynx） | 同上 | 0.4° | 2〜4° |

`BEND childDirAngleAccumSincePrevSample`（ボーン→子の向きの振れ）も median 0.33〜0.77°。**歩いている犬の脚が 1 フレームで 0.5 度しか動いていない。**

#### 2. 原因は Unity の平滑化ではなく、入力データがほぼ静止している

`meta.bin` の SMAL 回転行列 35 個をフレーム間で直接比較した（`scratchpad/smal_data_motion.py`、隣接フレームのみ）。

| データ | 連続フレーム間の回転量 median | p90 |
|---|---|---|
| **Animal SMAL**（新 bundle track0） | **0.10〜0.71°** | 1〜9° |
| **Animal SMAL**（旧 `bundle_animal.svb`） | **完全に同値** | — |
| **Human SMPL**（対照、`bundle_human_shots_driftfix_test.svb`） | **0.54〜1.31°** | 2〜10° |

- **Unity 側の実測（0.4〜1.0°）は入力（0.3〜0.7°）とほぼ一致**し、FK の親累積ぶんだけ大きい。**平滑化で潰れているのではない。**
  `SmalSmoothHalfLifeSec = 0.12f` の遅れは入力が 0.5°/frame なら 2° 程度で、77° の誤差とは桁が違う。
- **「データが完全に静止している」は言い過ぎ。** human SMPL の半分程度で、走っている犬としては少ないが異常値ではない。
- **旧 bundle と新 bundle で SMAL データは 1 桁まで同値。** `depthdriftfix_shotsfix` の再ビルドは姿勢データを変えていない。

#### 3. したがって 77° は静的なオフセット

`bendSmal` が恒等に近いとき `tw = restWorldRot = worldFk0 * bindRotWorld[bone]` になる。つまり**ボーンは「モデルの bind pose を胴体の向きで回しただけ」の位置にほぼ留まる**。`[ANIMALKP]` はそれと AniMer keypoint の向きを比べているので、誤差の主成分は

> **Unity リグの bind pose の四肢方向 と、実際の動物の四肢方向 とのずれ**

になる。body_pose は最大でも 51°（`bodyPose_maxAngle`）で、しかもフレーム間でほとんど変化しない。

#### 4. 傍証: 既存の TAIL-REST-CHECK が同じ桁を示している

`AnimalSmalFkApplier` は joint 25/26 についてだけ `restDirAngleDeg = Vector3.Angle(smalRestDir, unityRestDirWorld)` を出している。

| track | joint | median | min | max |
|---|---|---|---|---|
| Track_0 | 25 / 26 | **72.1° / 62.5°** | 11.9 / 4.5 | 141.4 / 147.5 |
| Track_1 | 25 / 26 | **83.1° / 76.9°** | 35.7 / 1.6 | 130.5 / 133.1 |

**SMAL の rest 方向と Unity ボーンの rest 方向が 62〜83° 食い違っている。** これは `[ANIMALKP]` の Upper / Neck の誤差（72〜83°）と**同じ桁**。

コード中のコメントは「150 度以上なら `FromToRotation` の軸が不定」という閾値しか見ていないが、**70〜80° のずれ自体が問題**である可能性が高い。そして**この検査は尻尾（25/26）にしか配線されていない。四肢では一度も測られていない。**

#### 次の一手（確定した推奨）

**既存の TAIL-REST-CHECK を四肢と首（joint 7/8/11/12/15/17/18/21/22）に広げて `restDirAngleDeg` を測る。** ログ追加のみで挙動は変えない。

- 四肢の `restDirAngleDeg` が `[ANIMALKP]` の誤差と一致するなら、**主因は FK の式ではなくリグの bind pose と SMAL rest skeleton の対応**。per-model の話になる（`cache.bindDirLocal` / ボーン命名 / T-pose）。
- 一致しないなら、`jointFrameMap` の共役（ロール不定）を疑う番になる。

### REST-CHECK の結果と、真因の特定（2026-08-28）

#### 1. REST-CHECK を四肢へ広げた結果

| model | joint | 部位 | restDirAngleDeg median | p10 | p90 |
|---|---|---|---|---|---|
| Dog | 7 / 11 | 前 Upper | 90.2 / 97.9 | 66 / 74 | 121 / 131 |
| Dog | 8 / 12 | 前 Lower | 71.8 / 88.6 | 42 / 60 | 113 / 125 |
| Dog | 15 | Neck | 78.0 | 50 | 110 |
| Dog | **17 / 21** | **後 Upper** | **124.5 / 114.7** | 107 / 98 | **149.7** / 141 |
| Dog | 18 / 22 | 後 Lower | 86.0 / 93.9 | 66 / 71 | 126 / 127 |
| Lynx | **17 / 21** | **後 Upper** | **130.2 / 123.3** | 117 / 110 | 139 / 131 |

**後肢 Upper だけが 115〜130° と突出**し、Dog では p90 が 149.7° と**コード自身が「軸不定の疑いあり」とする 150° に達している**。両モデルで同じ傾向なのでリグ個別の問題ではない。

#### 2. ただしこの角度の「大きさ」は欠陥の証拠にならない

`smalRestDir` は **SMAL native 軸**（+X 尻尾→頭、+Y 左、+Z 上）、`unityRestDirWorld` は **Unity world**。両者のなす角には**座標系の違いそのもの**が含まれるので、大きくて当然。**当初「62〜83° のずれ自体が問題」と書いたが、この理由で撤回する。**

joint 間の**ばらつき**（59.6〜124.5°）も、固定の変換を別々の方向ベクトルに当てれば角度が変わるので、単独では欠陥の証拠にならない。

#### 3. 定数は一次資料と完全一致

`SmalRestDirByJoint` の 11 個すべてを `Docs/smal-rest-skeleton.json`（一次資料）と照合（`scratchpad/smal_rest2.py`）。**全部 0.0° 差**。joint 名も `LLeg1/LLegBack1/Neck/Tail1` と対応どおり。**定数は誤りではない。**

#### 4. 真因: body_pose の回転量が足りない

`meta.bin` の body_pose の**絶対量**（恒等回転からの角度）を測った（`scratchpad/bodypose_mag.py`）。

| データ | 四肢 Upper の median | 全 joint median（joint0 除く） |
|---|---|---|
| **Animal SMAL**（Dog） | LFUp 9.5° / RFUp 12.4° / LRUp 18.9° / RRUp 13.0° | **13.0°** |
| **Animal SMAL**（Lynx） | 8.0 / 8.1 / 9.7 / 9.8° | **9.8°** |
| **Human SMPL**（対照） | 16.4 / 20.0°、膝 39.6 / 32.8° | **18.8°**（p90 66.6°） |

**四肢の付け根が 8〜19° しか回っていない。** 一方 `[ANIMALKP]` の誤差は 72〜84°。**body_pose の最大値（24〜47°）を全部使っても届かない。** つまり誤差は FK が姿勢を失っているのではなく、**そもそも入力にその姿勢が入っていない**。

#### 5. 決定的: SMAL block と keypoints3d が同じ bundle の中で食い違う

**関節の内角**（座標系に依らない量）で直接比較した（`scratchpad/smal_vs_kp.py`）。SMAL 側は rest skeleton + kintree + body_pose を FK して算出。

**ハブを使わない遠位の角度**（左右で別の点しか使わないので [ANIMALKP] の Upper のような汚染が無い）:

| track | 部位 | SMAL FK | keypoints3d | 差 | SMAL rest |
|---|---|---|---|---|---|
| Dog | LF wrist | 22.2° | 22.3° | 15.3° | 20.9° |
| Dog | RF wrist | 24.8° | 16.0° | 15.1° | 20.9° |
| Dog | **LR ankle** | **30.7°** | **69.1°** | **41.5°** | 22.9° |
| Dog | **RR ankle** | **25.3°** | **66.8°** | **37.2°** | 22.9° |
| Lynx | LF wrist | 30.1° | 16.2° | 16.3° | 20.9° |
| Lynx | RF wrist | 32.5° | 16.8° | 16.6° | 20.9° |
| Lynx | **LR ankle** | **26.7°** | **65.0°** | **39.0°** | 22.9° |
| Lynx | **RR ankle** | **27.9°** | **57.2°** | **28.8°** | 22.9° |

- **前肢の手首は一致する**（22.2 vs 22.3 等）。→ こちらの FK・対応づけ・パースが正しいことの内部対照になっている。
- **後肢の飛節（ankle）は SMAL 25〜31° に対し keypoints 57〜69°。両動物とも同じ向きに 29〜42° 食い違う。**
- SMAL 側は rest（22.9°）からほとんど動いていない（+2〜+8°）。

**同じ bundle の中の 2 つの表現が、後肢について別の姿勢を述べている。** 座標系に依らない量での比較なので、Unity 側の変換の問題ではない。

#### 結論

| 寄与 | 大きさ | 所在 |
|---|---|---|
| **body_pose が後肢の曲がりを持っていない** | 29〜42° | **bundle 生成側** |
| `[ANIMALKP]` Upper の指標由来の下駄 | 前肢 約 25° / 後肢 約 9° | Unity 側（指標） |
| `jointFrameMap` のロール不定 | 未定量 | Unity 側（構造的） |

Unity 側で `AnimalPoseApplier` / `AnimalSmalFkApplier` を触っても、**後肢の姿勢は入力に無いので出ない。**

#### 残る Unity 側の構造的欠陥（副次）

`jointFrameMap = Quaternion.FromToRotation(smalRestDir, unityRestDirWorld)` は **rest 方向しか拘束していない**。`jointFrameMap * R(smalRestDir, θ)` はどれも同じ写像条件を満たすので、**ボーン軸まわりのロールが未定**。`bendUnity = jointFrameMap * bendSmal * Inv(jointFrameMap)` は回転軸 n を `jointFrameMap * n` に写すため、ロールがずれると**「前に曲がる」が「横に開く」に化ける**。

正しい frame map には第 2 の基準方向（`LookRotation(forward, up)` の up に相当）が要る。現状は 1 本しか無い。ただし body_pose 自体が 8〜19° しかないので、**これを直しても効果は上の 29〜42° より小さい。順番としては後**。

### 撤回 2026-08-28: AniMer keypoint と SMAL joint の対応づけが検証に落ちた

D-007（「SMAL block と keypoints3d が食い違う」）を送る前に、対応づけを**独立な不変量**で検証したところ**通らなかった**。

内角の比較は「AniMer のチェーンと SMAL の kintree が対応している」前提でしか意味を持たない。その対応は `AnimalPoseJointChains`（**現行 bundle では実行されない keypoint 経路のために書かれた定義**）由来で、生成側は「関節番号の解剖学的意味は断定できない」としている（D-006 回答）。

#### セグメント長比（スケール不変・ハブ非依存）による照合 — `scratchpad/chain_ratio.py`

| chain | SMAL の joint | SMAL 2:3 | KP 2:3（犬） | KP 2:3（猫） | |
|---|---|---|---|---|---|
| LF | LLeg1>LLeg2>LLeg3>LFoot | **1.63** | 0.79 | 0.76 | **不一致** |
| RF | RLeg1>RLeg2>RLeg3>RFoot | **1.63** | 0.78 | 0.76 | **不一致** |
| LR | LLegBack1>2>3>LFootBack | **1.32** | 0.81 | 0.91 | **不一致** |
| RR | RLegBack1>2>3>RFootBack | **1.32** | 0.88 | 0.95 | **不一致** |

**約 2 倍ずれている。** SMAL は第 2 セグメントが第 3 の 1.3〜1.6 倍だが、keypoints では第 3 のほうが長い。

さらに悪いことに、**後肢は「1 つずらした」対応のほうがよく合う**:

| chain | ずらした SMAL 比（1:2） | KP 2:3（犬 / 猫） |
|---|---|---|
| LR | 0.94 | 0.81 / 0.91 |
| RR | 0.94 | 0.88 / 0.95 |

つまり「前肢は一致・後肢だけ大きくずれる」という観測は、**後肢チェーンが 1 つずれている**（飛節の角度と膝の角度を比べている）でも同じように説明できる。犬・猫の両方で同じ向きに出ることも説明できてしまう。

#### 撤回するもの

- **D-007 の「SMAL block と keypoints3d が後肢について食い違う」は根拠不十分。** 送る前に止めた
- **`[ANIMALKP]` の角度差（Neck 76°、Upper 72〜84° など）も同じ対応づけに乗っている。** 数値の解釈をこれ以上進めない
- 「splay 65° が Upper の指標に下駄を乗せている」も、チェーンが解剖学的に正しい前提だった

#### 撤回しないもの（対応づけに依存しない）

| 事実 | 根拠 |
|---|---|
| 現行 animal bundle では SMAL FK 経路だけが走り、keypoint 経路は実行されない | `[SMAL-PIPE] hasSmalPose=true` 全件 |
| SMAL FK 後のボーンは 0.4〜1.0°/frame しか動かない | `trueDeltaDegSincePrevSample` |
| 入力の body_pose も 0.3〜0.7°/frame（Unity は入力を忠実に再現、平滑化のせいではない） | meta.bin 直読み |
| body_pose の絶対量は四肢付け根で 8〜19° | meta.bin 直読み |
| `SmalRestDirByJoint` の定数 11 個は一次資料と 0.0° 差で一致 | `smal-rest-skeleton.json` 照合 |
| 旧 `bundle_animal.svb` と新 bundle で SMAL データは同値 | meta.bin 直読み |
| `jointFrameMap` はロールが未拘束（構造的） | コード |
| paw×4 / toe×2 / head / tailTip は body_pose 未適用 | コード |

#### 教訓

**指標を作るときは、その対応づけ自体を独立な量で検証してから数値を読む。** `[ANIMALKP]` は対応づけを検証せずに 3 セッション使った。内角も長さ比も同じデータから只で計算できたのに、先に角度だけ見ていた。

#### 次にやること

**AniMer 26 関節の骨格構造をデータから復元する。** フレーム間で距離がほぼ一定な keypoint ペアは剛体リンクで結ばれているので、全ペアの距離分散を測れば**仮定なしにトポロジーが出る**。それを SMAL rest skeleton の比率と突き合わせれば対応づけが決まる。

### AniMer 26 関節のトポロジーをデータから復元した（2026-08-28）

剛体リンクで結ばれた 2 点は姿勢が変わっても距離が変わらない。全 325 ペアの距離の変動係数 `cv = sd/mean` を測り、cv を重みに最小全域木を取った（`scratchpad/kp_topology.py`、track0 = 犬、1146 フレーム）。**仮定を一切置かずに骨格が出る。**

#### 復元された構造（track0）

```
kp7  (次数4) ── kp10 ── kp16*        kp18 (次数3) ── kp19*
      ├─────── kp11 ── kp17*               └─ kp25 ── kp21 (次数4)
      ├─────── kp12 (次数3) ── kp9 (次数3) ── kp15*
      │                 │            └─ kp3*
      │                 └─ kp13 (次数3) ── kp8 ── kp14*
      │                            └─ kp4*
      └─────── kp18                  kp21 ── kp0 ── kp20 (次数5)
                                       ├─ kp5*      ├─ kp1*  ├─ kp2*
                                       └─ kp6*      ├─ kp24* └─ kp23 ── kp22*
(* = 末端)
```

最も剛体的なペアは **kp12–kp13（cv 0.0296、0.232m）**。

#### `AnimalPoseJointChains` との照合

| チェーン定義 | リンク | 復元された木にあるか |
|---|---|---|
| LeftFront {18,13,9,15} | 18→13 | **無い** |
| | 13→9 | **無い**（13 の隣接は 4, 8, 12） |
| | 9→15 | **ある**（cv 0.0477） |
| RightFront {18,12,8,14} | 18→12 | **無い** |
| | 12→8 | **無い**（12 の隣接は 7, 9, 13） |
| | 8→14 | **ある**（cv 0.0444） |
| LeftRear {7,11,17,6} | 7→11 | **ある**（cv 0.0641） |
| | 11→17 | **ある**（cv 0.0436） |
| | 17→6 | **無い**（6 は 21 につながる） |
| RightRear {7,10,16,5} | 7→10 | **ある**（cv 0.0538） |
| | 10→16 | **ある**（cv 0.0453） |
| | 16→5 | **無い**（5 は 21 につながる） |

- **後肢の近位 2 リンクは裏が取れた**（7→11→17、7→10→16）
- **前肢の近位リンクは全部外れ**。kp13 と kp12 は互いに最も剛体的（0.232m）で、**左右の肘が互いに剛体になることはない**。この 2 点は四肢ではなく胴体側の点である可能性が高い
- **四肢の末端リンク（17→6、16→5）はどちらも外れ**。kp5 / kp6 は kp21 につながっている

#### したがって

`AnimalPoseJointChains` は **前肢について誤っており、四肢の末端についても誤っている**可能性が高い。この定義は現行 bundle では実行されない keypoint 経路のために書かれたもので、**一度も検証されていなかった**。

`[ANIMALKP]` の数値も、D-007 の内角比較も、この定義に乗っている。**どちらも解釈を進めない。**

#### 注意（結論にしないこと）

- cv は最良でも 0.030 で、真の剛体（cv < 0.01）ほど小さくない。推定器のノイズが 3〜5% あるということで、木の細部（特に cv > 0.06 の枝）は信頼できない
- 最小全域木は必ず全点をつなぐので、**弱い枝も強制的に採用される**。次数や末端の判定はその影響を受ける
- **これは対応づけの候補であって確定ではない。** 生成側に確認する

### 対応表を受領して検証した（2026-08-28、D-007 回答）

生成側が AniMer の SMAL pkl（`my_smpl_00781_4_all.pkl`）を直接ロードし、`keypoint_vertices_idx` の 26 点と 35 関節を rest pose で最近傍マッチングした対応表を出してきた。こちらの判定基準で検証した（`scratchpad/verify_new_map.py`）。

#### 正しい鎖

```
前肢 左 = kp12(肩) → kp8(肘)  → kp14(手根) → kp3(前足)
前肢 右 = kp13(肩) → kp9(肘)  → kp15(手根) → kp4(前足)
後肢 左 = kp7(骨盤)→ kp10(膝) → kp16(飛節) → kp5(後足)
後肢 右 = kp7(骨盤)→ kp11(膝) → kp17(飛節) → kp6(後足)
```

`AnimalPoseJointChains` は **前肢の起点が誤り（kp18 は「き甲」ではなく頭）**、かつ **前肢・後肢とも左右が入れ替わっていた。**

#### 検証 2（通った）: 主張するリンクは対立候補より短い — 10/10

最小全域木が cv だけで枝を選んだため、剛体な肩同士をまたぐ長い枝（8–13 = 0.345m）を拾っていた。**本物のボーンは短いほう**（8–12 = 0.229m）。左右を入れ替えた候補と長さを比べると:

| リンク | 主張 | 対立候補（左右逆） |
|---|---|---|
| 左 / 右 肩→肘 | **0.229 / 0.231m** | 0.345 / 0.346m |
| 左 / 右 肘→手根 | **0.294 / 0.292m** | 0.388 / 0.385m |
| 左 / 右 膝→飛節 | **0.161 / 0.157m** | 0.282 / 0.295m |
| 左 / 右 飛節→後足 | **0.187 / 0.196m** | 0.311 / 0.286m |
| 左 / 右 手根→前足 | **0.110 / 0.108m** | 0.273 / 0.262m |

**10 件すべて主張のほうが短く、左右がきれいに対称**（0.229 vs 0.231 など）。左右の割り当てもこれで裏が取れた。**私の最小全域木の読み（前肢の交差リンク）が誤っていた。**

#### 検証 1b（通った）: 前肢の 1:2 長さ比

新対応では kp12/kp13 が左右別の肩なのでハブ非依存になる。

| | SMAL | KP 犬 | KP 猫 |
|---|---|---|---|
| 前肢 左 / 右 | 0.85 | 0.78 / 0.79 | 0.76 / 0.76 |

**1.5 倍以内。一致。**

#### 検証 1（通らなかったが、判定基準のほうが誤り）: 2:3 長さ比

| | SMAL | KP 犬 | KP 猫 |
|---|---|---|---|
| 前肢 | 1.63 | 2.73 / 2.80 | 2.90 / 3.02 |
| 後肢 | 1.32 | 0.88 / 0.81 | 0.95 / 0.91 |

**通らない（1.5 倍を超える）。ただしこの判定基準は「26 点 ≒ 35 関節」を前提にしていた。**

生成側の回答が明示しているとおり、**26 点はメッシュ表面の頂点群の平均であって骨格関節そのものではない**。回答の最近傍距離も kp3↔LFoot = 0.102m、kp14↔LLeg3 = 0.074m と表面ぶんずれている。第 3 セグメント（手根→前足 0.110m、飛節→後足 0.187m）はこのオフセットと同じ桁なので、比が合わないのは当然。

裏付けとして、**遠位の keypoint は距離の変動係数そのものが大きい**:

| セグメント | cv |
|---|---|
| 肩→肘 / 肘→手根 / 膝→飛節 | 0.042〜0.048 |
| **飛節→後足 / 手根→前足** | **0.124〜0.176** |

比が合わない第 3 セグメントは、**表面オフセットと推定ノイズの両方が最大の場所**。したがってこの不一致を対応表への反証とは扱わない。**判定基準の設定ミスとして記録する。**

#### 顔まわりは確度が下がる（回答に明記あり）

kp18 = **Head**（頭）。**「き甲」ではない。** `AnimalHeadKeypoints.FrontRoot = 18` のコメント（体の前端・き甲）は誤り。
kp24 = 鼻先端、kp2 = 鼻筋・マズル中央、kp20/21 = 左右の耳、kp0/1 = 左右の目（推定）、kp22/23 = 口角（推定）、kp19 = 尾の先端、kp25 = 尾の中間（`Tail4` と完全一致）。

私の従来の推定（kp20/21=耳、22/23=目、24=鼻先、2=顎、19=尻尾先、25=尻尾中間）は、**耳・鼻先・尾は当たり、目と口角は取り違えていた**（22/23 が口角、0/1 が目）。

#### 結論

**対応表を受け入れる。** 検証 2（10/10）と検証 1b が通り、通らなかった検証 1 は基準側の誤りと説明がつく。

### 対応表に合わせたコード修正（2026-08-28）

| ファイル | 修正 |
|---|---|
| `AnimalPoseJointChains.cs` | 4 チェーンすべて訂正。前肢の起点を kp18（頭）から kp12/kp13（左右の肩）へ。前肢・後肢とも左右を入れ替え |
| `AnimalHeadKeypoints.cs` | `FrontRoot=18`（き甲）→ `Head=18`。`Chin=2` → `Muzzle=2`。`LeftShoulder=12` / `RightShoulder=13` を追加 |
| `AnimalPoseApplier.ApplyAnimalHeadPose` | 首を **両肩の中点 → 頭** に。head は 18→24（頭→鼻先端）。`animalNeckUsesBodyToHeadSegment` フラグを削除 |
| `StreamingStereoVideoPlayer.Playback.partial.cs` | `[ANIMALKP]` のペアを訂正。**Neck は診断から外した**（26 関節に首の点が無い） |
| `AnimalBodyBasisResolver.cs` | **挙動は正しかった。変数名だけ訂正**（`withersHub`→`headHub`、`headRoot`→`noseTip`、`leftHip`/`rightHip`→`leftKnee`/`rightKnee`） |
| `BatchPlaybackLogger.cs` / `Model.cs` | `-neckSeg` と `SetAnimalNeckSegmentForDiag` を撤去 |

**`AnimalBodyBasisResolver` と `AnimalPoseJointChains` は左右の割り当てが食い違っていた**（前者が正しく後者が誤り）。**同じ番号体系を 2 箇所で別々に解釈していたこと自体が、少なくとも一方が推測だった証拠**だった。気付く機会はあった。

#### 過去の数値の扱い

`[ANIMALKP]` の値（Neck 76°、Upper 72〜84°、Lower 18〜59°、Paw 15〜42° など）は**すべて誤った対応で測ったもの。破棄する。** 訂正後の測定と混ぜて比較しないこと。

### 訂正後の測定（2026-08-28）— 後肢 Upper だけが外れる

コンパイル・実行とも通過（`[PLACE]` 58406、`[ANIMALKP]` 出力あり、エラーなし）。

#### `[ANIMALKP]` 訂正後（過去の数値とは比較しない）

| 部位 | Dog median | Lynx median | body_pose |
|---|---|---|---|
| LFUp / RFUp | **17 / 15°** | 25 / 37° | 駆動 |
| LFLo / RFLo | 32 / 28° | 35 / 50° | 駆動 |
| LFPaw / RFPaw | 36 / 28° | 35 / 41° | なし（親追従） |
| **LRUp / RRUp** | **76 / 75°** | **78 / 73°** | 駆動 |
| LRLo / RRLo | 30 / 41° | 40 / 47° | 駆動 |
| LRPaw / RRPaw | 24 / 15° | 21 / 17° | なし（親追従） |
| **駆動ボーン全体** | **32°** | **47°** | |

**前肢は 15〜50° に収まり、後肢 Upper だけが 73〜78° で突出**。90° 超の割合も後肢 Upper だけが 16〜22%（他は 0〜3%）。**両モデルで同じ。**

誤った対応で測っていたときは「Upper 全部が 72〜84° で近位ほど悪い」に見えていたが、**実際は後肢 Upper 単独の問題**だった。

#### REST-CHECK との対応

| joint | 部位 | REST-CHECK（Dog / Lynx） | ANIMALKP（Dog） |
|---|---|---|---|
| 7 / 11 | 前肢 Upper | 91.0 / 98.6, 71.1 / 76.5 | 17 / 15 |
| 8 / 12 | 前肢 Lower | 73.0 / 89.8, 60.5 / 69.8 | 32 / 28 |
| **17 / 21** | **後肢 Upper** | **124.5 / 114.8, 130.4 / 123.6** | **76 / 75** |
| 18 / 22 | 後肢 Lower | 86.5 / 94.8, 69.2 / 75.1 | 30 / 41 |

**REST-CHECK が 110° を超えるのは joint 17 / 21 だけ**（他は 60〜99°）で、**姿勢誤差が突出するのも同じ 2 つ**。両モデルで一致。

REST-CHECK の**絶対値**は座標系の差を含むので意味を持たないが、**同一モデル内で 2 つの joint だけが 30〜40° 上に外れている**のは、SMAL の rest 大腿方向と Unity リグの bind pose 大腿方向が、他の関節より大きく食い違っていることを示す。

SMAL の rest 方向: 後肢 Upper `(+0.338, ±0.087, −0.937)` は **下かつ前（+X = 尻尾→頭）** を向く。前肢 Upper `(+0.045, ∓0.095, −0.994)` はほぼ真下。四足動物のリグは大腿が**後ろ下がり**なのが普通なので、ここが食い違う。

そして後肢 Upper の body_pose は 9.7〜18.9° しかないので、**この差を埋められない。**

#### 内角の再検証（対応表を適用）

| track | 部位 | SMAL FK | keypoints3d | 差 |
|---|---|---|---|---|
| Dog | LF elbow / RF elbow | 24.3 / 18.1° | 16.0 / 22.3° | **11.1 / 6.3°** |
| Dog | LF wrist / RF wrist | 22.2 / 24.8° | 19.5 / 23.7° | **4.9 / 4.0°** |
| Lynx | LF elbow / RF elbow | 23.3 / 20.5° | 16.8 / 16.2° | **13.0 / 6.5°** |
| Lynx | LF wrist / RF wrist | 30.1 / 32.5° | 54.7 / 53.4° | 17.9 / 21.4° |
| **Dog** | **LR / RR ankle** | **30.7 / 25.3°** | **66.8 / 69.1°** | **37.2 / 43.1°** |
| **Lynx** | **LR / RR ankle** | **26.7 / 27.9°** | **57.2 / 65.0°** | **30.7 / 37.1°** |

**前肢は一致**（犬で 4〜11°）。**後肢の飛節は 29〜43° 食い違う**（両動物）。SMAL 側は rest 値 22.9° からほとんど動いていない（+2〜+8°）。

対応の左右を直しても後肢の数値は入れ替わっただけで、**食い違いそのものは残った**（後肢は接続の形が元から正しく、ラベルだけ左右逆だったため）。

#### ただし後肢の内角比較には交絡がある

生成側の回答どおり 26 点は**メッシュ表面の頂点平均**で、骨格関節ではない。最近傍距離は kp16↔LLegBack3 = **0.009m**（ほぼ一致）だが **kp10↔LLegBack2 = 0.107m**、kp5↔LFootBack = 0.103m。

後肢のセグメントは短い（膝→飛節 0.161m、飛節→後足 0.187m）ので、**0.10m のオフセットはセグメント長の 66%**。角度を 30° 動かすには十分。**後肢はこの比較が最も効かない場所**で、そこに食い違いが出ている。

前肢は肘→手根 0.294m とセグメントが長く、オフセット（0.050〜0.074m）の影響が小さい。犬で 4〜5° に収まったのはそのため。

**したがって「SMAL block が後肢で誤っている」とはまだ言えない。** 交絡を外すには、生成側に**同じ内角を SMAL の posed mesh から（keypoint 頂点群で）計算してもらう**必要がある。相手は v_template を持っているので実行できる。

### 後肢 Upper の 73〜78° にも指標の下駄がある（2026-08-28）

D-007 は「解決」で確定した。生成側が AniMer 本体のモデルコードと配布 bundle の `pose.smal` をそのまま forward し、**posed mesh 由来の内角が配布物の `keypoints3d` と 12 フレーム全部で小数 2 桁まで一致**、骨格関節だけの FK は 27〜41°（犬）でこちらの 25〜31° 帯と同じと報告。**`body_pose` と `keypoints3d` に矛盾は無く、こちらの実装も正しかった。** 差 30〜43° は kp10 の表面オフセット（0.107m = セグメント長の 66%）が作っていた。

**同じ交絡が `[ANIMALKP]` の後肢 Upper にも効いている。**

#### 測定した下駄 — `scratchpad/rear_upper_floor.py`

`[ANIMALKP]` の後肢 Upper が比べているもの:

- ボーン方向 = `rear_*_upper` → 子 = **股関節 → 膝**
- 目標方向 = kp7 → kp10 = **尾の付け根 → 膝**（kp7 は `Tail1`、股関節ではない）

実際の `body_pose` で FK した姿勢について、この 2 方向のなす角を全フレーム測った。

| track | 部位 | 起点の食い違い | median | p10 | p90 |
|---|---|---|---|---|---|
| Dog | 後肢 左 / 右 | `Tail1` vs `LLegBack1` / `RLegBack1` | **21.5 / 22.1°** | 19.8 / 20.1 | 24.0 / 25.2 |
| Lynx | 後肢 左 / 右 | 同上 | **22.4 / 23.0°** | 21.7 / 21.9 | 23.4 / 23.9 |
| Dog / Lynx | **前肢 左 / 右** | `LLeg1` vs `LLeg1`（同一） | **0.0°** | 0.0 | 0.0 |

**前肢がちょうど 0.0°** なのは、対応表で kp12/kp13 が `LLeg1`/`RLeg1`（肩）そのものだから。**手法の内部対照として完璧**で、後肢の 22° が本物の下駄であることを裏づける。

#### したがって後肢 Upper の 73〜78° は額面ではない

| 寄与 | 大きさ | 根拠 |
|---|---|---|
| 起点が尾の付け根であることの下駄 | **22°**（実測、ばらつき小） | 上の表 |
| kp10 の表面オフセット | **最大 19°**（`asin(0.107 / 0.322)`、向きは不明なので上限） | D-007 の対応表 |
| 残差 | **少なくとも 34°** | 75 − 22 − 19 |

前肢 Upper は 15〜37°（Lynx 右が 37°）なので、**残差 34° 以上が「後肢だけが特別に悪い」と言えるかは、この見積もりでは決まらない。**

**「後肢 Upper が真の欠陥」という結論は保留する。** 前回 Upper 全体を欠陥と報告しかけて対応づけの誤りだったので、同じ轍を踏まない。

#### 指標を直す（次の一手）

後肢には股関節に相当する keypoint が無いので、起点を揃えられない。代わりに**両辺を同じ量にする**:

- 目標 = kp7 → kp10（尾の付け根 → 膝）
- ボーン側 = **`cache.tailBase.position` → `rear_*_lower.position`**（Unity の尾の付け根 → 膝）

Unity リグには `tail_base` があるので同じ意味の方向が作れる。これで 22° の下駄は消える。

**注意**: これは「そのボーンの回転が合っているか」ではなく「2 点間の向きが合っているか」の測定に変わる。最優先目標が「モデルの体勢を keypoints3d に一致させること」なので指標としてはむしろ適切だが、**前肢（回転ベース）と後肢（点間ベース）で意味が変わるので、混ぜて平均しない。**

### 下駄を除いた測定（2026-08-28）— 後肢 Upper は外れ値ではなかった

`[ANIMALKP]` に点間ベースの後肢 Upper（`LRUpTB` / `RRUpTB` = Unity の `tail_base` → `rear_*_lower` 対 kp7 → kp10/11）を追加して測り直した。コンパイル・実行とも通過（`[PLACE]` 59425、エラーなし）。

| | 回転ベース（下駄あり） | **点間ベース（下駄なし）** | 90°超 |
|---|---|---|---|
| Dog LRUp / RRUp | 77 / 75° | **40 / 36°** | 22〜25% → **0%** |
| Lynx LRUp / RRUp | 79 / 73° | **30 / 28°** | 0〜15% → **0%** |

**後肢 Upper の実際の誤差は 28〜40°。** 73〜79° は起点の食い違い（尾の付け根 vs 股関節）が作っていた。

#### 全部位が同じ帯に収まる

| 部位 | Dog | Lynx |
|---|---|---|
| 前肢 Upper | 17 / 15° | 26 / 37° |
| 前肢 Lower | 33 / 29° | 31 / 50° |
| **後肢 Upper（下駄なし）** | **40 / 36°** | **30 / 28°** |
| 後肢 Lower | 32 / 41° | 41 / 47° |
| 駆動ボーン全体（回転ベースのみ） | 33° | 46° |

**特定の部位が突出していない。** 「後肢 Upper が真の欠陥」という結論を保留したのは正しかった。

なお下がり幅（35〜49°）は「22° の下駄 + kp10 オフセット上限 19°」とおおむね整合するが、点間ベースは Unity の `tail_base` 位置とリグの比率も反映するので、**純粋な下駄の除去ではない**。分解として厳密に扱わないこと。

#### 現状の到達点

姿勢の一致度は**全四肢セグメントで一様に 28〜50°**。単一の犯人はいない。残る誤差の候補は 3 つで、いずれも未定量:

| 候補 | 性質 | 備考 |
|---|---|---|
| `body_pose` の回転量が小さい（四肢付け根 8〜19°） | 入力 | human SMPL の対照は前肢 16〜20°・膝 33〜40°。ただしモデルも骨格定義も違う |
| `jointFrameMap` のロール未拘束 | Unity 側・構造的 | `FromToRotation` は rest 方向しか拘束しない。第 2 基準方向が要る |
| リグの bind pose と SMAL rest skeleton の差 | Unity 側・モデルごと | REST-CHECK で後肢 Upper だけ 110° 超。ただし絶対値は座標系差を含む |

**次に手を付けるならこの 3 つのどれかだが、まず「どれがどれだけ効いているか」を分離する測定を設計すること。** 部位ごとの角度だけでは分離できない（この 3 セッションで 2 回、分離せずに犯人を決めて外している）。

### 分離測定（2026-08-28）— 測定 A は無効、候補 1 は論理的に消える

#### 測定 A（「データの下限」）は成立しなかった

SMAL rest skeleton + `body_pose` を FK して keypoints3d と Kabsch 位置合わせし、「最良の位置合わせでも残る方向差 = データが許す下限」を求めようとした（`scratchpad/data_floor.py`）。

**無効。** `meta.bin` の SMAL block は **`betas` を 41 個持ち、全部非ゼロ（最大 1.07）**（`scratchpad/beta_probe.py`）。こちらは `Docs/smal-rest-skeleton.json`（**平均形状**）で FK していたので、関節位置そのものが違う。

証拠: Kabsch の残差 RMS が **0.210〜0.252m**。体長 0.9m に対し 23〜28% で、位置合わせとして成立していない。出た数値（下限 24〜28°、LRUpTB 47〜57° など）は**すべて形状不一致の産物。破棄する。**

`shapedirs` / `J_regressor` はこちらに無いので、**この測定は Unity 側では実行できない。**

#### 候補 1「body_pose の回転量が足りない」は論理的に消える

D-007 の回答で、生成側が **posed mesh 由来の keypoint が配布物の `keypoints3d` と 12 フレーム全部で小数 2 桁まで一致**することを確認している。

つまり **`globalOrient` + `body_pose` + `betas` が `keypoints3d` を完全に決めている。** 入力は自己完結していて、姿勢の情報が欠けているわけではない。

**「body_pose が human SMPL より小さい（8〜19° 対 16〜40°）」は誤差の説明にならない。** SMAL は betas で形状を、body_pose で姿勢を表すので、同じ見た目の動きに必要な回転量が SMPL と同じである理由がない。**human との比較自体が無効だった。撤回する。**

#### したがって残る候補は Unity 側の 2 つだけ

| 候補 | 内容 |
|---|---|
| **(i) 形状・bind pose の不一致** | Unity リグの bind pose と比率が、SMAL の**betas 適用後の**形状と違う。同じ相対回転を当てても world 方向が変わる |
| **(ii) `jointFrameMap` のロール未拘束** | `FromToRotation` は rest 方向しか拘束しないので、曲げの回転軸が任意のロールぶんずれる |

#### 測定 B: 曲げを切って (i) と (ii) を分ける

`AnimalSmalFkApplier` で `bendUnity` を恒等に固定する（= `globalOrient` と bind pose だけで姿勢を作る）診断フラグを入れ、`[ANIMALKP]` を比較する。

| 結果 | 読み方 |
|---|---|
| 誤差(曲げ無) ≈ 誤差(曲げ有) | 曲げが何も寄与していない。誤差はすべて (i) |
| 誤差(曲げ無) **<** 誤差(曲げ有) | **曲げが悪化させている** → (ii) が実害を出している |
| 誤差(曲げ無) > 誤差(曲げ有) | 曲げは効いている。残差は (i) |

`bendUnity` は `body_pose` の寄与そのものなので、これを切れば **(ii) の経路が完全に消える**。1 回のバッチで決まる。

### 測定 B の結果（2026-08-28）— body_pose を切っても誤差が変わらない

`-noBend true/false` の A/B（同一ビルド、フラグのみ）。適用ログ `disableSmalBendForDiag=true` を確認済み（true 側 1 行、false 側 0 行）。

| track | 部位 | 曲げ有 | 曲げ無 | 差 |
|---|---|---|---|---|
| Dog | LFUp / RFUp | 17 / 15° | 10 / 16° | **−7 / +1** |
| Dog | LFLo / RFLo | 32 / 29° | 28 / 30° | −4 / +1 |
| Dog | LRUpTB / RRUpTB | 39 / 36° | 39 / 36° | **0 / 0** |
| Dog | LRLo / RRLo | 31 / 41° | 33 / 40° | +2 / −1 |
| **Dog 駆動計** | | **30°** | **29°** | **−1** |
| Lynx | LFUp / RFUp | 27 / 37° | 25 / 35° | −2 / −2 |
| Lynx | LFLo / RFLo | 36 / 49° | 28 / 46° | −8 / −3 |
| Lynx | LRUpTB / RRUpTB | 30 / 28° | 30 / 29° | 0 / +1 |
| Lynx | LRLo / RRLo | 41 / 47° | 36 / 49° | −5 / +2 |
| **Lynx 駆動計** | | **34°** | **33°** | **−1** |

#### 読み方

**body_pose を完全に切っても誤差が 1° しか変わらない。** しかも変わる部位は**ほぼすべて「切ったほうが良くなる」側**（−10 〜 −2）で、改善した部位は 1 つだけ（Lynx RRPaw +4）。

| 候補 | 判定 |
|---|---|
| **(i) 形状・bind pose の不一致** | **誤差 30〜34° のほぼ全部を占める。** 曲げを切っても残る |
| **(ii) `jointFrameMap` のロール未拘束** | **実害は出ている**（部位ごとに最大 10° 悪化させている）が、`body_pose` が 8〜19° しかないので**上限がその程度** |

つまり現状は **「リグの T-pose を `globalOrient` で回しただけ」で 30° の一致度**が出ており、**`body_pose` の適用は差し引きゼロ〜わずかに有害**。

#### 効きうる幅の見積もり

- (ii) を直しても、`body_pose` の総量が 8〜19° なので**取り返せるのはせいぜい 10〜20°**（30° → 20° 程度が上限）
- (i) は per-model の作業（bind pose・比率を SMAL の betas 適用後の形状へ寄せる）。**こちらのほうが大きい**が、52 体のリグに対する作業になる

#### まだ分けきれていないこと

「(ii) の transport が曲げを無駄にしている」のか「SMAL の姿勢がそもそも rest に近く、正しく transport しても改善しない」のかは、**この測定では分けられない**。

分けるには **Unity リグの関節内角（upper と lower のなす角）を曲げ有無で比べる**。SMAL 側の内角は rest から +1.2〜+21.5° 動いている（実測済み）ので、

- Unity の内角も同程度動く → transport の大きさは合っている。残差は向き（ロール）
- Unity の内角がほとんど動かない → transport が曲げを失っている

**この測定はまだやっていない。** 次に (ii) へ手を付けるなら先にこれを取ること。

### 測定 C の結果（2026-08-28）— 曲げは届いているが、半分になり、右後膝だけ逆を向く

`[ANIMALANG]`（リグの関節内角。keypoint とは無関係）を曲げ有無で比較。適用ログ確認済み。

| track | 関節 | Unity 曲げ無 | Unity 曲げ有 | **Unity の変化** | SMAL の変化 | 到達率 |
|---|---|---|---|---|---|---|
| Dog | LFel（左肘） | 22.0° | 30.0° | **+8.0** | +18.9 | 42% |
| Dog | RFel（右肘） | 19.0° | 26.0° | **+7.0** | +12.7 | 55% |
| Dog | LRkn（左膝） | 48.0° | 53.0° | **+5.0** | +21.5 | 23% |
| **Dog** | **RRkn（右膝）** | 49.0° | 47.0° | **−2.0** | +14.3 | **−14%** |
| Lynx | LFel | 9.0° | 23.0° | **+14.0** | +17.9 | 78% |
| Lynx | RFel | 16.0° | 24.0° | **+8.0** | +15.1 | 53% |
| Lynx | LRkn | 73.0° | 79.0° | **+6.0** | +6.3 | 95% |
| **Lynx** | **RRkn** | 74.0° | 66.0° | **−8.0** | +9.9 | **−81%** |

#### 分かったこと

1. **曲げは届いている。** transport が丸ごと失っているわけではない（+5〜+14°）
2. **ただし到達率が 23〜95%、平均でおよそ半分。** 平滑化のせいではない（`body_pose` はフレーム間 0.3〜0.7°しか動かないので、`SmalSmoothHalfLifeSec = 0.12f` の定常減衰はほぼゼロ）
3. **右後膝（RRkn）だけ、両モデルとも逆向きに曲がる**（Dog −2.0 / Lynx −8.0 に対し SMAL は +14.3 / +9.9）

#### 3 が具体的な欠陥

**左右で振る舞いが違う。** `SmalRestDirByJoint` の左右は Y 成分の符号だけが違う鏡像（joint 17/21、18/22）だが、`Quaternion.FromToRotation` は**左右それぞれ独立に**ロールを決めるので、鏡像のペアが鏡像の写像にならない。片側だけ曲げ軸が反転しうる。

これは「`jointFrameMap` のロールが未拘束」という構造的欠陥（候補 ii）が、**実際に符号の反転として現れている**ということ。

`FromToRotation(a, b)` は `a → b` しか拘束せず、`jointFrameMap * R(a, θ)` はどの θ でも同じ条件を満たす。`bendUnity = jointFrameMap * bendSmal * Inv(jointFrameMap)` は曲げの回転軸 n を `jointFrameMap * n` に写すので、**ロールがずれると屈曲が伸展に化ける。** REST-CHECK が 110° を超える後肢（joint 17/21）で特に効く。

#### 直すなら

正しい frame map には**第 2 の基準方向**が要る（`LookRotation(forward, up)` の up に相当）。候補:

- SMAL rest skeleton の**兄弟ボーン方向**（例: 大腿の rest 方向に加えて、骨盤→尾の方向）を第 2 軸にして、両系で `LookRotation` を組んで `map = unityBasis * Inverse(smalBasis)` とする
- これなら鏡像のペアが自動的に鏡像の写像になる

#### 効きうる幅（変わらず）

`body_pose` の総量が 8〜19° なので、**完全に直しても取り返せるのは 10〜20°**。誤差 30〜34° のうち残り 20° 前後は候補 (i)（リグの bind pose・比率が SMAL の betas 適用後の形状と違う）で、そちらのほうが大きい。

**ただし (ii) は 1 箇所の数式修正、(i) は 52 体のリグ作業。** 費用対効果では (ii) が先。

### 2 軸版 jointFrameMap の A/B（2026-08-28）— 機構は確認できたが、この直し方は採用しない

`useTwoAxisJointFrameMap` を追加（`SmalRollRefJoint` で「同じ肢のもう 1 本」を第 2 軸にする）。適用ログ確認済み。

#### 判定 1: リグの内角 — **設計どおり効いた**

| track | 関節 | 曲げ無 | 従来 | 2 軸 | SMAL の変化 | 従来の変化 | 2 軸の変化 |
|---|---|---|---|---|---|---|---|
| Dog | LFel / RFel | 22 / 19° | 30 / 26° | 30 / 26° | +18.9 / +12.7 | +8.0 / +7.0 | **同値** |
| Dog | LRkn | 48° | 54° | **85°** | +21.5 | +6.0 | **+37.0** |
| **Dog** | **RRkn** | 49° | **47°** | **73°** | +14.3 | **−2.0** | **+24.0 = 符号が直った** |
| Lynx | LFel / RFel | 9 / 16° | 23 / 24° | 23 / 24° | +17.9 / +15.1 | +14.0 / +8.0 | **同値** |
| Lynx | LRkn | 73° | 79° | 83° | +6.3 | +6.0 | +10.0 |
| **Lynx** | **RRkn** | 74° | **66°** | **80°** | +9.9 | **−8.0** | **+6.0 = 符号が直った** |

**右後膝の符号反転は両モデルとも直った。** 機構（ロールが屈曲／伸展を決めている）は確認できた。

ただし **Dog の膝は行き過ぎ**（LRkn +37 対 SMAL +21.5、RRkn +24 対 +14.3）。conjugation は回転角を保つので**量**は変わらない。軸が変わったぶん、それまで捻りに逃げていた回転が曲げに入った、ということ。

#### 前肢は 2 軸版が走っていない（重要）

前肢の値が完全に同値なのは、**`TryBuildDirectionBasis` がフォールバックしているから**。前肢の rest 方向 joint 7 と 8 のなす角は **5.4°** しかなく、直交化した副軸の長さが `sin(5.4°)² = 0.0088 < 0.01`（閾値）で false を返す。

後肢は joint 17 と 18 が 32.6° 離れているので 2 軸版が走る。

**つまり前肢についてはこの A/B は何も測っていない。** 「前肢は変化なし」を効果の証拠にしない。なお前肢は副軸がほぼ縮退しているので、そもそもこの副軸では数値的にロールを決められない。

#### 判定 2: keypoints3d との角度差 — **改善しない。むしろ悪化**

| track | 部位 | 従来 | 2 軸 | 差 |
|---|---|---|---|---|
| Dog | **LRLo / RRLo** | 32 / 41° | **59 / 56°** | **+27 / +15** |
| Dog | LRPaw / RRPaw | 24 / 15° | 30 / 20° | +6 / +5 |
| Dog | その他 | | | 0〜+2 |
| **Dog 駆動計** | | **31°** | **32°** | **+1** |
| Lynx | RRLo | 47° | 58° | +11 |
| Lynx | LRPaw | 22° | 15° | **−7** |
| **Lynx 駆動計** | | **34°** | **35°** | **+1** |

**全体では改善なし（+1）。後肢 Lower が大きく悪化。**

#### 結論: 採用しない。既定 OFF のまま

`useTwoAxisJointFrameMap` は false のまま残す。

**なぜ効かないか。** ロールを直しても、**正しいロールが rest 幾何から復元できない**。副軸（同じ肢のもう 1 本）は SMAL の rest skeleton でも Unity の bind pose でも定義できるが、**両者の rest 姿勢そのものが違う**（候補 i）ので、揃えた「つもり」の平面が実は別の平面になる。符号は直るが正しい向きにはならない。

つまり **候補 (ii) は実在するが、単独では利益が出ない。** (i) を直さない限り、どんなロールの決め方をしても正解に届かない。

#### 費用対効果の再評価

前回「(ii) は 1 箇所の数式修正なので先」と書いたが、**撤回する。**

| 候補 | 実測の結果 |
|---|---|
| (ii) `jointFrameMap` のロール | 符号は直せるが keypoints 一致度は改善しない（+1）。**(i) に依存している** |
| (i) リグの bind pose・比率 | 誤差 30〜34° のほぼ全部。**唯一の実効的なレバー** |

また測定 B で分かったとおり、**`body_pose` を切っても誤差は 1° しか変わらない**。(ii) をどう直しても動かせる幅はその範囲に収まる。

### 動物は rest からどれだけ離れているか（2026-08-28）— 姿勢の「予算」は 13〜15°

「52 体のリグ作業」と書いたのは**推論であって測定していなかった**ので、内訳を決める数値を取った（`scratchpad/pose_vs_rest.py`）。

body frame（`globalOrient` を外した動物自身の座標系）で、各ボーンの posed 方向と rest 方向のなす角。

| track | ボーン | median | p10 | p90 |
|---|---|---|---|---|
| Dog | LFUp / RFUp | 11.4 / 11.2° | 2.0 / 7.0 | 19.9 / 32.2 |
| Dog | LFLo / RFLo | 26.1 / 28.1° | 7.3 / 9.1 | 51.3 / 52.3 |
| Dog | LRUp / RRUp | 17.5 / 12.6° | 7.9 / 5.5 | 31.4 / 26.9 |
| Dog | LRLo / RRLo | 12.7 / 13.9° | 5.0 / 2.8 | 32.0 / 30.5 |
| **Dog 四肢計** | | **15.3°** | 5.0 | 38.9 |
| **Lynx 四肢計** | | **12.7°** | 4.3 | 29.2 |
| Dog / Lynx Neck | | 32.3 / 22.8° | | |
| Dog / Lynx TailBase | | 28.5 / 21.5° | | |

#### 内訳が決まった

**動物の四肢は自分の rest 姿勢から median 13〜15° しか離れていない。** これが「姿勢を正しく適用したときに得られる利益の全額」。

| 内訳 | 大きさ |
|---|---|
| **姿勢（rest からのずれ）を適用できていない分** | **13〜15°**（p90 で 29〜39°） |
| **リグの rest 姿勢が動物の rest 姿勢と違う分 + `globalOrient`・軸補正の誤差** | **残り。実測 30〜34° との差** |
| 実測（現状） | 30〜34° |

角度は線形に足せないので厳密な分解ではないが、**姿勢適用をどれだけ完璧にしても 15° 以上は残る**ことは言える。測定 B（`body_pose` を切っても 1° しか変わらない）とも整合する。

#### 「52 体のリグ作業」は撤回する

残差は「リグの bind pose が SMAL の rest と違う」ことに由来するが、**それをリグ側で直す必要があるとは限らない。** 現状のコードは

```
tw[joint] = bendUnity * restWorldRot          // restWorldRot = worldFk0 * bindRotWorld[bone]
```

で、**リグの bind pose を姿勢のベースラインとして焼き込んでいる**（`bendSmal` が恒等なら、ボーンはリグの T-pose を `globalOrient` で回した位置に留まる）。

ベースラインを「リグの bind pose」ではなく「SMAL の posed 方向」に置き換えれば、rest 姿勢の不一致は**コード側で消せる可能性がある**。これはリグ 52 体の作業ではなく 1 箇所の数式の話。

**未検証。** 実装前に「SMAL の posed 方向を Unity 側で作れるか」を確かめること（`SmalRestDirByJoint` は平均形状の値で、betas 適用後の rest 方向とは違う。方向は位置より betas に鈍感だが、それも未測定）。

### Animal AimAt 第 1 版は失敗（2026-08-28）— keypoint の絶対位置に向けてはいけない

SMAL FK のあとに四肢を keypoint へ向ける段（Human の `enableKeypointAimAt` 相当）を入れて A/B した。

**目視で悪化。** 7 秒（f00210）の犬は、OFF では自然に立っているのに、**ON では脚が開き前脚が縮んで破綻**した。

#### 原因: スケールが 2 倍違う

`[PLACE]` の実測:

```
scale=0.4086  modelH=1.5206  target=0.4202  depth=0.706
```

**表示モデルは高さ 0.42m**（ミニチュアスケール、[[placement-scale-judgment]]）。一方 `jointsWorld[i] = anchorWorld + camRotation * jointsCam[i]` の keypoint 骨格は**実寸**で、体長 kp7→kp18 が **0.87m**。

**約 2 倍。** 各ボーンを「自分の位置から keypoint の絶対位置へ」向けると、目標がモデルの外側の遠くにあるので、遠位ほど向きが大きく狂う。実際に破綻したのは遠位。

#### 直し方: セグメントの向きを使う

絶対位置ではなく **2 つの keypoint の差**（= セグメントの向き）を渡す。差はスケール不変なので 2 倍の食い違いが消える。`ApplyAnimalBoneFromPoints` は `(pointB - pointA).normalized` しか使わないので、引数を変えるだけ。

| ボーン | セグメント | 対応の正確さ |
|---|---|---|
| 前肢 Upper | kp12→kp8 / kp13→kp9 | **正確**（肩→肘） |
| 前肢 Lower | kp8→kp14 / kp9→kp15 | **正確** |
| 前肢 Paw | kp14→kp3 / kp15→kp4 | 正確（ただし kp3/4 は表面点でオフセット大） |
| **後肢 Upper** | kp7→kp10 / kp7→kp11 | **不正確。kp7 は尾の付け根で股関節ではなく 22° の下駄**（実測済み） |
| 後肢 Lower | kp10→kp16 / kp11→kp17 | **正確** |
| 後肢 Paw | kp16→kp5 / kp17→kp6 | 正確（同上） |

**後肢 Upper は AimAt から外す**（SMAL FK のまま）。既知の 22° の系統誤差を入れるより、現状の 28〜40° を残すほうがよい。ここは keypoint に情報が無い。

#### 教訓

**keypoint とモデルは別のスケールにある。** 位置を直接使う処理を書くときは必ずスケールを確認する。向き（差）だけを使えば影響を受けない。

### Animal AimAt 第 2 版（セグメント向き）の結果（2026-08-28）

引数を「ボーン位置 → keypoint 絶対位置」から「keypoint 2 点の差」に変えた。適用ログ確認済み、コンパイル・実行とも通過。

#### 目視

| フレーム | OFF | ON（第 2 版） |
|---|---|---|
| 7s 犬（f00210） | 自然に立っている | **自然。破綻なし。**前脚が 1 本わずかに上がる（歩行中なら妥当） |
| 32s 犬（f00960） | 脚がやや開いて硬い | **整った** |
| 42s 猫（f01260） | 破綻なし | **破綻なし。**前脚の位置がわずかに変わる |

**第 1 版の破綻（脚が開き前脚が縮む）は解消。**

#### 数値

| 指標 | 結果 |
|---|---|
| AimAt を当てた部位の `[ANIMALKP]` | **全部 0°**（当然。keypoint で駆動しているので指標として意味を持たない） |
| **AimAt を当てていない部位**（後肢 Upper・Head） | **±1° = 副作用なし** |
| リグの内角（肘 / 膝） | 肘 20〜29°、膝 28〜52°。**棒（0 近く）にも逆折れ（150 超）にもなっていない** |

膝は −14〜−30° 変化（55→41、66→36 など）。SMAL FK が作っていた曲げが keypoint の値に置き換わった。

#### 検証の限界（重要）

**この画像で正しさは確認できない。** `video.mp4` は inpaint 済みで**本物の動物が消されている**ので、比較対象が画面内に無い。判定できたのは「破綻していないか」「不自然でないか」まで。

**正しさの確認には実機（または `source/pre_removal_stereo_video.mp4` を使う通常モード）で、元映像の動物と重ねて見る必要がある。**

#### 既定値

`enableAnimalKeypointAimAt` は **false のまま**。実機確認まで既定を変えない。Human の AimAt も実機で 2 回確認されている（[[aimat-is-not-removable]]）。
