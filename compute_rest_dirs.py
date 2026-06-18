import json
import numpy as np

with open('Docs/smal-rest-skeleton.json') as f:
    skel = json.load(f)
J = {j['id']: np.array(j['position']) for j in skel['joints']}
pairs = [
    ('LLeg1->LLeg2', 7, 8), ('LLeg2->LLeg3', 8, 9), ('LLeg3->LFoot', 9, 10),
    ('RLeg1->RLeg2', 11, 12), ('RLeg2->RLeg3', 12, 13), ('RLeg3->RFoot', 13, 14),
    ('LLegBack1->LLegBack2', 17, 18), ('LLegBack2->LLegBack3', 18, 19), ('LLegBack3->LFootBack', 19, 20),
    ('RLegBack1->RLegBack2', 21, 22), ('RLegBack2->RLegBack3', 22, 23), ('RLegBack3->RFootBack', 23, 24),
    ('root->Neck', 0, 15), ('Neck->Head', 15, 16),
    ('root->Tail1', 0, 25), ('Tail1->Tail2', 25, 26),
]
for name, a, b in pairs:
    d = J[b] - J[a]
    n = d / np.linalg.norm(d)
    print(f'{name}: dir=({n[0]:.6f}f, {n[1]:.6f}f, {n[2]:.6f}f)')
