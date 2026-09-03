using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

public partial class StreamingStereoVideoPlayer : MonoBehaviour
{
    // パネルの中身を canvas 座標で書き出し、はみ出しと重なりを機械的に検出する。
    //
    // バッチのキャプチャはカメラの画角にパネル右端が入らず、「値が見えない」のが
    // レイアウトの問題なのか単に写っていないだけなのか判断できなかった（2026-09-03）。
    // 絵で判断できないものは数えるしかない。
    //
    // BatchPlaybackLogger の -dumpPanelLayout true で 1 回だけ走らせる。

    private bool panelLayoutDumped;

    // 開いているパネルを書き出す。設定・モデル一覧・モデル編集のどれでも同じ扱い。
    // -openSettings / -openPicker / -pickerTab で開いたものが対象になる。
    private void DumpPanelLayoutIfRequested()
    {
        if (!batchDumpPanelLayout || panelLayoutDumped)
        {
            return;
        }

        bool settingsReady = runtimeSettingsRoot != null && runtimeSettingsOpen;
        bool pickerReady = runtimeModelPickerRoot != null && runtimeModelPickerOpen;
        if (!settingsReady && !pickerReady)
        {
            return;
        }

        panelLayoutDumped = true;

        if (settingsReady)
        {
            DumpOnePanelLayout(
                "Settings", runtimeSettingsRoot, RuntimeSettingsDefaultCanvasWidth, RuntimeSettingsDefaultCanvasHeight);
        }

        if (pickerReady)
        {
            string tab = runtimeModelPickerTab == ModelPickerTabEdit ? "ModelEdit" : "ModelList";
            DumpOnePanelLayout(
                tab, runtimeModelPickerRoot, RuntimeModelPickerDefaultCanvasWidth, RuntimeModelPickerDefaultCanvasHeight);
        }
    }


    private static void DumpOnePanelLayout(string title, GameObject root, float canvasW, float canvasH)
    {
        Transform panel = root.transform.Find("Panel");
        if (panel == null)
        {
            Debug.Log($"[LAYOUT] {title}: Panel が見つかりません");
            return;
        }

        var items = new List<(string name, Rect rect)>();
        for (int i = 0; i < panel.childCount; i++)
        {
            Transform child = panel.GetChild(i);
            RectTransform rt = child as RectTransform;
            if (rt == null || !child.gameObject.activeSelf)
            {
                continue;
            }

            Vector2 size = rt.rect.size;
            Vector2 center = rt.anchoredPosition;
            items.Add((child.name, new Rect(center.x - size.x * 0.5f, center.y - size.y * 0.5f, size.x, size.y)));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[LAYOUT] ==== {title}  canvas {canvasW}x{canvasH}  要素 {items.Count}");

        float halfW = canvasW * 0.5f;
        float halfH = canvasH * 0.5f;
        int outside = 0;
        foreach ((string name, Rect r) in items)
        {
            bool over = r.xMin < -halfW || r.xMax > halfW || r.yMin < -halfH || r.yMax > halfH;
            if (over)
            {
                outside++;
            }

            sb.AppendLine(
                $"[LAYOUT]   {name,-32} x[{r.xMin,7:F0}..{r.xMax,7:F0}] y[{r.yMin,7:F0}..{r.yMax,7:F0}]" +
                (over ? "  ★はみ出し" : string.Empty));
        }

        int overlaps = 0;
        for (int i = 0; i < items.Count; i++)
        {
            for (int j = i + 1; j < items.Count; j++)
            {
                Rect a = items[i].rect;
                Rect b = items[j].rect;
                // Panel 全面を覆う背景など、片方がもう片方を完全に含む場合は数えない。
                if (Contains(a, b) || Contains(b, a))
                {
                    continue;
                }

                if (!a.Overlaps(b))
                {
                    continue;
                }

                float ox = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin);
                float oy = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
                overlaps++;
                sb.AppendLine(
                    $"[LAYOUT]   ★重なり {items[i].name} × {items[j].name}  ({ox:F0} x {oy:F0} px)");
            }
        }

        sb.AppendLine($"[LAYOUT] はみ出し {outside} 件 / 重なり {overlaps} 件");
        Debug.Log(sb.ToString());
    }


    private static bool Contains(Rect outer, Rect inner)
    {
        return outer.xMin <= inner.xMin && outer.xMax >= inner.xMax &&
               outer.yMin <= inner.yMin && outer.yMax >= inner.yMax;
    }
}
