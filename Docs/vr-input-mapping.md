# VR 入力の割り当て

実機のコントローラで何がどのボタンに載っているか。2026-08-31 にコードから確認した実測。

## 現状

| 操作 | 入力 | 実装 |
|---|---|---|
| **UI パネルのボタン・スライダー** | **トリガー** | `Assets/InputSystem_Actions.inputactions` の `Click` アクション → `<XRController>/trigger`。`InputSystemUIInputModule` 経由 |
| **動画内オブジェクトの選択**（回転・モデル変更の対象決め） | **トリガー** | `RuntimeXrRayPickReader.TryReadPointerPose` が `CommonUsages.triggerButton` を読む → `[Pick] track=...` |
| **再生 / 一時停止のトグル** | **A ボタン** | `RuntimePauseInputReader.TryReadPrimaryButtonPressed` が `CommonUsages.primaryButton` を読む。`EnablePauseHotkey` は定数 true |
| 再生 / 一時停止（PC） | Space / P | 同上（キーボード） |

**つまり「触る」系はすでに全部トリガーに載っている。** A ボタンに載っているのは
一時停止トグルだけ。

## ユーザー操作でモーションを起こす仕組みは無い

`InteractiveTriggerSource` は `Random` と `SystemFrameOut` の 2 つだけ
（[StreamingStereoVideoPlayer.InteractiveMotion.partial.cs](../Assets/Scripts/StereoPlayer/StreamingStereoVideoPlayer.InteractiveMotion.partial.cs)）。

トリガーでオブジェクトを指しても、起きるのは

- `selectedManualRotationTrackId` の更新（Settings パネルの回転・スケールの対象になる）
- `runtimeModelPickerTrackId` の更新（Change で開くピッカーの対象になる）

だけで、**モデル側は何も反応しない**。「指したら振り向く・尻尾を振る」といった
ユーザー起因のインタラクションは未実装
（[interactive-motion-events.md](interactive-motion-events.md) の将来分）。

## 一時停止をトリガーへ移すのは単純な付け替えでは済まない

トリガーは UI クリックとオブジェクト選択に使われているので、そのまま移すと

- パネルのボタンを押すたびに再生 / 一時停止が切り替わる
- 動画内のオブジェクトを指すたびに切り替わる

`TryResolveXrPickRay` はパネルを開いている間のトリガーを無視するガードを持つが、
これは「パネルの裏のスクリーンを拾わない」ためのもので、一時停止側には掛からない。

移すなら、A ボタンの一時停止を**やめる**（パネルの Pause ボタンだけにする）か、
グリップなど別のボタンへ移すのが素直。


---

# ランタイム UI の不具合（2026-08-31 実測）

実機で「モデル選択画面に名前しか出ない」「設定を開くと変な点が見える」と報告された件。
`BatchPlaybackLogger` に `-openPicker` / `-openSettings` を足して Editor で撮って調べた。

## 1. ピッカーのプレビューが見えない

プレビュー機能は最初から実装されていた（`CreateRuntimeModelPickerPreview` が各行の左に
実物のモデルを置く）。見えていなかった理由は 2 つ重なっていた。

### ① world space canvas の z は等倍

canvas の localScale は `(幅m/幅px, 高さm/高さpx, 1)` = `(0.000857, 0.000879, **1.0**)`。
x/y は 1/1000 程度なのに **z だけ 1**。ここに uniform スケールのモデルを置くと、
1.7m の人体が **幅 4cm・奥行き 51m** の針になる。さらに `localPosition.z = -35` は
「35 **メートル** 手前」を意味していた。

対策:
- z 位置はパネル面から 1cm 手前（`PreviewForwardOffsetMeters`、z が等倍なので実寸）
- z のスケールに `高さm/高さpx` を掛けて見かけを等倍に戻す（`ApplyPreviewScale`）

### ② 大きさの測り方が壊れていた

`TryCalculateLocalRendererBounds` は `renderer.bounds`（**world** AABB）の 8 隅を
`root.InverseTransformPoint` で holder ローカルへ戻していた。holder の親は
極端に非等倍（z だけ 1）で、しかもパネルごと回転している。
world 軸の AABB をこの行列で戻すと、**パネル法線方向の僅かな厚みが x（1/0.00086 倍）へ漏れる**。

```
（修正前）00_Female_A_01 boundsSize=(623.160, 2.043, 0.650) maxSize=623.160 → scale 下限 8 にクランプ
（修正後）00_Female_A_01 boundsSize=(  1.549, 1.891, 0.542) maxSize=  1.891 → scale 27.49
```

623 という値のせいで `52 / maxSize = 0.08` となり、`Mathf.Clamp(scale, 8f, 90f)` の
**下限 8 に張り付いて**いた。結果、本来の 1/3 の大きさでしか出ていなかった。

mesh の**ローカル** bounds を `root.worldToLocalMatrix * renderer.localToWorldMatrix` で
運ぶ形に直した（`ElseOrientationDiagnostics.TryCombineBounds` と同じやり方）。
途中のスケールが行列内で相殺されるので正しい寸法が出る。

### ③ 毎フレーム 6 体を作り直していた

`UpdateRuntimeModelPickerUiState` は `UI.cs` から**毎フレーム**呼ばれ、その中の
`UpdateRuntimeModelPickerEntryButtons` が無条件に `ClearRuntimeModelPickerPreviews()` →
6 体 Instantiate を回していた。人体は 1 体 30 renderer あるので、Quest では確実に重い。

ページ・対象 track・カテゴリ・選択 index・prefab 数が変わったときだけ作り直すようにした。
`[PREVIEW]` ログが 1 回の再生で 6 行（＝ 1 回だけ構築）になったことを確認。

## 2. 設定を開くと見える「変な点」＝ 手動回転ガイド

拡大すると**ピンクの球と短い軸**で、`ManualYawGuideFactory` が作る回転ガイドだった。
本来は頭上に出る矢印だが、モデルの中に埋まって点にしか見えていなかった。

原因は単位の取り違え。ガイドは instance の子で、instance の localScale は bbox 合わせで
0.26 などになっている。ところが

```csharp
Bounds b = ComputeObjectBounds(instance);   // world AABB
float y = Mathf.Clamp(height * 1.1f, 0.8f, 2.4f);   // これを local に入れていた
```

と、**world で測った値を local の位置・大きさにそのまま入れて**いた。
instance のスケールぶんもう一度縮むので、頭上どころか腰の高さに小さく出る。

world で決めた寸法（頭上 12cm、長さ 30cm、軸 2.5cm、先端 7.5cm）を `lossyScale.y` で割って
local に直す形にした。モデルの大小によらずガイドの実寸が一定になる。

修正後のキャプチャでピンクの点は消えた。**ただし矢印が頭上に見えることまでは未確認**
（モデルのローカル +z 方向に伸びるので、視線方向を向いていると頭の後ろに隠れて短く見える）。
実機で向きを確認したい。

## 3. まだ見ていない: 設定パネルのレイアウト

「UI が使いにくい」の具体例は「変な点」だけだったので、レイアウトの作り直しはしていない。
キャプチャではパネル右端（`anchorX 0.88` の値テキスト群）が画面外で写っておらず、
**あの絵から「値が見えない」と結論はできない**。

計算上分かっているのは 1 点だけ: 値テキストは幅 280 を 0.88 に中央寄せするので
canvas 900 の中で 652〜932 を占め、**右へ 32px はみ出す**。右寄せなので文字自体は
右端に寄るが、パネルの縁に接する。

次は `runtimeSettingsRoot` を歩いて RectTransform を canvas 座標でダンプし、
はみ出し・重なりが実際に出ている要素だけを直す。


## 4. ピッカーを 2 行 3 列にした（2026-08-31）

プレビューが出るようになっても 1 行 6 件では 1 件 64 単位しか取れず、
「小さすぎて何のモデルか分からない」という指摘。件数は 6 のまま配置を変えた。

| | 変更前 | 変更後 |
|---|---|---|
| 配置 | 1 列 × 6 行 | **3 列 × 2 行** |
| セル | 860 × 64 | **300 × 185**（面積 約 4.5 倍） |
| プレビューの狙い | 52 単位 | **118 単位** |
| 名前 | 行の左に `> 1. 名前` | セル下端に中央寄せで `1. 名前`（選択は枠の色） |

セル座標は `ResolveModelPickerCellCenter(index)` の 1 箇所に集約した。
ボタンとプレビューが別々の式を持つと、片方だけ直したときにずれる。

### 併せて直した: 小さいモデルが上限クランプで潰れていた

`Mathf.Clamp(scale, 8f, 90f)` の**上限 90** が、球のような小さいモデルを潰していた。

```
00_Baseball  maxSize=0.076 → 必要な倍率 1552.60
03_Golf      maxSize=0.044 → 必要な倍率 2681.76
```

上限 90 では野球ボールが意図の 6%、ゴルフボールが 3% の大きさにしかならない。
bounds の計算を直したので異常値は出ない。`Clamp(scale, 1f, 3000f)` に広げた。

## 5. 「自由に見る」を押しても反応しない

`HomeMenu.Load` が `SceneManager.LoadScene`（**同期**）を押した直後に呼んでいた。
同期ロードはその場でフレームを止めるので、押した見た目の変化が何も描かれないまま数秒固まる。

押した直後に「読み込み中」を出し、**2 フレーム描かせてから** `LoadSceneAsync` に渡す形にした。
1 フレームだと Canvas の再構築が間に合わないことがある。

## 6. bundle ピッカーが開いた瞬間に頭へ追従する

追従は仕様で、開いてから `BundlePickerPlacementSettleSeconds`（0.5 秒）の間だけ毎フレーム
置き直している。tracking origin が Device に切り替わる前に固定すると、切り替えで
パネルが視界の外へ飛ぶため（2026-08-28 の対処）。この 0.5 秒が「追従」に見えていた。

**置き場所が決まるまで Canvas を切って見せない**ようにした。出た時点で静止している。
root を非アクティブにすると `UpdateBundlePickerPlacement` を回す側の前提が変わるので、
Canvas の enabled だけを切っている。


## 7. 回転ガイドが上空へ飛ぶ（2026-08-31、自分で入れた退行）

「Settings を開くと紫のものが上へ飛んでいく」。原因は §2 の修正に含まれていた自己参照。

ガイドは instance の子なので、高さを測る `ComputeObjectBounds` の
`GetComponentsInChildren<Renderer>` に**ガイド自身が入る**。

```
ガイドを頭上に置く → 次のフレームの「モデル上端」がガイドぶん上がる
→ ガイドがさらに上へ → 毎フレーム積み上がる
```

旧実装は `y` を `Clamp(height * 1.1f, 0.8f, 2.4f)` としていたので頭打ちになり、
症状が「モデルに埋まった点」で止まっていた。clamp を外した時点でこの依存が表面化した。

`ComputeObjectBoundsExcluding(instance, manualYawGuideRoot)` に差し替えて、
ガイド配下の renderer を測定から外した。

**教訓**: 対象の子として置いたものを、対象の bounds から測ってはいけない。

## 8. ピッカーのヘッダを作り直した

- **Close → 右上の X アイコン**（60×60、(438, 288)）。以前は "Close" が対象送りの隣にあり
  押し間違えやすかった
- **Next target > → track ID のボタン列**。出ている track の ID を全部並べて直接押せる。
  現在の対象は青。**固定枠にしない** — 上限を決め打ちすると、それを超えた ID に
  到達できなくなる。出ている数だけその場で作り、多いときは間隔を詰める（行幅上限 900）
- ドロップダウンにしなかった理由: VR で開いたリストは別のワールド座標に浮き、
  レイで追いにくい。常時表示なら 1 クリックで届く

### レイが乗ったセルを拡大する

`RuntimeHoverNotifier`（`IPointerEnterHandler` / `IPointerExitHandler`）をセルに付け、
乗っているセルのプレビューだけ 1.55 倍にする。素のスケールは
`runtimeModelPickerPreviewBaseScales` に控えて外れたら戻す。

別枠に大きく出す案は採らなかった。グリッドを覆ってしまい、どれを見ているか分からなくなる。

## 9. パネルの前後をコントローラで押し引きする

「掴んで前後に動かしたい」への対応。掴み代は**下端**（上に置くとタイトルと重なり、
視線も上へ引っ張られる）。

**上下のドラッグ量では距離を変えない。** 「上へ動かす＝遠ざかる」は対応がねじれている。
UI のドラッグイベントは 2D の差分しか持たないので、掴んでいる間だけ
`RuntimeXrRayPickReader.TryReadPointerPose` でコントローラの姿勢を直接読み、
頭→奥方向への移動量をそのまま距離にする（手を 1m 動かすとパネルも 1m）。

コントローラの姿勢はトラッキング空間なので、頭の姿勢差からリグの回転を復元して world に直す。

範囲は −0.35m 〜 +0.70m。頭から最低 0.35m は残す。Settings とピッカーで値を共有する
（片方だけ動くと揃わない）。

## バッチで確認できないもの

- **track ID の行** … 再生が再開すると `CloseEditPanelsForResume` がピッカーを閉じるため、
  バッチでは開いた状態を保てない。開けた瞬間はまだインスタンスが無く `ids=0` になる。
  `-openPicker` は閉じられたら開き直すようにしてあるが、それでも撮れなかった
- **hover の拡大** … ポインタが無い
- **押し引きのドラッグ** … 入力が無い

いずれも実機で見るしかない。診断ログ `[TARGETROW]` は `-diagLogs true` で出る。

## 10. モデル変更で対象 track がタブから消える（2026-08-31 実機）

「0 と 1 が出ている状態で 0 のモデルを変えたら、上のタブから 0 が消えて 1 だけになった」。

`GetAvailableTrackIdsForManualRotation()` が**生きているインスタンスだけ**を見ていた。

```
モデル変更 → RecreateTrackInstanceForModelSelection がインスタンスを破棄
→ 次に ApplyTrackFrame が走るまで再生成されない
→ ピッカーを開いている間は再生を止めているので走らない
→ 一覧から消える（＝いま選んでいる track が選べなくなる）
```

いま表示中のフレームに写っている track（`metaFrameObjects`）との**和**を取るようにした。
インスタンスの有無は「作り直し中かどうか」でしかなく、対象として選べるかとは別の話。

副次的に、バッチでも track 行を確認できるようになった（開いた直後はまだインスタンスが
無く `ids=0` だったのが、`ids=3 buttons=3` になった）。

## 11. 掴み代のドラッグが実機で効かなかった

`IDragHandler` は EventSystem のドラッグ閾値（ピクセル）を超えないと発火しない。
レイでパネルを掴んだまま手を前後させても、**画面上のレイの移動量は小さい**ので
閾値に届かず、一度も始まっていなかった。

`IPointerDownHandler` / `IPointerUpHandler` に変更。ボタンが押せている以上こちらは確実に届く。
掴んでいる間にコントローラの姿勢を毎フレーム読む部分は変えていない。

**教訓**: VR のレイ操作で「画面上の移動量」を前提にした UI イベントは当てにならない。
押下・解放のような離散イベントを起点にして、量は 3D 側から取る。

## 12. 静止モードにしてもアプリを開くと歩行の境界線になる（2026-09-01 実機ログ）

### 結論: **アプリは切り替えていない。** Quest の Auto Stationary が近くの roomscale 境界を再利用している

`adb logcat -c` で消してから、静止に設定 → アプリ起動、の順で採取した記録。

```
20:32:37  ログ開始（ここで消した）
20:35:56  uiMode=2 (HOME)      boundary=ALWAYS_ON  app=com.oculus.vrshell      ← アプリ起動前
20:36:15  uiMode=8 (IMMERSIVE) boundary=ALWAYS_ON  app=...urpblank            ← アプリ起動
20:36:17  I/OVRPlugin: Unavailable OpenXR extension: XR_EXTX2_stationary_reference_space
20:36:17〜 I/AutoStationaryHelper: getGuardianDataFromNearbyRoomscale Distance to HMD: 2.17〜2.28
          （1 秒ごとに継続）
```

**アプリ起動前の時点（20:35:56、まだ Home）で既に `ALWAYS_ON`。** 起動時も同じ値のままで、
アプリが書き換えた形跡は無い。

そして起動後は `AutoStationaryHelper::getGuardianDataFromNearbyRoomscale` が
**約 2.2m 先の roomscale 境界からガーディアンを引いて**きている。これが「静止にしたのに
歩行の境界が出る」の正体。Quest 側の機能であって、アプリの宣言や tracking origin とは別。

### 否定された仮説

`AndroidManifest.xml` の
`<uses-feature android:name="android.hardware.vr.headtracking" android:required="true" />`
が roomscale を強制している、と最初に疑ったが、**この記録では支持されない**
（アプリ起動前から ALWAYS_ON だった）。manifest は変更していない。

### アプリ側でできること／できないこと

- `ForceStationaryTrackingOrigin = true` で `TrackingOriginModeFlags.Device` を要求済み。
  実機ログに `[XR] tracking origin -> Device` が出ない＝**最初から Device** なので、
  こちらは意図どおり効いている
- `XR_EXTX2_stationary_reference_space`（Meta の静止参照空間の拡張）は
  **この実行環境では使えない**とログに出ている。アプリから静止を明示的に要求する手段が無い

### 対処は端末側

- 保存済みの roomscale 境界を消す（設定 → 境界）か、その場所から離れる
- 端末に「自動静止（Auto Stationary）」の設定があれば切る

`immersiveAppBoundaryType` は「没入アプリで境界を表示するか」であって、
静止／ローム の区別ではない点に注意（Home の vrshell も同じ値を出す）。

## 13. Human の LOD と VRoid モデルの揺れもの（2026-09-02 実測）

### LOD: Human だけ。ForceLOD(0) で固定した

`ModelQualityDiagnostics.Dump` の実測。

| フォルダ | LODGroup を持つ prefab |
|---|---|
| Human | **12 / 17**（いずれも 5 段） |
| Animal | 0 / 52 |
| Else | 0 / 7 |

LOD は画面占有率で段を選ぶが、このプロジェクトのモデルは bbox 合わせで **0.26 倍**などに
縮むので、実質いつも最下位が選ばれる。「遠いとポリゴンが減る」の正体。

同時に置くオブジェクトは多くて 5 体程度なので、`TrackInstanceFactory.ForceHighestLod` で
`LODGroup.ForceLOD(0)` を掛けて常に最上位にした。ピッカーのプレビューにも同じ処理を入れた
（小さく表示されるので放っておくと粗くなる）。

### 揺れもの: 入っているのに動いていない。原因は未特定

`06_Female_C.vrm`（VRoid 由来、Human で唯一の .vrm）の中身:

```
[VRM]    1 x UniVRM10.Vrm10Instance
[VRM]  131 x UniVRM10.VRM10SpringBoneJoint
[VRM]   28 x UniVRM10.VRM10SpringBoneCollider
[VRM]   12 x UniVRM10.VRM10SpringBoneColliderGroup
```

生成時の状態:

```
[SPRING] Track_0 joints=131 vrm10Instance=enabled updateType=プロパティ無し lossyScale=(1.0000,...)
```

**設定は揃っている。** `Vrm10Instance` は enabled で、この UniVRM のバージョンには
「更新しない」に相当するスイッチ（UpdateType）自体が無い。

にもかかわらず、実際の描画ではスカートも髪も**体の軸に沿って固まったまま**で、
被写体が逆さになっても落ちない（f=200 / f=260 のキャプチャで確認）。

### 構造的な相性の悪さ

揺れものは「前フレームの world 位置から積分する」前提で動く。ところがこのプロジェクトは

- 毎フレーム モデルを別の world 位置へ置き直す（anchorZ が動く）
- 毎フレーム ボーンの world 回転を FK で上書きする
- shot ごとにスケールを付け替える

を全部やっている。揺れものが最も苦手な入力を与え続けている。

### 判断

Human 17 個のうち 1 個。プロジェクトの最優先は keypoints3d への姿勢一致であり、
布のシミュレーションではない。**このモデルを外すのが妥当**と考える。

外すなら、番号を振り直すのではなく **`IsIndexedPrefabName` に引っかからない名前へ変える**
（例: `_06_Female_C.vrm`）。ファイルを消さずに一覧から外れ、他のモデルの番号も動かない。
`model_selection.json` は prefab 名で保存しているので、他のモデルの保存値も壊れない。
