using System;
using UnityEngine;
using UnityEngine.EventSystems;

// スライダーを掴んでいる／離したことだけを呼び出し側へ知らせる。
//
// **IDragHandler は使わない。** VR ではレイを保持したまま手を動かしても
// 画面上の移動量が小さく、ピクセル閾値を超えないと発火しない（2026-09-01 実機）。
// 押した・離したなら確実に来る。
//
// 進捗バー用。掴んでいる間は「いまの再生位置」で つまみ を上書きしないため、
// および値が動くたびにシークしないために要る。
[DisallowMultipleComponent]
public sealed class RuntimeSliderDragNotifier : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    public Action<bool> onDragChanged;

    public bool IsDragging { get; private set; }

    public void OnPointerDown(PointerEventData eventData)
    {
        IsDragging = true;
        onDragChanged?.Invoke(true);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        if (!IsDragging)
        {
            return;
        }

        IsDragging = false;
        onDragChanged?.Invoke(false);
    }


    // パネルが閉じるなどで無効化されたときは、掴んだままにしない。
    // 残すと進捗バーが二度と更新されなくなる。
    private void OnDisable()
    {
        if (!IsDragging)
        {
            return;
        }

        IsDragging = false;
        onDragChanged?.Invoke(false);
    }
}
