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
