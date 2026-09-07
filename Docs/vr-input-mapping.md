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

## 14. 横長モデルは側面を見せる（2026-09-02）

機関車のプレビューが「細長い塊」にしか見えなかった。prefab の補正で長軸（18.507m）が
ホルダーの Z を向くので、正面から見ると**端から見る形**になっていた。

奥行きが横幅の 1.2 倍より長いモデルは、プレビューを Y 軸 90 度回して側面を見せる。
回したあとに bounds を測り直す（回転前の値で中心を引くと位置がずれる）。

```
（修正前）06_DieselLocomotive boundsSize=(3.593, 5.259, 18.507)  ← 長軸が Z
（修正後）06_DieselLocomotive boundsSize=(18.507, 5.259, 3.593)  ← 長軸が X
```

球は差が出ず、人・動物は高さが最長なので条件に入らない。実測で該当するのは機関車だけ。

一覧に収める基準は最長軸のままにした。高さで合わせると幅が 3.5 倍になりセル（300）を
はみ出す。細く見えるが、機関車と分かる形にはなっている。

## 15. 「この track にはモデルを置かない」（2026-09-02）

track 0 と 1 が出ているとき、1 にはモデルを置かない、という指定ができるようにした。

### 置き場所

ヘッダの track 行の左端に **`表示しない` / `表示する` のトグル**。
一覧のセルに混ぜるとページを跨いだときに見失うので、常に同じ位置に出す。
非表示の間はボタンが赤系になり、ステータス行が `Selected: 表示しない` になる。

### 仕組み

- 選択値に `HiddenModelIndex = -1` を入れる。一覧の index は 0 以上なので衝突しない
- `ResolvePrefabFromSelection` が null を返す。**`Mathf.Clamp` より前に見ること**
  （-1 は 0 に丸められてしまう）
- `TrackInstanceLifecycle.GetOrCreate` は prefab が null なら既存インスタンスを**破棄**する。
  非アクティブにするだけでは、姿勢適用やスケールのロックが走り続ける理由が残る

### 保存

`model_selection.json` に `"model": "(none)"`（`HiddenModelName`）として動画ごと・track ごとに
保存する。index ではなく名前で持つのはモデルの増減でずれないため。
復元時は一覧を探しに行かず、`HiddenModelIndex` に直す。
実験のセッション上書きにも同じ経路で乗る。`operations.csv` にも
`change_model ... prefab=(none)` として残る。

## 実機で未確認の項目（2026-09-02 時点）

ヘッドセット充電中のため保留。次にビルドしたときにまとめて見る。

| # | 項目 | 期待する挙動 |
|---|---|---|
| 1 | 掴み代の感度 2.5 倍 | 手 20cm でパネル 50cm。強すぎないか |
| 2 | 手前の限界 −1.0m / 最低距離 0.25m | もっと前まで来るか |
| 3 | LOD 固定 | Human が遠くでも粗くならないか |
| 4 | VRoid モデル除外 | 一覧が 16 件になり、`Female_C` が消えているか |
| 5 | バッチ音声ミュート | （実機ではなく、私のバッチ実行時に音が鳴らないか） |
| 6 | 機関車のプレビュー | 側面向きで、機関車と分かるか |
| 7 | 「表示しない」 | 押すとモデルが消え、もう一度押すと戻るか |
| 8 | 「表示しない」の保存 | アプリを開き直しても非表示のままか |
| 9 | 回転ガイドの矢印 | 頭上に見えるか（§7 で直したが未確認のまま） |
| 10 | 設定パネル右端の値 | `0.0 deg` / `x1.00` が読めるか（32px はみ出す計算） |

## 16. キーフレームの削除と、掴んで 3 軸に回す（2026-09-02）

### キーを消せるようにした

Track 行に `Del` を追加。**現在フレームにキーがあるときだけ押せる**ので、押せるかどうかで
「キー」か「補間」かが分かる。yaw / pitch / roll / scale を**まとめて**消す。

`Yaw 0` などの Reset とは別物。Reset は「0 のキーを打つ」なので、一度打ったフレームは
以後ずっとキーであり続ける。打ち間違えを取り消す手段がこれまで無かった。

Track 行は 110 幅・110 間隔で 5 つに詰めた（`<` `>` `Yaw 0` `Scl 1` `Del`）。
右端は 150 で、TrackValue の枠（202 から）に掛からない。

### 回転を 3 軸に

`manualPitchKeyframesByTrack` / `manualRollKeyframesByTrack` を追加。
`model_selection.json` には `"pitch"` `"roll"` として保存する（`"scale"` を足したときと同じ
5 か所 + 復元経路）。

適用順は **yaw（world の上）→ pitch（その結果の右）→ roll（その結果の前）**。
pitch = roll = 0 のときは以前と同じ式になるので、**保存済みの yaw はそのままの意味で効く**。

keypoint 側（`ApplyManualYawToJoints`）も同じ順で組む。片方だけ変えるとモデルの向きと
keypoint の向きがずれる。

### 掴んで回す

三方向の動きに軸を割り当てる案は採らなかった。「前後に動かすと roll」は現実の動作と
対応しない。**コントローラの姿勢の差分をそのまま渡す**と、1 つの動作で 3 軸すべてが決まる。

```
delta = 今のコントローラ姿勢 * inverse(掴んだ瞬間の姿勢)
適用後 = delta * 掴んだ瞬間の対象の回転
```

保存はキーフレームが軸ごとの float なので、結果を euler に落として yaw/pitch/roll に入れる。

**掴む対象**: 実測でモデルにはほとんど当たり判定が無い（Human 0/16、Animal 0/52、
Else は球 6 個だけ）。`TrackInstanceFactory.AddGrabCollider` が mesh の bounds から
`BoxCollider`（isTrigger）を root に足す。

**スクリーンのピックとは干渉しない。** `TryPickScreenByRay` は `Physics.Raycast` ではなく
平面との数学的な交差で解いているので、collider を足しても「指して選ぶ」挙動は変わらない。

パネル（Settings / ピッカー / bundle ピッカー）を開いている間は掴まない。
パネル操作のトリガーで対象が回ってしまうため。

診断は `[GRAB] 掴んだ / 離した`。実機でしか動かないので、効かないときはこれで切り分ける。

## 画面と UI の配置（2026-09-07 調査）

実機で挙がった 3 点の指摘の原因。いずれも**同じ設計に起因する**。

### 指摘 1: Screen Dist を動かすと UI も小さく／大きくなる

UI は**画面を基準に配置されている**。`UpdateRuntimeControlsPlacement` は
`basis = leftScreen / rightScreen` を起点に `RuntimeControlsPlacement.ResolveBarPose` を呼び、
設定パネル（`UpdateRuntimeSettingsPlacement`）とモデルピッカーも同じ基準を使う。

canvas の localScale 自体は `ControlsBarSizeMeters` の固定値で距離に依らない。
**小さく見えるのは遠近によるもの**で、画面が遠ざかると UI も一緒に遠ざかる。

### 指摘 2: 画面が設定パネルに被って戻れなくなる

`PlaceScreens` は `head.position` と `ResolveYawOnlyViewRotation(head.rotation)` を使い、
**その時の頭の位置・向きの正面**に画面を置く。

さらに毎フレームの監視があり、頭が **0.35m 動くか 35° 回る**と
`RecenterScreensToCurrentFacing()` が走って画面が新しい正面へ飛ぶ
（`StreamingStereoVideoPlayer.Core.partial.cs` の 575〜590 行付近）。

スライダーを操作するために視線を動かすと閾値を越え、画面が飛んだ先が設定パネルの位置と
重なる。設定パネル側は `runtimeSettingsPlacementLockDepth`（`PlaceScreensWithoutMovingSettings`）で
その場に固定されるので、画面だけが動いて被る。

### 指摘 3 の下調べ: 機械内蔵の正面は読める

シーンのリグは **OVRCameraRig**。`XRInputSubsystem` は既に扱っており
（`TryApplyPreferredTrackingOriginMode`、`trackingOriginUpdated` を購読済み）、
ユーザーの Reset View が設定する**トラッキング原点**がそのまま「機械が持っている正面」。

原点は頭カメラの親（OVRCameraRig の TrackingSpace）なので、
`head.parent` の姿勢を使えば live の頭向きに依らない固定の正面が得られる。

### 対処（2026-09-07）

| 指摘 | 対処 | 状態 |
|---|---|---|
| 画面が設定に被って戻れない | 頭の動きによる自動再センタリングを廃止（`autoRecenterScreensOnHeadMove` 既定 OFF）| **実機で確認済み** |
| 画面を VR の正面に | トラッキング原点の**向きだけ**を使う（`useTrackingOriginForScreenFacing`）| **実機で Reset View 追従を確認** |
| UI が小さい | 視点から 1.2m に固定し、視点へ正対（`pinRuntimeUiDistance`）| **実機で大きさ OK** |
| Screen Dist で UI が上下する | 画面からの「隙間」を距離に比例させる（`ScaleUiOffsetForDistance`）| バッチで確認、実機は未 |

### NG: トラッキング原点の「位置」まで使うこと

最初の実装で `LockPinholeBasis` の**位置**を原点（床の中心）にしたところ、
実機で「human の体勢が変わった」と指摘された。

**pinhole の位置は必ず目の位置**でなければいけない。モデルはそこから画面のピクセルへ
向かう光線上に置かれるので、目でない点を基準にすると、原点から離れて立つほど
見え方がずれる。**原点から取ってよいのは向きだけ。**

### NG: UI の位置を直すのに「画面のサイズ」を換算すること

「Screen Dist で UI が上下する」を直すとき、最初は UI 配置に渡す画面サイズを
既定距離のものに換算した。**逆に動いた。**

画面は `fitScreenToFov` で距離に比例して大きくなるので、**角度としては既に一定で正しい**。
動く原因は「画面の端からの隙間」と「バーの大きさ」が**固定値**であること。
遠いほどそれらの角度が小さくなり、UI が画面に寄っていく。
**距離に比例させるのは隙間の側。**

### コントローラのレイが 2 本出る・始点がずれる（2026-09-07 実機報告）

**まだ確定していない。**以下は「コードから分かったこと」と「推測」を分けて書く。

#### 確定していること

- **自前のレイは 1 本しか作られない。**プロジェクト内の `LineRenderer` は
  `RuntimePointerRayFactory` の 1 箇所だけ。二重に出ているのではない
- **リグが二重になってもいない。**ISDK リグ（`Assets/InteractionSDK/ComprehensiveInteraction.prefab`
  = Meta の `OVRComprehensiveInteractors.prefab` の variant）を持つのは
  HomeScene / ExperimentScene / TestScene の 3 つ。`LoadSceneMode.Additive` で重ねる
  `TrialScene`（`ExperimentController.cs:294`）は**持たない**
- **今回の画面・UI の変更が原因ではない。**レイに関わる 3 ファイルは `fd42e97` 以降変更していない
- **ISDK リグはレイ interactor を 2 種類積んでいる。**
  `OVRComprehensiveInteractors` → `BaseInteractors` の中に
  `Ray/ControllerRayInteractor.prefab` と `Ray/HandRayInteractor.prefab` の**両方**がある。
  さらに `SyntheticHandData` / `SyntheticControllerData` /
  `ControllerInHandVisibilityActiveState` があり、シーンにも
  `LeftControllerInHandAnchor` / `RightHandOnControllerAnchor` が置かれている
  = **コントローラを握った手を合成するモード**

#### 有力な推測

**ISDK 側だけで 2 本出ている**可能性がある。コントローラのレイ（実機のコントローラ位置から）と、
合成した手のレイ（手の位置から）は**始点が違う**。
これが「手元が違う始点になってる」に一番よく合う。

自前 1 本 + ISDK 1 本、という組み合わせもありうる。**絵を見ないと決められない。**

#### 今回の変更で「新しく見えるようになった」かもしれない筋

自前のレイを消すのは「パネルが開いているとき」ではなく
**「パネルを指しているとき」**（`GrabRotate.partial.cs:105`）。
UI を視点から 1.2m に固定したことで world canvas がコントローラの振り幅の中に入り、
ISDK 側のレイが反応する場面が増えた可能性がある。

#### 否定した筋

- **握り位置へのフォールバックではない（実機で確定）。**
  `RuntimeXrRayPickReader.TryReadAimPose` は `PointerPosition` が取れないと
  `devicePosition`（握り）へ落ちるが、実機のログは
  `[RAY] コントローラ姿勢の出どころ=aim`（2026-09-07 18:16）。
  **自前のレイは指し棒の姿勢を使えている。**残る 1 本は ISDK 側
- **view camera の取り違えでもない。**`ViewCameraSelection.Select` は
  `MainCamera` タグの最初のカメラを返し、`OVRCameraRig` では
  `CenterEyeAnchor` と `LeftEyeAnchor` の**両方**が `MainCamera` タグを持つ。
  ただし `LeftEyeAnchor` / `RightEyeAnchor` の Camera は `m_Enabled: 0` で、
  シーン側の override も無い。`IsUsable` が弾くので **CenterEyeAnchor が返る**。
  `centerEyePosition` と対応が取れており、ここにずれは無い

#### 次にやること

実機で「2 本のうち片方を消したら何本になるか」を見る。ユーザーに
**この 2 本が今回のビルドより前から出ていたか**を確認してから触る。

### Screen Dist を動かすと画面が左右に付いてくる（2026-09-07 実機報告・原因特定）

#### 症状

Screen Dist を調整しているときに左右を見ると、画面がその方向に付いてくる。
**「決まったあの場所から左右は動いたらダメ」**（ユーザー）。

#### 原因: スライダーを動かすたびに「そのときの頭の位置」へ置き直している

`OnRuntimeScreenDistanceSliderChanged`（`UI.Settings.partial.cs:335`）は
値が変わるたび `PlaceScreensWithoutMovingSettings` → `PlaceScreens` を呼ぶ。
`PlaceScreens` は `ResolveScreenAnchor` から基準点を取るが、そこが

```csharp
anchorPosition = head != null ? head.position : Vector3.zero;   // ← 生きている頭の位置
```

**向きだけをトラッキング原点から取り、位置は毎回そのときの頭にしていた。**
だからスライダーを触るたびに、画面（と pinhole 基準、つまりモデル全部）が
現在の頭の位置へスナップし直す。首を振ってから動かすと、その分だけ横へ寄る。

常時追従ではなく**スライダーを動かした瞬間だけ**動くのが、この経路の指紋。
`PlaceScreensWithoutMovingSettings` の呼び出し元は 2 箇所だけで、
どちらもスライダーの `onValueChanged`（Screen Dist と FOVx）。毎フレームの経路は無い。
`LateUpdate` の毎フレーム追従（`ForceScreensInFrontOfViewCamera`）は `false` で無効、
頭の動きによる再センタリング（`autoRecenterScreensOnHeadMove`）も既定 `false`。
新しく足したフィールドはシーンに serialize されておらず、コード既定値で動いている
（`TestScene` / `TrialScene` を grep して確認）。

#### 設計として正しい形

**画面の基準点と pinhole の基準点は同じ 1 点でなければならない。**
映像はその点から撮られたことになっていて、画面はその点から manifest の FOV を
張るように置かれ、モデルはその点から画面の画素へ向かう光線上に置かれる。
2 つを別の点にすると、映像とモデルが食い違う。

いまはその 1 点が「毎回の頭の位置」なので、シーン全体（画面 + モデル）が
頭と一緒に平行移動する。**この 1 点を最初に 1 回決めて固定すれば**、
画面もモデルも動かなくなり、頭を動かすと固定されたシーンを別の角度から見る形になる。

再取得するのは Reset View（`trackingOriginUpdated`）のときだけでよい。

#### 過去の失敗（繰り返さないこと）

この基準点を**トラッキング原点の位置**（床の中心）にしたら、実機で
「human の体勢が変わった」と言われた。原点は目より 1.6m 下にあり、
目でない点から投影することになるため。**固定するのは「最初の目の位置」で、
床の原点ではない。**

#### 対処と検証（2026-09-07）

`ResolveScreenAnchor` の基準点を**最初の目の位置で固定**した（`lockScreenAnchorPosition`、既定 ON）。
取り直すのは Reset View（`RecenterScreensToCurrentFacing`）と再生開始（`OnPrepared`）だけ。

バッチに `-anchorLock` と `-headShift` を足して A/B を撮った
（`BatchShiftViewerAndReplaceScreens` が「首を振ってから Screen Dist を触る」を再現する）。
`bundle_human.svb` の f150 で、視点を右へ 0.35m 動かしてからスライダーと同じ経路で置き直す:

| | スクリーンの中心 x | 幅 |
|---|---:|---:|
| 基準（視点を動かさない）| 959.5 px | 757 px |
| **修正前**（固定しない）| **959.5 px（±0.0）** | 757 px |
| **修正後**（固定する）| **864.5 px（−95.0）** | 757 px |

**修正前は視点を 0.35m 動かしても画面が 1 画素も動かない。**
つまり画面が完全に付いてきていた。修正後は世界に残るので、右へ動いた分だけ
左に見える（正しい視差）。幅は両方 757px で変わらず、**平行移動だけで
大きさは動いていない**ことも確認できる。

比較画像: `docs/tmp/anchor_lock_ab.png`。

**画像全体の差は 1.248% あるが、これはスクリーンではなくモデルの差。**
±0.0px と矛盾して見えるので注記しておく。差が出ている画素の**外接矩形は
x 741..1048 / y 507..720 で、100% がスクリーンの内側**（スクリーンは
x 581..1338 / y 351..835）。人物が写っている場所そのもの。
`BatchShiftViewerAndReplaceScreens` は視点を動かして `PlaceScreens` を呼ぶが、
モデルの配置はそのフレームの Update で既に済んでいるため、
**スクリーンだけ動いてモデルが 1 フレーム遅れる**。バッチ再現の副作用であって、
実機の挙動ではない。

#### 代償

基準点を固定すると、そこから頭を大きく動かしたとき、左右の目に出る絵が
本来の視点からずれるので**立体感が歪む**（`StereoScreenEyeSeparationMeters` は
基準点から見て正しくなるように置いてあるため）。
「決まった場所から動くな」という要求とはトレードオフで、要求どおりにしてある。

**UI も画面と一緒に world に残る。**UI は画面からの隙間で置いているので、
画面が固定されれば UI も固定される。頭を振ると UI が横に見えることになり、
これは「設定に被って戻れない」と同じ種類の不満になりうる。
**UI だけ頭に追従させる選択肢もある**（2026-09-07 時点でユーザーの判断待ち）。

### 2 本の内訳が確定（2026-09-07 実機）

**青が自前、白が ISDK。**ユーザーが実機で「青と白」と確認した。
自前のレイの色はコードで `Color(0.35f, 0.8f, 1f)` = 水色なので一致する。

#### 対処: 自前の線は描かない（`showPointerRay` 既定 OFF）

ユーザーの判断は「白だけでいい、青は見せなくていい。判定にだけ使えば」。
`UpdatePointerRay` の描画を飛ばすだけで、**掴む判定は今までどおり自前の姿勢**
（OpenXR aim pose）を使う。操作は変わらない。

#### 解決（2026-09-07 実機）

「線が白 1 本になり、今までどおり掴める」を確認。**この項目は閉じた。**

#### NG: ISDK の線を `LineRenderer` として測ろうとしたこと

「青と白が同じ方向を指している」という前提を数値で確かめようとして、
シーン内の `LineRenderer` を自前のものと比べるログを書いた。**空振りだった。**

```
[RAY] 線を描く候補 0 個
```

**ISDK は `LineRenderer` で線を描いていない**（tube 状のメッシュを使う）。
書いた比較は永久に何も出ないコードだったので削除した。

前提を通したのは**実機で掴めたこと**。狙って掴めるなら、見えている線と
判定に使う方向は実用上一致している。**測れないときに測ったふりをしない。**

なお `[RAY] 線を描く候補` の棚卸し自体は残してある（自前の線が二重に
作られていないかの確認に使える）。
