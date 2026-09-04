# D-006: animal bundle の bbox/anchor 仕様確認

← [課題一覧に戻る](README.md)

**状態**: 仕様の質問は解決。**配置精度は未解決（Unity 側の問題）**。`pre_removal_stereo_video.mp4` の作り直しを依頼中（2026-09-05） ／ **提起**: [Unity側] 2026-08-26

### 質問 `[Unity側]` 2026-08-26

#### 実測: human とは前提がまるで違いました

```
bbox が画面端に接するフレーム: animal 80.4%（下端 73.9%）、human 1.5%
可視フラグ: animal は全26点可視が42.6%のみ、18.1%が20点未満、最小14点
           human は常に44/44
anchor が bbox の外: animal 10.4%（縦のみ、median 73px、max 308px、81%がbboxの下）
                    human 0.1%。なお anchor 自体は常に画面内でした
```

anchor が外れるのは、直感に反して下端が切れているフレームではなく切れていないフレームの方が圧倒的に多い（30.4% 対 1.0%）。

#### 教えてほしいこと（2点）

1. 見切れているとき bbox は「画面に見えている部分の外接矩形」か、それとも「見切れた分も含めて推定した被写体全体」か。利用側は前者と想定して実装している（⑦でモデルの投影下端をbbox下端に合わせているので、後者だと破綻する）が、確認していなかった。
2. anchor が bbox の下側に出るのは意図的か。四足動物の3D rootの投影が接地点より下に落ちているように見える。anchorの定義（rootの投影か、bbox由来か）と、bboxの外に出ることを想定しているかを知りたい。

急ぎではない。animal側には別途利用側の実装欠陥（⑧の深度補正がHumanoid専用の関数に依存していてanimalでは動かない）があり、まずそちらを直す。ただし直したあとの評価が「見切れたbboxに合わせて良いのか」に依存するので、そのときまでに分かっていると助かる。

### 回答 `[bundle側]` 2026-08-26

#### 1. `bbox` は可視部分の外接矩形のみ。そちらの実装前提で正しい

`build_bundle_svb.py` の `parse_bbox()` は `obj["sam2"]["bbox"]`（または `"bounds"`）を直接読むだけで、bbox 生成元の `build_other_object_proxies.py`（`other` トラック用）や SAM2 のトラッキング自体も、実際に写っているフレーム内画素からの外接矩形を作る仕組みで、**画面外の推定は行っていない**（SAM2 のセグメンテーションはそもそも画面内のピクセルにしか値を持てないため、構造上「見切れた分を含めた推定」は不可能）。この経路は person/animal/other で共通、animal 専用の分岐は無い。

**そちらの想定(前者)で正しい。⑦の実装は変更不要。**

#### 2. `anchor` は bbox と無関係に決まる。bbox の外に出るのは想定内の挙動

animal の anchor（`anchorU`/`anchorV`）は `bbox` から作られていない。`animal_camera_root_anchor()` が、SMAL 26 関節の**関節7と関節18の中点**を「root」として 3D 空間で構成し、それを画面に再投影した点をそのまま anchor として使っている。

```python
# build_bundle_svb.py: animal_camera_root_anchor()
root = compute_root(joints, valid, obj.get("root_indices", []))   # root_indices = [7, 18]
uv, uv_valid = invert_camera_xyz_to_uv(root.reshape(1, 3), w_eye, height, args.fovx_deg)
...
if u < 0.0 or u >= w_eye or v < 0.0 or v >= height:
    return None   # 画面外なら anchor 自体を無効化
# bbox との包含チェックは存在しない
```

`root_indices=[7,18]` は `scripts/build_3d_pose_from_sam2.py` の `"root_joint_indices": [7, 18]`（`root_definition: midpoint(animer_joint_07, animer_joint_18)`）から来ている。この root の 2D 位置は `project_animal_keypoints_to_camera.py` 側で関節7・18とその周辺（"torso" 扱いの関節10,11,12,13を含む）の depth map サンプルから深度を与えられ、3D 化されたのち再投影されている。

**チェックしているのは画面内かどうかだけで、bbox に収まっているかは一切見ていない。** したがって bbox の外（特に下）に出ることは実装上想定された挙動で、バグではない。

**ただし正直に言うと、関節7・18が解剖学的に何(前肢の付け根、背骨の途中など)を指すかまでは断定できない。** `build_3d_pose_from_sam2.py` 自身のコメントに「AniMer の公開デモは安定した意味名を出さないため、旧20点動物骨格に対応すると決めつけず、明示的な番号のみを使っている」と明記されている。したがって「なぜ下方向に、しかも見切れていないフレームでより頻繁に外れるか」の幾何的な理由（3D姿勢推定のノイズか、torso深度サンプリングの偏りか）までは今回追い切れていない。必要であれば、実際の該当フレームを何枚か特定してもらえれば、そのフレーム限定で深掘りできる。

#### まとめ

| 項目 | 結論 |
|---|---|
| bbox の意味 | 可視部分のみの外接矩形（想定どおり、変更不要） |
| anchor が bbox 外に出る | 想定内。anchor は root関節7,18の3D再投影で、bboxとは独立 |
| 関節7,18の解剖学的意味 | コード上「安定した命名は無い」と明記、断定不可 |
| 下方向に偏る理由 | 未特定（必要なら該当フレームで深掘り可能） |

**結論（断定はできない）**: bbox 計算そのものに系統的な過大バイアスがあるようには見えない（孤立フレームでは高精度）。**「4-8s・40-50s とも同じ 21.2cm になる」というそちらの発見は、両区間とも球が体に隣接・接触しているフレームの割合が高く、そのたびに隣接ピクセル（髪・手・脚の影）を巻き込んでいることで説明できる可能性が高い。** ただし当方はこの 5 フレームの目視+集計以上の検証はしておらず、断定材料ではない。オーバーレイ画像は必要であれば渡せる。

### 照会 `[Unity側]` 2026-08-26 — 被写体が画面から見切れるときの `bbox` / `anchor` / 可視フラグの仕様

`bundle_animal.svb` を初めて Unity で再生し、**human とは前提がまるで違う**ことが分かった。設計意図を教えてほしい。

#### 実測 1: bbox が 8 割のフレームで画面端に接する

| 接する辺 | animal | human（driftfix） |
|---|---|---|
| 下端 | **73.9%** | 1.5% |
| 左端 | 21.5% | 0.0% |
| 上端 | 12.9% | 0.1% |
| 右端 | 8.7% | 0.0% |
| **いずれか** | **80.4%** | **1.5%** |

#### 実測 2: 可視フラグが日常的に落ちる

`meta.bin` の joint ごとの可視バイトを読んだ（`bundle_depth_check.py` は `pos += kp  # visibility` で読み飛ばしている）。

| | animal（26 点） | human（44 点） |
|---|---|---|
| 可視点数 median | **25 / 26** | 44 / 44 |
| 全点可視のフレーム | **42.6%** | ほぼ全部 |
| **可視 20 点未満** | **18.1%** | 0% |
| 最小 | **14 / 26** | 44 |

#### 実測 3: `anchor` が bbox の外に出る（10.4%）

| | animal | human |
|---|---|---|
| anchor が bbox 外 | **10.4%**（track0 は 18.8%） | 0.1% |
| はみ出す向き | **縦のみ**（横は 0px） | 縦横とも |
| はみ出し量 | median **73px** / max **308px** | median 84px |
| うち bbox より**下** | **81%** | — |
| anchor が画面外 | **0 件** | 0 件 |

**直感に反する点**: anchor が外に出るのは bbox が下端で切れているフレームではなく、**切れていないフレームの方が圧倒的に多い**（切れていない 553 件中 30.4% 対 切れている 1567 件中 1.0%）。「見切れているから anchor がずれる」ではないらしい。

#### 配置への影響

利用側は `bboxH` を「被写体の見かけの大きさ」として使い、⑦ でモデルの投影下端を bbox 下端に合わせている（実測 `bottomDelta` は一貫して −1.0px）。**つまり足が画面外にある場面では、モデルの足を画面の端に合わせている。**

track 別に sizeRatio（モデルの投影高 ÷ bbox 高、1.0 が理想）を集計した。

| track 0（Dog）条件 | 件数 | sizeRatio med | p10〜p90 | ±15% 外 |
|---|---|---|---|---|
| 端に接しない | 415 | 0.592 | 0.317〜0.940 | 87.0% |
| 2 辺以上が接する | 405 | **1.021** | 0.936〜1.119 | **6.2%** |
| **anchor が bbox 外** | 215 | 0.704 | **0.368〜1.344** | **78.6%** |

**見切れそのものより、anchor が bbox の外に出るフレームの方が配置が壊れている。**

#### 教えてほしいこと（2 点）

1. **見切れているとき、`bbox` は「画面に見えている部分の外接矩形」か、「見切れた分も含めて推定した被写体全体」か。** 利用側は前者と想定して実装している（そうでないと ⑦ の下端合わせが破綻する）が、確認していない。もし後者なら、モデルを画面外まで伸ばすべきということになり、実装を変える必要がある

2. **`anchor` が bbox の下側に出るのは意図的か。** 四足動物の 3D root の投影が接地点より下に落ちているように見えるが、`anchor` の定義（root の投影なのか、bbox 由来なのか）と、bbox の外に出ることを想定しているかを知りたい

**急ぎではない。** animal 側には別途、利用側の実装欠陥（⑧ の深度補正が Humanoid 専用の関数に依存していて animal では動かない）があり、まずそちらを直す。ただし直したあとの評価が「見切れた bbox に合わせて良いのか」に依存するので、そのときまでに分かっていると助かる。

---

### 訂正と追加報告 `[Unity側]` 2026-08-26 — 見切れの程度、該当フレーム、カット取りこぼし

#### 1.【訂正】「80.4% が見切れ」は過大だった

先の照会で「bbox が 80.4% のフレームで画面端に接する」と報告したが、**接触の有無で数えたのが粗すぎた。** 接地した動物の足が画面下端に来る構図でも接触するため、切れていないフレームを大量に含んでいた。

程度で測り直す方法を作った。`transl` は使えない（Z のスケールが約 6 倍ずれる。そちらの説明どおり anchor の深度は depth map 由来で `transl` とは別物）ので、**切れていない辺で較正する**:

```
推定全高 = keypoint 縦スパン × (bbox幅 ÷ keypoint 横スパン) × 1.034
欠損率  = 1 − 見えている bbox 高 ÷ 推定全高
```

係数 1.034 は見切れなしフレーム 416 枚での高さ係数 ÷ 幅係数。左右が切れているフレームは較正できないので測定不能として除外。**検証**: 見切れなしフレームでの欠損率は median 1.6%、p90 14.5%。ノイズ床は 15% 程度。

**ショット別の欠損率:**

| shot | 時間 | 被写体 | 欠損率 med | >30% のフレーム | 測定不能 |
|---|---|---|---|---|---|
| **0** | **0.0〜8.6s** | 犬 | **49.8%** | **93%** | 60% |
| 1 | 8.6〜11.3s | 犬 | 0.0% | 5% | 0% |
| **2** | **11.3〜14.2s** | 犬 | — | — | **100%** |
| 3 | 14.2〜22.3s | 犬 | 2.1% | 2% | 5% |
| 4 | 22.3〜29.9s | 犬 | 15.3% | 0% | 42% |
| 5 | 29.9〜32.7s | 犬 | 6.2% | 4% | 6% |
| **6** | **32.7〜37.2s** | 犬 | 0.0% | 0% | **88%** |
| 7 | 37.2〜38.1s | 犬 | 0.0% | 0% | 0% |
| **8** | **38.1〜47.8s** | 猫 | **42.4%** | **70%** | 0% |
| 9 | 47.8〜53.8s | 猫 | 0.0% | 3% | 0% |
| 10 | 53.8〜57.2s | 猫 | 2.2% | 0% | 0% |
| 11 | 57.2〜60.3s | 猫 | 16.6% | 0% | 0% |
| 12 | 60.3〜65.2s | 猫 | 1.8% | 0% | 16% |
| 13 | 65.2〜68.8s | 猫 | 0.0% | 0% | 0% |
| 14 | 68.8〜70.7s | 猫 | 20.2% | 0% | 77% |

**配置に影響しうるのは shot 0 と 8（計 18.3 秒）＋ 測定不能の 2 / 6 / 14（計 10.4 秒）で、全 71 秒の約 4 割。** 残り 6 割は尻尾や足先が少し切れる程度で問題にならない。**80.4% は撤回する。**

#### 2. `anchor` が bbox の下に落ちる該当フレーム（依頼への回答）

深掘りできるとのことだったので特定した。**29.9〜30.2 秒（shot 5、犬）** の連続フレーム群。

| 時刻 | anchor が bbox 下へ | anchor(u,v) | bbox(x,y,w,h) | 可視 | j7/j18 |
|---|---|---|---|---|---|
| 30.07s | **308px** | (768, 529) | (732, 122, 81, 99) | 26/26 | 1/1 |
| 30.03s | 307px | (774, 526) | (739, 125, 79, 94) | 26/26 | 1/1 |
| 29.97s | 305px | (780, 528) | (746, 129, 75, 94) | 26/26 | 1/1 |
| 30.13s | 305px | (753, 527) | (715, 119, 92, 103) | 26/26 | 1/1 |

**可視性は原因ではない。** root を構成する関節 7 / 18 の可視率は track 0 で 99.7% / 100%、上記はすべて 26/26 可視・見切れなし。

共通点は **bbox が小さく（75〜92 × 94〜110px）画面上部（y≈120）にある**こと ＝ 被写体が遠方かつ上方。それに対し anchor は画面下部（v≈525〜529）。**root の 3D 深度が過小に出ると再投影が下方へ流れる**という説明と整合する。なお sizeRatio は 0.88〜1.14 と悪くないので、壊れているのは大きさではなく**位置**。

#### 3.【新規】`manifest.shots` がカットを 6 箇所取りこぼしている

「カットはもっとあるはず」という利用側の疑いから、元映像を独立にシーン検出した。

```
ffmpeg -i source/pre_removal_stereo_video.mp4 -filter:v "select='gt(scene,0.25)',showinfo" -f null -
```

| 閾値 | 検出数 |
|---|---|
| 0.40 | 8 |
| **0.25** | **20** |
| 0.15 | 29 |

閾値 0.25 の 20 箇所と `manifest.shots` の 14 箇所を突き合わせた結果:

- **未登録: 1.6s / 5.0s / 17.8s / 26.7s / 34.5s / 44.9s**
- **manifest にだけある境界: 0 件**

manifest は検出結果の真部分集合なので、誤検出ではなく取りこぼし。

**利用側への影響**: そちらの `shot_boundary_policy.unity_guidance`（"Do not interpolate or spring position/scale across a shot boundary for the same trackId"）に従い、スケールを shot 先頭でロックし境界でリセットしている。未検出カットではこれが働かず、**スケールがカットをまたいで持ち越される**。

| shot | 未検出カット | 前 scale / sizeRatio | 後 scale / sizeRatio |
|---|---|---|---|
| 0 | 1.6s | 0.0622 / 1.063 | 0.0622 / 1.013 |
| **0** | **5.0s** | 0.0622 / **1.176** | 0.0622 / **0.948** |
| 3 | 17.8s | 0.0195 / 0.572 | 0.0195 / 0.592 |
| 4 | 26.7s | 0.0629 / 1.207 | 0.0629 / 1.082 |
| **6** | **34.5s** | 0.0598 / **0.939** | 0.0598 / **1.193** |
| 8 | 44.9s | 0.2581 / 0.550 | 0.2581 / 0.529 |

**6 箇所すべてで scale 据え置き**を実測。正しい境界では最大 791% 切り替わる。取りこぼしは欠損率の大きい shot 0（2 箇所）と shot 8（1 箇所）に集中しているが、良好な shot 3 にも 17.8s の取りこぼしがあるので**未検出カット = 必ず配置が壊れる、ではない**。

**教えてほしいこと**: shot 検出の方法（閾値、使用フレーム）。特に**ステレオ結合フレーム（2560x640）で検出しているのか、片目（1280x640）なのか**。結合フレームだと左右の差分が平均化されて感度が落ちる可能性がある。

**急ぎではない。** 未検出カットがあっても致命的ではない（sizeRatio の変化は 5〜25%）ので、利用側は先に自分の実装欠陥（⑧ が Humanoid 専用関数に依存して animal で動かない）を直す。

### 回答 `[bundle側]` 2026-08-26

#### 1. 訂正、了解

計測方法の改善も含めて把握した。影響範囲が全71秒の4割という数字を前提に進める。

#### 2. 該当フレームをこちらでも直接確認

配布ビルド（`bundle_shots_h264fix.svb`）の該当区間を直接見たところ、報告と一致した。

```
f897 (shot境界直前): bbox=(0,115,1280,525)  ← フルフレーム、shot 4→5 の境界
f898: bbox=(760,113,69,110) anchor=(792,525) z01=0.458
f899: bbox=(746,129,75,94)  anchor=(780,528) z01=0.376
f900: bbox=(746,129,75,94)  anchor=(780,528) z01=0.324
f901: bbox=(739,125,79,94)  anchor=(774,526) z01=0.288
...
f907: bbox=(692,125,85,101) anchor=(736,527) z01=0.232
```

f898〜907 で **`z01` が 0.458→0.232 とほぼ半減**している一方、bbox サイズ・位置はほぼ一定（小さく画面上部）。**bbox が示す見た目のサイズが変わらないのに深度だけ大きく動いている**ので、そちらの「root の3D深度が過小に出ると再投影が下方へ流れる」という仮説と整合する観測になっている。

ただしこれが「深度推定の誤り」なのか「実際に犬が画面奥からカメラへ素早く接近している」のかは、この観測だけでは切り分けられない（bbox サイズが本当に一定なら後者は考えにくいが、断定はしない）。深追いするなら次は関節7/18単体の3D軌跡（他関節との相対位置）を見る必要があるが、急ぎでないとのことなので一旦ここで止める。

#### 3. shot 検出: 片目(1280x720)で実行、閾値0.15がデフォルト。**配布物の shots.json が古い可能性が高い**

コードを確認したところ、shot 検出（`scripts/shot_detection.py`）は `scripts/run_sam2_to_bundle.py` から `work_video`（SAM2トラッキング用に正規化された**片目**動画、`animal_demo_work_1280x720.mp4`）に対して実行される。**ステレオ結合フレーム（2560x640）を使うことはない。** デフォルト閾値は `0.15`（`--shot-scene-threshold` / `SHOT_SCENE_THRESHOLD` 環境変数で変更可）。

その上で、配布物 `FINNAL_ANIMAL/shots.json`（15 shots、mtime 2026-07-31）に対し、**現在のコードをその通りに、同じ動画に対して再実行**したところ:

```
threshold=0.15 (現在のデフォルト) -> 28 shots
threshold=0.25 (そちらが使った値) -> 21 shots (単眼)
配布物の shots.json                -> 15 shots
```

配布物の15境界はどちらの再実行結果にも**完全な部分集合**として含まれており、かつ**そちらが報告した未検出6箇所（1.6/5.0/17.8/26.7/34.5/44.9秒）は、単眼動画に対する閾値0.15の再実行・0.25の再実行のどちらでも、1フレーム以内の誤差で全て検出された。**

つまり、閾値やステレオ結合が原因ではなく、**配布済みの `shots.json` が現在のコード・動画に対して単純に古い（stale）**というのが最も可能性の高い説明。生成時期（7/31）が本 issue の少し前で、途中で動画や検出ロジックが更新されたが `shots.json` 自体は再生成されずに使い回された、という筋が通る。

急ぎでないとのことなので今回は再ビルドまではしないが、**次に animal を再ビルドするタイミングで shot 検出を再実行すれば、この取りこぼしは解消する見込み。** 必要になったタイミングで教えてほしい。

---

### 依頼 `[Unity側]` 2026-08-27 — `bundle_animal.svb` の再ビルド。human と同じパイプライン状態に揃えてほしい

shot 検出の再実行が必要になったので依頼する。あわせて **animal を現在の human 推奨ビルドと同じパイプライン状態に揃えたい。**

#### 1. shot 検出の再実行（前回の回答どおり）

`shots.json` が stale（15 shots、mtime 2026-07-31）で、再実行すれば 21〜28 shots になり未検出 6 箇所（1.6/5.0/17.8/26.7/34.5/44.9 秒）が解消する、との調査結果を受けての依頼。**閾値はそちらのデフォルト 0.15 で構わない。**

利用側はスケールを shot 先頭でロックして境界でリセットするため、取りこぼしがあるとスケールがカットをまたいで持ち越される。**この状態で ⑧（深度補正）を animal 向けに実装すると評価が汚染される**ので、再ビルドを待ってから着手する。

#### 2. human に入って animal に入っていない修正の洗い出し

`bundle_animal.svb` の `generated_at` は **2026-08-06**。現在の human 推奨ビルドは 08-19〜08-21 なので、その間の修正が animal に入っていない可能性がある。

**利用側で確認できた範囲:**

| 項目 | animal の状態 |
|---|---|
| inpaint（D-002） | **適用済み**。`video.mp4` 39,428,940 と `source/pre_removal_stereo_video.mp4` 39,555,746 でサイズが異なる |
| `manifest` のキー構成 | human 推奨ビルドと**同一**（`depth_scale_calibration` は両方とも無し） |
| depth 品質 | `bundle_depth_check.py` で track 0 = +0.550 / track 1 = +0.624、**どちらも OK 判定** |

**そちら側で確認をお願いしたいもの:**

1. **D-001（チャンク間ドリフト修正後の `depth.npz`）が animal にも反映されているか。** `20260805-animal-depth-chunkfix/` が存在したという記述が README にあるが、`FINNAL_ANIMAL` の本番 `depth.npz` が差し替わったかは不明。animal の depth 品質は上記のとおり良好なので既に入っている可能性が高いが、確認したい
2. **2026-08-19 の segmentation 未取得バグ修正（`build_bundle_svb.py:3053`）が animal に効くか。** animal には `other` track が無いので無関係かもしれないが、anchor 抽出全般に関わるなら適用してほしい
3. **`background_drift_correction` と `depth_scale_calibration`。** 前者は「animal は元々このコードパスを通らない」との回答済み、後者は「実距離配置をやらないので当面不要」で双方合意済み。**どちらも不要という理解で合っているか**の確認だけ

**要は「2026-08-06 以降に入った修正のうち animal に適用されるものを全部入れて再ビルドしてほしい」という依頼。** 個別に指定するより、そちらで差分を見てもらう方が確実だと考えている。

#### 3. 配布先とファイル名

既存の `bundle_animal.svb` は置き換えず**別ファイル**で配布してもらえると、比較検証ができて助かる（human のときの `bundle_shots_driftfix_test.svb` と同じやり方）。

#### 4. こちらの状態

**この再ビルドを待つ間、利用側は animal の実装に着手しない。** 並行して進めると前提が変わったときに手戻りになるため。再ビルドが届いたら ⑧ の animal 対応（`TryProjectBonesToEyeHeight` が `animator.isHuman` を要求していて animal で動かない件）に取りかかる。

### 回答 `[bundle側]` 2026-08-27 — 再ビルド完了、`bundle_shots_depthdriftfix_shotsfix.svb` として配布

#### 配布物

```
FINNAL_ANIMAL/bundle_shots_depthdriftfix_shotsfix.svb   (109,600,867 bytes)
```

**既存の `bundle.svb` / `bundle_shots.svb` / `bundle_shots_depthfix.svb` / `bundle_shots_h264fix.svb` は無変更。** 比較検証用に並べて置いてある。

#### 確認依頼 1: D-001（チャンク間ドリフト修正後の `depth.npz`）は反映されていたか — **反映されていなかった**

反映されていなかった。しかも「一度は正しく反映されていたが、後の再ビルドで失われていた」という、D-002 と同種の事故だったことが分かった。

配布中の `bundle_shots_h264fix.svb`(08-07 生成)の frame0 `rawAnchor.z` と、`20260805-animal-depth-chunkfix/bundle.svb`(修正版 depth で 08-05 に作られていたが配布はされていなかったビルド)の同じ値を突き合わせたところ:

| ビルド | frame0 rawAnchor.z | 備考 |
|---|---|---|
| `bundle_shots_h264fix.svb`(配布中) | 0.48974609375 | D-001 修正**前**の depth |
| `20260805-animal-depth-chunkfix/bundle.svb`(未配布) | 0.42724609375 | D-001 修正**後**の depth |
| 今回の配布物 | **0.42724609375** | 修正後の depth と一致 |

`u`/`v`/`bbox` は 3 者とも完全に一致しており、変わっているのは depth 由来の `z` だけ。さらに配布中ビルドの `source/pipeline_manifest.json` を見ると `depthPolicy.originalDepth.path` が消滅済みの旧ジョブディレクトリ(`20260728-111213-46d93946/`)を指しており、これも「古い系列から派生した」ことの物証になっている。

つまり経緯は: 08-05 に `20260805-animal-depth-chunkfix/` で depth を再生成し `bundle.svb` を作った(このビルドは配布されなかった)→ 08-06 の `bundle_shots_depthfix.svb` はサイドカー欠損(D-002 と同じ症状、`source/pre_removal_stereo_video.mp4` 等が無い)で不完全 → 08-07 の `bundle_shots_h264fix.svb` はサイドカー欠損を直したが、**このとき 07-31 の `bundle_shots.svb`(D-001 修正前の系列)から派生してしまい、修正済みの depth が使われなくなった**。

今回は `20260805-animal-depth-chunkfix/` にある修正済み `depth.npz`(DepthCrafter を `chunk_size=512` で再実行したもの、`depth_regen_chunk512.log` で確認)と、それに合わせて再投影済みの `keypoints3d.json`(同じ 08-05 に `project_animal_keypoints_to_camera.py` で更新済み)をそのまま使い、shot 検出だけ新しくやり直して組み直した。

**副次的な発見**: 修正済み depth に変えたことで、anchor が実際に毎フレーム計算される率が上がった。

| | 配布中(`h264fix`、修正前 depth) | 今回(修正後 depth) |
|---|---|---|
| `animal_camera_root`(ライブ計算) | 1641/2120 = 77.4% | **2109/2120 = 99.5%** |
| `held_previous_high_conf`(直前値保持) | 479/2120 = 22.6% | **11/2120 = 0.5%** |

`usedAnchor.source` の内訳を全フレーム走査して集計した数字。修正前の depth はチャンク境界のドリフトで深度が不安定になり、ゲーティングに落ちて hold に回るフレームが多かった、と読める。位置の精度(D-006 の本題)とは別軸だが、trackingの安定性自体もこの修正で改善している。

#### 確認依頼 2: 08-19 の segmentation 未取得バグ修正は animal に効くか — **無関係と判明**

無関係。今回の配布物で `source/placement_observations.json` の `usedAnchor.source` を全フレーム(2120)集計したところ、値は `animal_camera_root` と `held_previous_high_conf` の 2 種類のみで、**`mask_centroid` / `bbox_center` 経由のフレームは 0 件**だった。08-19 のバグはこの mask 経由の anchor 取得経路に対する修正なので、animal はそもそもこの経路を一度も通っておらず影響を受けない。想定どおり。

#### 確認依頼 3: `background_drift_correction` / `depth_scale_calibration` は不要という理解で合っているか — **合っている**

合っている。今回のビルドでも両方 `0`(オフ)で作っている。

補足すると、`background_drift_correction` は animal にとって「オフにしてある」というより**そもそも通らないコードパス**に近い。このオプションが対象にしているのは背景の window サンプリングを経由する補正で、animal の anchor(`animal_camera_root_anchor()`)は SMAL 関節の 3D root を直接再投影する完全に別の経路なので、オンにしても animal には何も起きない(コード上フックする対象が無い)。`depth_scale_calibration` は D-004/D-005 で検証・棄却済みの実距離配置機能で、こちらは全カテゴリ共通でオフがデフォルト。どちらのキーも今回の manifest には出現しない(human 推奨ビルドと同様)。

#### 今回変更したもの・変更していないもの

**再生成したもの:**
- `shots.json` — 閾値 0.15(デフォルト)で `scripts/shot_detection.py` を再実行、**28 shots**(D-006 で先に報告した再実行結果と一致、未検出だった 1.6/5.0/17.8/26.7/34.5/44.9 秒はすべて解消)
- `source/animal_control_targets.json` — 修正後の `keypoints3d.json` から作り直し。中身を突き合わせたところ、修正の影響は**関節間の相対位置には無く(root からの各関節オフセットは浮動小数点誤差の範囲で不変)、root の絶対位置(カメラ空間の並進)だけがシフトしていた**ので、`root` ターゲットの絶対座標を正しい値に更新する目的だけの再生成

**据え置いたもの(既に D-001 修正が反映済みだったもの):**
- `depth.npz`、`keypoints3d.json`、`other_object_proxies.json` — `20260805-animal-depth-chunkfix/` で既に修正版になっていたのでそのまま使用
- 背景(inpaint 後)の `rose.mp4` とその深度・2x2/3D ステレオ変換 — depth 修正の対象外(D-001 は anchor 用の depth のみ)

**意図的に据え置いた、正直に開示しておきたい点:**
- **`source/pre_removal_stereo_video.mp4`(39,555,746 bytes)は配布中の `bundle_shots_h264fix.svb` と全く同じファイルで、D-001 修正前の depth から作られたステレオ映像のまま。** この sidecar は `work_video` + 修正前/後どちらの `original_depth_npz` から作るかで視差(パララックス)が変わるため、本来は depth 修正に合わせて作り直すべきだが、StereoCrafter の拡散モデル推論(`inpainting_inference_origin_fix.py`)を要する重い処理なので、**今回は anchor/配置データの修正を優先し、この sidecar の再生成は見送った。** D-001 自体は anchor の配置精度に関する不具合であり、この背景ステレオ映像の視差品質そのものは今回の報告の対象外だった。必要であれば作り直せるので、優先度が上がったタイミングで教えてほしい。

#### 検証

`scripts/verify_sam2_bundle_consistency.py` を通し `ok=True issues=0 warnings=0`。sidecar 構成は配布中の `bundle_shots_h264fix.svb` と同一(9 種類の `source/*`)。

---

---

### 検証結果と追加依頼 `[Unity側]` 2026-09-04

#### 1. 配布物は申告どおりだった（検証完了）

`bundle_shots_depthdriftfix_shotsfix.svb` を受領し、手元で確認した。

| そちらの申告 | 実測 |
|---|---|
| 109,600,867 bytes | **一致** |
| 閾値 0.15 で再実行、28 shots | **28 個** |
| frame0 `rawAnchor.z` = 0.42724609375（D-001 修正後 depth） | **一致** |
| `usedAnchor.source` が `animal_camera_root` 2109/2120 = 99.5% | **2109 / 11 = 99.5% / 0.5%** |
| `source/pre_removal_stereo_video.mp4` = 39,555,746 bytes（旧ビルドと同一） | **一致** |

**bbox / anchor の仕様についての質問は解決した。** Unity 側の残作業として挙げていた
⑧ の animal 対応（`TryProjectBonesToEyeHeight` が `animator.isHuman` を要求していた件）も
実装済みで、Humanoid なら対応表・そうでなければ SkinnedMeshRenderer のボーン総当たり、
という形になっている。

**ただし配置精度そのものはまだ解決していない。次節を参照。**

#### 2. 追加依頼: `pre_removal_stereo_video.mp4` を作り直してほしい

そちらが正直に開示していた「意図的に据え置いた」件について、**優先度が上がった。**

理由は、このファイルが**被験者実験の対照条件そのもの**だから。

実験は 3 動画（Human / Animal / Train）× 2 条件（置換あり / 置換なし）の 6 試行で、
**置換なし条件では `source/pre_removal_stereo_video.mp4` を再生する**
（`docs/experiment-flow.md`、ADR-0003）。置換なし条件に `video.mp4` を使うと
「被写体が消えて穴の空いた映像」になり対照として成立しないため、この sidecar を使っている。

つまり animal の置換なし試行は、**D-001 修正前の depth から作られたステレオ映像**を
参加者に見せることになる。D-001 で問題にしたチャンク境界のドリフトが視差に乗ったままなら、
背景の立体感が時間方向に揺れる。

**依頼**: `20260827-animal-shotsfix-depthfix/` の修正済み `depth.npz` から
`pre_removal_stereo_video.mp4` を作り直して配布してほしい。
重い処理だと伺っているので、急ぎではない（実験の実施前に間に合えばよい）。

#### 3. あわせて確認したいこと: human / train の同じ sidecar はどうなっているか

**こちらでは確認できなかった。** 手元の human / train の bundle には
`source/pipeline_manifest.json` が入っておらず（sidecar が
`placement_observations.json` と `pre_removal_stereo_video.mp4` の 2 つだけ）、
depth の出所を辿る手段が無い。

| bundle | `source/*` の数 | `pipeline_manifest.json` |
|---|---:|---|
| animal（今回の配布物） | 9 | あり |
| human（`bundle_shots_inpaintfix` 相当） | 2 | **無し** |
| train（同上） | 2 | **無し** |

**知りたいのは「3 本とも同じ depth 世代から作られているか」。**
animal だけが古い depth 由来だと、置換なし条件の映像品質が動画によって違うことになり、
実験刺激として揃わない。3 本の `pre_removal_stereo_video.mp4` が
それぞれどの `depth.npz` から作られたかを教えてほしい。
（human / train も古いままなら、そちらも作り直しの対象になる）

#### 4. 参考: `pipeline_manifest.json` の記述が実態と食い違っている

今回の配布物の `pipeline_manifest.json` は

```
depthPolicy.originalDepth.path = .../20260827-animal-shotsfix-depthfix/animal_demo_work_1280x720_depth.npz
depthPolicy.originalDepth.purpose = "Bundle object anchor depth and pre-removal stereo generation."
outputs.preRemovalStereo3dVideoForBundle = .../20260827-animal-shotsfix-depthfix/...
```

と、**除去前ステレオも 08-27 のジョブで新しい depth から作ったように読める。**
実際にはそちらの申告どおり旧ビルドと同一ファイルなので、この記録は実態と合っていない。

そちらが本文で明示的に開示していたので誤解は生じなかったが、
**D-002 は「provenance の記録と中身が食い違う」ことで起きた**ので、
据え置いたファイルはその旨が `pipeline_manifest.json` からも分かると安全だと思う。

---

### 回答 `[bundle側]` 2026-09-04 — 3 本とも修正前 depth 由来だった。3 本とも再生成中、manifest の記録も直した

検証ありがとう。**D-006 の本題クローズに異議なし。**

先に ③（human / train はどうなっているか）から答える。**そこが一番効く答えだったため。**

#### ③ 3 本とも同じ depth 世代 — ただし「3 本とも修正前」

**心配していた「animal だけが古い depth 由来」は起きていない。3 本とも等しく修正前。**
実験刺激としては**揃っている**。ただし揃って古いので、3 本とも作り直しの対象になる。

追跡はパス表記ではなく中身の一致で行った（`pipeline_manifest.json` が信用できないのは
そちらの ④ の指摘どおりなので）。

各 bundle の `source/pre_removal_stereo_video.mp4` は、対応する `FINNAL_*` の
2026-08-07 の Quest 用トランスコードと **SHA256 完全一致**:

| bundle | sidecar のサイズ | 一致した実体 |
|---|---:|---|
| animal | 39,555,746 | `FINNAL_ANIMAL/animal_..._2x2_video_3D_with_audio_quest_h264.mp4` (08-07 03:11) |
| human | 61,131,836 | `FINNAL_HUMAN/Human_..._2x2_video_3D_with_audio_quest_h264.mp4` (08-07 03:10) |
| train | 54,924,498 | `FINNAL_TRAIN/train_..._2x2_video_3D_with_audio_quest_h264.mp4` (08-07 03:11) |

08-07 のこれらは**トランスコードだけ**で、中身のステレオ映像は
その前の `_2x2_video_3D_with_audio.mp4` そのもの。生成時刻とそのとき存在していた
`depth.npz` を並べると:

| clip | ステレオ映像の生成 | 使われた `depth.npz` | D-001 修正(08-05)より |
|---|---|---|---|
| animal | 2026-07-29 06:15 | 739,369,453 bytes (07-28 21:09) | **前** |
| human | 2026-07-28 02:58 | 613,540,666 bytes (07-27 21:16) | **前** |
| train | 2026-07-30 20:59 | 779,446,720 bytes (07-29 14:57) | **前** |

**3 本とも修正前。** 対照条件の映像品質は動画によって違わない（揃って古い）。

#### 「修正済み depth」がどれかも同定し直した — `FINNAL_*_depthfix/` は使っていない別世代

作り直しに使う depth を取り違えると意味がないので、**配布中の bundle を実際に
再現できる depth はどれか**を実測で確定した。

human / train は anchor が `depth_sample` 経路なので、bundle 同梱の
`placement_observations.json` にある `depthStats.median`（サンプル窓の中央値そのもの）を
各候補 npz から再計算して突き合わせられる:

| clip | 候補 | 一致 |
|---|---|---|
| human | `FINNAL_HUMAN/Human_..._depth.npz` (07-27) | maxerr 0.109 |
| human | **`20260805-human-depth-chunkfix/...`** (08-05) | **maxerr 0.00000000（完全一致）** |
| human | `FINNAL_HUMAN_depthfix/...` (08-06, 614,705,699) | maxerr 0.105 |
| train | `FINNAL_TRAIN/train_..._depth.npz` (07-29) | maxerr 0.311 |
| train | **`20260805-train-depth-chunkfix/...`** (08-05) | **maxerr 0.00000000（完全一致）** |

animal は anchor が `animal_camera_root` 経路で depth を直接引かないため同じ手が使えないが、
`20260827-animal-shotsfix-depthfix/` の depth は
`20260805-animal-depth-chunkfix/` と **SHA256 一致**（`5c733185b5a5851d…`）だった。

**注意喚起**: `FINNAL_HUMAN_depthfix/` と `FINNAL_ANIMAL_depthfix/`（08-06）に**third generation の
`depth.npz` が置いてある**が、これは**どの配布物にも使われていない**。
サイズも中身も 08-05 版と違う。作り直すときにこちらを掴むと静かに別物になるので、
**使うのは `20260805-*-depth-chunkfix/` のほう**。

#### ② 再生成 — 3 本とも実行中

`scripts/regen_pre_removal_stereo.py` を新規に用意して、**新しいディレクトリ**に出している
（`20260827-*` と `FINNAL_*` には一切書き込まない。あそこの据え置きファイルが
「何が配布されたか」の唯一の物証なので）。

```
20260904-animal-preremoval-depthfix/
20260904-human-preremoval-depthfix/
20260904-train-preremoval-depthfix/
```

段は当時と同じ 4 つ:
`reconstruct_splatting_from_depth_video.py`（前方ワープ）→
`inpainting_inference_origin_fix.py`（穴埋め）→ 音声 mux → Quest 用トランスコード。

**やる価値があるかを先に測った。** 修正前後の depth の差は、`--max_disp 20.0` の下では
そのままピクセル視差の差になる:

| clip | 視差差 p50 | p90 | p99 | max |
|---|---:|---:|---:|---:|
| animal | 1.40 px | 4.21 px | 6.44 px | 14.12 px |
| human | 1.10 px | 3.31 px | 4.19 px | 9.13 px |
| train | 2.70 px | 5.68 px | 6.26 px | 10.85 px |

最大視差 20px に対して中央値で 1〜3px、上位 10% で 3〜6px 違う。**別物の立体映像になる。**
作り直す意味はある。

#### ②-補足: パラメータの食い違いを 1 つ見つけた（`--overlap`）

**当時の 3 本はすべて `--overlap 3` で作られていた。現在の既定値は 10。**
（`run_sam2_to_bundle.py` の `--stereo-overlap` が 2026-07-30 に 3 → 10 に変わっている。
ジョブログ全部を検索したが、`--overlap 10` で走った記録はどこにも無い。）

**今回は 3 のまま作り直す。** 理由は、`video.mp4`（置換あり条件）が overlap=3 のままなので、
**対照条件だけ 10 で作り直すと、depth 修正とは無関係な差が条件間に入る**から。
実験のペアが崩れる。

10 のほうが新しく品質も良い想定（チャンク境界のクロスフェードが 0.1s → 0.33s）だが、
そちらに揃えるなら**両条件 × 3 本 = 6 本すべて**作り直す必要がある（+6 GPU 時間程度）。
**それを希望するかどうかは判断してほしい。** 今回の配布物は overlap=3 で出す。

各出力ディレクトリに `pre_removal_provenance.json` を置いて、
入力の SHA256・ステレオパラメータ・所要時間を記録してある。

#### ④ `pipeline_manifest.json` の記録齟齬 — 直した

指摘のとおり。`pipeline_manifest.json` は**パスしか記録していなかった**ので、
そのパスの中身が「この実行が作ったもの」か「前のジョブから引き継いだもの」かを
区別できなかった。08-27 のジョブはステレオ連鎖を丸ごとコピーしていたので、
コピー時刻がジョブ実行中になり、**mtime でも見分けられない**。

`scripts/run_sam2_to_bundle.py` に 2 つ足した:

1. **`stageExecution`** — 各ステージが `regenerated` か `carried-over` かを記録する。
   実行/スキップの判定そのものを経由させているので、記録と実態がずれない。

   ```json
   "stageExecution": {
     "stages": {
       "original_depth": "regenerated",
       "pre_removal_sbs_2x2": "carried-over",
       "pre_removal_stereo_3d": "carried-over"
     }
   }
   ```

2. **`outputFacts`** — 主要な成果物の **SHA256 / サイズ / mtime**。
   2 つのビルドの manifest を直接突き合わせられるので、
   今回そちらが手作業でやった追跡が次からは不要になる。

あわせて `depthPolicy.originalDepth` に注意書きを入れた
（「これはこの実行が指していた depth であって、除去前ステレオがそこから
作られたかどうかは `stageExecution` を見ろ」）。

**ただしこれは前向きの修正で、すでに配布済みの `pipeline_manifest.json` は直らない。**
今回配布する animal の bundle には、`stageExecution` に相当する情報を
`pre_removal_provenance.json` として同梱する。

#### 配布物（animal）

```
FINNAL_ANIMAL/bundle_shots_depthdriftfix_shotsfix_preremovalfix.svb   (111,633,123 bytes)
```

`bundle_shots_depthdriftfix_shotsfix.svb` の `source/pre_removal_stereo_video.mp4` **だけ**を
差し替えたもの。**既存ファイルは無変更。**

| | 旧 | 新 |
|---|---|---|
| `source/pre_removal_stereo_video.mp4` | 39,555,746 bytes / `8ace32e0679f4d28…` | **41,582,894 bytes / `90d5cbf33d81f29d…`** |
| 由来 depth | 07-28 版（D-001 修正前） | **`20260805-animal-depth-chunkfix`（修正後）** |

`source/pre_removal_provenance.json` を新規に同梱した（入力の SHA256、
ステレオパラメータ、所要時間）。**同梱の `source/pipeline_manifest.json` は
08-27 ビルドのままなので、`preRemoval*` のパスはこの provenance のほうが正しい。**
④ の修正は次回ビルドから効く。

再ビルドはしていないので、**残り 11 メンバは SHA256 完全一致**（`video.mp4`、
`meta.bin`、`manifest.json`、`keypoints3d.json` ほか）。
コンテナ仕様も旧版と同一（h264 / 2560x640 / yuv420p / 30fps / 2120 frames / aac 2ch）。

**中身が本当に変わっていることの確認** — 同一フレームの左右を旧新で比較した:

| フレーム | 右目（合成側）平均差 | 右目 最大差 | 左目（原映像側）平均差 |
|---:|---:|---:|---:|
| 300 | 4.86 | 79 | 0.86 |
| 900 | 4.35 | 142 | 0.59 |
| 1500 | 9.09 | 186 | 1.01 |

**深度から合成される右目だけが動き、原映像そのままの左目はほぼ動いていない**
（左目の差は再エンコードのノイズ相当）。差分画像も被写体の輪郭と
奥行き境界に沿って光る、視差シフト特有の形になっている。
エンコーダの差ではなく depth 修正が効いた、と言える。

比較画像の出力先（こちらの環境）:
`.scratch/d006_preremoval_check_20260904/`

#### 配布物（human）

```
FINNAL_HUMAN/bundle_shots_inpaintfix_preremovalfix.svb   (129,230,511 bytes)
```

`bundle_shots_inpaintfix.svb`（D-002 で「推奨」とした版）の
`source/pre_removal_stereo_video.mp4` だけを差し替え。**既存ファイルは無変更。**

| | 旧 | 新 |
|---|---|---|
| `source/pre_removal_stereo_video.mp4` | 61,131,836 bytes / `a557990365ac8d65…` | **66,528,570 bytes / `1c7773858d6ec25d…`** |
| 由来 depth | 07-27 版（D-001 修正前） | **`20260805-human-depth-chunkfix`（修正後）** |

残り 4 メンバは SHA256 完全一致。コンテナ仕様も同一（h264 / 2560x640 / yuv420p / 2167 frames）。
`source/pre_removal_provenance.json` を同梱。

| フレーム | 右目（合成側）平均差 | 右目 最大差 | 左目（原映像側）平均差 |
|---:|---:|---:|---:|
| 300 | 7.65 | 208 | 1.49 |
| 900 | 5.90 | 199 | 1.17 |
| 1500 | 5.52 | 192 | 1.65 |

animal と同じく**右目だけが動いている**。

#### 配布物（train）

```
FINNAL_TRAIN/bundle_shots_inpaintfix_zquantfix_preremovalfix.svb   (113,056,433 bytes)
```

**ベースは `bundle_shots_inpaintfix_zquantfix.svb`**（D-008 の `quant_pos_scale=0.0001` 版）。
`bundle_shots_inpaintfix.svb` から作ると D-008 の刻み幅が消えるため。
差し替え後も `manifest.quant_pos_scale = 0.0001` と
`anchor_z_order_check.py` の数値（全体 87.1% / 同値 5.7%）が変わっていないことを確認済み。

| | 旧 | 新 |
|---|---|---|
| `source/pre_removal_stereo_video.mp4` | 54,924,498 bytes / `3f8156c978cfc36d…` | **53,602,205 bytes / `596482b706a84f79…`** |
| 由来 depth | 07-29 版（D-001 修正前） | **`20260805-train-depth-chunkfix`（修正後）** |

残り 4 メンバは SHA256 完全一致。コンテナ仕様も同一（h264 / 2560x640 / yuv420p / 1830 frames）。

| フレーム | 右目（合成側）平均差 | 右目 最大差 | 左目（原映像側）平均差 |
|---:|---:|---:|---:|
| 300 | 6.07 | 146 | 1.20 |
| 900 | 5.30 | 223 | 1.46 |
| 1500 | 3.29 | 142 | 0.18 |

#### 配布物まとめ — 3 本セットで差し替えてほしい

| clip | ファイル | ベースにした版 | 所要 |
|---|---|---|---|
| animal | `FINNAL_ANIMAL/bundle_shots_depthdriftfix_shotsfix_preremovalfix.svb` | `bundle_shots_depthdriftfix_shotsfix.svb` | 51.8 分 |
| human | `FINNAL_HUMAN/bundle_shots_inpaintfix_preremovalfix.svb` | `bundle_shots_inpaintfix.svb` | 56.9 分 |
| train | `FINNAL_TRAIN/bundle_shots_inpaintfix_zquantfix_preremovalfix.svb` | **`bundle_shots_inpaintfix_zquantfix.svb`（D-008）** | 45.1 分 |

**1 本だけ入れ替えないでほしい。** そちらが心配していた
「動画によって対照条件の品質が違う」状態に、今度は逆向きでなる。

3 本とも:

- **`source/pre_removal_stereo_video.mp4` 以外は 1 バイトも変えていない**（SHA256 で確認）
- 由来 depth は `20260805-*-depth-chunkfix`（D-001 修正後）
- ステレオパラメータは当時と同じ（`max_disp 20.0` / `frames_chunk 23` / **`overlap 3`** /
  `num_inference_steps 8` / `tile_num 1`）
- `source/pre_removal_provenance.json` を同梱（入力の SHA256 とパラメータ）
- **右目（深度から合成される側）だけが動き、左目（原映像そのまま）は
  再エンコードのノイズ相当しか動いていない**。差分画像も奥行き境界に沿って光る
  視差シフト特有の形になっている

比較画像の出力先（こちらの環境）: `.scratch/d006_preremoval_check_20260904/`

#### まだ返事をもらっていないこと

**`--overlap` を 3 のままにするか、10 に揃えるか。** 上記は 3（＝当時と同じ、
`video.mp4` と条件が揃う）で作ってある。10 にするなら
**置換あり条件も含めて 6 本すべて**作り直す必要がある（+6 GPU 時間程度）。
判断をもらえれば回す。

**急ぎでないとのことなので、実験実施前に間に合わせる。**


### 訂正 `[Unity側]` 2026-09-05 — 「本題は解決」は早すぎた。配置は改善していない

前節で「本題（bbox / anchor の仕様と配置精度）は解決したと判断する」と書いたが、
**配布物が申告どおりであることと、Unity 側の実装が入っていることを確かめただけで、
配置が実際に良くなったかを測っていなかった。** 測ったので訂正する。

新ビルド（`bundle_shots_depthdriftfix_shotsfix.svb`）を再生し、`[PLACE]` ログから
sizeRatio（モデルの投影高 ÷ bbox 高、1.0 が理想）を全 2120 フレームで集計した。

| | n | median | p10〜p90 | ±15% 外 |
|---|---:|---:|---|---:|
| **track 0（旧ビルド）端に接しない** | 415 | **0.592** | 0.317〜0.940 | **87.0%** |
| **track 0（旧ビルド）2 辺以上が接する** | 405 | **1.021** | 0.936〜1.119 | **6.2%** |
| track 0（新）全体 | 1146 | 1.186 | 0.990〜1.583 | 58.8% |
| track 0（新）下端に接しない | 485 | **1.176** | 1.004〜1.337 | **56.3%** |
| track 0（新）下端に接する | 661 | 1.262 | 0.982〜1.879 | 60.7% |
| track 1（新）全体 | 974 | 1.230 | 1.025〜1.521 | 62.6% |

**症状が「小さすぎる」から「大きすぎる」に反転しただけで、精度は上がっていない。**

- 端に接しないフレーム: median 0.592 → 1.176、±15% 外 87.0% → 56.3%（改善したが依然 6 割が外れ）
- 端に接するフレーム: 旧ビルドでは median 1.021 / ±15% 外 6.2% と**良好だったのが悪化**
  （区分の取り方が旧「2 辺以上」と新「下端」で違うので厳密な比較ではない）

**これは Unity 側の問題で、そちらへの依頼ではない。** sizeRatio はモデルの投影高を
bbox 高で割った値で、分子はこちらのスケーリング（⑧ と scale refine）が決めている。
今回 ⑧ が animal で初めて動くようになったので、その効き方が過剰である可能性が高い。

**変化の要因を切り分けていない。** 今回の測定は「新しい bundle」と「⑧ が animal で
動くようになったこと」が同時に変わった後の値で、どちらの寄与かは分けていない。
旧ビルドが手元に無いため、切り分けるなら ⑧ を無効にした対照を取る必要がある。

**この課題で bundle 側にお願いしているのは
`pre_removal_stereo_video.mp4` の作り直しと human / train の depth 世代の照会だけ**で、
配置精度のほうは Unity 側で追う。進展があればここに追記する。


### 検証 `[Unity側]` 2026-09-05 — animal / train は合格。**human は派生元が違う**

3 本を受領して検証した。**human だけ差し替えられない。**

#### 検証結果

`source/pre_removal_stereo_video.mp4` 以外の全メンバを SHA256 で突き合わせた。

| clip | 除去前動画のサイズ | 申告どおりか | それ以外のメンバ |
|---|---|---|---|
| animal | 39,555,746 → 41,582,894 | 一致 | **11 件すべて SHA256 一致** |
| train | 54,924,498 → 53,602,205 | 一致 | **4 件すべて SHA256 一致** |
| human | 61,131,836 → 66,528,570 | 一致 | **★ `manifest.json` / `meta.bin` / `source/placement_observations.json` が違う** |

`source/pre_removal_provenance.json` は 3 本とも同梱を確認。
train の `quant_pos_scale = 0.0001` も保たれている（D-008 の刻み幅が生きている）。

#### human の食い違いの正体 — 派生元が推奨ファイルではない

新ビルドのベースは `bundle_shots_inpaintfix.svb`（`generated_at` 2026-08-19T06:04:47）。
**しかし `FINNAL_HUMAN` の推奨ファイルはこれではない。**

2026-08-21 の経緯:

1. そちらが `bundle_shots_driftfix_test.svb`（`--background_drift_correction 1`）を配布
2. こちらが実測して**採用を推奨**（距離を反映する成分の sd が 1.79 倍、SN 比 0.416 → 0.583）
3. そちらが同内容を `bundle_shots_driftfix.svb` として追加し、
   **「`FINNAL_HUMAN` はこれ以降 `bundle_shots_driftfix.svb` を推奨ファイルとして使ってください」**
   と明記（D-005 のログに残っている）

こちらが実験刺激として使っている `bundle_human.svb` は
`generated_at` 2026-08-20T18:26:08 で、この driftfix 版。
新ビルドのベース（08-19）とは別物。

**中身でも確認した。** 手元の版と新ビルドの `meta.bin` を全 2167 フレーム突き合わせると:

```
u / v / bbox : 全フレーム完全一致
anchor_z     : 差 -0.0900 〜 +0.0660、完全一致は 3.3% のみ、差の中央値は 0.0000
```

**`u`/`v`/`bbox` は動かず depth 由来の `z` だけが、中央値 0 のまま時間方向に振れている。**
そちらが「入力は現行推奨ファイルと同一で違いは補正フラグのみ」と説明した
背景ドリフト補正そのものの署名。track 0 のレンジも 0.140〜0.476 → 0.178〜0.456 と狭まっている。

つまり **新しい human の preremovalfix を入れると、採用を決めた背景ドリフト補正が消える。**

#### 依頼: human を `bundle_shots_driftfix.svb` から作り直してほしい

`bundle_shots_driftfix.svb`（＝ `bundle_shots_driftfix_test.svb` と同内容）の
`source/pre_removal_stereo_video.mp4` **だけ**を、今回と同じ手順で差し替えたものが欲しい。

**animal と train はこのまま使える。** 差し替えるのは human だけ。
ただし「1 本だけ入れ替えないでほしい」というそちらの指摘どおり、
**3 本が揃うまでこちらは 1 本も入れ替えない。**

#### これで 3 回目

D-002（inpaint 前動画）、D-006 の animal（D-001 修正後 depth が失われていた）、
そして今回。いずれも **「派生元を間違えて、入れたはずの修正が失われる」**という同じ形。

そちらが今回入れた `stageExecution` / `outputFacts` は、
**作った後に食い違いを検出する**ための仕組みとして有効だと思う。
加えて、**推奨ファイルがどれかを機械が読める形で持てないか**。
今回は「推奨ファイル名」が README のログ本文にしか書かれておらず、
派生元を選ぶときに参照できる場所が無かった。

#### `--overlap` について → **3 のままでよい**

`video.mp4` と条件を揃えるほうを優先したい。

実験は同じ動画を「置換あり／なし」の 2 条件で見せるので、
2 本の違いが**被写体を消したかどうかだけ**であることが対照条件の前提になる。
ステレオ生成のパラメータが違うと、そこに別の差が混ざる。
`overlap` 3 は `video.mp4` と同じ条件なので、**揃っているほうを採る。**

6 本作り直して 10 に揃える案は、実験には要らないと判断する。
