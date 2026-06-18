import json
import numpy as np

with open('Docs/smal-rest-skeleton.json') as f:
    skel = json.load(f)
J = {j['id']: np.array(j['position']) for j in skel['joints']}
d = J[32] - J[16]
n = d / np.linalg.norm(d)
print(f'Head->Mouth: dir=({n[0]:.6f}f, {n[1]:.6f}f, {n[2]:.6f}f)')
