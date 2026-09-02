using System;
using UnityEngine;
using UnityEngine.EventSystems;

// パネルの掴み代。掴んでいる／離したことだけを呼び出し側へ知らせる。
// 動かす量は呼び出し側がコントローラの姿勢から決める。
//
// **IDragHandler ではなく IPointerDownHandler / IPointerUpHandler を使う。**
// drag イベントは EventSystem のドラッグ閾値（ピクセル）を超えないと始まらず、
// レイでパネルを掴んだまま手を前後させるだけでは画面上の移動量が小さく、
// 実機で一度も発火しなかった（2026-08-31）。押下と解放なら確実に来る。
[DisallowMultipleComponent]
public sealed class RuntimePanelDragHandle : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    public Action<bool> onDragStateChanged;

    public void OnPointerDown(PointerEventData eventData)
    {
        onDragStateChanged?.Invoke(true);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        onDragStateChanged?.Invoke(false);
    }


    // 掴んだままパネルが閉じた・作り直された場合に掴みっぱなしにしない。
    private void OnDisable()
    {
        onDragStateChanged?.Invoke(false);
    }
}
