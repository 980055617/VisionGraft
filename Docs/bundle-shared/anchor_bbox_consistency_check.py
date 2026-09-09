"""D-004 の解析スクリプト（2026-09-09 の [Unity側] 追加報告への回答）。

**「bbox はほとんど動いていないのに `anchor_z` が動く」区間**を全編から拾う。

理屈:
  実サイズの変わらない対象なら、見かけの大きさ `s`（bbox の高さ）は距離 Z に対して
  `s ∝ 1/Z`。DepthCrafter の視差は `disp = a/Z + b` なので、**disp は s に対して
  アフィン**になる。`a`,`b` が未知でも「s が動いていないのに disp が動いた」は
  それだけで矛盾として検出できる（D-004 の `a`,`b` 較正が要らないのはここ）。

見る値:
  - `disp`     … 1 - anchor_z。bundle に `source/placement_observations.json` が
                 あれば `rawAnchor.z`（EMA 前・量子化前）から作る。無ければ
                 meta.bin の量子化済み `anchor_z` を使う
  - `s`        … bbox の高さ。`--metric area` で sqrt(bbox 面積) に切り替えられる
                 （回転する `other` は高さだけだと暴れるため）
  - Unity 換算 … Unity runtime は anchor_z を線形にカメラ空間 Z として使う
                 （`train_depth_order.py` 参照）。描画高は 1/Z に比例するので、
                 **anchor_z が w% 下がると描画高は 1/(1-w) 倍**になる。
                 その倍率を「描画高」列に出す

使い方:
    python anchor_bbox_consistency_check.py <bundle.svb> [...]
        [--window 6]        比較する前後フレーム間隔（既定 6 = 0.2 秒 @30fps）
        [--disp-jump 0.02]  この以上 disp が動いたら候補
        [--bbox-tol 0.02]   この以下しか bbox が動いていなければ矛盾とみなす
        [--metric height|area]
        [--top 15]          事象を長い順に何件出すか
        [--track 0]         track を絞る
        [--used]            EMA 後（meta.bin に載る値）で判定する

**`anchor_z` の中身は track によって別物なので注意**（2026-09-09 に判明）:

  - `keypoint_depth_sample` / `mask_centroid_depth_sample` … depth map を 7x7 窓で
    サンプルした正規化 disparity。上のアフィンの理屈がそのまま成り立つ
  - `animal_camera_root` … **depth map を一切見ていない。** AniMer のカメラ空間 root Z
    （`animer_joint_scale` そのまま = AniMer 独自スケール。メートルではない）。
    EMA も通っていない。**「disp」という呼び方も、`s` に対してアフィンという前提も
    成り立たない。**「`anchor_z` が動いたのに `bbox` が動かない」という検出自体は
    有効（`anchor_z` は両経路とも「大きいほど遠い」）だが、原因は depth サンプリングでは
    ないので、depth 側の対処では直らない

track ごとの由来は出力の `anchor 由来:` 行に出る。

**この検出は「bbox のほうが正しい」を前提にしていない。** 片方が動いて片方が
動かない、という不一致を数えるだけ。どちらが原因かは個別に depth を見る必要がある
（2026-09-09 の frame 1075-1101 は「腕が骨盤アンカーの前を横切る」自己遮蔽だった）。
"""
import argparse
import json
import statistics as st
import sys
import zipfile

sys.path.insert(0, __file__.rsplit("/", 1)[0])
from train_depth_order import _load_full  # noqa: E402

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")


def load_series(path, use_used):
    """track -> {frame: (disp, size_h, size_area)} と fps を返す。

    placement_observations.json があればそちらを優先する（EMA 前の生値が取れる）。
    """
    b = _load_full(path)
    fps = b["manifest"].get("fps", 30.0) or 30.0
    obs = None
    try:
        with zipfile.ZipFile(path) as z:
            obs = json.loads(z.read("source/placement_observations.json"))
    except KeyError:
        pass

    series, source = {}, None
    if obs is not None:
        key = "usedAnchor" if use_used else "rawAnchor"
        source = "placement_observations.json / %s.z" % key
        for fr in obs["frames"]:
            fi = fr["frameIndex"]
            for o in fr.get("objects", ()):
                z_val = (o.get(key) or {}).get("z")
                if z_val is None:
                    continue
                bw, bh = float(o["bbox"][2]), float(o["bbox"][3])
                series.setdefault(o["trackId"], {})[fi] = (
                    1.0 - float(z_val), bh, (bw * bh) ** 0.5,
                    str((o.get("rawAnchor") or {}).get("source") or "?"),
                    bool(o.get("edgeTouch")))
    else:
        source = "meta.bin / anchor_z（量子化済み）"
        for fi, objs in enumerate(b["frames"]):
            for o in objs:
                z_val = o["anchorZq"] * b["qpos"]
                bw, bh = float(o["bboxW"]), float(o["bboxH"])
                series.setdefault(o["trackId"], {})[fi] = (1.0 - z_val, bh, (bw * bh) ** 0.5, "?", False)
    return series, fps, source, b


def events(frames, window, disp_jump, bbox_tol, metric_idx):
    """(開始, 終了, Δdisp, Δs 比) の候補を作る。連続するものは後段でまとめる。"""
    out = []
    for f in frames:
        g = f + window
        if g not in frames:
            continue
        d0, d1 = frames[f][0], frames[g][0]
        s0, s1 = frames[f][metric_idx], frames[g][metric_idx]
        if s0 <= 0:
            continue
        dd = d1 - d0
        ds = (s1 - s0) / s0
        if abs(dd) >= disp_jump and abs(ds) <= bbox_tol:
            out.append((f, g, dd, ds))
    return out


def merge(evts, window):
    """重なり合う候補を 1 つの事象にまとめる。

    戻り値は (区間開始, 区間終了, 代表窓の開始, 代表窓の終了, Δdisp, Δs 比)。
    **Δdisp / Δs は「代表窓」の値であって区間全体の値ではない。**
    区間の端どうしを引いても一致しないので、検証するときは代表窓のほうを見ること。
    """
    merged = []
    for f, g, dd, ds in sorted(evts):
        if merged and f <= merged[-1][1]:
            s0, s1, rf, rg, rdd, rds = merged[-1]
            if abs(dd) > abs(rdd):
                rf, rg, rdd, rds = f, g, dd, ds
            merged[-1] = (min(s0, f), max(s1, g), rf, rg, rdd, rds)
        else:
            merged.append((f, g, f, g, dd, ds))
    return merged


def main():
    p = argparse.ArgumentParser()
    p.add_argument("bundles", nargs="+")
    p.add_argument("--window", type=int, default=6)
    p.add_argument("--disp-jump", type=float, default=0.02)
    p.add_argument("--bbox-tol", type=float, default=0.02)
    p.add_argument("--metric", choices=("height", "area"), default="height")
    p.add_argument("--top", type=int, default=15)
    p.add_argument("--track", type=int, default=None)
    p.add_argument("--used", action="store_true")
    p.add_argument("--skip-edge-touch", action="store_true",
                   help="bbox が画面端に接するフレームを除く。bbox が切れていると "
                        "「大きさが変わらない」の前提が崩れるため。**11.0% 等の既定値とは "
                        "比較できない別の数字**なので、診断用に分けて見ること")
    a = p.parse_args()
    metric_idx = 1 if a.metric == "height" else 2

    for path in a.bundles:
        series, fps, source, b = load_series(path, a.used)
        print("=" * 78)
        print(path)
        print("  値: %s / bbox: %s / window=%d フレーム (%.2f 秒) / disp_jump=%.3f bbox_tol=%.3f"
              % (source, a.metric, a.window, a.window / fps, a.disp_jump, a.bbox_tol))
        for tid in sorted(series):
            if a.track is not None and tid != a.track:
                continue
            frames = series[tid]
            if a.skip_edge_touch:
                frames = {k: v for k, v in frames.items() if not v[4]}
            if len(frames) < a.window + 2:
                continue
            keys = sorted(frames)
            srcs = {}
            for k in keys:
                srcs[frames[k][3]] = srcs.get(frames[k][3], 0) + 1
            disp = [frames[k][0] for k in keys]
            step = [abs(disp[i + 1] - disp[i]) for i in range(len(disp) - 1)]
            evts = merge(events(frames, a.window, a.disp_jump, a.bbox_tol, metric_idx), a.window)
            covered = sum(e[1] - e[0] for e in evts)
            print()
            print("  track %d: %d フレーム / 1-anchor_z のレンジ %.3f-%.3f / 隣接フレーム差の中央値 %.4f・p99 %.4f"
                  % (tid, len(keys), min(disp), max(disp), st.median(step),
                     sorted(step)[int(len(step) * 0.99)] if step else 0.0))
            print("    anchor 由来: %s"
                  % ", ".join("%s %d 件" % (k, v) for k, v in sorted(srcs.items(), key=lambda kv: -kv[1])))
            if "animal_camera_root" in srcs:
                print("    ※ animal_camera_root は depth map 由来ではない（AniMer のカメラ空間 root Z）。"
                      "検出は有効だが depth 側の対処では直らない")
            print("    矛盾事象 %d 件 / 対象フレームの %.1f%% を覆う"
                  % (len(evts), 100.0 * covered / max(1, len(keys))))
            if not evts:
                continue
            print("    %-13s %-13s %-13s %8s %8s %8s %9s"
                  % ("事象の区間", "代表窓", "代表窓の秒", "Δ手前", "Δbbox", "Δz/z", "描画高"))
            print("    （Δ 列は代表窓の値。事象の区間の端どうしを引いた値ではない。"
                  "Δ手前 = −Δanchor_z で、正なら手前に寄った）")
            for s0, s1, f, g, dd, ds in sorted(evts, key=lambda e: -abs(e[4]))[:a.top]:
                z0, z1 = 1.0 - frames[f][0], 1.0 - frames[g][0]
                dz = (z1 - z0) / z0 if z0 else float("nan")
                grow = (z0 / z1) if z1 else float("nan")
                print("    %-13s %-13s %-13s %+8.4f %+7.1f%% %+7.1f%% %8.2f倍"
                      % ("%d-%d" % (s0, s1), "%d-%d" % (f, g),
                         "%.2f-%.2f" % (f / fps, g / fps), dd, 100 * ds, 100 * dz, grow))
        print()


if __name__ == "__main__":
    main()
