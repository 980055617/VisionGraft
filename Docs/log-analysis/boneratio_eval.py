# -*- coding: utf-8 -*-
"""配置の良し悪しを **2 つの指標で同時に** 出す。

2026-09-08 の反省: ⑧ を直したとき shot 先頭の boneRatio だけを見て採否を決め、
走り全体では悪化しているのを見落とした（中央値 1.122 → 1.222）。
**片方だけ見ない。** この 2 つを必ず並べる。

  1. shot 先頭の boneRatio  … 設計目標は 1.000
  2. 走り全体の分布        … 中央値・四分位・最大。頭の飛び出し（上端差）も

使い方:
  python boneratio_eval.py <log> [<log> ...]
"""
import re
import statistics as st
import sys

RX = re.compile(
    r"\[PLACE\] f=(\d+) track=(\d+) Person sizeRatio=([\d.]+).*?"
    r"boneRatio=([\d.]+) boneTopDelta=(-?[\d.]+) boneBottomDelta=(-?[\d.]+)")

# [KP2D] からの身長比。**boneRatio とは分母が違う。**
#   boneRatio = モデルの頭→つま先 ÷ bbox 高
#   身長比    = モデルの首→足     ÷ 元映像の keypoint の首→足
# bbox の上端は髪の上まで含み、モデルの「頭」は頭蓋の中の関節なので、
# boneRatio を 1.0 に合わせるとモデルの首が上がる。ユーザーが見ているのは後者。
RX_KP_HEAD = re.compile(r"\[KP2D\] f=(\d+) track=(\d+) bboxH=(\d+)")
RX_KP_PART = re.compile(r"(\w+)=(-?[\d.]+),(-?[\d.]+),(-?[\d.]+),(-?[\d.]+)")


def load_height(path):
    """フレーム -> (bboxH, モデルの首→足 ÷ 元映像の首→足)。"""
    out = {}
    with open(path, "rb") as fh:
        for raw in fh:
            line = raw.decode("utf-8", "replace")
            m = RX_KP_HEAD.search(line)
            if not m:
                continue
            p = {}
            for a, b, c, d, e in RX_KP_PART.findall(line):
                if a != "f":
                    p[a] = (float(b), float(c), float(d), float(e))
            if not {"Neck", "RFoot", "LFoot"} <= set(p):
                continue
            src = max(p["RFoot"][1], p["LFoot"][1]) - p["Neck"][1]
            mdl = max(p["RFoot"][3], p["LFoot"][3]) - p["Neck"][3]
            if src > 1:
                out[int(m.group(1))] = (int(m.group(3)), mdl / src)
    return out


# ⑧ が「勝手に作り出した」前後移動。bundle の anchorZ はその人が実際に動いた量で、
# 立っているだけなら 10cm 程度しか動かない。⑧ の確定深度がそれより大きく振れていたら、
# その差は姿勢の誤差を距離に変換してしまったぶん（2026-09-08 実測: bundle 0.118m に対し
# ⑧ 0.750m）。**実機で「急にサイズが変わる」と見えるのはこれ。**
RX_D8_F = re.compile(r"\[PLACE\] f=(\d+) track=0 Person")
RX_D8 = re.compile(r"\[DEPTH8\] track=0 anchorZ=(-?[\d.]+) before=([\d.]+) "
                   r"ratio=([\d.]+) afterRatio=([\d.]+) final=([\d.]+)")


def load_depth(path):
    cur, out = None, {}
    with open(path, "rb") as fh:
        for raw in fh:
            line = raw.decode("utf-8", "replace")
            mf = RX_D8_F.search(line)
            if mf:
                cur = int(mf.group(1))
            m = RX_D8.search(line)
            if m and cur is not None:
                out[cur] = (float(m.group(1)), float(m.group(5)))
    return out


# 案 D（大きさをスケールで払う）を入れると、跳ねは深度ではなく **スケール** に出る。
# 深度だけ見ていると「直った」と誤読するので、両方出す。
RX_SCALE = re.compile(r"\[PLACE\] f=(\d+) track=0 Person .*?scale=([\d.]+)")


def load_scale(path):
    out = {}
    with open(path, "rb") as fh:
        for raw in fh:
            m = RX_SCALE.search(raw.decode("utf-8", "replace"))
            if m:
                out[int(m.group(1))] = float(m.group(2))
    return out


def load(path):
    """フレームごとに最後の 1 行を採る（バッチは同じフレームを何度も出す）。"""
    last = {}
    with open(path, "rb") as fh:
        for raw in fh:
            m = RX.search(raw.decode("utf-8", "replace"))
            if m:
                last[int(m.group(1))] = (
                    float(m.group(4)), float(m.group(5)), float(m.group(6)))
    return last


def shot_starts(frames, gap=10):
    """フレーム番号が gap 以上飛んだところを shot の先頭とみなす。

    バッチのログは連番とは限らないので、これは近似。**厳密な shot 境界が要るなら
    manifest の shots を読むこと。**ここでは「走り全体」との対比が目的。
    """
    out, prev = [], None
    for f in frames:
        if prev is None or f - prev > gap:
            out.append(f)
        prev = f
    return out


def report(path):
    d = load(path)
    if not d:
        print(f"{path}: [PLACE] の Person 行が無い")
        return
    fs = sorted(d)
    br = [d[f][0] for f in fs]
    top = [abs(d[f][1]) for f in fs]
    starts = shot_starts(fs)
    sbr = [d[f][0] for f in starts]

    q = st.quantiles(br, n=4) if len(br) >= 4 else [float("nan")] * 3
    print(f"--- {path}")
    print(f"  フレーム数={len(fs)}  範囲={fs[0]}..{fs[-1]}")
    print(f"  [1] shot 先頭 boneRatio  n={len(sbr)} "
          f"中央={st.median(sbr):.3f} 値={[f'{v:.3f}' for v in sbr[:8]]}")
    print(f"  [2] 走り全体 boneRatio   中央={st.median(br):.3f} "
          f"Q1={q[0]:.3f} Q3={q[2]:.3f} 最小={min(br):.3f} 最大={max(br):.3f}")
    print(f"      |上端差|            中央={st.median(top):.1f}px 最大={max(top):.1f}px")

    h = load_height(path)
    if h:
        near = [v for _, (b, v) in h.items() if b >= 350]   # 人物が大きく写る区間
        allv = [v for _, (b, v) in h.items()]
        print(f"  [3] 身長比（首→足）     全体 中央={st.median(allv):.3f} "
              f"最大={max(allv):.3f}")
        if near:
            print(f"      うち bboxH>=350     n={len(near)} 中央={st.median(near):.3f} "
                  f"範囲={min(near):.3f}..{max(near):.3f}  ← 実機で見ている区間")

    dep = load_depth(path)
    if dep:
        dfs = sorted(dep)
        az = [dep[f][0] for f in dfs]
        fin = [dep[f][1] for f in dfs]
        pairs = [(a, b) for a, b in zip(dfs, dfs[1:]) if b - a == 1]
        step = [abs(dep[b][1] - dep[a][1]) for a, b in pairs] or [0.0]
        astep = [abs(dep[b][0] - dep[a][0]) for a, b in pairs] or [0.0]
        print(f"  [4] 前後の動き  bundle(anchorZ) 幅={max(az)-min(az):.3f}m "
              f"1frame最大={1000*max(astep):.0f}mm")
        print(f"      ⑧ が確定した深度 幅={max(fin)-min(fin):.3f}m "
              f"1frame最大={1000*max(step):.0f}mm  "
              f"← bundle の {(max(fin)-min(fin))/max(max(az)-min(az), 1e-6):.1f} 倍")

    sc = load_scale(path)
    if sc:
        sfs = sorted(sc)
        sv = [sc[f] for f in sfs]
        sp = [(a, b) for a, b in zip(sfs, sfs[1:]) if b - a == 1]
        sstep = [abs(sc[b] - sc[a]) / max(sc[a], 1e-6) for a, b in sp] or [0.0]
        print(f"  [5] スケールの揺れ  幅={min(sv):.4f}..{max(sv):.4f}"
              f"（{100*(max(sv)/max(min(sv),1e-6)-1):.1f}%）"
              f"  1frame最大={100*max(sstep):.2f}%")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        raise SystemExit(1)
    for p in sys.argv[1:]:
        report(p)
