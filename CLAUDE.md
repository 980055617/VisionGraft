# VisionGraft — Claude 作業ガイド

## SMPL リターゲティング（最重要）

### 座標変換ルール

バンドルは **globalOrient・body_pose ともに R（通常形式・column convention）で格納**している。

| データ | flipCameraY | transposeMatrix | flipCameraY での否定対象 |
|--------|-------------|-----------------|--------------------------|
| `globalOrient` | true | false | m10, m11, m12 (= row1 of stored R = D*R の変換) |
| `body_pose` | false | false | なし |
| joint positions (meta.bin) | — | — | そのまま使用（変換済み）|

`TryReadRotationMatrix` のパラメータ:
- `globalOrient` → `flipCameraY: true, transposeMatrix: false`（row1 否定 → column 抽出 → D*R）
- `body_pose` → `flipCameraY: false, transposeMatrix: false`（column 抽出 = R をそのまま使用）

**NG パターン:**
- `D*R*D`（両辺乗算）→ 直立人が上下逆
- body_pose に `transposeMatrix: true`（row 抽出）→ R^T（逆回転）適用 → **LHip が後ろ・LKnee が伸びる（実際に視覚確認）**
- globalOrient に `transposeMatrix: true` → R^T が fk[0] に入りFK全体がずれる

**なぜ直立テストで body_pose のバグが見えないか:** body_pose = identity の場合、R = R^T = I なので row/column 抽出で同じ結果。歩行など動的ポーズで初めてずれが現れる。

**検証済みデータ（複数フレーム実機確認）:**
- `transposeMatrix:false`（R）: LHip X ≈ -65° → hip flexion（前） ✓、LKnee X ≈ +35° → knee flexion（前） ✓
- `transposeMatrix:true`（R^T）: LHip X ≈ +67° → hip extension（後ろ） ✗、LKnee 直立 ✗（実際に確認）

### FK 公式

```
fk[0] = camRotation * globalOrient_D*R   // world-space pelvis orientation
fk[j] = fk[parent] * body_pose[j]        // 変換なし
correctedLocal[j] = Inv(parentFk) * tPoseLocal * parentFk * smplLocal
targetHipsWorld = bindHipsWorld * fk[0]   // Hips は直接 world 回転で設定
```

不変条件: `world_rot[j] = bindRotWorld[j] * fk[j]`

### camRotation
```csharp
camRotation = LookRotation(-screenFront, screen.up)  // TryGetPinholeBasis() で取得
// screen.up ≈ world +Y（VR ヘッドセット up）
```

---

## 絶対に変えてはいけないこと

- **IK 禁止**: Human モデルの姿勢適用で IK（TwoBone IK 等）を復活させない
- **ShouldUseSmplOnlyPose() = true**: 常に true。変更前に必ず確認
- **ShouldUseHumanSmplRootOrientation() = false**: globalOrient は FK 内で処理
- **Animator 無効化**: `DisableHumanAnimatorPlayback` は再有効化しない

---

## 関連ファイル

- `Assets/Scripts/StereoPlayer/StreamingStereoVideoPlayer.HumanSmpl.partial.cs` — 変換・FK
- `Assets/Scripts/StereoPlayer/StreamingStereoVideoPlayer.Humanoid.partial.cs` — cache ビルド
- `Assets/Scripts/StereoPlayer/HumanoidRigCache.cs` — bindRotLocal/bindRotWorld
- `Assets/Scripts/StereoPlayer/StreamingStereoVideoPlayer.PersonSmpl24.partial.cs` — 配置
- `Assets/Scripts/StereoPlayer/StreamingStereoVideoPlayer.PosePipeline.partial.cs` — パイプライン

---

## 作業方針

- 変換式を変える前に「直立人でどうなるか」を具体的に計算して検証する
- わからない点はコードを調べてから、それでも不明ならユーザーに質問する
- 修正の根本原因が確認できてから変更に入る
