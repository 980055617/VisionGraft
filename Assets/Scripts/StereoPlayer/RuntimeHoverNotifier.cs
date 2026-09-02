using System;
using UnityEngine;
using UnityEngine.EventSystems;

// レイ（またはマウス）が乗った／外れたことだけを呼び出し側へ知らせる。
// 自分では見た目を変えない。モデルピッカーのセルに付けて、乗ったモデルを拡大するのに使う。
[DisallowMultipleComponent]
public sealed class RuntimeHoverNotifier : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public int index = -1;
    public Action<int, bool> onHoverChanged;

    public void OnPointerEnter(PointerEventData eventData)
    {
        onHoverChanged?.Invoke(index, true);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        onHoverChanged?.Invoke(index, false);
    }


    // パネルを閉じるなどで無効化されたときも「外れた」ことにする。
    // これをしないと、開き直したときに前回のセルが拡大したまま残る。
    private void OnDisable()
    {
        onHoverChanged?.Invoke(index, false);
    }
}
