# -*- coding: utf-8 -*-
"""shot 先頭の boneRatio を測る。**配置の正しさはここで見る。**

全区間の中央値を 1.0 と比べてはいけない。⑨ がスケールを合わせるのは shot 先頭の
1 フレームだけで、その後は姿勢で boneRatio が 1.0〜2.2 動く。全区間中央値 1.2 前後は
設計どおり（docs/bundle-placement.md）。

使い方: python shotstart_boneratio.py <bundle 名> <ログ...>
"""
import io
import json
import re
import statistics
import sys
import zipfile

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

SA = r"C:/Users/y9800/Unity_project/VisionGraft/Assets/StreamingAssets/"
PAT = re.compile(r"\[PLACE\] f=(\d+) track=(\d+) (\S+) .*?boneRatio=([-\d.]+)")


def shot_starts(bundle):
    m = json.loads(zipfile.ZipFile(SA + bundle).read("manifest.json"))
    s = set()
    for sh in (m.get("shots") or []):
        s.update(range(int(sh[0]), int(sh[0]) + 3))
    return s


def q(v, p):
    v = sorted(v)
    return v[min(len(v) - 1, int(len(v) * p))]


def report(path, starts):
    seen = {}
    for line in io.open(path, encoding="utf-8", errors="replace"):
        m = PAT.search(line)
        if m:
            seen[(int(m.group(1)), int(m.group(2)))] = (
                int(m.group(1)), m.group(3), float(m.group(4)))
    print("=== %s" % path)
    for cat in ("Person", "Animal"):
        rows = [r for r in seen.values() if r[1] == cat]
        if not rows:
            continue
        for label, sel in (("shot 先頭 ★", [r for r in rows if r[0] in starts]),
                           ("全区間     ", rows)):
            v = [r[2] for r in sel]
            if not v:
                continue
            out = 100.0 * sum(1 for x in v if abs(x - 1.0) > 0.15) / len(v)
            print("   %-7s %s n=%-5d med=%.3f  p10=%.3f p90=%.3f  ±15%%外=%.1f%%"
                  % (cat, label, len(v), statistics.median(v), q(v, .1), q(v, .9), out))
    print()


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    st = shot_starts(sys.argv[1])
    for p in sys.argv[2:]:
        report(p, st)
