# -*- coding: utf-8 -*-
"""Screen Dist を変えたバッチログ 2 本を突き合わせ、⑧ の深度解を検証する。

使い方:
    python screendist_depth_check.py <1.0m のログ> <3.0m のログ>

読むタグ:
  [DEPTH8]   track= anchorZ= before= ratio= afterRatio= final= screenMoved=mm
             （修正後は rootZ= bodyMinusRoot=mm も付く）
    before     ⑧ が深度として扱う値。**修正後は体（Hips）、修正前は root**
    ratio      順序クランプ・平滑化を通した後の倍率
    afterRatio before * ratio * projectedDepthScaleK（クランプ前の解）
    final      Clamp(afterRatio, MinDistanceFromHead, screenDist - 0.0001)
  [HIPSTAGE] stage= rootZ= bodyZ= gap=mm    root と体がどの段で離れるか
  [ROOTDIAG] rootZ= boneMin= boneMax= scale= localHips=(x, y, z)
  [PLACE]    boneRatio=                     最終的な当たり具合（1.0 が正しい）

合格条件（2026-09-05 に決めたもの）:
  1. ratio がどちらの距離でも 1 前後
  2. 3.0m で順序クランプが効かない（下限 < ratio）
  3. [PLACE] boneRatio が 3.0m でも 0.87〜1.06 に入る
"""
import io
import re
import statistics
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

D8 = re.compile(r"\[DEPTH8\] track=(\d+) anchorZ=([-\d.]+) before=([-\d.]+) "
                r"ratio=([-\d.]+) afterRatio=([-\d.]+) final=([-\d.]+) "
                r"screenMoved=([-\d.]+)mm(?: rootZ=([-\d.]+) bodyMinusRoot=([-+\d.]+)mm)?")
HS = re.compile(r"\[HIPSTAGE\] f=(\d+) track=(\d+) stage=(\S+) rootZ=([-\d.]+) "
                r"bodyZ=([-\d.]+) gap=([-+\d.]+)mm anchorZ=([-\d.]+) scale=([-\d.]+)")
RD = re.compile(r"\[ROOTDIAG\] f=\d+ rootZ=([-\d.]+) boneMin=([-\d.]+) boneMax=([-\d.]+) "
                r".*?scale=([-\d.]+) localMeshC=\([^)]*\) localHips=\(([-\d.]+), ([-\d.]+), ([-\d.]+)\)")
PL = re.compile(r"\[PLACE\] f=(\d+) track=(\d+) (\S+) .*?boneRatio=([-\d.]+)")


def med(v):
    return statistics.median(v) if v else float("nan")


def q(v, p):
    v = sorted(v)
    return v[min(len(v) - 1, int(len(v) * p))] if v else float("nan")


def load(path):
    d8, hs, rd, pl = [], {}, [], {}
    for line in io.open(path, encoding="utf-8", errors="replace"):
        m = D8.search(line)
        if m:
            d8.append(m.groups())
            continue
        m = HS.search(line)
        if m:
            hs.setdefault(m.group(3), []).append(
                (float(m.group(4)), float(m.group(5)), float(m.group(6)), float(m.group(7))))
            continue
        m = RD.search(line)
        if m:
            rd.append(tuple(float(x) for x in m.groups()))
            continue
        m = PL.search(line)
        if m:
            pl[(int(m.group(1)), int(m.group(2)))] = (m.group(3), float(m.group(4)))
    return d8, hs, rd, pl


def report(path):
    d8, hs, rd, pl = load(path)
    print("=== %s" % path)

    if hs:
        print("  -- root と体の乖離（段ごと） --")
        for stage in sorted(hs):
            v = hs[stage]
            print("     %-12s rootZ=%.3f bodyZ=%.3f gap=%+.0fmm anchorZ=%.3f  n=%d"
                  % (stage, med([x[0] for x in v]), med([x[1] for x in v]),
                     med([x[2] for x in v]), med([x[3] for x in v]), len(v)))

    if d8:
        person = [x for x in d8 if x[0] == "0"]
        v = person or d8
        before = [float(x[2]) for x in v]
        ratio = [float(x[3]) for x in v]
        after = [float(x[4]) for x in v]
        final = [float(x[5]) for x in v]
        clamped = sum(1 for x in v if abs(float(x[6])) > 0.5)
        gaps = [float(x[8]) for x in v if x[8]]
        print("  -- ⑧ (track 0) n=%d --" % len(v))
        print("     anchorZ  med=%.3f" % med([float(x[1]) for x in v]))
        print("     ⑧ が見る深度 med=%.3f  (p10 %.3f / p90 %.3f)" % (med(before), q(before, .1), q(before, .9)))
        if gaps:
            print("     体 - root   med=%+.0fmm" % med(gaps))
        print("     倍率      med=%.3f  (p10 %.3f / p90 %.3f)   ★ 1.0 付近が正常" % (med(ratio), q(ratio, .1), q(ratio, .9)))
        print("     解        med=%.3f   → final med=%.3f" % (med(after), med(final)))
        print("     クランプ発動 %.1f%%   ★ 0%% に近いのが正常" % (100.0 * clamped / len(v)))

    if rd:
        print("  -- リグ（剛体かの確認） --")
        print("     scale med=%.4f  localHips.z med=%.3f  span/scale med=%.3f"
              % (med([x[3] for x in rd]), med([x[6] for x in rd]),
                 med([(x[2] - x[1]) / x[3] for x in rd])))

    if pl:
        for cat in ("Person", "Animal"):
            v = [b for (c, b) in pl.values() if c == cat]
            if v:
                out = 100.0 * sum(1 for x in v if not 0.87 <= x <= 1.06) / len(v)
                print("  -- [PLACE] %s boneRatio med=%.3f (p10 %.3f / p90 %.3f)  0.87〜1.06 外 %.1f%%"
                      % (cat, med(v), q(v, .1), q(v, .9), out))
    print()


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    for p in sys.argv[1:]:
        report(p)
