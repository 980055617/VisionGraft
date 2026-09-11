# リファクタリングの記録と進め方

2026-09-06 に実施した分の記録と、次に触るときの手順。

## 安全網（これを先に用意してから触ること）

コードを動かす前に **2 つのベースライン**を取る。片方だけでは足りない。

### 0. コンパイル確認（Editor を開いたままできる。2026-09-11）

バッチモードと違い Editor を閉じてもらう必要が無い。Unity が書き出す応答ファイルを
出力先だけ変えて、同梱の Roslyn に渡す。

```
# runtime（Assembly-CSharp）
cp Library/Bee/artifacts/<hash>.dag/Assembly-CSharp.rsp <scratch>/Assembly-CSharp.rsp
#   -out: / -refout: を <scratch> の Windows 形式パス（C:/...）に置換。
#   /c/... は C:\c\... に化けるので使わない。新規ファイルは "Assets/.../X.cs" の行を末尾に足す
"C:/Program Files/Unity/Hub/Editor/6000.0.60f1/Editor/Data/NetCoreRuntime/dotnet.exe" \
  "C:/Program Files/Unity/Hub/Editor/6000.0.60f1/Editor/Data/DotNetSdkRoslyn/csc.dll" @<scratch>/Assembly-CSharp.rsp

# Editor + テスト（Assembly-CSharp-Editor）
#   同様に -out: / -refout: を変え、-r:"Library/Bee/.../Assembly-CSharp.ref.dll" を
#   上で出した ref.dll に差し替える
```

プロジェクトルートを cwd にして実行する（rsp のパスは相対）。exit 0 なら通っている。
元からある警告は 3 件（CS0162 ×2、CS0414 ×1）。**テストの実行まではできない**
（NUnit ランナーが無い）。テストは下の 1 で回す。

### 1. EditMode テスト

```
Unity.exe -batchmode -projectPath <proj> -runTests -testPlatform EditMode \
          -testResults <out>.xml -logFile <out>.log
```

**2026-09-06 時点のベースライン: 総数 527 / 成功 502 / 失敗 25。**

失敗 25 件は**元から失敗している**（12 件は UniVRM10・UniGLTF の third-party、
13 件はプロジェクト側の既存失敗）。**この 25 件を「自分が壊した」と読まないこと。**
判定は「失敗の集合が増えていないか」で行う。集合の比較は
`scratchpad/diff_tests.py` と同じ要領で `test-case[@result='Failed']` を突き合わせる。

### 2. 描画のキャプチャ

```
run_cap.ps1 -Tag <名前>_animal -Frames "60,150,250" -Extra "-swapModel 0:30:36"
run_cap.ps1 -Tag <名前>_human  -Bundle bundle_human.svb -Frames "60,150,250"
```

`docs/log-analysis/` の要領で画素差を取る。

**閾値: 実行ごとのノイズが最大 0.24%ある。**同一コードで 2 回撮って実測した値
（対照① 0.0945%、対照② 0.2353%）。⑨ は元々決定論的でないと
[bundle-placement.md](bundle-placement.md) にも記録がある。

**必ず「同一コードで 2 回撮った対照」を並べて判断すること。**
1 回の差分だけを見ると、ノイズを退行と読み違える。

## 2026-09-06 に実施した内容

| Phase | 内容 | 結果 |
|---|---|---|
| 0 | テストと描画のベースライン取得 | 527/502/25、ノイズ 0.24% |
| 1 | 未参照の private メンバを削除 | **12 メソッド + 2 フィールド、257 行** |
| 2 | pinhole 変換の重複集約 | **失敗・巻き戻し**（下記）|
| 2' | ループ内の冗長な `Quaternion.Inverse` を外へ | 3 箇所 |
| 3 | 巨大 partial の分割 | Playback 2,482 → **1,408 行** |

最終: コード差分 **+542 / −1306 行**。テスト・描画とも退行なし。

### Phase 1: 削除したもの

`ApplyAnimalBonesFromSegment` / `TryResolveTrackIdFromCollider`（物理を捨てた際の残骸）/
`TryGetHumanSmplRootRotation` / `TryGetSmplJointForHumanBone` / `TryGetHeadTarget` /
`IsManifestJointsSpaceRootRelative` / `GetManualYawOffsetDegForTrack` /
`SetManualYawOffsetDegForTrack` / `StepSelectedManualRotationTrack` /
`TryGetVisibleJointBounds` / `StepRuntimeModelPickerTarget` / `CreateWideLabel` /
`UpdateFovxSliderRange` / `HumanSmplFlipY` / `TestModelLockFrames`

**削除前に確認したこと**: シーン・Resources からの参照 0 件、
テストがリフレクションで触るのは `TryGetPinholeBasis` のみ（候補外）。
`BatchPlaybackLogger.Reattach` と `InteractiveMotionHumanClipPostprocessor.OnPreprocessModel` は
Unity が属性・規約で呼ぶので**参照 0 でも残す**。

### Phase 3: 分割したもの

| 移動先 | 内容 | 行数 |
|---|---|---|
| `StreamingStereoVideoPlayer.Debug.cs` | 診断ログ 4 メソッド | 437 |
| `StreamingStereoVideoPlayer.OtherDepth.partial.cs`（新規）| Else の深度追従・貫通解決（⑨⑩）11 メソッド | 656 |

**同じ partial クラス内の移動なので、参照関係もシリアライズも変わらない。**
これが god class を触るときの唯一安全な方向。

## NG: public フィールドを別クラスへ移すこと

`StreamingStereoVideoPlayer` は **public フィールド 232 個**を持つ MonoBehaviour で、
値は**シーンにシリアライズされている**（screenDistanceMeters・displayTrackIds・各診断フラグ等）。
別コンポーネントへ移すと**シーンの調整値が失われる**。

なお未使用の public フィールドは 2 件（`MetaObj.skeletonRootCam` /
`hasSkeletonRootCam`）しか無く、Inspector のフィールドはほぼ全部使われている。
「使われていないフラグを消して減らす」余地は無い。

## NG: 正規表現による一括置換（Phase 2 の失敗）

`TryGetPinholeBasis` → `Quaternion.Inverse` → `worldToCam * (p - camOrigin)` の
3 行 1 組が **7 ファイル 31 箇所**に散っており、変数名も `worldToCam` / `inv` / `toCam` と
揺れていた。これを `PinholeView` 型に束ねようとして、正規表現で一括置換した。

**結果: コンパイルエラー 45 件。**

- `camOrigin` / `camRotation` を**ファイル全体**で置換したため、
  **メソッドの仮引数宣言や別の `TryGetPinholeBasis` 呼び出し**まで
  `Vector3 pinhole.origin` のような不正な形に壊した
- `worldToCam` の置換が二重に掛かり `pinhole.pinhole.worldToCam` になった

影響のない 2 ファイルは HEAD へ戻し、Playback は置換を打ち消して復元した。

**識別子の一括置換に正規表現を使わないこと。** スコープを見ないので必ず壊れる。
やるなら 1 箇所ずつ文脈を確認する。この重複の集約自体は有効なので、
やり直すときは手作業で。

## 残っている構造上の課題

| | 規模 |
|---|---:|
| `StreamingStereoVideoPlayer` の総行数 | 20,296 |
| 同 partial 数 | 37 |
| 同 public フィールド | 232 |
| `InteractiveMotion.partial.cs` | 1,840 行 |
| `AnimalPoseApplier.cs` | 1,466 行 |
| `Debug.cs` | 1,435 行 |

次に触るなら `InteractiveMotion.partial.cs`（1,840 行）を
partial 間で分けるのが同じ手順で安全にできる。
