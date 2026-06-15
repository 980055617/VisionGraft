# SMPL → Unity Humanoid リターゲティング 実装ガイド

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

### 正しい公式（2026-06-13 確定・標準FK）

```
tw[j] = parentTW[j] * bindRotLocal[j] * bodyPose[j]
bone.rotation = tw[j]
```

**根拠:**
- SMPL body_pose[j] は親 joint の現在フレームで定義（標準FK規約）
- body_pose は「T-pose 姿勢からの変位」→ 先に `bindRotLocal` で T-pose フレームを確立してから body_pose を重ねる
- T-pose 検証（bodyPose=identity）: `tw[j] = parentTW * bindRotLocal * I` → 展開して `worldGlobalOrient * bindRotWorld[j]` ✓
- 右腕: `bindRotLocal[RightUpperArm]` に 180°Z → 先に 180°Z フレームを確立し body_pose が正しく乗る ✓

### FK ループ実装（HumanSmpl.partial.cs）

```csharp
Quaternion worldGlobalOrient = fk[0];  // smoothed globalOrient
// fk 配列を tw（targetWorld per joint）として再利用
Quaternion[] tw = fk;
tw[0] = worldGlobalOrient * bindRotWorld[Hips];  // Hips target world rotation

for each joint in SmplJointTopologicalOrder (1..21):
    Quaternion parentTW = tw[SmplJointParentArray[joint]];

    // Spine/Chest は SpineBodyPoseScale でスケールダウン（SMPL 過大推定対策）
    Quaternion fkLocal = (joint == 3 || joint == 6)
        ? Quaternion.Slerp(Quaternion.identity, smplLocal, SpineBodyPoseScale)
        : smplLocal;

    // HumanBone マッピングなし → bindLoc=identity で tw 積算してスキップ
    if (!SmplJointToHumanBone.TryGetValue(joint, out boneId))
    {
        tw[joint] = parentTW * fkLocal;
        continue;
    }

    // bindRotLocal 取得（なければ identity）
    bindLoc = cache.bindRotLocal[boneId] ?? Quaternion.identity;

    // 標準FK: parentTW * bindRotLocal * bodyPose
    tw[joint] = parentTW * bindLoc * fkLocal;

    // BONE MISSING (UpperChest 等): tw は積算済み、bone 設定はスキップ
    // → 子 joint（肩・腕）は tw[9] を parentTW として使用するため伝播は維持される

    ApplyWorldRotation(bone, tw[joint]);
```

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

| パターン | 現象 |
|---|---|
| `globalOrient * fk_body * bindRotWorld` | body_pose → bindRot の順（逆）。bindRotLocal が小さい脚・腰は偶然近いが、右腕 bindRotLocal の 180°Z で約 77° ズレ ✗ |
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

## 調査ログ

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

**チューニング:** `HalfLifeSecDepth` を大きくすると→より安定（意図的な前後移動への追従が遅くなる）。
