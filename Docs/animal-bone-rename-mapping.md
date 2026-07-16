# Animal model bone rename mapping（50+ Animated Animals対応）

## 背景

`Assets/50+ Animated Animals/` パッケージ由来のモデルは `Assets/Resources/Models/Animal/` に
PrefabInstance（Prefab Variant）として配置されているが、ボーン名はパッケージ独自の命名のままで、
[ADR-0001](adr/0001-canonical-animal-rig-bone-names.md) が要求するカノニカルボーン名
（`spine`, `neck`, `head`, `tail_base/mid/tip`, `front_l/r_upper/lower/paw`,
`rear_l/r_upper/lower/paw/toe`）と一致しない。そのため `AnimalRigDefinition` による
名前解決に失敗し、ADR-0002 decision 3の信頼度バー（spine + 前後左右upper計4本の識別）を満たせず、
SMAL FKが自動的に無効化される（keypoint-IKフォールバックのみになる）。

対応として、各モデルのFBX階層を確認した上でカノニカル名へリネームする。
実際のリネームは `Assets/Editor/AnimalRigBoneRenamer.cs`
（Unity Editorメニュー `Tools > VisionGraft > Rename Animal Bones To Canonical`）で行う。
Prefabを一旦完全アンパックしてから直接Transformをリネームし、上書き保存する（Dog.prefabと同じ最終状態）。
`SkinnedMeshRenderer.bones` はTransform参照であり名前ではないため、リネームしてもスキニングは壊れない。

ボーン階層の正確な確認には `Assets/Editor/AnimalBoneHierarchyDumper.cs`
（メニュー `Tools > VisionGraft > Dump Animal Bone Hierarchy`）を使った。
FBXバイナリからの文字列抽出（`strings`コマンド等）はバイナリノイズが紛れて信頼できないため、
必ずUnity上でロードして実際の階層を確認すること。

## 命名の共通パターン

対応した3種はいずれも同じ構造を持っていた:

- 階層の最上位に **回転しない静的な参照ボーン**（`Root` / `Reference` / `BN_Root_01` 等）があり、
  その直下に **前脚・後脚・尾・脊柱チェーンすべての分岐点になる実質的な骨盤ボーン**がある。
  この骨盤ボーンを `spine` にリネームする（DogRootの`spine`がヒエラルキーの実質ルートである、という
  既存の前提と一致させるため）。静的な参照ボーンそのものはリネームしない。
- 前脚は3セグメント（`front_l/r_upper/lower/paw` = 肩・肘・手首相当）、
  後脚は4セグメント（`rear_l/r_upper/lower/paw/toe` = 股関節・膝・飛節・球節相当）を割り当てる。
  実際のボーン数がそれより多い場合（有蹄類の球節+蹄先など）は、末端側の余った1〜2本は
  リネームせず残す（＝FKで駆動されず、親からの剛体追従のみになる。Dogの`foot.00x`等の
  末端メッシュ用ボーンが未駆動のまま残るのと同じ扱い）。
- 首は複数セグメントのうち **頭に隣接する1本**を `neck` にする（首の途中と頭の間に未駆動ボーンを
  挟まないようにするため。逆に脊柱側に近い側を未駆動にする）。
- 尾は複数セグメントのうち base（付け根）・mid（中間）・tip（末端）に近い3本を選ぶ。
- 装飾用ボーン（耳・目・顎・鼻・指の枝分かれ・馬具/鞍・IKターゲット等）はリネーム対象外。

## モデル別マッピング

### Buffalo

実階層: `Root > Pelvis > {LegBL1..., LegBR1..., Spine1 > Spine2 > Chest > {LegFLCollarbone > LegFL1..., LegFRCollarbone > LegFR1..., Neck1 > Neck2 > Neck3 > Head, NeckFix1, NeckFix2}, Tail1...}`

| 元のボーン名 | カノニカル名 |
|---|---|
| Pelvis | spine |
| Neck3 | neck |
| Head | head |
| Tail1 / Tail2 / Tail4 | tail_base / tail_mid / tail_tip |
| LegFL1 / LegFL2 / LegFL3 | front_l_upper / front_l_lower / front_l_paw |
| LegFR1 / LegFR2 / LegFR3 | front_r_upper / front_r_lower / front_r_paw |
| LegBL1 / LegBL2 / LegBL3 / LegBLAnkle | rear_l_upper / rear_l_lower / rear_l_paw / rear_l_toe |
| LegBR1 / LegBR2 / LegBR3 / LegBRAnkle | rear_r_upper / rear_r_lower / rear_r_paw / rear_r_toe |

未駆動のまま残る末端: `LegFLAnkle`/`LegFLDigit11`（前脚の球節+蹄）, `LegBLDigit11`（後脚の蹄）,
`LegFLCollarbone`/`LegFRCollarbone`（鎖骨相当）, `Neck1`/`Neck2`（首の脊柱寄り2本）,
`NeckFix1`/`NeckFix2`（補正用ボーン）。

### Lion

実階層: `Reference > Hips > {LeftPelvis > LeftUpLeg..., RightPelvis > RightUpLeg..., Spine > Spine1 > Spine2 > Spine3 > Spine4 > {LeftShoulder01 > LeftShoulder02 > LeftArm..., Neck01 > Neck02 > Neck03 > Head, RightShoulder01 > RightShoulder02 > RightArm...}, Tail01...}` + `l/r_*FootIKTargetSHJnt`（未使用のIKターゲット、対象外）

| 元のボーン名 | カノニカル名 |
|---|---|
| Hips | spine |
| Neck03 | neck |
| Head | head |
| Tail01 / Tail04 / Tail07 | tail_base / tail_mid / tail_tip |
| LeftArm / LeftForeArm / LeftHand | front_l_upper / front_l_lower / front_l_paw |
| RightArm / RightForeArm / RightHand | front_r_upper / front_r_lower / front_r_paw |
| LeftUpLeg / LeftLeg / LeftFoot / LeftToeBase | rear_l_upper / rear_l_lower / rear_l_paw / rear_l_toe |
| RightUpLeg / RightLeg / RightFoot / RightToeBase | rear_r_upper / rear_r_lower / rear_r_paw / rear_r_toe |

3種の中で唯一、実ボーン数とカノニカルスロット数が完全一致（余りボーンなし）。
`LeftPelvis`/`RightPelvis`（後脚の骨盤寄りリンク）、`LeftShoulder01/02`・`RightShoulder01/02`
（肩の鎖骨寄りリンク）は未駆動のまま残る。

### Horse

実階層: `RootNode > Horse.001 > Skeleton > BN_Root_01 > BN_Spine_00_02 > {BN_L_Thing_051..., BN_Pelvis_060 > BN_Tail_00_061..., BN_R_Thing_056..., BN_Spine_01_03 > BN_Spine_02_04 > BN_Spine_03_05 > {BN_L_Clavicle_038 > BN_L_UpperArm_039..., BN_Neck_00_06 > BN_Neck_01_07 > BN_Neck_02_011 > BN_Neck_03_012 > BN_Head_00_016, BN_R_Clavicle_043 > BN_R_UpperArm_044...}}}`

| 元のボーン名 | カノニカル名 |
|---|---|
| BN_Spine_00_02 | spine |
| BN_Neck_03_012 | neck |
| BN_Head_00_016 | head |
| BN_Tail_00_061 / BN_Tail_02_063 / BN_Tail_04_065 | tail_base / tail_mid / tail_tip |
| BN_L_UpperArm_039 / BN_l_Forearm_040 / BN_L_Hand_041 | front_l_upper / front_l_lower / front_l_paw |
| BN_R_UpperArm_044 / BN_R_Forearm_045 / BN_R_Hand_046 | front_r_upper / front_r_lower / front_r_paw |
| BN_L_Thing_051 / BN_L_Calf_052 / BN_L_HorseLink_053 / BN_L_Foot_054 | rear_l_upper / rear_l_lower / rear_l_paw / rear_l_toe |
| BN_R_Thing_056 / BN_R_Calf_057 / BN_R_HorseLink_058 / BN_R_Foot_00 | rear_r_upper / rear_r_lower / rear_r_paw / rear_r_toe |

未駆動のまま残る末端: `BN_L_Toe_042`/`BN_R_Toe_047`（前脚の蹄）, `BN_L_Toe_2_055`（後脚の蹄）,
`BN_L_Clavicle_038`/`BN_R_Clavicle_043`（鎖骨相当）, `BN_Spine_01_03`/`BN_Spine_02_04`/`BN_Spine_03_05`
（胸郭側の脊柱3本）, `BN_Neck_00_06`/`BN_Neck_01_07`/`BN_Neck_02_011`（首の脊柱寄り3本）,
`BN_Pelvis_060`（尾の付け根専用リンク）。鞍・馬蹄鉄・たてがみ（`BN_Hair_*`, `BN_*_Stirrup_*`,
`Bridle_Saddle_0`等）は装飾メッシュ/アクセサリボーンのため対象外。

## 実施結果（2026-07-15）

`Tools > VisionGraft > Rename Animal Bones To Canonical` をUnity Editor上で実行し、Buffalo/Lion/Horseの
3体それぞれで20個のカノニカルボーン名（欠落・重複なし）への置換を確認済み。`bones not found`警告は出ていない。
リネーム後の各prefabはルートオブジェクトが`!u!1 GameObject`（Dog.prefabと同じ完全アンパック状態）になっている
ことも確認済み。

## selectedAnimalIndex とファイル名の対応（2026-07-15）

`StreamingStereoVideoPlayer.selectedAnimalIndex`（Inspectorの生の0始まり配列インデックス）と
実行時の「Change Model」UI（1始まり表示）の間で数字がズレて分かりにくいという指摘を受け、
`AnimalModelPriorityOrder`（`StreamingStereoVideoPlayer.Core.partial.cs`）で優先表示している
Animalモデルのprefabファイル自体に、配列インデックスと同じ番号を先頭に付けてリネームした
（`Assets/Editor/AnimalPriorityPrefabRenamer.cs`、メニュー
`Tools > VisionGraft > Rename Priority Animal Prefabs (Index Prefix)`）。

| ファイル名 | selectedAnimalIndexに入れる値 |
|---|---|
| `0_Dog.prefab` | 0 |
| `1_Wolf.prefab` | 1 |
| `2_WildBoar.prefab` | 2 |
| `3_Buffalo.prefab` | 3 |
| `4_Lion.prefab` | 4 |
| `5_Horse.prefab` | 5 |

`Resources.LoadAll`は`Sources/`内の小文字始まりFBXを除外するため大文字始まりのみを拾うフィルタに
なっていたが、数字始まりの名前もロードされるよう`LoadPrefabsFromResources`の判定
（`IsPrefabNameStart`）を「大文字 or 数字」に変更した。`AssetDatabase.RenameAsset`は
Prefabの場合ルートGameObjectの`m_Name`もファイル名と同じ値にリネームするため、実行時UIの
モデル名表示にも`"0_Dog"`のような接頭辞が出てしまう。これを避けるため、UI表示直前に
先頭の`^\d+_`パターンだけを取り除く`CleanModelDisplayName`をUI側（
`StreamingStereoVideoPlayer.UI.ModelPicker.partial.cs`）に追加し、UIには`Dog`のように
接頭辞なしで表示されるようにしている（`AnimalModelPriorityOrder`側の照合キーは接頭辞付きの
`"0_Dog"`のままなので、ソート・選択ロジック自体はファイル名と一致している）。

新しい優先種を追加する際は、①`AnimalRigBoneRenamer.cs`でボーンをカノニカル名にリネーム
→ ②`AnimalModelPriorityOrder`に`"<次の番号>_<名前>"`を追記
→ ③`AnimalPriorityPrefabRenamer.cs`の`Renames`にも追記してprefabファイルをリネーム、
の3ステップを実施する。

## 第2バッチ: 残り42種の一括リネーム（2026-07-15）

Wolf/WildBoarの修正をきっかけに、`Assets/Resources/Models/Animal/`内の残り全モデル（鳥類3種:
Goose/Guineafowl/Pheasant、二足のKangarooを除く）を同じ方式でカノニカル名にリネームした。

### 手順

1. `AnimalBoneHierarchyDumper.RunRemaining()`（メニュー
   `Tools > VisionGraft > Dump Remaining Animal Prefab Bone Hierarchies`）で、各prefabの
   `PrefabUtility.GetCorrespondingObjectFromSource`から元FBXを自動解決して階層をダンプ
   （個別にGUIDを調べる必要がない）。
2. ダンプ結果を突き合わせたところ、42種は少数の「リグ形状パターン」に分類できた
   （後述）。パターンごとに共通のリネーム辞書を生成するヘルパー関数
   （`LegStyleFull`/`RigStyleA`/`RigStyleC`/`LionStyleFull`/`LionStyleThumbFoot`、
   `AnimalRigBoneRenamer.cs`）を用意し、種ごとに首・尻尾のセグメント数だけ差し替えた。
3. 完全に固有の骨格（Deer1, Donkey, Donkey1.0, Goat1, Mammoth, Bear2.0, Beaver）は個別に
   Dictionaryを直書きした。
4. `Tools > VisionGraft > Rename Remaining Animal Bones To Canonical` を実行し、42種すべてで
   `spine`/`neck`/`head`/前後左右`upper`の7項目が検出されたことを確認済み（警告は
   Deer1.0の`toe`未マッピングのみ、後述の通り意図通り）。

### リグ形状パターン

| パターン | 判別方法 | 該当種 |
|---|---|---|
| LegStyleFull（Buffalo/Wolf型） | `Pelvis`/`LegFL1`/`LegBL1`等の命名 | BighornSheep, Gnou, FoxV2, Hyena, MountainGoat, DeerV2, BoarV2, Warthog, LionessV2, MooseF, MooseM, Badger, Rabbit |
| RigStyleA（Bear1.0型、脚2節+Ankle+Digit11） | `RigPelvis`/`RigLBLeg1`等の命名 | Bear1.0, BearWITHFUR, FoxWITHFUR |
| RigStyleC（Moose1.0型、脚3節+Ankle） | 同上だが脚が3節realで爪先はAnkle自体 | Moose1.0, Pronghorn1.0, Elk1.0, Doe1.0 |
| RigStyleA亜種（Deer1.0、爪先ボーンなし） | RigStyleAと同命名だがDigit11が実在しない | Deer1.0（`toe`は意図的に未マッピング） |
| LionStyleFull（Lion/WildBoar型） | `Hips`/`LeftArm`/`LeftUpLeg`等の命名、Neck常に3節 | Bloodhund, Lioness, Puma, Racoon, LabradorDog, Hare, Lynx, Goat, Deer, Moose, GrayWolf |
| LionStyleThumbFoot（後脚がThumb+ToeBaseに分岐） | `LeftFootToeBase`が存在 | AmericanMink, EuropeanBadger |
| 固有（Neck04・Tail08等の追加セグメント） | LionStyleFullと同形だが首4節・尾8節 | Fox |
| 完全固有 | 種ごとの独自命名規則 | Bear2.0, Beaver, Donkey, Donkey1.0, Goat1, Deer1, Mammoth |

### 低信頼度・要目視確認の項目

- **Deer1**（Reallusion/iClone系「RL_BoneRoot」リグ）: `l_elbow`の子に`l_forearm`と
  `l_wrist`が並列に存在し、どちらが実質的な前脚paw相当か判断が難しかった。`l_wrist`を
  採用したが、実際の見た目で前脚が不自然な場合はここを見直す。
- **Mammoth**: 首ボーンが存在せず（`c_back5`が直接`c_head`に接続）、`c_back5`を`neck`に
  流用した。またインデントの深さから階層を目視で再構成したため、他モデルより誤りの
  リスクが高い。
- 上記以外は、脚の実セグメント数がカノニカルのスロット数（前脚3・後脚4）より多い場合、
  末端側（爪先寄り）を未マッピングのまま残す方針（Dog.prefabの既存パターンを踏襲）。

## テクスチャ欠落・削除・再採番（2026-07-16）

Play Modeでの目視確認により、以下の問題が見つかり対応した。

- **BoarV2 / Deer1.0 / Elk1.0 / Moose / Moose1.0 / Pronghorn1.0**: 一部マテリアルにベースカラー
  テクスチャが割り当てられておらず白く表示されていた（ボーンリネームとは無関係。マテリアル名と
  テクスチャファイル名が一致せずFBXインポート時の自動リンクが働かなかったのが原因）。
  該当テクスチャは`Assets/50+ Animated Animals/`内に実在したため、`AssetDatabase.ExtractAsset`で
  マテリアルを外部`.mat`ファイルとして抽出し直してからテクスチャを割り当てた
  （`Assets/Editor/AnimalMaterialTextureFixer.cs`）。
  **注意**: 埋め込みFBXマテリアルは`SetTexture`+`SaveAssets`だけでは変更が永続化されない
  （再インポート時に再生成されるため）。また、既にunpack済みのprefab側は抽出後に参照が
  `NULL`に壊れるため、`Assets/Editor/AnimalPrefabMaterialRepair.cs`でprefab側のRenderer
  マテリアル参照を直接張り直す必要があった。
- **Gnou**: ボディの拡散色テクスチャがパッケージ内に見当たらず（`Gnou.gltf`/`Gnou.bin`に
  埋め込まれている可能性があり、`.fbx`と一緒にインポートされるPNGとしては存在しない）、
  修正不可と判断して選択肢から削除した（`Assets/Editor/AnimalGnouRemover.cs`）。
- **再採番**: Gnou削除に伴い、それ以降の全モデルの番号を1つずつ繰り上げて欠番が出ないように
  した（`Assets/Editor/AnimalIndexPrefixer.cs`を再実行）。番号プレフィックスが既に全モデルに
  付いているため、スクリプト自体もpriority配列を使わない単純なordinal文字列ソートに簡略化した。

## Pronghorn1.0のテクスチャ割り当てミス修正（2026-07-16）

`Pronghorn (1)/(2)/(3).png`はファイル名に意味がなく、初回はファイルサイズから
`(1)=Body, (2)=Teef, (3)=Head`と推測していたが、実際に画像を開いて確認したところ
**BodyとHeadが逆**だった（正しくは `(1)=Head, (2)=Teef, (3)=Body`）。番号だけで中身が
分からないテクスチャファイルは、割り当て前に必ず画像を目視確認すること（サイズ等からの
推測は外れることがある）。`Assets/Editor/AnimalMaterialTextureFixer.cs`は既に抽出済みの
`.mat`を優先して探すよう修正済みなので、テクスチャの割り当てだけをやり直したい場合は
`Fixes`配列の該当行を直して再実行すればよい（抽出・prefab側の参照修復は不要）。

## 未検証事項（実機確認が必要）

- `neck` / `spine` の選定は「頭・骨盤に隣接する1本」という設計判断だが、実際のbundle再生でSMAL FKを
  適用した際に見た目が不自然であれば選定を見直す（特に`spine`をどのセグメントにするかは
  複数の脊柱ボーンがある種で再検討の余地がある）。
- `IsAnimalRigReadyForSmalFk`（spine + 前後左右upper計4本の識別）がこの3種で通ることを
  Play Mode上で確認できていない。次回作業時にログで確認すること。
