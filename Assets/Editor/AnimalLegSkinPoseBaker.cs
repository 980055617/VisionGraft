using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using BigInteger = System.Numerics.BigInteger;

// 脚の基準姿勢の表 Assets/Resources/animal_leg_skin_pose.json（smalLegReferenceSkinPose、AnimalPoseApplier.ApplyLegReferenceSkinPose が読む）と、
// 鳥 3 体の基準姿勢の表 Assets/Resources/animal_reference_pose.json（非四足モードの smalNonQuadrupedReferencePose 用。読むのは AnimalPoseApplier の ApplyNonQuadrupedReferencePose）を
// リポジトリの中から作り直す（2026-10-08）。
//
// ==== (A) animal_leg_skin_pose.json ====
// 元は scratchpad にしか無かった 2 本を 1 本にした移植:
//   (1) rest_rotation_inventory.cs（2026-10-04、Unity の eval）の Animal の半分 = CollectInventory。
//       Resources/Models/model_index.txt の "Models/Animal/" の行ごとに Resources.Load → Object.Instantiate し、root を原点・単位・倍率 1 に置いて、
//       SkinnedMeshRenderer の骨ごとに root 座標の [px,py,pz,qx,qy,qz,qw] を 2 つ取る:
//         def  = prefab の既定姿勢（runtime が bind として採る姿勢）
//         skin = sharedMesh.bindposes から boneWorld = smr.localToWorld × bindpose⁻¹（メッシュが歪まない姿勢）
//       数は float.ToString("0.######", Invariant) の文字列で持つ（棚卸しの JSON に書かれていたものと同じ文字列）。
//   (2) impl_joints/I4/bake_leg_skin.py（2026-10-08、J-03）= AnimalLegSkinPoseBakeCore.Bake。
//       役（AnimalLegMappingFix.Table。表に無いモデルは正規名）から脚ごとの鎖（LCA(左右の上腕・上腿の役) の子で役の祖先、とその子孫）を決め、
//       局所回転 inv(親) × 骨 を double で計算して書く。除外の規則（前の球節の skin の左右差 > 体長の 0.05、上腕・上腿の役が無い、鎖の不整合）も同じ。
//
// 同じ JSON になるための約束（どれか 1 つでも崩すと、7 桁目や -0 の符号が変わる）:
//   - 数は (1) の文字列を一度通し、Python の json と同じ規則で double にする（小数点・指数の無い "0" は整数 = +0.0。小数は正しく丸める）。
//   - 四元数は double で、bake_leg_skin.py（qu.py）と同じ式・同じ順に計算する。内積は 0.0 から左から足す
//     （numpy の 4 要素の内積はこの順と同じで FMA も使わない。2026-10-08 に 20 万組で確かめた）。Unity の Quaternion は使わない。
//   - 書式は Python と同じ: "%.7f" % round(v, 7)（2 進の厳密値で偶数丸め、負は -0 でも "-0.0000000"）、json.dumps(round(x, 3))（"-0.0" 等）、
//     "%+.3f"、json.dumps の文字列（models は ensure_ascii=True、_excluded は False）、問題の文の Python の list の repr。
//   - 並びは Python の sorted（序数順）、改行は LF、UTF-8（BOM なし）、末尾に改行 1 つ。
// _meaning は元と同じ文。_source だけはこの道具で焼いたことを書く（元の表と比べると _source の 1 行だけ違う）。
//
// 2026-10-08 の査読（scratchpad birds/wf_out/review_bake.json）で直したこと:
//   上書きの安全（下の「上書きの安全」）、比べる前に CRLF を LF に直す（このリポジトリは autocrlf=true・text=auto で、取り出し直すと CRLF になる）、
//   鎖の幅優先の上限（8N+64。同じ名前の骨が 3 組以上あると輪が無くても当たり、Python と違う除外理由を書いた）をやめて本当の輪だけを探す
//   （輪が無いのに幅優先が 1,000 万個を超えたときだけ、違う JSON は書かずに例外で止める安全弁 OrderGuard を残した。Python の pop(0) の幅優先は
//   その大きさでは終わらないので、出る JSON が Python と違うことは無い。同じ名前の骨を 4 組にした Lynx でも 17749 個）、
//   表の行を runtime（StreamingStereoVideoPlayer.Playback.partial.cs の脚の割り当ての修正）と同じに読む、棚卸しの sha256 をログに出す、
//   prefab の AnimalBoneMappingOverride を焼く側が使わないことを note に出す、バッチの例を Start-Process -Wait なしにする。
// 2026-10-09 の 2 回目の査読（scratchpad birds/impl/skeptic_baker/）で直したこと:
//   (1) (B) で測るのに要る名前（Head など）が無いモデルを除外せず "legAsymBefore": NaN を書いていた。runtime の MiniJson は NaN で止まらなくなり、
//       書く前の確かめ（VerifyReferenceOnFreshInstances の MiniJson.Parse）で Editor・バッチが固まった。測れない・比が有限でない・書く回転が
//       有限でないモデルは _excluded へ、(B) の書き出しは有限でない数で例外、NaN / Infinity のある JSON は force でも Assets へ書かず、
//       MiniJson に渡す前にも確かめる（NonFiniteNumbers）。b4 照合の要約は 3 体とも測って合ったときだけ ok。
//   (2) Logs へ書いた回は止める理由があっても RESULT ok・exit 0 だった → RESULT wrote_with_blockers N・exit 1（上の「上書きの安全」と「実行」）。
//   (3) 保存した棚卸し・入力から焼くときも model_index.txt を確かめる。(4) 報告・控えの書く先を確かめる（Assets へは書かない）。
//   (5) 「局所回転が 0.01° より変わった」の数を double の角で数える（Unity の Quaternion.Angle は約 0.16° 未満を 0 と返す。
//       kEpsilon の判定 dot > 1−1e-6）。runtime の写しの 0.5° の判定は runtime と同じ Quaternion.Angle のまま、出す数は double。
//
// 確かめたこと（2026-10-08、Unity を起動せずに。scratchpad の birds/impl/legskin/run_harness.py・synth_test.py・selftest_diff.py）:
//   - 10/04 の棚卸し inv2/unity/animal_rest_rotations.json（sha256 28462a29ceeb94c5662ebf557ad1135de7f8d665a2dad24427bb9da775a4f240、
//     691,598 バイト）を AnimalLegSkinPoseBakeCore に通すと、_source を元の文にしたとき作業ツリーの（git 未追跡の）
//     Assets/Resources/animal_leg_skin_pose.json（sha256 9046972acafe406279d36fdc334ee671e95d621b4de8bf753ada40e0594a9f23、242,891 バイト、LF）と
//     バイト単位で一致（45 体・除外 7・1312 骨。Unity の Mono と .NET Framework の両方）。報告も bake_leg_skin_out.txt と一致（改行だけ CRLF → LF）。
//     棚卸しの 52920 個の数は Python の json.load + float() とビット単位で一致。表の 23 行はどれも runtime の読み方で使われる（行を捨てたモデルは 0）。
//   - 棚卸しを読んで WriteInventoryJson で書き直すと元のファイルとバイト単位で一致（-legSkinPoseInventoryOut の形が eval と同じ）。
//   - 書式と数の読み取り 32360 例が Python と一致。同じ名前の骨を 3 組にした Lynx でも Python と同じ JSON・報告（上限をやめたため）。
//   - Assembly-CSharp-Editor として Bee の rsp でコンパイルが通る。
//   未確認: prefab から取り出す側（CollectInventory）は Unity で動かしていない。最初の 1 回はログの inventorySha256= を上の 28462a29… と比べる
//   （同じなら prefab は 10/04 から変わっていない。-legSkinPoseInventoryOut の出力と 10/04 の棚卸しをバイトで比べても同じこと）。
//   焼いた表は diff_leg_skin.py で今の表と比べる（prefab が 10/04 から変わっていなければ _source の行だけ違う）。
//   Unity を上げて float.ToString("0.######") の桁の出し方が変わると、棚卸しの数の最後の桁が変わり、表も 1e-7 程度ずれうる。
//
// ==== (B) animal_reference_pose.json（鳥 3 体、2026-10-08） ====
// 設計: scratchpad birds/nq_design.txt の 3.6、wf_out/design_readable.txt の 6。オフラインの数: birds/b4_reference_pose_compose.py と _out.txt。
//   形は (A) と同じ（models → chains → bones { n, p, skin, def }）。skin = 書く目標の局所回転、def = prefab の既定姿勢の局所回転（runtime の 0.5° の確認用）。
//   Goose・Pheasant: 右脚の鎖（LegR1 から指まで）を左脚の鏡像にする。root 座標で S = diag(1,1,-1)（3 体とも横は root の Z、左が −Z）、
//     左右の軸の約束 C = (S·skinW_L·S)⁻¹·skinW_R を skin 姿勢から取り、W_R = S·W_L·S·C を親から順に局所へ（b4 の mirror_side と同じ式）。
//   Guineafowl: FBX のクリップ *A_StandStraight_Idle1 を t=0 で複製にサンプルし（AnimationClip.SampleAnimation。何も動かなければ AnimationMode）、
//     Root より下の全部の骨（名前が _end の葉を除く）の局所回転を目標にしてから、右脚を同じ式で鏡像にする。位置は書かない（runtime も回転だけ書く）。
//   計算は AnimalReferencePoseBakeCore（UnityEngine を使わない。double）。書いた姿勢を b4 と同じ測り方（脚の傾き・指の高さ・左右差・翼の広がり・頭の高さ・
//   Pelvis の回転）で測り、b4 の数と並べて出す。許容差を超えたら Assets へは書かない。prefab を開いたときは、書く JSON を runtime と同じ読み方
//   （MiniJson → float → 正規化、名前と親の名前で骨を探す、def と 0.5° 以内、親から順に ApplyWorldRotation(親の world × 目標)）で新しい複製に書き、
//   同じ数を測って計算と合うかも確かめる。
//   確かめたこと（2026-10-08、Unity を起動せずに。birds/impl/refpose/check_refpose.py）: FBX の移植（b1/b4 の Python）から作った入力で、
//     測った 7 行（3 体の既定姿勢・Guineafowl の立ち姿・焼く姿勢）が b4_reference_pose_compose_out.txt の行と文字列で一致。書く局所回転は
//     b4 の式（単位四元数にしたもの）と 1e-5° 以内（7 桁の丸め。b4 は 6 桁の棚卸しの四元数を正規化せずに行列にしているので、そのままの b4 とは 8e-4° 違う）。
//     float の "0.######" の入力・棚卸しの Unity の def・_end の葉が無い入力でも照合は通り、クリップが当たらない・別のクリップ・
//     リグが 90° 回った入力は止まる。入力の書き→読み直し→焼き直しで同じ JSON・報告になり、焼いた表は本物の MiniJson と runtime の
//     TryParseLegSkinPoseModel の規則で読める（3 体 11・77・11 骨、親が子より先）。
//     未確認: Unity での取り出し（全 Transform・SkinBind）とクリップのサンプル（SampleAnimation）。
//
// ==== 上書きの安全（A・B 共通。査読の指摘 1） ====
//   - 書く先が Assets の中なら、次のどれかで書かない（バッチは exit 1、メニューは書かずにダイアログで理由を出す）:
//     prefab が読めない（missing）/ model_index.txt が今の prefab と違う / 今のファイルにあるモデルが消える（models から _excluded へ移るのも数える）/
//     今のファイルが JSON として読めない / (B) では除外されたモデル・b4 との照合の不一致・runtime の読み方での確かめの不一致。
//     確かめたうえで上書きするなら、バッチに -legSkinPoseForce（A）/ -referencePoseForce（B）を付ける（メニューには無い）。
//   - 書く JSON に NaN / Infinity / -Infinity の数があれば、force を付けても Assets へは書かない（2026-10-09。runtime の MiniJson は
//     この字句の上で先へ進めず、Parse が戻らなくなる = 端末の再生が固まる）。(B) は測れないモデルを除外し、書く前にも有限でない数で例外にするので
//     出ないはずだが、(A) は Python とバイト単位で同じに書くので、前の球節の体長が 0 になる壊れた形なら "skinFrontFetlockLR": NaN を書きうる。
//   - 上書きする前に、今のファイルを Logs/<名前>.before_<yyyyMMdd_HHmmss>.json へそのまま写す。写せなければ書かない。
//   - メニューの (overwrite Resources) は、焼いて比べた結果（今のファイルとの違い・モデル数・警告）を出してから上書きするか聞く。
//   - Logs など Assets の外へ書くときは止めないが、止める理由が 1 つでもあれば RESULT は ok にせず wrote_with_blockers N・exit 1 で終える
//     （2026-10-09、査読の指摘 2。前は RESULT ok・exit 0 で、キューが b4 の不一致を成功と読めた）。
//   - 保存した棚卸し・入力から焼くとき（-legSkinPoseInventoryIn / -referencePoseInputIn）も、model_index.txt と今の prefab の突き合わせと、
//     棚卸し・入力のモデルの組と model_index.txt の行の突き合わせ（(B) は鳥 3 体の prefab 名）を行い、違えば止める理由にする（2026-10-09、指摘 3）。
//     prefab を開かないので、prefab の中身（骨・既定姿勢）が後から変わったことは分からない（ログの inventorySha256 を 10/04 の値と比べる）。
//   - 書く先の確かめ（2026-10-09、指摘 4。何かを集める・書く前に確かめ、だめなら何も書かずに終える）:
//     表を Assets の中へ書けるのは正規のパス（Assets/Resources/animal_leg_skin_pose.json・animal_reference_pose.json）だけ。
//     報告・棚卸し・入力の控えは Assets の中へ書かない。プロジェクトの中へ書くのは Logs/ の下だけ（ProjectSettings・Packages なども控えなしで
//     上書きしてしまうので断る）。プロジェクトの外は良い。表・報告・控え・読む入力は互いに別のファイル。
//
// ==== 実行 ====
// メニュー（Tools/VisionGraft/Animal/）:
//   Bake Leg Skin Pose (to Logs, compare)       … Logs/animal_leg_skin_pose.regen.json と Logs/animal_leg_skin_pose.regen_report.txt
//   Bake Leg Skin Pose (overwrite Resources)    … 比べた結果を見せてから Assets/Resources/animal_leg_skin_pose.json
//   Bake Reference Pose (to Logs, compare)      … Logs/animal_reference_pose.regen.json と Logs/animal_reference_pose.regen_report.txt
//   Bake Reference Pose (overwrite Resources)   … 比べた結果を見せてから Assets/Resources/animal_reference_pose.json
// バッチ（Editor は閉じておく。開いたままだと HandleProjectAlreadyOpenInAnotherInstance で走らない。新しい .cs の最初の起動はコンパイルが走る）:
//   -executeMethod AnimalLegSkinPoseBaker.BakeFromCommandLine -legSkinPoseOut <path> [-legSkinPoseReport <path>]
//       [-legSkinPoseInventoryOut <path>] [-legSkinPoseInventoryIn <path>] [-legSkinPoseForce]
//     -legSkinPoseOut          書き出す JSON（必須。相対パスはプロジェクトの根から。Assets の中なら正規のパスだけで、上書きの安全を通してから取り込み直す）
//     -legSkinPoseReport       bake_leg_skin_out.txt と同じ形の報告（改行は LF）＋ fatal / warning / disappearing / note / summary / compare / result の行
//     -legSkinPoseInventoryOut 棚卸し（rest_rotation_inventory.cs の animal_rest_rotations.json と同じ形・同じ文字列）を書く
//     -legSkinPoseInventoryIn  prefab を開かず、保存した棚卸しから焼く（10/04 の棚卸しを渡すと、今の表と _source の行以外が同じになるはず）
//   -executeMethod AnimalLegSkinPoseBaker.BakeReferencePoseFromCommandLine -referencePoseOut <path> [-referencePoseReport <path>]
//       [-referencePoseInputOut <path>] [-referencePoseInputIn <path>] [-referencePoseForce]
//     -referencePoseInputOut   取り出した入力（全 Transform の def・skin・クリップの姿勢）を書く。-referencePoseInputIn で同じ計算をやり直せる
//   ログの最後の "[LEGSKINBAKE] RESULT …" / "[REFPOSEBAKE] RESULT …" と exit code（EditorApplication.Exit。2026-10-09、査読の指摘 2）:
//     RESULT ok                     exit 0  書いた。止める理由 0
//     RESULT forced N               exit 0  Assets へ force で書いた（止める理由 N 件を越えた）
//     RESULT wrote_with_blockers N  exit 1  Assets の外（Logs など）へ書いたが、Assets へなら止める理由が N 件ある（b4 の不一致・消えるモデルなど）
//     RESULT failed_or_refused      exit 1  何も書いていない（例外・prefab を開けない・書く先がだめ・Assets への上書きを止めた）
//     ok で始まるのは成功だけ（"RESULT ok" を grep してよい）。
//   書く・止めるを決める前に、必ず "<タグ> summary ..."（棚卸し・入力の sha256、b4 照合）の行と "<タグ> report"（b4 と並べた測った行）をログに出す
//   （止めたときも数が残る。-legSkinPoseReport / -referencePoseReport は報告をファイルにも書きたいときだけ）。
//   Play 中は動かない。prefab の複製は開いているシーンに作ってすぐ消す（シーンが変更扱いになることがある。保存しなくてよい）。
//   PowerShell 5.1 の例（ASCII だけ。Start-Process -Wait は残ったコンパイルサーバを待って戻らないことがある = 2026-10-03 のキューの停止）。
//   最初の行は Editor が開いていれば止める（scratchpad inv/unity/unity_guard.ps1 の Assert-UnityClosed。無ければ `unity editors running` で確かめる。
//   `unity pipeline list` の isRunning は閉じていても true）。Assert-UnityClosed は exit 2 で止めるので、対話のコンソールに貼らず .ps1 に入れて走らせる:
//     . .\unity_guard.ps1; Assert-UnityClosed
//     $Unity = 'C:\Program Files\Unity\Hub\Editor\6000.0.60f1\Editor\Unity.exe'
//     $Proj = 'C:\Users\y9800\Unity_project\VisionGraft'
//     $Log = Join-Path $Proj 'Logs\legskin_bake.log'
//     $Proc = Start-Process -PassThru -FilePath $Unity -ArgumentList @('-batchmode', '-projectPath', $Proj, '-logFile', $Log,
//         '-executeMethod', 'AnimalLegSkinPoseBaker.BakeFromCommandLine', '-legSkinPoseOut', 'Logs/animal_leg_skin_pose.regen.json',
//         '-legSkinPoseReport', 'Logs/animal_leg_skin_pose.regen_report.txt', '-legSkinPoseInventoryOut', 'Logs/animal_rest_rotations.regen.json')
//     $null = $Proc.Handle; $Proc.WaitForExit(); "exit=$($Proc.ExitCode)"
//     Select-String -Path $Log -Pattern 'Scripts have compiler errors', 'error CS', 'HandleProjectAlreadyOpenInAnotherInstance', 'LEGSKINBAKE\]'
//   exit 0 でもコンパイルエラーで何も走っていないことがある。ログに "LEGSKINBAKE] RESULT ok" があるか必ず見る（B は 'REFPOSEBAKE\]'）。
//   RESULT が wrote_with_blockers なら、ログの "(Assets へ書くなら止める)" の行が理由（Assets へは進まない）。
//   -ArgumentList の配列は空白を含む要素を引用しないので、空白のあるパスは使わない。
public static class AnimalLegSkinPoseBaker
{
    public const string ResourcesJsonPath = "Assets/Resources/animal_leg_skin_pose.json";
    public const string ReferencePoseJsonPath = "Assets/Resources/animal_reference_pose.json";
    private const string ModelIndexPath = "Assets/Resources/Models/model_index.txt";
    private const string AnimalModelFolder = "Assets/Resources/Models/Animal";
    private const string AnimalLinePrefix = "Models/Animal/";
    private const string LogsJsonPath = "Logs/animal_leg_skin_pose.regen.json";
    private const string LogsReportPath = "Logs/animal_leg_skin_pose.regen_report.txt";
    private const string RefLogsJsonPath = "Logs/animal_reference_pose.regen.json";
    private const string RefLogsReportPath = "Logs/animal_reference_pose.regen_report.txt";
    private const string Tag = "[LEGSKINBAKE]";
    private const string RefTag = "[REFPOSEBAKE]";

    // 10/04 の棚卸し（scratchpad inv2/unity/animal_rest_rotations.json）と、それから焼いた今の表（作業ツリー、git 未追跡、LF）の sha256 と大きさ（2026-10-08 に確かめた）。
    // ログの inventorySha256= がこれと同じなら、prefab は 10/04 から変わっていない（scratchpad が消えてもログだけで確かめられる）。
    public const string ReferenceInventorySha256 = "28462a29ceeb94c5662ebf557ad1135de7f8d665a2dad24427bb9da775a4f240";
    public const int ReferenceInventoryBytes = 691598;
    public const string ReferenceTableSha256 = "9046972acafe406279d36fdc334ee671e95d621b4de8bf753ada40e0594a9f23";
    public const int ReferenceTableBytes = 242891;

    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    private const string SourceLive =
        "Assets/Editor/AnimalLegSkinPoseBaker.cs が Resources/Models/model_index.txt の Models/Animal の prefab をその場で開いて焼いた" +
        "（棚卸し rest_rotation_inventory.cs と impl_joints/I4/bake_leg_skin.py の移植。def = prefab の既定姿勢、" +
        "skin = SkinnedMeshRenderer.sharedMesh.bindposes、役 = AnimalLegMappingFix.Table）。鎖 = LCA(左右の上腕・上腿の役) の子で役の祖先、とその子孫。";

    private const string RefSourceLive =
        "Assets/Editor/AnimalLegSkinPoseBaker.cs（BakeReferencePose）が Resources/Models/model_index.txt の Models/Animal の鳥の prefab をその場で開いて焼いた" +
        "（def = prefab の既定姿勢、skin = SkinnedMeshRenderer.sharedMesh.bindposes、Guineafowl の目標 = FBX のクリップ *A_StandStraight_Idle1 の t=0 の回転。" +
        "鏡像は scratchpad birds/b4_reference_pose_compose.py の mirror_side と同じ式）。";

    // 焼いて、まだ書いていない結果（上書きの安全を確かめてから Commit で書く）。
    private sealed class PreparedBake
    {
        public string tag;
        public string title;
        public string forceFlag;
        public string outPath;
        public string reportPath;
        public string json;
        public string report;
        public string summary;
        public string comparison;
        public bool intoAssets;
        public bool logged;                                              // 要約と報告をログに出したか（LogPrepared。2 回出さない）
        public readonly List<string> fatal = new List<string>();         // force でも Assets へ書かない理由（NaN / Infinity。2026-10-09）
        public readonly List<string> warnings = new List<string>();      // Assets へ書くのを止める理由（force で越えられる）
        public readonly List<string> disappearing = new List<string>();  // 今のファイルから消えるモデル（同上）
        public readonly List<string> notes = new List<string>();         // 知らせるだけ（止めない）

        public List<string> Blockers()
        {
            var b = new List<string>(fatal);
            b.AddRange(warnings);
            b.AddRange(disappearing);
            return b;
        }
    }

    // ---- メニュー ----
    [MenuItem("Tools/VisionGraft/Animal/Bake Leg Skin Pose (to Logs, compare)")]
    public static void BakeToLogsMenu()
    {
        PreparedBake p = PrepareLegSkin(LogsJsonPath, LogsReportPath, null, null);
        LogResult(Tag, p != null ? Commit(p, false) : AnimalLegSkinPoseBakeCore.BakeOutcome.FailedOrRefused, p);
    }

    [MenuItem("Tools/VisionGraft/Animal/Bake Leg Skin Pose (overwrite Resources)")]
    public static void BakeToResourcesMenu()
    {
        OverwriteFromMenu(PrepareLegSkin(ResourcesJsonPath, LogsReportPath, null, null), "animal_leg_skin_pose.json", Tag);
    }

    [MenuItem("Tools/VisionGraft/Animal/Bake Reference Pose (to Logs, compare)")]
    public static void BakeReferencePoseToLogsMenu()
    {
        PreparedBake p = PrepareReferencePose(RefLogsJsonPath, RefLogsReportPath, null, null);
        LogResult(RefTag, p != null ? Commit(p, false) : AnimalLegSkinPoseBakeCore.BakeOutcome.FailedOrRefused, p);
    }

    [MenuItem("Tools/VisionGraft/Animal/Bake Reference Pose (overwrite Resources)")]
    public static void BakeReferencePoseToResourcesMenu()
    {
        OverwriteFromMenu(PrepareReferencePose(ReferencePoseJsonPath, RefLogsReportPath, null, null), "animal_reference_pose.json", RefTag);
    }

    // 焼いた結果と今のファイルとの違いを見せてから書く。止める理由があれば書かない（メニューには force が無い）。
    private static void OverwriteFromMenu(PreparedBake p, string title, string tag)
    {
        if (p == null)
        {
            LogResult(tag, AnimalLegSkinPoseBakeCore.BakeOutcome.FailedOrRefused, null);
            EditorUtility.DisplayDialog(title, "焼けなかった（Console の " + tag + " を見る）。何も書いていない。", "OK");
            return;
        }

        LogPrepared(p);
        List<string> blockers = p.Blockers();
        // メニューの書く先は必ず Assets の正規のパスで、force は無い（= 止める理由が 1 つでもあれば書かない）
        if (AnimalLegSkinPoseBakeCore.DecideCommit(true, p.fatal.Count, blockers.Count, false) == AnimalLegSkinPoseBakeCore.BakeOutcome.FailedOrRefused)
        {
            foreach (string b in blockers)
            {
                Debug.LogError(p.tag + " refused: " + b);
            }

            WriteReport(p, "refused " + blockers.Count + "（メニューからは上書きしない）");
            Debug.LogError(p.tag + " refused to write " + p.outPath + "（理由 " + blockers.Count + " 件。メニューからは上書きしない）");
            LogResult(tag, AnimalLegSkinPoseBakeCore.BakeOutcome.FailedOrRefused, p);
            EditorUtility.DisplayDialog(title,
                p.outPath + " は上書きしない（理由 " + blockers.Count + " 件）:\n- " + string.Join("\n- ", Head(blockers, 10).ToArray()) +
                "\n\n" + p.summary + "\n" + p.comparison +
                (p.fatal.Count > 0
                    ? "\n\nNaN / Infinity のある表は force でも書かない。"
                    : "\n\n確かめたうえで上書きするなら、バッチに " + p.forceFlag + " を付ける。") + "報告: " + p.reportPath, "OK");
            return;
        }

        string notes = p.notes.Count > 0 ? "\n\nnote:\n- " + string.Join("\n- ", Head(p.notes, 6).ToArray()) : "";
        if (!EditorUtility.DisplayDialog(title,
                p.outPath + " を上書きします（今のファイルは Logs へ控えを取る）。\n\n" + p.summary + "\n" + p.comparison + notes,
                "上書きする", "やめる"))
        {
            Debug.Log(p.tag + " やめた（何も書いていない）");
            LogResult(tag, AnimalLegSkinPoseBakeCore.BakeOutcome.FailedOrRefused, p);
            return;
        }

        LogResult(tag, Commit(p, false), p);
    }

    // ログの最後の "<タグ> RESULT …"（頭の「実行」の表。2026-10-09、査読の指摘 2）。
    private static void LogResult(string tag, AnimalLegSkinPoseBakeCore.BakeOutcome outcome, PreparedBake p)
    {
        Debug.Log(tag + " RESULT " + AnimalLegSkinPoseBakeCore.ResultToken(outcome, p != null ? p.Blockers().Count : 0));
    }

    // -executeMethod AnimalLegSkinPoseBaker.BakeFromCommandLine -legSkinPoseOut <path> [...]（頭の「実行」を参照）
    public static void BakeFromCommandLine()
    {
        var outcome = AnimalLegSkinPoseBakeCore.BakeOutcome.FailedOrRefused;
        int blockers = 0;
        try
        {
            string[] args = Environment.GetCommandLineArgs();
            string outPath = Arg(args, "-legSkinPoseOut");
            if (string.IsNullOrEmpty(outPath))
            {
                Debug.LogError(Tag + " -legSkinPoseOut <path> が要る");
            }
            else
            {
                outcome = Bake(outPath, Arg(args, "-legSkinPoseReport"), Arg(args, "-legSkinPoseInventoryOut"), Arg(args, "-legSkinPoseInventoryIn"),
                               HasFlag(args, "-legSkinPoseForce"), out blockers);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError(Tag + " 失敗: " + ex);
            outcome = AnimalLegSkinPoseBakeCore.BakeOutcome.FailedOrRefused;
        }

        Debug.Log(Tag + " RESULT " + AnimalLegSkinPoseBakeCore.ResultToken(outcome, blockers));
        if (Application.isBatchMode)
        {
            EditorApplication.Exit(AnimalLegSkinPoseBakeCore.ExitCode(outcome));
        }
    }

    // -executeMethod AnimalLegSkinPoseBaker.BakeReferencePoseFromCommandLine -referencePoseOut <path> [...]（頭の「実行」を参照）
    public static void BakeReferencePoseFromCommandLine()
    {
        var outcome = AnimalLegSkinPoseBakeCore.BakeOutcome.FailedOrRefused;
        int blockers = 0;
        try
        {
            string[] args = Environment.GetCommandLineArgs();
            string outPath = Arg(args, "-referencePoseOut");
            if (string.IsNullOrEmpty(outPath))
            {
                Debug.LogError(RefTag + " -referencePoseOut <path> が要る");
            }
            else
            {
                outcome = BakeReferencePose(outPath, Arg(args, "-referencePoseReport"), Arg(args, "-referencePoseInputOut"), Arg(args, "-referencePoseInputIn"),
                                            HasFlag(args, "-referencePoseForce"), out blockers);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError(RefTag + " 失敗: " + ex);
            outcome = AnimalLegSkinPoseBakeCore.BakeOutcome.FailedOrRefused;
        }

        Debug.Log(RefTag + " RESULT " + AnimalLegSkinPoseBakeCore.ResultToken(outcome, blockers));
        if (Application.isBatchMode)
        {
            EditorApplication.Exit(AnimalLegSkinPoseBakeCore.ExitCode(outcome));
        }
    }

    private static string Arg(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool HasFlag(string[] args, string name)
    {
        foreach (string a in args)
        {
            if (a == name)
            {
                return true;
            }
        }

        return false;
    }

    // (A) 焼いて書く。返り値は頭の「実行」の RESULT の表（blockers は止める理由の数）。表に入らないモデルは失敗ではない（_excluded に理由が入る）。
    public static AnimalLegSkinPoseBakeCore.BakeOutcome Bake(string outPath, string reportPath, string inventoryOutPath, string inventoryInPath, bool force,
                                                             out int blockers)
    {
        PreparedBake p = PrepareLegSkin(outPath, reportPath, inventoryOutPath, inventoryInPath);
        blockers = p != null ? p.Blockers().Count : 0;
        return p != null ? Commit(p, force) : AnimalLegSkinPoseBakeCore.BakeOutcome.FailedOrRefused;
    }

    // (B) 焼いて書く。返り値は Bake と同じ。
    public static AnimalLegSkinPoseBakeCore.BakeOutcome BakeReferencePose(string outPath, string reportPath, string inputOutPath, string inputInPath, bool force,
                                                                          out int blockers)
    {
        PreparedBake p = PrepareReferencePose(outPath, reportPath, inputOutPath, inputInPath);
        blockers = p != null ? p.Blockers().Count : 0;
        return p != null ? Commit(p, force) : AnimalLegSkinPoseBakeCore.BakeOutcome.FailedOrRefused;
    }

    // 書く先の確かめ（AnimalLegSkinPoseBakeCore.CheckOutputPaths。頭の「上書きの安全」の最後）。だめなら null を返し、何も書かない。
    private static string CheckPaths(string tag, string canonical, string outFlag, string outPath, string[] auxFlagsAndPaths, string[] inputFlagsAndPaths)
    {
        string bad = AnimalLegSkinPoseBakeCore.CheckOutputPaths(ProjectRoot(), canonical, outFlag, outPath, auxFlagsAndPaths, inputFlagsAndPaths,
                                                                out string normalized);
        if (bad != null)
        {
            Debug.LogError(tag + " " + bad + "。何も書かない");
            return null;
        }

        return normalized;
    }

    // ---- (A) 焼く（まだ書かない） ----
    private static PreparedBake PrepareLegSkin(string outPath, string reportPath, string inventoryOutPath, string inventoryInPath)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError(Tag + " Play 中は焼かない（prefab の複製がシーンに入るため）");
            return null;
        }

        // 2026-10-09（査読の指摘 4）: prefab を開く前・何かを書く前に、書く先を確かめる（報告の書く先がだめなこともあるので、報告も書かない）
        outPath = CheckPaths(Tag, ResourcesJsonPath, "-legSkinPoseOut", outPath,
                             new[] { "-legSkinPoseReport", reportPath, "-legSkinPoseInventoryOut", inventoryOutPath },
                             new[] { "-legSkinPoseInventoryIn", inventoryInPath });
        if (outPath == null)
        {
            return null;
        }

        var p = new PreparedBake
        {
            tag = Tag, title = "animal_leg_skin_pose.json", forceFlag = "-legSkinPoseForce", outPath = outPath, reportPath = reportPath,
        };
        List<AnimalLegSkinPoseBakeCore.InvModel> inventory;
        string source;
        string inventoryFileSha = null;
        if (!string.IsNullOrEmpty(inventoryInPath))
        {
            byte[] raw = File.ReadAllBytes(FullPath(inventoryInPath));
            inventoryFileSha = Sha256Hex(raw);
            inventory = AnimalLegSkinPoseBakeCore.ReadInventoryJson(DecodeUtf8(raw));
            source = "Assets/Editor/AnimalLegSkinPoseBaker.cs が保存した棚卸し " + Path.GetFileName(inventoryInPath) +
                     "（rest_rotation_inventory.cs と同じ形）から焼いた（impl_joints/I4/bake_leg_skin.py の移植。役 = AnimalLegMappingFix.Table）。" +
                     "鎖 = LCA(左右の上腕・上腿の役) の子で役の祖先、とその子孫。";

            // 2026-10-09（査読の指摘 3）: prefab を開かなくても、model_index.txt が今の prefab と合うかと、棚卸しのモデルの組が
            // model_index.txt の Models/Animal の行と同じかは確かめられる（違えば Assets へ書くのを止める理由）。
            CheckModelIndexIsCurrent(p.warnings);
            var invModels = new List<string>();
            foreach (AnimalLegSkinPoseBakeCore.InvModel m in inventory)
            {
                invModels.Add(m.model);
            }

            string setDiff = AnimalLegSkinPoseBakeCore.CompareInventoryWithIndex(invModels, ModelIndexAnimalLines());
            if (setDiff != null)
            {
                p.warnings.Add(setDiff);
            }

            p.notes.Add("保存した棚卸しから焼いた（prefab を開いていないので、prefab の骨・既定姿勢が棚卸しの後に変わっていても分からない。" +
                        "summary の inventorySha256 を 10/04 の " + ReferenceInventorySha256.Substring(0, 8) + "… と比べる）");
        }
        else
        {
            var errors = new List<string>();
            inventory = CollectInventory(p.warnings, errors, p.notes);
            if (errors.Count > 0)
            {
                foreach (string e in errors)
                {
                    Debug.LogError(Tag + " " + e);
                }

                Debug.LogError(Tag + " prefab を読む途中で失敗したので書かない（" + errors.Count + " 件）");
                return null;
            }

            CheckModelIndexIsCurrent(p.warnings);
            source = SourceLive;
        }

        CheckRoleKeysMatchRuntime(p.warnings);

        // 比べる相手の棚卸し（scratchpad が消えても 10/04 の棚卸しとログだけで比べられるように、正規の形に書き直したものの sha256 を出す）
        string inventoryJson = AnimalLegSkinPoseBakeCore.WriteInventoryJson(inventory);
        byte[] inventoryBytes = Utf8NoBom.GetBytes(inventoryJson);
        string inventorySha = Sha256Hex(inventoryBytes);
        if (!string.IsNullOrEmpty(inventoryOutPath))
        {
            WriteText(inventoryOutPath, inventoryJson);
        }

        AnimalLegSkinPoseBakeCore.Result result =
            AnimalLegSkinPoseBakeCore.Bake(inventory, AnimalLegMappingFix.Table, AnimalLegMappingFix.Key, source);
        p.notes.AddRange(result.notes);
        p.json = result.json;
        CheckRuntimeSafeJson(p);   // 2026-10-09（査読の指摘 1）: Python と同じに書くので NaN がありうる（前の球節の体長 0）。あれば force でも Assets へ書かない
        byte[] bytes = Utf8NoBom.GetBytes(result.json);
        p.summary = "models=" + result.modelCount + " excluded=" + result.excludedCount + " bytes=" + bytes.Length + " sha256=" + Sha256Hex(bytes) +
                    " | inventoryModels=" + inventory.Count + " inventoryBytes=" + inventoryBytes.Length + " inventorySha256=" + inventorySha +
                    (inventorySha == ReferenceInventorySha256
                        ? "（10/04 の棚卸しと同じ）"
                        : "（10/04 の棚卸し " + ReferenceInventorySha256.Substring(0, 8) + "… " + ReferenceInventoryBytes + " バイトと違う）") +
                    (inventoryFileSha != null ? " inventoryFileSha256=" + inventoryFileSha : "");
        CompareWithCurrent(p, ResourcesJsonPath, result.json);

        var rep = new StringBuilder(result.report);
        AppendReportTail(rep, p);
        p.report = rep.ToString();
        return p;
    }

    // ---- (B) 焼く（まだ書かない） ----
    private static PreparedBake PrepareReferencePose(string outPath, string reportPath, string inputOutPath, string inputInPath)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError(RefTag + " Play 中は焼かない（prefab の複製がシーンに入るため）");
            return null;
        }

        // 2026-10-09（査読の指摘 4）: prefab を開く前・何かを書く前に、書く先を確かめる
        outPath = CheckPaths(RefTag, ReferencePoseJsonPath, "-referencePoseOut", outPath,
                             new[] { "-referencePoseReport", reportPath, "-referencePoseInputOut", inputOutPath },
                             new[] { "-referencePoseInputIn", inputInPath });
        if (outPath == null)
        {
            return null;
        }

        var p = new PreparedBake
        {
            tag = RefTag, title = "animal_reference_pose.json", forceFlag = "-referencePoseForce", outPath = outPath, reportPath = reportPath,
        };
        List<AnimalReferencePoseBakeCore.Model> inputs;
        string source;
        var lineByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(inputInPath))
        {
            byte[] raw = File.ReadAllBytes(FullPath(inputInPath));
            inputs = AnimalReferencePoseBakeCore.ReadInputJson(DecodeUtf8(raw));
            source = "Assets/Editor/AnimalLegSkinPoseBaker.cs（BakeReferencePose）が保存した入力 " + Path.GetFileName(inputInPath) +
                     "（sha256 " + Sha256Hex(raw) + "）から焼いた（鏡像は scratchpad birds/b4_reference_pose_compose.py の mirror_side と同じ式）。";

            // 2026-10-09（査読の指摘 3）: prefab を開かなくても、model_index.txt が今の prefab と合うかと、入力の鳥 3 体の prefab 名が
            // model_index.txt の行と同じかは確かめられる（番号の振り直し・消えたモデル。違えば Assets へ書くのを止める理由）。
            CheckModelIndexIsCurrent(p.warnings);
            p.warnings.AddRange(AnimalReferencePoseBakeCore.CompareInputWithIndex(inputs, ModelIndexAnimalLines(), AnimalLegMappingFix.Key));
            p.notes.Add("保存した入力から焼いた（prefab を開いていないので、prefab の骨・既定姿勢・クリップが入力の後に変わっていても分からない。" +
                        "runtime の読み方での確かめ（unity: の行）も走らない）");
        }
        else
        {
            var errors = new List<string>();
            inputs = CollectReferenceInputs(p.warnings, errors, p.notes, lineByKey);
            if (errors.Count > 0)
            {
                foreach (string e in errors)
                {
                    Debug.LogError(RefTag + " " + e);
                }

                Debug.LogError(RefTag + " prefab を読む途中で失敗したので書かない（" + errors.Count + " 件）");
                return null;
            }

            CheckModelIndexIsCurrent(p.warnings);
            source = RefSourceLive;
        }

        string inputJson = AnimalReferencePoseBakeCore.WriteInputJson(inputs);
        byte[] inputBytes = Utf8NoBom.GetBytes(inputJson);
        if (!string.IsNullOrEmpty(inputOutPath))
        {
            WriteText(inputOutPath, inputJson);
        }

        AnimalReferencePoseBakeCore.Result result = AnimalReferencePoseBakeCore.Bake(inputs, AnimalLegMappingFix.Key, source);
        p.warnings.AddRange(result.problems);
        p.notes.AddRange(result.notes);
        p.json = result.json;
        bool safe = CheckRuntimeSafeJson(p);   // 2026-10-09（査読の指摘 1）: (B) は書き出しで例外にするので出ないはず。出たら force でも書かない
        var rep = new StringBuilder(result.report);
        if (lineByKey.Count > 0)
        {
            if (safe)
            {
                VerifyReferenceOnFreshInstances(result, lineByKey, p, rep);
            }
            else
            {
                rep.Append("unity: 書く JSON に NaN / Infinity があるので、runtime の読み方での確かめ（MiniJson）は飛ばした（MiniJson はそこで止まらなくなる）\n");
            }
        }

        byte[] bytes = Utf8NoBom.GetBytes(result.json);
        p.summary = "models=" + result.modelCount + " excluded=" + result.excludedCount + " bytes=" + bytes.Length + " sha256=" + Sha256Hex(bytes) +
                    " | inputModels=" + inputs.Count + " inputBytes=" + inputBytes.Length + " inputSha256=" + Sha256Hex(inputBytes) +
                    " | b4 照合 " + AnimalReferencePoseBakeCore.B4Summary(result);
        CompareWithCurrent(p, ReferencePoseJsonPath, result.json);
        AppendReportTail(rep, p);
        p.report = rep.ToString();
        return p;
    }

    // 書く先が Assets の中で既にあればそれと、無ければ Resources の表と比べる（説明と、消えるモデル）。
    private static void CompareWithCurrent(PreparedBake p, string resourcesPath, string json)
    {
        p.intoAssets = IsInsideAssets(p.outPath);
        string baseline = p.intoAssets && File.Exists(FullPath(p.outPath)) ? p.outPath : resourcesPath;
        string baselineFull = FullPath(baseline);
        string before = File.Exists(baselineFull) ? File.ReadAllText(baselineFull, Encoding.UTF8) : null;
        p.comparison = AnimalLegSkinPoseBakeCore.DescribeAgainst(before, json, baseline);
        if (before == null)
        {
            return;
        }

        try
        {
            p.disappearing.AddRange(AnimalLegSkinPoseBakeCore.FindDisappearing(before, json, AnimalLegMappingFix.Key));
        }
        catch (FormatException ex)
        {
            p.warnings.Add("今の " + baseline + " が JSON として読めない（" + ex.Message + "）");
        }
    }

    // 書く JSON に NaN / Infinity / -Infinity の数が無いか（AnimalLegSkinPoseBakeCore.NonFiniteNumbers。2026-10-09、査読の指摘 1）。
    // あれば force でも Assets へ書かない理由（p.fatal）にして false。runtime の MiniJson はこの字句の上で先へ進めず、Parse が戻らない。
    private static bool CheckRuntimeSafeJson(PreparedBake p)
    {
        List<string> nonFinite = AnimalLegSkinPoseBakeCore.NonFiniteNumbers(p.json);
        if (nonFinite.Count == 0)
        {
            return true;
        }

        p.fatal.Add("書く JSON に有限でない数が " + nonFinite.Count + " 個ある（runtime の MiniJson はそこで止まらなくなるので、" + p.forceFlag +
                    " を付けても Assets へは書かない）: " + string.Join(", ", Head(nonFinite, 5).ToArray()));
        return false;
    }

    private static void AppendReportTail(StringBuilder rep, PreparedBake p)
    {
        foreach (string f in p.fatal)
        {
            rep.Append("fatal: ").Append(f).Append('\n');
        }

        foreach (string w in p.warnings)
        {
            rep.Append("warning: ").Append(w).Append('\n');
        }

        foreach (string d in p.disappearing)
        {
            rep.Append("disappearing: ").Append(d).Append('\n');
        }

        foreach (string n in p.notes)
        {
            rep.Append("note: ").Append(n).Append('\n');
        }

        rep.Append("summary: ").Append(p.summary).Append('\n');
        rep.Append("compare: ").Append(p.comparison).Append('\n');
    }

    // 書く・止めるを決める前に、要約（棚卸し・入力の sha256、b4 照合）・今のファイルとの比べ・報告（b4 と並べた測った行）をログに出す。
    // 2026-10-08: 止めたときも数がログに残るように（前は書いたときだけ出していたので、-referencePoseReport なしで止まると b4 の数がどこにも出なかった）。
    private static void LogPrepared(PreparedBake p)
    {
        if (p.logged)
        {
            return;
        }

        p.logged = true;
        Debug.Log(p.tag + " summary " + p.summary + " | " + p.comparison);
        Debug.Log(p.tag + " report\n" + p.report);
    }

    // ---- 書く（上書きの安全。頭の「上書きの安全」を参照） ----
    // 返り値は頭の「実行」の RESULT の表。Assets の中へは、止める理由が無いか force のときだけ書く（NaN / Infinity は force でも書かない）。
    // Assets の外へは書くが、止める理由があれば WroteWithBlockers（RESULT wrote_with_blockers N・exit 1。2026-10-09、査読の指摘 2）。
    private static AnimalLegSkinPoseBakeCore.BakeOutcome Commit(PreparedBake p, bool force)
    {
        LogPrepared(p);
        List<string> blockers = p.Blockers();
        AnimalLegSkinPoseBakeCore.BakeOutcome outcome = AnimalLegSkinPoseBakeCore.DecideCommit(p.intoAssets, p.fatal.Count, blockers.Count, force);
        if (outcome == AnimalLegSkinPoseBakeCore.BakeOutcome.FailedOrRefused)
        {
            foreach (string b in blockers)
            {
                Debug.LogError(p.tag + " refused: " + b);
            }

            WriteReport(p, "refused " + blockers.Count + (p.fatal.Count > 0 ? "（うち force でも越えない " + p.fatal.Count + "）" : ""));
            Debug.LogError(p.tag + " refused to write " + p.outPath + "（理由 " + blockers.Count + " 件。" +
                           (p.fatal.Count > 0
                               ? "NaN / Infinity のある表は " + p.forceFlag + " を付けても書かない）"
                               : "確かめたうえで上書きするならバッチに " + p.forceFlag + "）"));
            return AnimalLegSkinPoseBakeCore.BakeOutcome.FailedOrRefused;
        }

        foreach (string b in blockers)
        {
            Debug.LogWarning(p.tag + (p.intoAssets ? " forced past: " : " (Assets へ書くなら止める) ") + b);
        }

        string full = FullPath(p.outPath);
        if (p.intoAssets && File.Exists(full))
        {
            string backup = BackupPath(p.outPath);
            File.Copy(full, FullPath(backup), false);   // 写せなければ例外で止まり、上書きしない
            Debug.Log(p.tag + " backup " + p.outPath + " -> " + backup);
        }

        WriteText(p.outPath, p.json);
        WriteReport(p,AnimalLegSkinPoseBakeCore.ResultToken(outcome, blockers.Count) +
                       (outcome == AnimalLegSkinPoseBakeCore.BakeOutcome.WroteWithBlockers ? "（書いたが、Assets へなら止める）" : ""));
        foreach (string n in p.notes)
        {
            Debug.Log(p.tag + " note: " + n);
        }

        Debug.Log(p.tag + " wrote " + p.outPath + " " + p.summary + " | " + p.comparison);
        return outcome;
    }

    private static void WriteReport(PreparedBake p, string status)
    {
        if (!string.IsNullOrEmpty(p.reportPath))
        {
            WriteText(p.reportPath, p.report + "result: " + status + "\n");
        }
    }

    // Logs/<名前>.before_<yyyyMMdd_HHmmss><拡張子>。同じ秒に 2 回なら _2, _3 … を足す。
    private static string BackupPath(string outPath)
    {
        string name = Path.GetFileNameWithoutExtension(outPath);
        string ext = Path.GetExtension(outPath);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(FullPath("Logs"));
        string candidate = "Logs/" + name + ".before_" + stamp + ext;
        for (int i = 2; File.Exists(FullPath(candidate)); i++)
        {
            candidate = "Logs/" + name + ".before_" + stamp + "_" + i.ToString(CultureInfo.InvariantCulture) + ext;
        }

        return candidate;
    }

    // ---- (1) rest_rotation_inventory.cs の Animal の半分（行ごとに同じ API を同じ順で呼ぶ。eval の 172〜218 行） ----
    // 違いは 3 つだけで、どれも成功したときの中身を変えない: 行の判定を序数比較にした（eval は既定のカルチャ）、
    // SkinBind で smr.bones をループの前に 1 回だけ取る（eval は添字ごとに取り直していた。中身は同じ）、
    // 例外が出たモデルは途中の文字列を残さず全体を失敗にする（eval は壊れた JSON を書き、bake_leg_skin.py がそこで止まった）。
    // 2026-10-08: 表の行を runtime と同じに確かめるため、runtime と同じ Transform の名前の全部（host = Animator の GameObject）を控える（JSON には書かない）。
    //   prefab の AnimalBoneMappingOverride は焼く側では使わない（bake_leg_skin.py と同じ）ので、空でなければ note に出す（査読の指摘 6）。
    private static List<AnimalLegSkinPoseBakeCore.InvModel> CollectInventory(List<string> warnings, List<string> errors, List<string> notes)
    {
        var list = new List<AnimalLegSkinPoseBakeCore.InvModel>();
        string[] idx = File.ReadAllLines(FullPath(ModelIndexPath));
        foreach (string raw in idx)
        {
            string line = raw.Trim();
            if (!line.StartsWith(AnimalLinePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            GameObject prefab = Resources.Load<GameObject>(line);
            if (prefab == null)
            {
                // eval も "missing" として飛ばし、そのモデルは棚卸しに入らなかった（= 表にも _excluded にも出ない）。Assets へ書くのを止める理由になる。
                warnings.Add("missing " + line + "（Resources.Load できない。model_index.txt が古い?）");
                continue;
            }

            var inst = (GameObject)UnityEngine.Object.Instantiate(prefab);
            try
            {
                Transform root = inst.transform;
                root.position = Vector3.zero;
                root.rotation = Quaternion.identity;
                root.localScale = Vector3.one;
                Animator animator = inst.GetComponentInChildren<Animator>(true);
                Transform rigRoot = animator != null ? animator.transform : root;
                var seen = new HashSet<Transform>();
                var m = new AnimalLegSkinPoseBakeCore.InvModel
                {
                    model = J(line.Substring(AnimalLinePrefix.Length)),
                    rigRoot = J(rigRoot.name),
                    rigRootDef = PQ(root.InverseTransformPoint(rigRoot.position), Quaternion.Inverse(root.rotation) * rigRoot.rotation),
                };
                foreach (SkinnedMeshRenderer smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr.bones == null)
                    {
                        continue;
                    }

                    foreach (Transform b in smr.bones)
                    {
                        if (b == null || !seen.Add(b))
                        {
                            continue;
                        }

                        m.bones.Add(new AnimalLegSkinPoseBakeCore.InvBone
                        {
                            n = J(b.name),
                            parent = J(b.parent != null ? b.parent.name : ""),
                            def = PQ(root.InverseTransformPoint(b.position), Quaternion.Inverse(root.rotation) * b.rotation),
                            skin = SkinBind(root, b),
                        });
                    }
                }

                // runtime（Playback.partial.cs の脚の割り当ての修正）と同じ: host = GetComponentInChildren<Animator>() の GameObject、名前は Transform の全部
                Animator runtimeAnimator = inst.GetComponentInChildren<Animator>();
                GameObject host = runtimeAnimator != null ? runtimeAnimator.gameObject : inst;
                m.transformNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (Transform t in host.GetComponentsInChildren<Transform>(true))
                {
                    m.transformNames.Add(t.name);
                }

                AnimalBoneMappingOverride ov = inst.GetComponentInChildren<AnimalBoneMappingOverride>(true);
                string ovText = ov != null ? DescribeOverride(ov) : "";
                if (ovText.Length > 0)
                {
                    notes.Add(line + ": prefab の AnimalBoneMappingOverride（" + ovText + "）は焼く側では使わない" +
                              "（役 = 正規名＋AnimalLegMappingFix.Table。bake_leg_skin.py と同じ。runtime の役とずれうる）");
                }

                list.Add(m);
            }
            catch (Exception ex)
            {
                errors.Add(line + " " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(inst);
            }
        }

        return list;
    }

    private static string DescribeOverride(AnimalBoneMappingOverride ov)
    {
        var parts = new List<string>();
        void Add(string k, string v)
        {
            if (!string.IsNullOrEmpty(v))
            {
                parts.Add(k + "=" + v);
            }
        }

        Add("spine", ov.spine);
        Add("neck", ov.neck);
        Add("head", ov.head);
        Add("tailBase", ov.tailBase);
        Add("tailMid", ov.tailMid);
        Add("tailTip", ov.tailTip);
        Add("frontLUpper", ov.frontLUpper);
        Add("frontLLower", ov.frontLLower);
        Add("frontLPaw", ov.frontLPaw);
        Add("frontRUpper", ov.frontRUpper);
        Add("frontRLower", ov.frontRLower);
        Add("frontRPaw", ov.frontRPaw);
        Add("rearLUpper", ov.rearLUpper);
        Add("rearLLower", ov.rearLLower);
        Add("rearLPaw", ov.rearLPaw);
        Add("rearLToe", ov.rearLToe);
        Add("rearRUpper", ov.rearRUpper);
        Add("rearRLower", ov.rearRLower);
        Add("rearRPaw", ov.rearRPaw);
        Add("rearRToe", ov.rearRToe);
        return string.Join(";", parts.ToArray());
    }

    // 焼く側の役のキー（AnimalLegSkinPoseBakeCore.RoleKeys）が runtime の AnimalLegMappingFix.IsOverrideKey と同じ 20 個か（表の行の読み方を揃えるため）。
    private static void CheckRoleKeysMatchRuntime(List<string> warnings)
    {
        var bad = new List<string>();
        foreach (string k in AnimalLegSkinPoseBakeCore.RoleKeys())
        {
            if (!AnimalLegMappingFix.IsOverrideKey(k))
            {
                bad.Add(k);
            }
        }

        if (AnimalLegMappingFix.IsOverrideKey("") || AnimalLegMappingFix.IsOverrideKey("front_l_upper"))
        {
            bad.Add("(知らないキーを受け付ける)");
        }

        if (bad.Count > 0)
        {
            warnings.Add("焼く側の役のキーが AnimalLegMappingFix.IsOverrideKey と合わない: " + string.Join(", ", bad.ToArray()));
        }
    }

    // eval の SkinBind（19〜36 行）: 最初に見つかった SkinnedMeshRenderer の bindposes から boneWorld = smr.localToWorld × bindpose⁻¹。
    // sharedMesh が無い SMR は飛ばす（骨の列挙のほうは飛ばさない。eval と同じ）。見つからなければ null（棚卸しの "null"）。
    private static string[] SkinBind(Transform root, Transform bone)
    {
        foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr.sharedMesh == null || smr.bones == null)
            {
                continue;
            }

            Matrix4x4[] bps = smr.sharedMesh.bindposes;
            Transform[] smrBones = smr.bones;
            for (int i = 0; i < smrBones.Length && i < bps.Length; i++)
            {
                if (smrBones[i] != bone)
                {
                    continue;
                }

                Matrix4x4 w = smr.transform.localToWorldMatrix * bps[i].inverse;
                Matrix4x4 l = root.worldToLocalMatrix * w;
                Vector3 p = l.GetColumn(3);
                Quaternion q = Quaternion.LookRotation(l.GetColumn(2), l.GetColumn(1));
                return PQ(p, q);
            }
        }

        return null;
    }

    // eval の F と PQ（12〜14 行）。この文字列が棚卸しの JSON の数そのもの。
    private static string F(float v)
    {
        return v.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static string[] PQ(Vector3 p, Quaternion q)
    {
        return new[] { F(p.x), F(p.y), F(p.z), F(q.x), F(q.y), F(q.z), F(q.w) };
    }

    // eval の J（15 行）: \ → /、" → '。名前はこれを通したものが棚卸しにも表にも入る。
    private static string J(string s)
    {
        return (s ?? "").Replace("\\", "/").Replace("\"", "'");
    }

    // ---- (B) 鳥の入力を取り出す（全 Transform の def・skin、Guineafowl はクリップの t=0 の姿勢） ----
    // model_index.txt の Models/Animal の行から、AnimalLegMappingFix.Key がキーと同じ行を探す（番号は振り直されることがあるので名前で引く）。
    private static List<AnimalReferencePoseBakeCore.Model> CollectReferenceInputs(List<string> warnings, List<string> errors, List<string> notes,
                                                                                    Dictionary<string, string> lineByKey)
    {
        var list = new List<AnimalReferencePoseBakeCore.Model>();
        var lines = new List<string>();
        foreach (string raw in File.ReadAllLines(FullPath(ModelIndexPath)))
        {
            string line = raw.Trim();
            if (line.StartsWith(AnimalLinePrefix, StringComparison.Ordinal))
            {
                lines.Add(line);
            }
        }

        foreach (AnimalReferencePoseBakeCore.Spec spec in AnimalReferencePoseBakeCore.Specs)
        {
            var matches = new List<string>();
            foreach (string line in lines)
            {
                if (AnimalLegMappingFix.Key(AnimalLegSkinPoseBakeCore.LastPathSegment(line)) == spec.key)
                {
                    matches.Add(line);
                }
            }

            if (matches.Count == 0)
            {
                warnings.Add("missing " + spec.key + "（model_index.txt の Models/Animal に無い）");
                continue;
            }

            if (matches.Count > 1)
            {
                errors.Add(spec.key + " が model_index.txt に " + matches.Count + " 行ある: " + string.Join(", ", matches.ToArray()));
                continue;
            }

            GameObject prefab = Resources.Load<GameObject>(matches[0]);
            if (prefab == null)
            {
                warnings.Add("missing " + matches[0] + "（Resources.Load できない。model_index.txt が古い?）");
                continue;
            }

            try
            {
                AnimalReferencePoseBakeCore.Model m = CollectReferenceModel(matches[0], prefab, spec, errors, notes);
                if (m != null)
                {
                    list.Add(m);
                    lineByKey[spec.key] = matches[0];
                }
            }
            catch (Exception ex)
            {
                errors.Add(matches[0] + " " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        return list;
    }

    private static AnimalReferencePoseBakeCore.Model CollectReferenceModel(string line, GameObject prefab, AnimalReferencePoseBakeCore.Spec spec,
                                                                           List<string> errors, List<string> notes)
    {
        AnimationClip clip = null;
        string modelPath = null;
        if (spec.mode == AnimalReferencePoseBakeCore.Mode.ClipThenMirrorLeftLeg)
        {
            clip = FindClip(prefab, spec, errors, notes, out modelPath);
            if (clip == null)
            {
                return null;
            }
        }

        var inst = (GameObject)UnityEngine.Object.Instantiate(prefab);
        try
        {
            Transform root = inst.transform;
            root.position = Vector3.zero;
            root.rotation = Quaternion.identity;
            root.localScale = Vector3.one;
            var smrBones = new HashSet<Transform>();
            foreach (SkinnedMeshRenderer smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.bones == null)
                {
                    continue;
                }

                foreach (Transform b in smr.bones)
                {
                    if (b != null)
                    {
                        smrBones.Add(b);
                    }
                }
            }

            var nodes = new List<Transform>();
            foreach (Transform t in inst.GetComponentsInChildren<Transform>(true))
            {
                if (t != root)
                {
                    nodes.Add(t);
                }
            }

            var m = new AnimalReferencePoseBakeCore.Model
            {
                model = J(line.Substring(AnimalLinePrefix.Length)),
                clip = clip != null ? J(clip.name) : null,
                source = modelPath,
            };
            var defLocal = new Quaternion[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
            {
                Transform t = nodes[i];
                bool isSmr = smrBones.Contains(t);
                defLocal[i] = t.localRotation;
                m.nodes.Add(new AnimalReferencePoseBakeCore.Node
                {
                    n = J(t.name),
                    parent = J(t.parent != null ? t.parent.name : ""),
                    smr = isSmr,
                    def = PQ(root.InverseTransformPoint(t.position), Quaternion.Inverse(root.rotation) * t.rotation),
                    skin = isSmr ? SkinBind(root, t) : null,
                });
            }

            if (clip != null)
            {
                string[][] poses = SampleClipPoses(inst, clip, nodes, defLocal, out int moved, out string how);
                notes.Add(spec.key + ": " + how + " で " + J(clip.name) + " を t=0 でサンプル（局所回転が 0.01° より変わった Transform " + moved + " / " + nodes.Count +
                          "。角は double で測った）");
                if (moved == 0)
                {
                    errors.Add(spec.key + ": クリップ " + J(clip.name) + " をサンプルしても骨が 1 本も動かない（パスが当たらない?）");
                    return null;
                }

                for (int i = 0; i < nodes.Count; i++)
                {
                    m.nodes[i].pose = poses[i];
                }
            }

            return m;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(inst);
        }
    }

    // prefab（FBX の variant）の元の FBX から、名前が spec.clipSuffix で終わるクリップを 1 本だけ探す（__preview__ は除く）。
    private static AnimationClip FindClip(GameObject prefab, AnimalReferencePoseBakeCore.Spec spec, List<string> errors, List<string> notes, out string modelPath)
    {
        GameObject src = PrefabUtility.GetCorrespondingObjectFromOriginalSource(prefab);
        string derived = src != null ? AssetDatabase.GetAssetPath(src) : null;
        if (!string.IsNullOrEmpty(derived) && derived.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
        {
            modelPath = derived;
        }
        else
        {
            modelPath = spec.modelPath;
            notes.Add(spec.key + ": prefab の元が FBX として分からない（" + (derived ?? "null") + "）ので " + spec.modelPath + " を使う");
        }

        if (!string.Equals(modelPath, spec.modelPath, StringComparison.Ordinal))
        {
            notes.Add(spec.key + ": FBX が想定と違う: " + modelPath + "（想定 " + spec.modelPath + "）");
        }

        var names = new List<string>();
        var matches = new List<AnimationClip>();
        foreach (UnityEngine.Object o in AssetDatabase.LoadAllAssetsAtPath(modelPath))
        {
            if (o is AnimationClip c && !c.name.StartsWith("__preview__", StringComparison.Ordinal))
            {
                names.Add(c.name);
                if (c.name.EndsWith(spec.clipSuffix, StringComparison.Ordinal))
                {
                    matches.Add(c);
                }
            }
        }

        names.Sort(StringComparer.Ordinal);
        notes.Add(spec.key + ": " + modelPath + " のクリップ " + names.Count + " 本: " + string.Join(", ", names.ToArray()));
        if (matches.Count != 1)
        {
            errors.Add(spec.key + ": " + modelPath + " に名前が '" + spec.clipSuffix + "' で終わるクリップが " + matches.Count + " 本（1 本のはず）");
            return null;
        }

        return matches[0];
    }

    // 複製にクリップの t=0 をサンプルし、全 Transform の root 座標の姿勢を読む。AnimationClip.SampleAnimation で何も動かなければ AnimationMode でもう一度
    // （AnimationMode は止めると値を戻すので、止める前に読む）。位置・倍率も変わるが、使うのは回転だけ（位置は既定姿勢のまま測る）。
    private static string[][] SampleClipPoses(GameObject inst, AnimationClip clip, List<Transform> nodes, Quaternion[] defLocal, out int moved, out string how)
    {
        clip.SampleAnimation(inst, 0f);
        how = "AnimationClip.SampleAnimation";
        string[][] poses = ReadPoses(inst.transform, nodes, defLocal, out moved);
        if (moved > 0)
        {
            return poses;
        }

        bool started = false;
        try
        {
            if (!AnimationMode.InAnimationMode())
            {
                AnimationMode.StartAnimationMode();
                started = true;
            }

            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(inst, clip, 0f);
            AnimationMode.EndSampling();
            how = "AnimationMode.SampleAnimationClip";
            return ReadPoses(inst.transform, nodes, defLocal, out moved);
        }
        finally
        {
            if (started)
            {
                AnimationMode.StopAnimationMode();
            }
        }
    }

    // moved = 局所回転が既定姿勢から 0.01° より変わった Transform の数。2026-10-09（査読の指摘 5）: 角は double で測る
    // （Unity の Quaternion.Angle は float の dot > 1−1e-6 を 0 と返すので、約 0.16° 未満が 0 になり、前は実際には約 0.16° 以上を数えていた。
    // 査読の試し: 0.16° → 0、0.17° → 0.168）。
    private static string[][] ReadPoses(Transform root, List<Transform> nodes, Quaternion[] defLocal, out int moved)
    {
        root.position = Vector3.zero;
        root.rotation = Quaternion.identity;
        root.localScale = Vector3.one;
        var poses = new string[nodes.Count][];
        moved = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            Transform t = nodes[i];
            if (AngleDeg(t.localRotation, defLocal[i]) > 0.01)
            {
                moved++;
            }

            poses[i] = PQ(root.InverseTransformPoint(t.position), Quaternion.Inverse(root.rotation) * t.rotation);
        }

        return poses;
    }

    // 2 つの回転の角（度）を double で（AnimalLegSkinPoseBakeCore.AngleDeg = qu.angle。小さい角も 0 にならない）。
    private static double AngleDeg(Quaternion a, Quaternion b)
    {
        return AnimalLegSkinPoseBakeCore.AngleDeg(new double[] { a.x, a.y, a.z, a.w }, new double[] { b.x, b.y, b.z, b.w });
    }

    // 書く JSON を runtime と同じ読み方で新しい複製に書き、b4 と同じ数を測る（AnimalPoseApplier.ApplyLegReferenceSkinPose・TryParseLegSkinPoseModel・
    // TryReadLegSkinPoseQuaternion・FindLegSkinPoseBone の写し。runtime の非四足モードの読み手はまだ無いので、その設計 nq_design.txt 3.3(5) のとおり）。
    private static void VerifyReferenceOnFreshInstances(AnimalReferencePoseBakeCore.Result result, Dictionary<string, string> lineByKey, PreparedBake p, StringBuilder rep)
    {
        // 2026-10-09（査読の指摘 1）: MiniJson は NaN / Infinity / -Infinity の上で先へ進めず Parse が戻らない（Editor・バッチが固まる）。
        // 呼ぶ側（CheckRuntimeSafeJson）でも確かめているが、MiniJson に渡す直前にもう一度見る。
        if (AnimalLegSkinPoseBakeCore.NonFiniteNumbers(result.json).Count > 0)
        {
            p.fatal.Add("書く JSON に NaN / Infinity があるので、runtime の読み方での確かめ（MiniJson）をしなかった");
            return;
        }

        if (!(MiniJson.Parse(result.json) is Dictionary<string, object> rootObj) || !rootObj.TryGetValue("models", out object modelsObj) ||
            !(modelsObj is Dictionary<string, object> models))
        {
            p.warnings.Add("書く JSON を MiniJson（runtime の読み手）で読めない");
            return;
        }

        rep.Append("unity: runtime の読み方で新しい複製に書いて測った（MiniJson → float → 正規化、名前と親の名前、def と 0.5° 以内、親から順に ApplyWorldRotation。" +
                   "書かない判定は runtime と同じ Quaternion.Angle、出す角 defCheck・residual は double）\n");
        foreach (KeyValuePair<string, object> kv in models)
        {
            AnimalReferencePoseBakeCore.Spec spec = AnimalReferencePoseBakeCore.FindSpec(kv.Key);
            if (spec == null || !lineByKey.TryGetValue(kv.Key, out string line))
            {
                continue;
            }

            List<RuntimeRow> rows = ParseRowsLikeRuntime(kv.Value);
            if (rows == null)
            {
                p.warnings.Add(kv.Key + ": runtime と同じ読み方（TryParseLegSkinPoseModel の写し）で読めない");
                continue;
            }

            GameObject prefab = Resources.Load<GameObject>(line);
            if (prefab == null)
            {
                p.warnings.Add(kv.Key + ": 確かめ用に " + line + " を Resources.Load できない");
                continue;
            }

            var inst = (GameObject)UnityEngine.Object.Instantiate(prefab);
            try
            {
                Transform root = inst.transform;
                root.position = Vector3.zero;
                root.rotation = Quaternion.identity;
                root.localScale = Vector3.one;
                Transform[] bones = inst.GetComponentsInChildren<Transform>(true);
                Transform pelvis = null;
                foreach (Transform t in bones)
                {
                    if (t != root && t.name == "Pelvis")
                    {
                        pelvis = t;
                        break;
                    }
                }

                if (pelvis == null)
                {
                    p.warnings.Add(kv.Key + ": 確かめ用の複製に Pelvis が無い");
                    continue;
                }

                Quaternion pelvisDef = Quaternion.Inverse(root.rotation) * pelvis.rotation;
                string[] pelvisSkin = SkinBind(root, pelvis);
                var targets = new Transform[rows.Count];
                double maxDef = 0.0;
                string fail = null;
                for (int i = 0; i < rows.Count && fail == null; i++)
                {
                    Transform t = FindLikeRuntime(bones, rows[i].name, rows[i].parent);
                    if (t == null)
                    {
                        fail = "骨 " + rows[i].name + "（親 " + rows[i].parent + "）が見つからない";
                        break;
                    }

                    // 書かない判定は runtime（ApplyLegReferenceSkinPose）と同じ Quaternion.Angle > 0.5f。出す数は double（Quaternion.Angle は約 0.16° 未満を 0 と返す）
                    float d = Quaternion.Angle(t.localRotation, rows[i].defLocal);
                    if (d > 0.5f)
                    {
                        fail = "骨 " + rows[i].name + " の今の局所回転が def と " + d.ToString("F2", CultureInfo.InvariantCulture) + "° 違う";
                        break;
                    }

                    maxDef = Math.Max(maxDef, AngleDeg(t.localRotation, rows[i].defLocal));
                    targets[i] = t;
                }

                if (fail != null)
                {
                    p.warnings.Add(kv.Key + ": runtime の規則なら書かない（" + fail + "）");
                    continue;
                }

                double maxResidual = 0.0;
                for (int i = 0; i < rows.Count; i++)
                {
                    Transform t = targets[i];
                    Quaternion parentWorld = t.parent != null ? t.parent.rotation : Quaternion.identity;
                    TransformWriter.ApplyWorldRotation(t, parentWorld * rows[i].skinLocal);
                    maxResidual = Math.Max(maxResidual, AngleDeg(t.localRotation, rows[i].skinLocal));
                }

                var pos = new Dictionary<string, double[]>(StringComparer.Ordinal);
                var rot = new Dictionary<string, double[]>(StringComparer.Ordinal);
                foreach (Transform t in bones)
                {
                    if (t == root)
                    {
                        continue;
                    }

                    Vector3 pp = root.InverseTransformPoint(t.position);
                    Quaternion qq = Quaternion.Inverse(root.rotation) * t.rotation;
                    pos[J(t.name)] = new double[] { pp.x, pp.y, pp.z };
                    rot[J(t.name)] = new double[] { qq.x, qq.y, qq.z, qq.w };
                }

                AnimalReferencePoseBakeCore.Measures um = AnimalReferencePoseBakeCore.Measure(
                    spec, pos, rot, new double[] { pelvisDef.x, pelvisDef.y, pelvisDef.z, pelvisDef.w },
                    pelvisSkin != null ? AnimalReferencePoseBakeCore.QuatOfTokens(pelvisSkin) : null);
                rep.Append("  ").Append(kv.Key).Append(" bones=").Append(rows.Count).Append(" defCheck=")
                   .Append(maxDef.ToString("F4", CultureInfo.InvariantCulture)).Append("° residual=")
                   .Append(maxResidual.ToString("F4", CultureInfo.InvariantCulture)).Append("°\n");
                rep.Append(AnimalReferencePoseBakeCore.FormatMeasures("unity: 複製に runtime の規則で書いた", um)).Append('\n');
                if (result.final.TryGetValue(kv.Key, out AnimalReferencePoseBakeCore.Measures cm))
                {
                    List<string> diff = AnimalReferencePoseBakeCore.CompareMeasures(cm, um, 0.05, 0.1);
                    foreach (string d in diff)
                    {
                        p.warnings.Add(kv.Key + ": Unity の複製に runtime の規則で書いた結果が焼いた計算と合わない: " + d);
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(inst);
            }
        }
    }

    private sealed class RuntimeRow
    {
        public string name;
        public string parent;
        public Quaternion skinLocal;
        public Quaternion defLocal;
    }

    // AnimalPoseApplier.TryParseLegSkinPoseModel の写し（読めない骨が 1 本でもあれば null）。
    private static List<RuntimeRow> ParseRowsLikeRuntime(object node)
    {
        var list = new List<RuntimeRow>();
        if (!(node is Dictionary<string, object> model) || !model.TryGetValue("chains", out object chainsObj) || !(chainsObj is List<object> chains))
        {
            return null;
        }

        foreach (object chainObj in chains)
        {
            if (!(chainObj is Dictionary<string, object> chain) || !chain.TryGetValue("bones", out object bonesObj) || !(bonesObj is List<object> boneList))
            {
                return null;
            }

            foreach (object boneObj in boneList)
            {
                if (!(boneObj is Dictionary<string, object> bone) ||
                    !bone.TryGetValue("n", out object nameObj) || !(nameObj is string boneName) ||
                    !bone.TryGetValue("p", out object parentObj) || !(parentObj is string parentName) ||
                    !TryReadQuaternionLikeRuntime(bone, "skin", out Quaternion skinLocal) ||
                    !TryReadQuaternionLikeRuntime(bone, "def", out Quaternion defLocal))
                {
                    return null;
                }

                list.Add(new RuntimeRow { name = boneName, parent = parentName, skinLocal = skinLocal, defLocal = defLocal });
            }
        }

        return list.Count > 0 ? list : null;
    }

    // AnimalPoseApplier.TryReadLegSkinPoseQuaternion の写し。
    private static bool TryReadQuaternionLikeRuntime(Dictionary<string, object> bone, string key, out Quaternion q)
    {
        q = Quaternion.identity;
        if (!bone.TryGetValue(key, out object value) || !(value is List<object> a) || a.Count != 4)
        {
            return false;
        }

        var c = new float[4];
        for (int i = 0; i < 4; i++)
        {
            if (a[i] is double d)
            {
                c[i] = (float)d;
            }
            else if (a[i] is long l)
            {
                c[i] = l;
            }
            else
            {
                return false;
            }
        }

        float n = Mathf.Sqrt(c[0] * c[0] + c[1] * c[1] + c[2] * c[2] + c[3] * c[3]);
        if (!(n > 0.5f && n < 1.5f))
        {
            return false;
        }

        q = new Quaternion(c[0] / n, c[1] / n, c[2] / n, c[3] / n);
        return true;
    }

    // AnimalPoseApplier.FindLegSkinPoseBone の写し。
    private static Transform FindLikeRuntime(Transform[] bones, string name, string parentName)
    {
        for (int i = 0; i < bones.Length; i++)
        {
            Transform t = bones[i];
            if (t != null && t.parent != null && t.name == name && t.parent.name == parentName)
            {
                return t;
            }
        }

        return null;
    }

    // 焼く対象（model_index.txt）が今の prefab の並び（ModelResourceIndexGenerator と同じ規則）と違えば警告（Assets へ書くのを止める理由）。中身は変えない。
    private static void CheckModelIndexIsCurrent(List<string> warnings)
    {
        var expected = new List<string>();
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { AnimalModelFolder }))
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            string name = Path.GetFileNameWithoutExtension(assetPath);
            if (string.IsNullOrEmpty(name) || name.Length < 3 || !char.IsDigit(name[0]) || !char.IsDigit(name[1]) || name[2] != '_')
            {
                continue;
            }

            expected.Add(assetPath.Replace("Assets/Resources/", string.Empty).Replace(".prefab", string.Empty));
        }

        expected.Sort(string.CompareOrdinal);
        List<string> listed = ModelIndexAnimalLines();
        var missing = new List<string>();
        foreach (string e in expected)
        {
            if (!listed.Contains(e))
            {
                missing.Add(e);
            }
        }

        var extra = new List<string>();
        foreach (string l in listed)
        {
            if (!expected.Contains(l))
            {
                extra.Add(l);
            }
        }

        if (missing.Count > 0 || extra.Count > 0)
        {
            warnings.Add("model_index.txt の Models/Animal が今の prefab と違う（Tools/VisionGraft/モデル一覧を作り直す の後に焼き直す）: 載っていない [" +
                         string.Join(", ", missing.ToArray()) + "] 余分 [" + string.Join(", ", extra.ToArray()) + "]");
        }
    }

    // model_index.txt の "Models/Animal/" の行（前後の空白を除いたもの。CollectInventory・CollectReferenceInputs と同じ読み方）。
    private static List<string> ModelIndexAnimalLines()
    {
        var lines = new List<string>();
        foreach (string raw in File.ReadAllLines(FullPath(ModelIndexPath)))
        {
            string line = raw.Trim();
            if (line.StartsWith(AnimalLinePrefix, StringComparison.Ordinal))
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static List<string> Head(List<string> items, int n)
    {
        if (items.Count <= n)
        {
            return items;
        }

        var h = items.GetRange(0, n);
        h.Add("…ほか " + (items.Count - n) + " 件");
        return h;
    }

    private static string ProjectRoot()
    {
        return Path.GetDirectoryName(Application.dataPath);
    }

    private static string FullPath(string path)
    {
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ProjectRoot(), path));
    }

    private static bool IsInsideAssets(string path)
    {
        string assets = FullPath("Assets") + Path.DirectorySeparatorChar;
        return FullPath(path).StartsWith(assets, StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeUtf8(byte[] raw)
    {
        string s = Encoding.UTF8.GetString(raw);
        return s.Length > 0 && s[0] == '\uFEFF' ? s.Substring(1) : s;
    }

    private static void WriteText(string path, string text)
    {
        string full = FullPath(path);
        string dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(full, text, Utf8NoBom);
        string assets = FullPath("Assets") + Path.DirectorySeparatorChar;
        if (full.StartsWith(assets, StringComparison.OrdinalIgnoreCase))
        {
            AssetDatabase.ImportAsset("Assets/" + full.Substring(assets.Length).Replace('\\', '/'), ImportAssetOptions.ForceUpdate);
        }
    }

    private static string Sha256Hex(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] h = sha.ComputeHash(bytes);
            var sb = new StringBuilder();
            foreach (byte b in h)
            {
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }
    }
}

// bake_leg_skin.py の移植（UnityEngine を使わない。Unity の外の試験台でも同じコードを動かすため）。
// 行の対応は各メソッドの頭に書いた（bake_leg_skin.py の行番号）。
public static class AnimalLegSkinPoseBakeCore
{
    // ---- 棚卸しの 1 体分（rest_rotation_inventory.cs の animal_rest_rotations.json の 1 要素。数は書き出した文字列のまま持つ） ----
    public sealed class InvBone
    {
        public string n;        // 骨の名前（J を通したもの）
        public string parent;   // 親の名前（無ければ ""）
        public string[] def;    // [px,py,pz,qx,qy,qz,qw] の 7 つの文字列
        public string[] skin;   // 同じ形。bindposes に無い骨は null（棚卸しの null）
    }

    public sealed class InvModel
    {
        public string model;        // model_index.txt の "Models/Animal/" の後ろ（J を通したもの）
        public string rigRoot;      // 棚卸しを書き直すときだけ使う（bake は読まない）
        public string[] rigRootDef; // 同上
        public readonly List<InvBone> bones = new List<InvBone>();
        // runtime と同じ Transform の名前の全部（prefab を開いたときだけ。JSON には書かない）。null なら骨と親の名前で代える（ResolveRoles）
        public HashSet<string> transformNames;
    }

    public sealed class Result
    {
        public string json;     // animal_leg_skin_pose.json の中身
        public string report;   // bake_leg_skin_out.txt と同じ形（改行は LF）
        public int modelCount;
        public int excludedCount;
        public readonly List<string> notes = new List<string>();   // 表の行を runtime と同じく使わなかったモデルなど（2026-10-08。報告の末尾にも出す）
    }

    // bake_leg_skin.py 213〜216 行の文（そのまま）。
    public const string Meaning =
        "脚の鎖（肩甲骨→脚の役→指）の skin 姿勢（SkinnedMeshRenderer.sharedMesh.bindposes = メッシュが歪まない姿勢）の局所回転 [qx,qy,qz,qw]。" +
        "def は棚卸しの既定姿勢の局所回転（runtime が今の局所回転と比べ、0.5° より違えばモデルが変わったとみて書かない）。" +
        "smalLegReferenceSkinPose（AnimalPoseApplier.ApplyLegReferenceSkinPose）が smalLegReferenceSkinPoseModels に載ったモデルだけ、" +
        "リグのキャッシュを作るときに親から順に一度だけ書く。キーは prefab 名から先頭の数字_を外したもの。";

    // bake_leg_skin.py 41 行: 体長比。v09 と同じ（|L−R| > 0.05 を非対称と数えた）
    private const double SkinAsymLimit = 0.05;

    // bake_leg_skin.py 43〜47 行（CANON。役 → 正規名）
    private static readonly string[,] CanonRoles =
    {
        { "frontLUpper", "front_l_upper" }, { "frontLLower", "front_l_lower" }, { "frontLPaw", "front_l_paw" },
        { "frontRUpper", "front_r_upper" }, { "frontRLower", "front_r_lower" }, { "frontRPaw", "front_r_paw" },
        { "rearLUpper", "rear_l_upper" }, { "rearLLower", "rear_l_lower" }, { "rearLPaw", "rear_l_paw" }, { "rearLToe", "rear_l_toe" },
        { "rearRUpper", "rear_r_upper" }, { "rearRLower", "rear_r_lower" }, { "rearRPaw", "rear_r_paw" }, { "rearRToe", "rear_r_toe" },
        { "spine", "spine" }, { "neck", "neck" }, { "head", "head" }, { "tailBase", "tail_base" }, { "tailMid", "tail_mid" }, { "tailTip", "tail_tip" },
    };

    // bake_leg_skin.py 48〜49 行（LEGS: 脚, 上の役, 反対側の上の役）
    private static readonly string[,] Legs =
    {
        { "frontL", "frontLUpper", "frontRUpper" }, { "frontR", "frontRUpper", "frontLUpper" },
        { "rearL", "rearLUpper", "rearRUpper" }, { "rearR", "rearRUpper", "rearLUpper" },
    };

    // bake_leg_skin.py 50〜51 行（LEG_ROLES）
    private static readonly Dictionary<string, string[]> LegRoles = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        { "frontL", new[] { "frontLUpper", "frontLLower", "frontLPaw" } },
        { "frontR", new[] { "frontRUpper", "frontRLower", "frontRPaw" } },
        { "rearL", new[] { "rearLUpper", "rearLLower", "rearLPaw", "rearLToe" } },
        { "rearR", new[] { "rearRUpper", "rearRLower", "rearRPaw", "rearRToe" } },
    };

    private static readonly string[] UpperRoles = { "frontLUpper", "frontRUpper", "rearLUpper", "rearRUpper" };

    private sealed class Bone
    {
        public string parent;
        public double[] def;    // 7 つ（Python の json → float と同じ値）。null あり
        public double[] skin;
    }

    private sealed class BakedBone
    {
        public string n;
        public string p;
        public double[] skin;   // canon_q 済み（丸める前。書くときに "%.7f" % round(v, 7) と同じ丸め）
        public double[] def;
        public double delta;    // round(angle, 2)（報告だけ）
    }

    private sealed class Chain
    {
        public string leg;
        public string root;
        public string girdle;
        public List<BakedBone> bones;
    }

    private sealed class ModelEntry
    {
        public string prefab;
        public double defLr;
        public double skinLr;
        public List<Chain> chains;
    }

    // ---- bake()（bake_leg_skin.py 96〜207 行） ----
    // table は AnimalLegMappingFix.Table（bake_leg_skin.py は AnimalLegMappingFix.cs を正規表現で読んでいた。同じ中身）、key は AnimalLegMappingFix.Key。
    public static Result Bake(List<InvModel> inventory, IDictionary<string, string> table, Func<string, string> key, string sourceText)
    {
        var models = new Dictionary<string, ModelEntry>(StringComparer.Ordinal);
        var excluded = new Dictionary<string, string>(StringComparer.Ordinal);
        var log = new List<string>();
        var notes = new List<string>();
        foreach (InvModel m in inventory)
        {
            string prefab = LastPathSegment(m.model);                        // 103: m["model"].split("/")[-1]
            string k = key(prefab);                                          // 104
            Dictionary<string, string> roles = ResolveRoles(table, k, m.transformNames ?? NamesOf(m), out string rowNote);   // 105〜109（runtime と同じ読み方）
            if (rowNote != null)
            {
                notes.Add(prefab + ": " + rowNote);
            }

            var b = new Dictionary<string, Bone>(StringComparer.Ordinal);    // 110〜115: 同じ名前は後の骨が勝つ
            var dup = new HashSet<string>(StringComparer.Ordinal);
            foreach (InvBone ib in m.bones)
            {
                if (b.ContainsKey(ib.n))
                {
                    dup.Add(ib.n);
                }

                b[ib.n] = new Bone { parent = ib.parent, def = ParseTokens(ib.def), skin = ParseTokens(ib.skin) };
            }

            var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // 116〜118: 親の名前 → 子の名前（棚卸しの順）
            foreach (InvBone ib in m.bones)
            {
                if (!children.TryGetValue(ib.parent, out List<string> list))
                {
                    list = new List<string>();
                    children[ib.parent] = list;
                }

                list.Add(ib.n);
            }

            var missing = new List<string>();                                // 120〜123
            foreach (string r in UpperRoles)
            {
                if (!b.ContainsKey(roles[r]))
                {
                    missing.Add(roles[r]);
                }
            }

            if (missing.Count > 0)
            {
                excluded[prefab] = "上腕・上腿の役が棚卸しに無い（" + string.Join(",", missing.ToArray()) + "）";
                continue;
            }

            double? skinLr = FrontFetlockLr(b, roles, true);                 // 124〜131
            double? defLr = FrontFetlockLr(b, roles, false);
            if (skinLr == null)
            {
                excluded[prefab] = "前の球節（front_l_paw / front_r_paw）の skin が無く、skin 姿勢の左右を確かめられない";
                continue;
            }

            if (Math.Abs(skinLr.Value) > SkinAsymLimit)
            {
                excluded[prefab] = "skin 姿勢そのものが左右非対称（前の球節の前後差 " + FormatFixed(skinLr.Value, 3, true) +
                                   " 体長、|L−R| > " + FormatFixed(SkinAsymLimit, 2, false) + "）";
                continue;
            }

            var chains = new List<Chain>();                                  // 148〜202
            string problem = null;
            var otherRolesAll = new HashSet<string>(roles.Values, StringComparer.Ordinal);
            for (int li = 0; li < Legs.GetLength(0) && problem == null; li++)
            {
                string leg = Legs[li, 0];
                string up = roles[Legs[li, 1]];
                if (!TryLca(b, up, roles[Legs[li, 2]], out string girdle) || !TryAncestors(b, up, out List<string> path))
                {
                    // 親の名前をたどると輪になる（TryAncestors が本当の輪だけを見分ける）。Python の anc() はここで止まらない（無限ループ）。
                    problem = leg + ": 骨の名前の親子が輪になっている（" + up + " から上へたどれない）";
                    break;
                }

                int gi = girdle == null ? -1 : path.IndexOf(girdle);
                if (gi < 1)
                {
                    problem = leg + ": girdle が決まらない";
                    break;
                }

                string root = path[gi - 1];
                if (!b.TryGetValue(girdle, out Bone gb) || gb.def == null || gb.skin == null)
                {
                    problem = leg + ": girdle " + girdle + " に def / skin が無い";
                    break;
                }

                // 親が先・兄弟は棚卸しの順（parent-first の幅優先。st.pop(0)、訪れた印なし）。
                // 2026-10-08（査読の指摘 4）: 上限 8N+64 をやめ、root からたどれる名前の親子に本当の輪があるときだけ止める。Python の幅優先が終わらないのは
                // この輪があるときだけなので、輪が無ければ（同じ名前の骨が何組あっても）Python と同じ順・同じ長さの order になり、同じ除外理由を書く。
                if (HasNameCycle(children, root))
                {
                    problem = leg + ": 鎖 " + root + " の下で骨の名前が輪になっている";
                    break;
                }

                var order = new List<string>();
                var queue = new List<string> { root };
                int head = 0;
                while (head < queue.Count)
                {
                    string x = queue[head++];
                    order.Add(x);
                    if (children.TryGetValue(x, out List<string> ch))
                    {
                        queue.AddRange(ch);
                    }

                    if (order.Count > OrderGuard)
                    {
                        // 輪は無いが、同じ名前の骨が多すぎて order が組み合わせで膨らむ（Python も現実的な時間では終わらない）。違う JSON は書かずに止める。
                        throw new InvalidOperationException(prefab + " " + leg + ": 鎖 " + root + " の幅優先が " + OrderGuard + " 個を超えた（同じ名前の骨が多すぎる）");
                    }
                }

                var mine = new HashSet<string>(StringComparer.Ordinal);
                foreach (string r in LegRoles[leg])
                {
                    mine.Add(roles[r]);
                }

                var bad = new List<string>();
                foreach (string x in order)
                {
                    if (otherRolesAll.Contains(x) && !mine.Contains(x))
                    {
                        bad.Add(x);
                    }
                }

                if (bad.Count > 0)
                {
                    problem = leg + ": 鎖 " + root + " に別の役 " + PyListRepr(bad) + " が入る";
                    break;
                }

                var notIn = new List<string>();
                foreach (string r in LegRoles[leg])
                {
                    if (b.ContainsKey(roles[r]) && !order.Contains(roles[r]))
                    {
                        notIn.Add(roles[r]);
                    }
                }

                if (notIn.Count > 0)
                {
                    problem = leg + ": 役の骨 " + PyListRepr(notIn) + " が鎖 " + root + " の下に無い";
                    break;
                }

                var dups = new List<string>();
                foreach (string x in order)
                {
                    if (dup.Contains(x))
                    {
                        dups.Add(x);
                    }
                }

                if (dups.Count > 0)
                {
                    problem = leg + ": 名前が重複 " + PyListRepr(dups);
                    break;
                }

                var bones = new List<BakedBone>();
                foreach (string x in order)
                {
                    Bone bx = b[x];
                    string par = bx.parent;
                    if (bx.def == null || bx.skin == null || !b.TryGetValue(par, out Bone bp) || bp.def == null || bp.skin == null)
                    {
                        problem = leg + ": " + x + " か親 " + par + " に def / skin が無い";
                        break;
                    }

                    double[] qsP = Norm(Quat(bp.skin));                      // 189〜196
                    double[] qsB = Norm(Quat(bx.skin));
                    double[] qdP = Norm(Quat(bp.def));
                    double[] qdB = Norm(Quat(bx.def));
                    double[] skinLocal = CanonQ(Mul(Inv(qsP), qsB));
                    double[] defLocal = CanonQ(Mul(Inv(qdP), qdB));
                    bones.Add(new BakedBone { n = x, p = par, skin = skinLocal, def = defLocal, delta = PyRound(AngleDeg(skinLocal, defLocal), 2) });
                }

                if (problem != null)
                {
                    break;
                }

                chains.Add(new Chain { leg = leg, root = root, girdle = girdle, bones = bones });
            }

            if (problem != null)
            {
                excluded[prefab] = problem;
                continue;
            }

            if (defLr == null)
            {
                // Python は round(None, 3) で止まる（def は棚卸しに必ずあるので起きない）。
                throw new InvalidOperationException(prefab + ": 既定姿勢の前の球節の左右が計算できない");
            }

            models[k] = new ModelEntry { prefab = prefab, defLr = defLr.Value, skinLr = skinLr.Value, chains = chains };   // 203
            var parts = new List<string>();                                                                                    // 204〜206
            foreach (Chain c in chains)
            {
                double maxDelta = c.bones[0].delta;
                foreach (BakedBone bb in c.bones)
                {
                    if (bb.delta > maxDelta)
                    {
                        maxDelta = bb.delta;
                    }
                }

                parts.Add(c.leg + " " + c.root + "(<-" + c.girdle + ") n=" + c.bones.Count.ToString(CultureInfo.InvariantCulture) +
                          " maxDelta=" + FormatFixed(maxDelta, 1, false));
            }

            log.Add(prefab.PadRight(20) + " key=" + k.PadRight(16) + " def L-R " + FormatFixed(defLr.Value, 3, true) +
                    " skin L-R " + FormatFixed(skinLr.Value, 3, true) + "  " + string.Join(" | ", parts.ToArray()));
        }

        Result result = Write(models, excluded, log, sourceText);
        if (notes.Count > 0)
        {
            // 今の表と 10/04 の棚卸しでは notes は空（報告は bake_leg_skin_out.txt と同じのまま）。行を使わなかったときだけ末尾に足す。
            result.notes.AddRange(notes);
            var rep = new StringBuilder(result.report);
            foreach (string n in notes)
            {
                rep.Append("note: ").Append(n).Append('\n');
            }

            result.report = rep.ToString();
        }

        return result;
    }

    // 幅優先の order がこれを超えたら止める（輪が無くても、同じ名前の骨が何組もあると組み合わせで膨らむ。Lynx を 4 組にしても 17749）。
    // 上限で別の除外理由を書くのではなく例外で焼くのをやめる（バッチは exit 1、何も書かない）。Python はこの大きさでは終わらないので結果は違わない。
    // Editor が止まったりメモリを使い切ったりしないための安全弁（2026-10-08）。
    private const int OrderGuard = 10000000;

    // 焼く側の役のキー（AnimalBoneMappingOverride のフィールド名 = AnimalLegMappingFix.TrySetOverrideField の 20 個と同じ）。
    internal static List<string> RoleKeys()
    {
        var keys = new List<string>();
        for (int i = 0; i < CanonRoles.GetLength(0); i++)
        {
            keys.Add(CanonRoles[i, 0]);
        }

        return keys;
    }

    private static bool IsRoleKey(string key)
    {
        for (int i = 0; i < CanonRoles.GetLength(0); i++)
        {
            if (string.Equals(CanonRoles[i, 0], key, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // 棚卸しのファイルから焼くとき（Transform の名前の全部が分からない）の代わり: 骨の名前と親の名前。
    private static HashSet<string> NamesOf(InvModel m)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (InvBone ib in m.bones)
        {
            names.Add(ib.n);
            if (!string.IsNullOrEmpty(ib.parent))
            {
                names.Add(ib.parent);
            }
        }

        return names;
    }

    // 105〜109 の代わり（2026-10-08、査読の指摘 5）: roles = dict(CANON) を表の行で上書きする。行は runtime
    // （StreamingStereoVideoPlayer.Playback.partial.cs の脚の割り当ての修正）と同じに読む: ';' で区切り、'=' が無いか先頭にある組は飛ばし、
    // キーと骨の名前は Trim。知らないキー（AnimalLegMappingFix.IsOverrideKey と同じ 20 個に無い）か、モデルに無い骨の名前が 1 つでもあれば
    // runtime はその行を丸ごと使わない（prefab の上書きと正規名のまま）ので、ここも行を使わず正規名で焼き、note に理由を返す。同じキーは後が勝つ。
    // 骨の名前の有無は names で見る: prefab を開いたときは runtime と同じ Transform の名前の全部、棚卸しのファイルから焼くときは骨と親の名前
    // （骨でも親でもない Transform の名前は分からないので、その名前を使う行は runtime と違って捨てる）。
    // bake_leg_skin.py は区切りが「役=骨」でないと止まった。今の 23 行はどれも「役=骨」だけで、10/04 の棚卸しのどのモデルでも捨てる行は無い（harness で確かめた）。
    internal static Dictionary<string, string> ResolveRoles(IDictionary<string, string> table, string k, ICollection<string> names, out string note)
    {
        note = null;
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < CanonRoles.GetLength(0); i++)
        {
            roles[CanonRoles[i, 0]] = CanonRoles[i, 1];
        }

        if (table == null || !table.TryGetValue(k, out string row) || row == null)
        {
            return roles;
        }

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (string pair in row.Split(new[] { ';' }))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string roleKey = pair.Substring(0, eq).Trim();
            string value = pair.Substring(eq + 1).Trim();
            bool known = IsRoleKey(roleKey);
            if (!known || (names != null && !names.Contains(value)))
            {
                note = "AnimalLegMappingFix.Table[\"" + k + "\"] の '" + roleKey + "=" + value + "' の" + (known ? "骨がこのモデルに無い" : "キーを知らない") +
                       "ので、runtime と同じく行を丸ごと使わず正規名で焼いた";
                return roles;
            }

            pairs.Add(new KeyValuePair<string, string>(roleKey, value));
        }

        foreach (KeyValuePair<string, string> kv in pairs)
        {
            roles[kv.Key] = kv.Value;
        }

        return roles;
    }

    // root から名前の親子（children。同じ名前の骨の子はまとめて 1 つの名前の子になる）をたどって、たどり中の名前へ戻る辺（輪）があるか。
    // 自分自身が親の骨も輪。反復の深さ優先（たどり中の印 onStack と、たどり終えた印 done）。
    internal static bool HasNameCycle(Dictionary<string, List<string>> children, string root)
    {
        var onStack = new HashSet<string>(StringComparer.Ordinal) { root };
        var done = new HashSet<string>(StringComparer.Ordinal);
        var stack = new List<KeyValuePair<string, int>> { new KeyValuePair<string, int>(root, 0) };
        while (stack.Count > 0)
        {
            int top = stack.Count - 1;
            string x = stack[top].Key;
            int i = stack[top].Value;
            if (children.TryGetValue(x, out List<string> ch) && i < ch.Count)
            {
                stack[top] = new KeyValuePair<string, int>(x, i + 1);
                string y = ch[i];
                if (onStack.Contains(y))
                {
                    return true;
                }

                if (done.Contains(y))
                {
                    continue;
                }

                onStack.Add(y);
                stack.Add(new KeyValuePair<string, int>(y, 0));
            }
            else
            {
                stack.RemoveAt(top);
                onStack.Remove(x);
                done.Add(x);
            }
        }

        return false;
    }

    // 79〜93: (front_l_paw − front_r_paw)・前 / 体長。前 = 上腕の役の中点 − 上腿の役の中点（水平）。名前は役ではなく正規名 front_l_paw / front_r_paw。
    private static double? FrontFetlockLr(Dictionary<string, Bone> b, Dictionary<string, string> roles, bool skin)
    {
        if (!b.ContainsKey("front_l_paw") || !b.ContainsKey("front_r_paw"))
        {
            return null;
        }

        string[] names = { "front_l_paw", "front_r_paw", roles["frontLUpper"], roles["frontRUpper"], roles["rearLUpper"], roles["rearRUpper"] };
        foreach (string n in names)
        {
            if (Pose(b[n], skin) == null)
            {
                return null;
            }
        }

        double[] fl = Pose(b[roles["frontLUpper"]], skin);
        double[] fr = Pose(b[roles["frontRUpper"]], skin);
        double[] rl = Pose(b[roles["rearLUpper"]], skin);
        double[] rr = Pose(b[roles["rearRUpper"]], skin);
        var fwd = new double[3];
        for (int i = 0; i < 3; i++)
        {
            double fc = (fl[i] + fr[i]) / 2;
            double rc = (rl[i] + rr[i]) / 2;
            fwd[i] = fc - rc;
        }

        fwd[1] = 0.0;
        double bl = Math.Sqrt(Dot(fwd, fwd));
        for (int i = 0; i < 3; i++)
        {
            fwd[i] = fwd[i] / bl;
        }

        double[] pl = Pose(b["front_l_paw"], skin);
        double[] pr = Pose(b["front_r_paw"], skin);
        var d = new double[3];
        for (int i = 0; i < 3; i++)
        {
            d[i] = pl[i] - pr[i];
        }

        return Dot(d, fwd) / bl;
    }

    private static double[] Pose(Bone bone, bool skin)
    {
        return skin ? bone.skin : bone.def;
    }

    // 133〜139: anc(n) = [n, 親, 親の親, …, 棚卸しに無い最初の名前]。名前が輪になったら false（Python は止まらない）。
    private static bool TryAncestors(Dictionary<string, Bone> b, string n, out List<string> path)
    {
        path = new List<string>();
        while (b.TryGetValue(n, out Bone bone))
        {
            path.Add(n);
            if (path.Count > b.Count + 1)
            {
                return false;
            }

            n = bone.parent;
        }

        path.Add(n);
        return true;
    }

    // 141〜146: lca(a, b) = anc(a) を下から見て、anc(b) に最初に入っているもの。無ければ null。
    private static bool TryLca(Dictionary<string, Bone> b, string a, string other, out string lca)
    {
        lca = null;
        if (!TryAncestors(b, other, out List<string> pb) || !TryAncestors(b, a, out List<string> pa))
        {
            return false;
        }

        var sb = new HashSet<string>(pb, StringComparer.Ordinal);
        foreach (string x in pa)
        {
            if (sb.Contains(x))
            {
                lca = x;
                break;
            }
        }

        return true;
    }

    // ---- write()（bake_leg_skin.py 210〜255 行）。1 骨 1 行の手書きの JSON ----
    private static Result Write(Dictionary<string, ModelEntry> models, Dictionary<string, string> excluded, List<string> log, string sourceText)
    {
        var lines = new List<string>();
        lines.Add("{");
        lines.Add("  \"_meaning\": " + PyJsonString(Meaning, false) + ",");
        lines.Add("  \"_source\": " + PyJsonString(sourceText ?? "", false) + ",");
        lines.Add("  \"_excluded\": {");
        var ex = new List<string>(excluded.Keys);
        ex.Sort(StringComparer.Ordinal);
        for (int i = 0; i < ex.Count; i++)
        {
            lines.Add("    " + PyJsonString(ex[i], false) + ": " + PyJsonString(excluded[ex[i]], false) + (i + 1 < ex.Count ? "," : ""));
        }

        lines.Add("  },");
        lines.Add("  \"models\": {");
        var mk = new List<string>(models.Keys);
        mk.Sort(StringComparer.Ordinal);
        for (int mi = 0; mi < mk.Count; mi++)
        {
            ModelEntry e = models[mk[mi]];
            lines.Add("    " + PyJsonString(mk[mi], true) + ": {");
            lines.Add("      \"prefab\": " + PyJsonString(e.prefab, true) + ", \"defFrontFetlockLR\": " + PyJsonRound3(e.defLr) +
                      ", \"skinFrontFetlockLR\": " + PyJsonRound3(e.skinLr) + ",");
            lines.Add("      \"chains\": [");
            for (int ci = 0; ci < e.chains.Count; ci++)
            {
                Chain c = e.chains[ci];
                lines.Add("        { \"leg\": " + PyJsonString(c.leg, true) + ", \"root\": " + PyJsonString(c.root, true) +
                          ", \"girdle\": " + PyJsonString(c.girdle, true) + ", \"bones\": [");
                for (int bi = 0; bi < c.bones.Count; bi++)
                {
                    BakedBone bb = c.bones[bi];
                    lines.Add("          { \"n\": " + PyJsonString(bb.n, true) + ", \"p\": " + PyJsonString(bb.p, true) +
                              ", \"skin\": [" + Join7(bb.skin) + "], \"def\": [" + Join7(bb.def) + "] }" + (bi + 1 < c.bones.Count ? "," : ""));
                }

                lines.Add("        ] }" + (ci + 1 < e.chains.Count ? "," : ""));
            }

            lines.Add("      ]");
            lines.Add("    }" + (mi + 1 < mk.Count ? "," : ""));
        }

        lines.Add("  }");
        lines.Add("}");
        string text = string.Join("\n", lines.ToArray()) + "\n";
        new JsonParser(text).ParseDocument();   // 245: 書く前に JSON として読めることを確かめる（読めなければ例外）

        var rep = new StringBuilder();
        rep.Append("baked ").Append(models.Count.ToString(CultureInfo.InvariantCulture)).Append(" models, excluded ")
           .Append(excluded.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (string line in log)
        {
            rep.Append(line).Append('\n');
        }

        rep.Append("excluded:\n");
        foreach (string p in ex)
        {
            rep.Append("  ").Append(p.PadRight(20)).Append(' ').Append(excluded[p]).Append('\n');
        }

        rep.Append("json bytes ").Append(new UTF8Encoding(false).GetByteCount(text).ToString(CultureInfo.InvariantCulture)).Append('\n');
        return new Result { json = text, report = rep.ToString(), modelCount = models.Count, excludedCount = excluded.Count };
    }

    // ", ".join("%.7f" % v for v in round(v, 7) の列)
    internal static string Join7(double[] q)
    {
        var parts = new string[q.Length];
        for (int i = 0; i < q.Length; i++)
        {
            parts[i] = FormatFixed(q[i], 7, false);
        }

        return string.Join(", ", parts);
    }

    internal static string LastPathSegment(string s)
    {
        int i = s.LastIndexOf('/');
        return i < 0 ? s : s.Substring(i + 1);
    }

    // ---- qu.py の式（norm / inv / mul / angle）と canon_q（74〜76 行）。double で、Python と同じ順に計算する ----
    internal static double Dot(double[] a, double[] b)
    {
        double s = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            s += a[i] * b[i];
        }

        return s;
    }

    internal static double[] Quat(double[] pq)
    {
        return new[] { pq[3], pq[4], pq[5], pq[6] };
    }

    // qu.norm: q / sqrt(q·q)
    internal static double[] Norm(double[] q)
    {
        double n = Math.Sqrt(Dot(q, q));
        return new[] { q[0] / n, q[1] / n, q[2] / n, q[3] / n };
    }

    // qu.inv: [-x, -y, -z, w] / (q·q)
    internal static double[] Inv(double[] q)
    {
        double n = Dot(q, q);
        return new[] { -q[0] / n, -q[1] / n, -q[2] / n, q[3] / n };
    }

    // qu.mul: Hamilton 積（Unity の a * b）
    internal static double[] Mul(double[] a, double[] b)
    {
        double ax = a[0], ay = a[1], az = a[2], aw = a[3];
        double bx = b[0], by = b[1], bz = b[2], bw = b[3];
        return new[]
        {
            aw * bx + ax * bw + ay * bz - az * by,
            aw * by + ay * bw + az * bx - ax * bz,
            aw * bz + az * bw + ax * by - ay * bx,
            aw * bw - ax * bx - ay * by - az * bz,
        };
    }

    // canon_q: 正規化して w < 0 なら符号を反転（w が -0 のときは反転しない。比較なので Python と同じ）
    internal static double[] CanonQ(double[] q)
    {
        double[] n = Norm(q);
        return n[3] < 0 ? new[] { -n[0], -n[1], -n[2], -n[3] } : n;
    }

    // qu.angle: degrees(2 acos(min(1, |norm(a)·norm(b)|)))。math.degrees(x) = x * (180 / π)
    internal static double AngleDeg(double[] a, double[] b)
    {
        double d = Math.Abs(Dot(Norm(a), Norm(b)));
        return 2 * Math.Acos(d < 1.0 ? d : 1.0) * (180.0 / Math.PI);
    }

    // ---- Python の数の読み書きと同じ結果を出すための関数 ----
    private static readonly double NegativeZero = BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000UL));

    // Python の float("nan") と同じビット（.NET の double.NaN は符号ビットが立っている。値としての違いは無い）
    private static readonly double PythonNaN = BitConverter.Int64BitsToDouble(0x7FF8000000000000L);

    // char 1 つの TrimStart / TrimEnd / Split は .NET Standard 2.1 にしか無いので配列で渡す（Unity の外の .NET Framework でも同じコードを試すため）
    private static readonly char[] Zero = { '0' };

    private static readonly double[] Pow10 =
    {
        1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22,
    };

    internal static double[] ParseTokens(string[] tokens)
    {
        if (tokens == null)
        {
            return null;
        }

        if (tokens.Length != 7)
        {
            throw new FormatException("棚卸しの姿勢が 7 つの数でない（" + tokens.Length + " 個）");
        }

        var v = new double[7];
        for (int i = 0; i < 7; i++)
        {
            v[i] = ParseNumberToken(tokens[i]);
        }

        return v;
    }

    // Python の json.load の数 → numpy の float と同じ値にする。
    //   小数点も指数も無い数は json が int にする → float(int)。"-0" も +0.0 になる。
    //   それ以外は float(文字列) = 正しく丸めた double（有効 15 桁以下・10 の指数 ±22 以内は整数 ÷ 10^k の 1 回の割り算で正しく丸まる。
    //   棚卸しの数は "0.######" の 7 桁以下なので必ずこちら。それ以外は DecimalToDouble で厳密に丸める）。NaN / Infinity / -Infinity も json と同じ。
    //   .NET / Mono の double.Parse は使わない（実装によって丸めが違うことがある）。
    internal static double ParseNumberToken(string tok)
    {
        if (tok == "NaN")
        {
            return PythonNaN;
        }

        if (tok == "Infinity")
        {
            return double.PositiveInfinity;
        }

        if (tok == "-Infinity")
        {
            return double.NegativeInfinity;
        }

        if (tok.IndexOf('.') < 0 && tok.IndexOf('e') < 0 && tok.IndexOf('E') < 0)
        {
            BigInteger bi = BigInteger.Parse(tok, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            if (bi.IsZero)
            {
                return 0.0;
            }

            return DecimalToDouble(BigInteger.Abs(bi), 0, bi.Sign < 0);
        }

        return ParseDecimal(tok);
    }

    private static double ParseDecimal(string tok)
    {
        int p = 0;
        bool neg = false;
        if (p < tok.Length && (tok[p] == '-' || tok[p] == '+'))
        {
            neg = tok[p] == '-';
            p++;
        }

        var digits = new StringBuilder();
        int exp10 = 0;
        bool point = false;
        for (; p < tok.Length; p++)
        {
            char c = tok[p];
            if (c >= '0' && c <= '9')
            {
                digits.Append(c);
                if (point)
                {
                    exp10--;
                }
            }
            else if (c == '.' && !point)
            {
                point = true;
            }
            else if (c == 'e' || c == 'E')
            {
                exp10 += int.Parse(tok.Substring(p + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                break;
            }
            else
            {
                throw new FormatException("数が読めない: " + tok);
            }
        }

        string d = digits.ToString().TrimStart(Zero);
        if (d.Length == 0)
        {
            return neg ? NegativeZero : 0.0;
        }

        int tz = 0;
        while (tz < d.Length - 1 && d[d.Length - 1 - tz] == '0')
        {
            tz++;
        }

        if (tz > 0)
        {
            d = d.Substring(0, d.Length - tz);
            exp10 += tz;
        }

        if (d.Length <= 15 && exp10 >= -22 && exp10 <= 22)
        {
            double v = long.Parse(d, NumberStyles.None, CultureInfo.InvariantCulture);
            v = exp10 >= 0 ? v * Pow10[exp10] : v / Pow10[-exp10];
            return neg ? -v : v;
        }

        return DecimalToDouble(BigInteger.Parse(d, NumberStyles.None, CultureInfo.InvariantCulture), exp10, neg);
    }

    private static readonly BigInteger TwoPow52 = BigInteger.One << 52;
    private static readonly BigInteger TwoPow53 = BigInteger.One << 53;

    // d × 10^exp10（d > 0）に最も近い double（偶数丸め、非正規数・桁あふれも IEEE と同じ）。Python の float(str) / float(int) と同じ値。
    private static double DecimalToDouble(BigInteger d, int exp10, bool neg)
    {
        if (d.IsZero)
        {
            return neg ? NegativeZero : 0.0;
        }

        BigInteger num = exp10 >= 0 ? d * BigInteger.Pow(10, exp10) : d;
        BigInteger den = exp10 >= 0 ? BigInteger.One : BigInteger.Pow(10, -exp10);
        // num / den = q × 2^e、2^52 <= q < 2^53 になる e を探す（最初の見積もりから多くて 1 つずれる）
        int e = BitLength(num) - BitLength(den) - 53;
        BigInteger q = BigInteger.Zero;
        BigInteger r = BigInteger.Zero;
        BigInteger div = BigInteger.One;
        for (int guard = 0; guard < 4; guard++)
        {
            if (e < -1074)
            {
                e = -1074;   // 非正規数: 指数を固定して仮数を減らす
            }

            BigInteger n2 = e >= 0 ? num : num << -e;
            div = e >= 0 ? den << e : den;
            q = BigInteger.DivRem(n2, div, out r);
            if (q >= TwoPow53)
            {
                e++;
                continue;
            }

            if (q < TwoPow52 && e > -1074)
            {
                e--;
                continue;
            }

            break;
        }

        int c = (r << 1).CompareTo(div);
        if (c > 0 || (c == 0 && !q.IsEven))
        {
            q += BigInteger.One;
            if (q == TwoPow53)
            {
                q = TwoPow52;
                e++;
            }
        }

        double v;
        if (e > 971)
        {
            v = double.PositiveInfinity;
        }
        else if (q >= TwoPow52)
        {
            v = BitConverter.Int64BitsToDouble(((long)(e + 1075) << 52) | (long)(q - TwoPow52));
        }
        else
        {
            v = BitConverter.Int64BitsToDouble((long)q);
        }

        return neg ? -v : v;
    }

    private static int BitLength(BigInteger x)
    {
        byte[] bytes = x.ToByteArray();   // 2 の補数・リトルエンディアン（x > 0）
        int top = bytes.Length - 1;
        while (top > 0 && bytes[top] == 0)
        {
            top--;
        }

        int n = top * 8;
        for (int b = bytes[top]; b != 0; b >>= 1)
        {
            n++;
        }

        return n;
    }

    // Python の "%.{decimals}f"（plusSign なら "%+.{decimals}f"）。2 進の厳密値を 10 進で偶数丸めする（dtoa の mode 3 と同じ）。
    // 符号は符号ビットで決める: -0.0 と、丸めて 0 になる負の数は "-0.000…"（Python の round(v, n) が -0.0 を返し、それを "%.nf" で書いた結果と同じ）。
    internal static string FormatFixed(double v, int decimals, bool plusSign)
    {
        if (double.IsNaN(v))
        {
            return plusSign ? "+nan" : "nan";
        }

        bool neg = BitConverter.DoubleToInt64Bits(v) < 0;
        if (double.IsInfinity(v))
        {
            return neg ? "-inf" : plusSign ? "+inf" : "inf";
        }

        return (neg ? "-" : plusSign ? "+" : "") + FixedDigits(neg ? -v : v, decimals);
    }

    private static string FixedDigits(double av, int decimals)
    {
        long bits = BitConverter.DoubleToInt64Bits(av);
        int be = (int)((bits >> 52) & 0x7FF);
        long mant = bits & 0xFFFFFFFFFFFFFL;
        if (be == 0)
        {
            be = 1;
        }
        else
        {
            mant |= 1L << 52;
        }

        int e2 = be - 1075;   // av = mant × 2^e2（厳密）
        BigInteger num = new BigInteger(mant) * BigInteger.Pow(10, decimals);
        BigInteger q;
        if (e2 >= 0)
        {
            q = num << e2;
        }
        else
        {
            BigInteger den = BigInteger.One << -e2;
            q = BigInteger.DivRem(num, den, out BigInteger r);
            int c = (r << 1).CompareTo(den);
            if (c > 0 || (c == 0 && !q.IsEven))
            {
                q += BigInteger.One;
            }
        }

        string s = q.ToString(CultureInfo.InvariantCulture);
        if (decimals == 0)
        {
            return s;
        }

        if (s.Length <= decimals)
        {
            s = new string('0', decimals - s.Length + 1) + s;
        }

        return s.Substring(0, s.Length - decimals) + "." + s.Substring(s.Length - decimals);
    }

    // Python の round(v, n)（10 進で偶数丸めした値に最も近い double。符号も保つ）
    internal static double PyRound(double v, int decimals)
    {
        if (double.IsNaN(v) || double.IsInfinity(v))
        {
            return v;
        }

        return ParseDecimal(FormatFixed(v, decimals, false));
    }

    // json.dumps(round(x, 3))（json は float を repr で書く。NaN / Infinity / -Infinity は json の綴り）。例: "0.066"・"-0.0"・"0.0"
    internal static string PyJsonRound3(double v)
    {
        if (double.IsNaN(v))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(v))
        {
            return "Infinity";
        }

        if (double.IsNegativeInfinity(v))
        {
            return "-Infinity";
        }

        return PyFloatRepr(PyRound(v, 3));
    }

    // Python の repr(float)（dtoa の mode 0 = 往復できる最短の 10 進。同じ長さが複数なら値に近いほう。
    // 小数点の位置 decpt が -4 以下か 16 より大きければ指数表記 "1.5e+16"、それ以外は "0.066"・"12.0"・"-0.0"）。
    internal static string PyFloatRepr(double v)
    {
        if (double.IsNaN(v))
        {
            return "nan";
        }

        if (double.IsInfinity(v))
        {
            return v > 0 ? "inf" : "-inf";
        }

        bool neg = BitConverter.DoubleToInt64Bits(v) < 0;
        if (v == 0)
        {
            return neg ? "-0.0" : "0.0";
        }

        ShortestDigits(neg ? -v : v, out string digits, out int decpt);   // 値 = 0.d1d2… × 10^decpt
        string body;
        if (decpt <= -4 || decpt > 16)
        {
            int exp = decpt - 1;
            body = digits.Substring(0, 1) + (digits.Length > 1 ? "." + digits.Substring(1) : "") +
                   "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
        }
        else if (decpt <= 0)
        {
            body = "0." + new string('0', -decpt) + digits;
        }
        else if (decpt < digits.Length)
        {
            body = digits.Substring(0, decpt) + "." + digits.Substring(decpt);
        }
        else
        {
            body = digits + new string('0', decpt - digits.Length) + ".0";
        }

        return (neg ? "-" : "") + body;
    }

    // x > 0 の最短の 10 進。x = m × 2^e の丸めの範囲（隣の double との中点まで。m が偶数なら端も含む＝偶数丸めで x に戻る）に入る、
    // 有効桁がいちばん少ない 10 進を探す。範囲の単位は 2^(e-2)（x = 4m、上の端 4m+2、下の端 4m-2。2 のべきちょうどなら下の隙間が半分で 4m-1）。
    private static void ShortestDigits(double x, out string digits, out int decpt)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        int be = (int)((bits >> 52) & 0x7FF);
        long mant = bits & 0xFFFFFFFFFFFFFL;
        long m = be == 0 ? mant : mant | (1L << 52);
        int e2 = (be == 0 ? -1074 : be - 1075) - 2;
        bool even = (m & 1) == 0;
        BigInteger x4 = new BigInteger(m) * 4;
        BigInteger hi4 = x4 + 2;
        BigInteger lo4 = mant == 0 && be > 1 ? x4 - 1 : x4 - 2;

        int top = (int)Math.Floor(Math.Log10(x));   // 10^top <= x < 10^(top+1) に厳密に直す
        while (CompareScaled(BigInteger.One, top, x4, e2) > 0)
        {
            top--;
        }

        while (CompareScaled(BigInteger.One, top + 1, x4, e2) <= 0)
        {
            top++;
        }

        for (int p = 1; p <= 17; p++)
        {
            int s = top - p + 1;   // p 桁目の単位 10^s
            BigInteger near = RoundScaled(x4, e2, s);
            foreach (BigInteger cand in new[] { near, near - BigInteger.One, near + BigInteger.One })
            {
                if (cand.Sign <= 0)
                {
                    continue;
                }

                int cLo = CompareScaled(cand, s, lo4, e2);
                int cHi = CompareScaled(cand, s, hi4, e2);
                if (even ? (cLo >= 0 && cHi <= 0) : (cLo > 0 && cHi < 0))
                {
                    string ds = cand.ToString(CultureInfo.InvariantCulture);
                    decpt = s + ds.Length;
                    digits = ds.TrimEnd(Zero);
                    return;
                }
            }
        }

        // 17 桁で必ず見つかる（ここには来ない）
        string full = RoundScaled(x4, e2, top - 16).ToString(CultureInfo.InvariantCulture);
        decpt = top - 16 + full.Length;
        digits = full.TrimEnd(Zero);
    }

    // a × 10^pow10 と b × 2^pow2 の大小（a, b >= 0、厳密）
    private static int CompareScaled(BigInteger a, int pow10, BigInteger b, int pow2)
    {
        BigInteger l = a;
        BigInteger r = b;
        if (pow10 >= 0)
        {
            l *= BigInteger.Pow(10, pow10);
        }
        else
        {
            r *= BigInteger.Pow(10, -pow10);
        }

        if (pow2 >= 0)
        {
            r <<= pow2;
        }
        else
        {
            l <<= -pow2;
        }

        return l.CompareTo(r);
    }

    // units × 2^pow2 / 10^pow10 を偶数丸めした整数
    private static BigInteger RoundScaled(BigInteger units, int pow2, int pow10)
    {
        BigInteger num = units;
        BigInteger den = BigInteger.One;
        if (pow2 >= 0)
        {
            num <<= pow2;
        }
        else
        {
            den <<= -pow2;
        }

        if (pow10 >= 0)
        {
            den *= BigInteger.Pow(10, pow10);
        }
        else
        {
            num *= BigInteger.Pow(10, -pow10);
        }

        BigInteger q = BigInteger.DivRem(num, den, out BigInteger r);
        int c = (r << 1).CompareTo(den);
        if (c > 0 || (c == 0 && !q.IsEven))
        {
            q += BigInteger.One;
        }

        return q;
    }

    // json.dumps(s, ensure_ascii=…)（\" \\ \b \f \n \r \t、ほかの制御文字は \u00xx。ensure_ascii なら 0x7f 以上も \uxxxx（小文字の 16 進、UTF-16 の単位ごと））
    internal static string PyJsonString(string s, bool ensureAscii)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ' || (ensureAscii && c > '~'))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    // Python の list の repr（"%s" % [..]）: ['a', 'b']
    internal static string PyListRepr(List<string> items)
    {
        var parts = new string[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            parts[i] = PyStrRepr(items[i]);
        }

        return "[" + string.Join(", ", parts) + "]";
    }

    // Python の str の repr: ' で囲む（' を含み " を含まなければ "）。\\ と囲みの引用符、\t \n \r、制御文字は \xNN、印字できない非 ASCII は \x / \u / \U。
    internal static string PyStrRepr(string s)
    {
        char quote = s.IndexOf('\'') >= 0 && s.IndexOf('"') < 0 ? '"' : '\'';
        var sb = new StringBuilder();
        sb.Append(quote);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == quote || c == '\\')
            {
                sb.Append('\\').Append(c);
            }
            else if (c == '\t')
            {
                sb.Append("\\t");
            }
            else if (c == '\n')
            {
                sb.Append("\\n");
            }
            else if (c == '\r')
            {
                sb.Append("\\r");
            }
            else if (c < ' ' || c == '\x7f')
            {
                sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
            }
            else if (c < '\x7f')
            {
                sb.Append(c);
            }
            else
            {
                int len = char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
                int cp = len == 2 ? char.ConvertToUtf32(c, s[i + 1]) : c;
                if (IsPyPrintable(CharUnicodeInfo.GetUnicodeCategory(s, i)))
                {
                    sb.Append(s, i, len);
                }
                else if (cp <= 0xff)
                {
                    sb.Append("\\x").Append(cp.ToString("x2", CultureInfo.InvariantCulture));
                }
                else if (cp <= 0xffff)
                {
                    sb.Append("\\u").Append(cp.ToString("x4", CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append("\\U").Append(cp.ToString("x8", CultureInfo.InvariantCulture));
                }

                i += len - 1;
            }
        }

        sb.Append(quote);
        return sb.ToString();
    }

    private static bool IsPyPrintable(UnicodeCategory cat)
    {
        switch (cat)
        {
            case UnicodeCategory.Control:
            case UnicodeCategory.Format:
            case UnicodeCategory.Surrogate:
            case UnicodeCategory.PrivateUse:
            case UnicodeCategory.OtherNotAssigned:
            case UnicodeCategory.LineSeparator:
            case UnicodeCategory.ParagraphSeparator:
            case UnicodeCategory.SpaceSeparator:
                return false;
            default:
                return true;
        }
    }

    // ---- 今の表との比べ（2026-10-08、査読の指摘 1・3。(A) と (B) で共通。UnityEngine を使わないので試験台でも同じコードを動かす） ----
    // 比べる前に CRLF を LF に直す（このリポジトリは core.autocrlf=true・text=auto で、表を取り出し直すと CRLF になる。道具が書くのは LF）。
    internal static string Lf(string s)
    {
        return s == null ? null : s.Replace("\r\n", "\n");
    }

    // 書く前の表と行で比べた一言（正確な比べ方は diff_leg_skin.py）。
    public static string DescribeAgainst(string before, string after, string label)
    {
        if (before == null)
        {
            return label + " が無い（初めて書く）";
        }

        bool crlf = before.IndexOf("\r\n", StringComparison.Ordinal) >= 0;
        string a0 = Lf(before);
        string b0 = Lf(after);
        string eol = crlf ? "（" + label + " は改行が CRLF。LF に直して比べた）" : "";
        if (a0 == b0)
        {
            return label + " と同じ" + (crlf ? eol : "（バイト単位）");
        }

        string[] a = a0.Split(new[] { '\n' });
        string[] b = b0.Split(new[] { '\n' });
        int differing = Math.Abs(a.Length - b.Length);
        bool onlySource = a.Length == b.Length;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] == b[i])
            {
                continue;
            }

            differing++;
            if (!(a[i].StartsWith("  \"_source\": ", StringComparison.Ordinal) && b[i].StartsWith("  \"_source\": ", StringComparison.Ordinal)))
            {
                onlySource = false;
            }
        }

        if (onlySource && differing == 1)
        {
            return label + " と _source の行だけ違う（数・骨・除外は同じ）" + eol;
        }

        return label + " と " + differing + " 行違う（行数 " + a.Length + " → " + b.Length + "）。diff_leg_skin.py で比べる" + eol;
    }

    // 表の中身の要約: models のキー → prefab、_excluded の prefab → 理由。JSON として読めなければ FormatException。
    public sealed class TableSummary
    {
        public readonly Dictionary<string, string> models = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> excluded = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public static TableSummary ReadTableSummary(string json)
    {
        var s = new TableSummary();
        if (!(new JsonParser(json).ParseDocument() is Dictionary<string, object> root))
        {
            throw new FormatException("一番外がオブジェクトでない");
        }

        if (root.TryGetValue("models", out object mo) && mo is Dictionary<string, object> models)
        {
            foreach (KeyValuePair<string, object> kv in models)
            {
                string prefab = kv.Value is Dictionary<string, object> md && md.TryGetValue("prefab", out object po) && po is string ps ? ps : kv.Key;
                s.models[kv.Key] = prefab;
            }
        }

        if (root.TryGetValue("_excluded", out object eo) && eo is Dictionary<string, object> ex)
        {
            foreach (KeyValuePair<string, object> kv in ex)
            {
                s.excluded[kv.Key] = kv.Value as string ?? "";
            }
        }

        return s;
    }

    // 今の表（current）にあって新しい表（next）で消えるモデル。models のキーは Key（"Lynx"）、_excluded のキーは prefab 名（"05_Horse"）なので、
    // どちらも key() を通して比べる。models から _excluded へ移るものも数える（runtime の表から無くなるため。理由は添える）。
    // _excluded から models へ移るのは消えない。どちらの表も CRLF でよい（JSON として読む）。
    public static List<string> FindDisappearing(string current, string next, Func<string, string> key)
    {
        TableSummary a = ReadTableSummary(current);
        TableSummary b = ReadTableSummary(next);
        var nextExcludedByKey = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in b.excluded)
        {
            nextExcludedByKey[key(kv.Key)] = kv;
        }

        var list = new List<string>();
        var currentModels = new List<string>(a.models.Keys);
        currentModels.Sort(StringComparer.Ordinal);
        foreach (string k in currentModels)
        {
            if (b.models.ContainsKey(k))
            {
                continue;
            }

            list.Add(nextExcludedByKey.TryGetValue(k, out KeyValuePair<string, string> ex)
                ? "models の " + k + "（" + a.models[k] + "）が _excluded へ移る: " + ex.Value
                : "models の " + k + "（" + a.models[k] + "）が新しい表のどこにも無い");
        }

        var currentExcluded = new List<string>(a.excluded.Keys);
        currentExcluded.Sort(StringComparer.Ordinal);
        foreach (string p in currentExcluded)
        {
            string k = key(p);
            if (b.models.ContainsKey(k) || nextExcludedByKey.ContainsKey(k))
            {
                continue;
            }

            list.Add("_excluded の " + p + " が新しい表のどこにも無い");
        }

        return list;
    }

    // ---- 終わり方（2026-10-09、査読の指摘 2。バッチの "<タグ> RESULT …" と exit code。UnityEngine を使わないので試験台で表を確かめる） ----
    public enum BakeOutcome
    {
        FailedOrRefused,    // 何も書いていない（例外・prefab を開けない・書く先がだめ・Assets への上書きを止めた・メニューでやめた）
        Ok,                 // 書いた。止める理由 0
        Forced,             // Assets へ force で書いた（止める理由 N を越えた）
        WroteWithBlockers,  // Assets の外へ書いたが、Assets へなら止める理由が N ある
    }

    // "ok" で始まるのは Ok だけ（ログを "RESULT ok" で grep してよい）。
    public static string ResultToken(BakeOutcome outcome, int blockers)
    {
        switch (outcome)
        {
            case BakeOutcome.Ok:
                return "ok";
            case BakeOutcome.Forced:
                return "forced " + blockers.ToString(CultureInfo.InvariantCulture);
            case BakeOutcome.WroteWithBlockers:
                return "wrote_with_blockers " + blockers.ToString(CultureInfo.InvariantCulture);
            default:
                return "failed_or_refused";
        }
    }

    // exit code: 書いて止める理由が無い（か force で越えた）ときだけ 0。Logs へ書いても止める理由があれば 1（キューが成功と読まないように）。
    public static int ExitCode(BakeOutcome outcome)
    {
        return outcome == BakeOutcome.Ok || outcome == BakeOutcome.Forced ? 0 : 1;
    }

    // 書くか止めるか（AnimalLegSkinPoseBaker.Commit とメニューの上書きが使う。UnityEngine を使わないので試験台で 16 通りを確かめる。2026-10-09）。
    // blockers は止める理由の全部（fatal を含む）、fatal はそのうち force でも越えない数（NaN / Infinity）。
    // FailedOrRefused = 書かない: Assets の中で fatal がある（force でも）か、止める理由があって force なし。
    // それ以外は書く: 理由 0 → Ok、Assets の中で force → Forced、Assets の外 → WroteWithBlockers（RESULT wrote_with_blockers・exit 1）。
    public static BakeOutcome DecideCommit(bool intoAssets, int fatal, int blockers, bool force)
    {
        if (intoAssets && (fatal > 0 || (blockers > 0 && !force)))
        {
            return BakeOutcome.FailedOrRefused;
        }

        if (blockers == 0 && fatal == 0)
        {
            return BakeOutcome.Ok;
        }

        return intoAssets ? BakeOutcome.Forced : BakeOutcome.WroteWithBlockers;
    }

    // ---- 書く先の確かめ（2026-10-09、査読の指摘 4。UnityEngine を使わない。試験台で確かめる） ----
    // 表（outPath）: Assets の中なら表の正規のパス canonical だけ（上書きの安全を通す。normalized は canonical の綴り = 取り込むパスの大文字小文字を揃える）。
    //   Assets の外でプロジェクトの中なら Logs/ の下だけ。プロジェクトの外は良い。
    // 報告・控え（aux）: Assets の中は不可（前は控えを取らずに上書きし、取り込んでいた。報告のパスの打ち間違い 1 つで表が報告の文に置き換わる）。
    //   プロジェクトの中なら Logs/ の下だけ（ProjectSettings・Packages なども同じく控えなしで上書きしてしまう）。プロジェクトの外は良い。
    // 表・報告・控え・読む入力（inputs）は互いに別のファイル（大文字小文字を区別しない。Windows のパス）。
    // aux・inputs は { フラグ名, パス, フラグ名, パス, … }（パスが null・空の組は飛ばす）。だめなら理由（何も書かずに終える）、良ければ null。
    public static string CheckOutputPaths(string projectRoot, string canonical, string outFlag, string outPath, string[] auxFlagsAndPaths,
                                          string[] inputFlagsAndPaths, out string normalized)
    {
        normalized = null;
        if (string.IsNullOrEmpty(outPath))
        {
            return outFlag + " が空";
        }

        try
        {
            string sep = Path.DirectorySeparatorChar.ToString();
            string root = Path.GetFullPath(projectRoot).TrimEnd('\\', '/') + sep;
            string Full(string p) => Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(root, p));
            bool Under(string full, string dir) => full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
            bool InProject(string full) => Under(full + sep, root);
            string assets = Full("Assets") + sep;
            string logs = Full("Logs") + sep;
            var used = new List<KeyValuePair<string, string>>();

            string fo = Full(outPath);
            if (Under(fo, assets))
            {
                if (!string.Equals(fo, Full(canonical), StringComparison.OrdinalIgnoreCase))
                {
                    return outFlag + " " + outPath + ": Assets の中へ書けるのは " + canonical + " だけ";
                }

                normalized = canonical;
            }
            else if (InProject(fo) && !Under(fo, logs))
            {
                return outFlag + " " + outPath + ": プロジェクトの中で Assets の外へ書くのは Logs/ の下だけ";
            }
            else
            {
                normalized = outPath;
            }

            used.Add(new KeyValuePair<string, string>(outFlag, fo));
            for (int i = 0; auxFlagsAndPaths != null && i + 1 < auxFlagsAndPaths.Length; i += 2)
            {
                string a = auxFlagsAndPaths[i + 1];
                if (string.IsNullOrEmpty(a))
                {
                    continue;
                }

                string fa = Full(a);
                if (Under(fa, assets))
                {
                    normalized = null;
                    return auxFlagsAndPaths[i] + " " + a + ": 報告・控えは Assets の中へ書かない（控えを取らずに上書きしてしまう）";
                }

                if (InProject(fa) && !Under(fa, logs))
                {
                    normalized = null;
                    return auxFlagsAndPaths[i] + " " + a + ": 報告・控えをプロジェクトの中へ書くのは Logs/ の下だけ";
                }

                used.Add(new KeyValuePair<string, string>(auxFlagsAndPaths[i], fa));
            }

            for (int i = 0; inputFlagsAndPaths != null && i + 1 < inputFlagsAndPaths.Length; i += 2)
            {
                if (!string.IsNullOrEmpty(inputFlagsAndPaths[i + 1]))
                {
                    used.Add(new KeyValuePair<string, string>(inputFlagsAndPaths[i], Full(inputFlagsAndPaths[i + 1])));
                }
            }

            for (int i = 0; i < used.Count; i++)
            {
                for (int j = i + 1; j < used.Count; j++)
                {
                    if (string.Equals(used[i].Value, used[j].Value, StringComparison.OrdinalIgnoreCase))
                    {
                        normalized = null;
                        return used[i].Key + " と " + used[j].Key + " が同じファイル（" + used[i].Value + "）";
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            normalized = null;
            return "パスが読めない（" + ex.Message + "）";
        }

        return null;
    }

    // ---- 保存した棚卸しと model_index.txt の突き合わせ（2026-10-09、査読の指摘 3。-legSkinPoseInventoryIn のとき） ----
    // 棚卸しのモデルの組が model_index.txt の Models/Animal の行と同じか（並びは見ない。重複は数える）。違えば理由、同じなら null。
    // どちらも "Models/Animal/" が付いていれば外して比べる（棚卸しの model は外した名前、model_index.txt は付いた行）。
    public static string CompareInventoryWithIndex(IEnumerable<string> inventoryModels, IEnumerable<string> indexLines)
    {
        var inv = new List<string>();
        foreach (string m in inventoryModels)
        {
            inv.Add(StripAnimalPrefix(m));
        }

        var idx = new List<string>();
        foreach (string l in indexLines)
        {
            idx.Add(StripAnimalPrefix(l));
        }

        var invSet = new HashSet<string>(StringComparer.Ordinal);
        var dup = new List<string>();
        foreach (string m in inv)
        {
            if (!invSet.Add(m) && !dup.Contains(m))
            {
                dup.Add(m);
            }
        }

        var idxSet = new HashSet<string>(idx, StringComparer.Ordinal);
        var notInInventory = new List<string>();
        foreach (string l in idx)
        {
            if (!invSet.Contains(l) && !notInInventory.Contains(l))
            {
                notInInventory.Add(l);
            }
        }

        var extra = new List<string>();
        foreach (string m in inv)
        {
            if (!idxSet.Contains(m) && !extra.Contains(m))
            {
                extra.Add(m);
            }
        }

        if (notInInventory.Count == 0 && extra.Count == 0 && dup.Count == 0)
        {
            return null;
        }

        return "棚卸しのモデルが model_index.txt の Models/Animal と違う（棚卸しが古い? prefab を開いて焼き直す）: 棚卸しに無い " + PyListRepr(notInInventory) +
               " 余分 " + PyListRepr(extra) + (dup.Count > 0 ? " 重複 " + PyListRepr(dup) : "");
    }

    internal static string StripAnimalPrefix(string s)
    {
        const string prefix = "Models/Animal/";
        return s != null && s.StartsWith(prefix, StringComparison.Ordinal) ? s.Substring(prefix.Length) : s ?? "";
    }

    // ---- runtime の MiniJson が読めない数（2026-10-09、査読の指摘 1） ----
    // JSON の中の NaN / Infinity / -Infinity（と、double にすると有限でない数）の場所 "$.models.Goose.legAsymBefore = NaN" の列。
    // runtime の MiniJson はこの字句の上で先へ進めない（ParseValue が進まずに null を返し、ParseObject の while(true) が回り続ける。
    // 2026-10-09 の査読で、本物の MiniJson.cs が 5 秒たっても戻らないことを確かめた）。道具の JsonParser で読み（文字列の中の "NaN" は
    // 数ではないので数えない）、数の字句だけを見る。JSON として読めなければ FormatException。
    public static List<string> NonFiniteNumbers(string json)
    {
        var found = new List<string>();
        CollectNonFinite(new JsonParser(json).ParseDocument(), "$", found);
        return found;
    }

    private static void CollectNonFinite(object v, string path, List<string> found)
    {
        if (v is RawNumber rn)
        {
            if (!IsFinite(ParseNumberToken(rn.token)))
            {
                found.Add(path + " = " + rn.token);
            }
        }
        else if (v is Dictionary<string, object> o)
        {
            foreach (KeyValuePair<string, object> kv in o)
            {
                CollectNonFinite(kv.Value, path + "." + kv.Key, found);
            }
        }
        else if (v is List<object> a)
        {
            for (int i = 0; i < a.Count; i++)
            {
                CollectNonFinite(a[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", found);
            }
        }
    }

    // double.IsFinite は .NET Standard 2.1 にしか無い（試験台の .NET Framework でも同じコードを動かすため自前）。
    internal static bool IsFinite(double v)
    {
        return !double.IsNaN(v) && !double.IsInfinity(v);
    }

    internal static bool AllFinite(double[] v)
    {
        if (v == null)
        {
            return false;
        }

        foreach (double x in v)
        {
            if (!IsFinite(x))
            {
                return false;
            }
        }

        return true;
    }

    // ---- 棚卸しの JSON（rest_rotation_inventory.cs の animal_rest_rotations.json）の読み書き ----
    // 書き: eval の 173〜217 行と同じ文字列（1 体 1 行、区切りは ",\n"、最後に "\n]\n"）。
    public static string WriteInventoryJson(List<InvModel> inventory)
    {
        var sa = new StringBuilder();
        sa.Append("[\n");
        for (int mi = 0; mi < inventory.Count; mi++)
        {
            InvModel m = inventory[mi];
            sa.Append(mi == 0 ? "" : ",\n");
            sa.Append("{\"model\":\"").Append(m.model).Append("\"");
            sa.Append(",\"rigRoot\":\"").Append(m.rigRoot ?? "").Append("\",\"rigRootDef\":").Append(TokensText(m.rigRootDef));
            sa.Append(",\"bones\":[");
            for (int bi = 0; bi < m.bones.Count; bi++)
            {
                InvBone b = m.bones[bi];
                sa.Append(bi == 0 ? "" : ",");
                sa.Append("{\"n\":\"").Append(b.n).Append("\",\"parent\":\"").Append(b.parent)
                  .Append("\",\"def\":").Append(TokensText(b.def))
                  .Append(",\"skin\":").Append(TokensText(b.skin)).Append("}");
            }

            sa.Append("]}");
        }

        sa.Append("\n]\n");
        return sa.ToString();
    }

    private static string TokensText(string[] tokens)
    {
        return tokens == null ? "null" : "[" + string.Join(",", tokens) + "]";
    }

    // 読み: Python の json と同じ文法で読み、数は文字列のまま取り出す（double にするのは ParseNumberToken。Python と同じ値にするため）。
    public static List<InvModel> ReadInventoryJson(string text)
    {
        if (!(new JsonParser(text).ParseDocument() is List<object> arr))
        {
            throw new FormatException("棚卸しの JSON の一番外が配列でない");
        }

        var list = new List<InvModel>();
        foreach (object mo in arr)
        {
            Dictionary<string, object> md = AsObject(mo, "モデル");
            var m = new InvModel
            {
                model = RequireString(md, "model"),
                rigRoot = md.TryGetValue("rigRoot", out object rr) ? rr as string : null,
                rigRootDef = md.TryGetValue("rigRootDef", out object rd) ? Tokens(rd, "rigRootDef") : null,
            };
            if (!md.TryGetValue("bones", out object bonesObj) || !(bonesObj is List<object> bones))
            {
                throw new FormatException(m.model + ": bones が無い");
            }

            foreach (object bo in bones)
            {
                Dictionary<string, object> bd = AsObject(bo, "骨");
                m.bones.Add(new InvBone
                {
                    n = RequireString(bd, "n"),
                    parent = RequireString(bd, "parent"),
                    def = bd.TryGetValue("def", out object dv) ? Tokens(dv, "def") : throw new FormatException(m.model + ": def が無い"),
                    skin = bd.TryGetValue("skin", out object sv) ? Tokens(sv, "skin") : throw new FormatException(m.model + ": skin が無い"),
                });
            }

            list.Add(m);
        }

        return list;
    }

    private static Dictionary<string, object> AsObject(object o, string what)
    {
        return o as Dictionary<string, object> ?? throw new FormatException("棚卸しの" + what + "がオブジェクトでない");
    }

    private static string RequireString(Dictionary<string, object> d, string key)
    {
        if (d.TryGetValue(key, out object v) && v is string s)
        {
            return s;
        }

        throw new FormatException("棚卸しに文字列の " + key + " が無い");
    }

    private static string[] Tokens(object v, string what)
    {
        if (v == null)
        {
            return null;
        }

        if (!(v is List<object> a))
        {
            throw new FormatException("棚卸しの " + what + " が配列でない");
        }

        var t = new string[a.Count];
        for (int i = 0; i < a.Count; i++)
        {
            t[i] = a[i] is RawNumber rn ? rn.token : throw new FormatException("棚卸しの " + what + " に数でないものがある");
        }

        return t;
    }

    internal sealed class RawNumber
    {
        public readonly string token;

        public RawNumber(string token)
        {
            this.token = token;
        }
    }

    // Python の json.loads（strict）と同じ文法: 空白は ' ' \t \n \r、文字列の中の制御文字は不可、NaN / Infinity / -Infinity は可、
    // 数は -?(0|[1-9]\d*)(\.\d+)?([eE][-+]?\d+)?、同じキーは後が勝つ、最後の値の後は空白だけ。
    // 値: Dictionary<string, object> / List<object> / string / RawNumber / bool / null。
    internal sealed class JsonParser
    {
        private readonly string s;
        private int i;

        public JsonParser(string text)
        {
            s = text;
        }

        public object ParseDocument()
        {
            SkipWs();
            object v = ParseValue();
            SkipWs();
            if (i != s.Length)
            {
                throw Error("値の後に余分な文字がある");
            }

            return v;
        }

        private FormatException Error(string what)
        {
            return new FormatException("JSON の " + i + " 文字目: " + what);
        }

        private void SkipWs()
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r'))
            {
                i++;
            }
        }

        private bool Take(string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) == 0)
            {
                i += word.Length;
                return true;
            }

            return false;
        }

        private object ParseValue()
        {
            if (i >= s.Length)
            {
                throw Error("値が無い");
            }

            char c = s[i];
            if (c == '"')
            {
                return ParseString();
            }

            if (c == '{')
            {
                return ParseObject();
            }

            if (c == '[')
            {
                return ParseArray();
            }

            if (Take("null"))
            {
                return null;
            }

            if (Take("true"))
            {
                return true;
            }

            if (Take("false"))
            {
                return false;
            }

            if (c == '-' || (c >= '0' && c <= '9'))
            {
                int start = i;
                if (TryNumber())
                {
                    return new RawNumber(s.Substring(start, i - start));
                }

                i = start;
            }

            if (Take("NaN"))
            {
                return new RawNumber("NaN");
            }

            if (Take("Infinity"))
            {
                return new RawNumber("Infinity");
            }

            if (Take("-Infinity"))
            {
                return new RawNumber("-Infinity");
            }

            throw Error("値が読めない");
        }

        private bool TryNumber()
        {
            if (s[i] == '-')
            {
                i++;
            }

            if (i < s.Length && s[i] == '0')
            {
                i++;
            }
            else if (i < s.Length && s[i] >= '1' && s[i] <= '9')
            {
                while (i < s.Length && s[i] >= '0' && s[i] <= '9')
                {
                    i++;
                }
            }
            else
            {
                return false;
            }

            if (i + 1 < s.Length && s[i] == '.' && s[i + 1] >= '0' && s[i + 1] <= '9')
            {
                i++;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9')
                {
                    i++;
                }
            }

            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                int save = i;
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-'))
                {
                    i++;
                }

                if (i < s.Length && s[i] >= '0' && s[i] <= '9')
                {
                    while (i < s.Length && s[i] >= '0' && s[i] <= '9')
                    {
                        i++;
                    }
                }
                else
                {
                    i = save;
                }
            }

            return true;
        }

        private string ParseString()
        {
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length)
                {
                    throw Error("文字列が閉じていない");
                }

                char c = s[i++];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c < ' ')
                {
                    throw Error("文字列の中に制御文字がある");
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (i >= s.Length)
                {
                    throw Error("文字列が閉じていない");
                }

                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length ||
                            !int.TryParse(s.Substring(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                        {
                            throw Error("\\u の後が 16 進 4 桁でない");
                        }

                        sb.Append((char)code);
                        i += 4;
                        break;
                    default:
                        throw Error("知らないエスケープ \\" + e);
                }
            }
        }

        private Dictionary<string, object> ParseObject()
        {
            i++;
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            SkipWs();
            if (i < s.Length && s[i] == '}')
            {
                i++;
                return d;
            }

            while (true)
            {
                SkipWs();
                if (i >= s.Length || s[i] != '"')
                {
                    throw Error("キーが文字列でない");
                }

                string k = ParseString();
                SkipWs();
                if (i >= s.Length || s[i] != ':')
                {
                    throw Error("':' が無い");
                }

                i++;
                SkipWs();
                d[k] = ParseValue();
                SkipWs();
                if (i < s.Length && s[i] == ',')
                {
                    i++;
                    continue;
                }

                if (i < s.Length && s[i] == '}')
                {
                    i++;
                    return d;
                }

                throw Error("',' か '}' が無い");
            }
        }

        private List<object> ParseArray()
        {
            i++;
            var a = new List<object>();
            SkipWs();
            if (i < s.Length && s[i] == ']')
            {
                i++;
                return a;
            }

            while (true)
            {
                SkipWs();
                a.Add(ParseValue());
                SkipWs();
                if (i < s.Length && s[i] == ',')
                {
                    i++;
                    continue;
                }

                if (i < s.Length && s[i] == ']')
                {
                    i++;
                    return a;
                }

                throw Error("',' か ']' が無い");
            }
        }
    }
}

// 鳥 3 体の基準姿勢の表 Resources/animal_reference_pose.json を焼く計算（2026-10-08。UnityEngine を使わない。Unity の外の試験台でも同じコードを動かす）。
// 入力は root の子孫の全 Transform の root 座標の def（prefab の既定姿勢）・skin（bindposes。SkinnedMeshRenderer の骨だけ）・pose（クリップの t=0。Guineafowl だけ）。
// 式は scratchpad birds/b4_reference_pose_compose.py（mirror_side と measures）の移植。b4 は回転行列、ここは四元数（double）。
// 位置は書かない（runtime も回転だけ書く）ので、測るときの位置は既定姿勢の親からのずらしを親の回転の変化だけ回したもの（b4 の G3 = DEF translations と同じ）。
public static class AnimalReferencePoseBakeCore
{
    public enum Mode
    {
        MirrorLeftLeg,          // 既定姿勢のまま、右脚の鎖を左脚の鏡像にする（Goose・Pheasant）
        ClipThenMirrorLeftLeg,  // Root より下の全部の骨をクリップの t=0 の回転にしてから、右脚を鏡像にする（Guineafowl）
    }

    public sealed class Spec
    {
        public string key;                 // AnimalLegMappingFix.Key（prefab 名から先頭の数字_を外したもの）
        public Mode mode;
        public string clipSuffix;          // ClipThenMirrorLeftLeg: FBX のクリップ名の末尾
        public string modelPath;           // ClipThenMirrorLeftLeg: 想定の FBX（prefab の元から分からないときだけ使う）
        public string leftPrefix = "LegL";
        public string rightPrefix = "LegR";
        public string b4Def;               // b4_reference_pose_compose_out.txt の行（そのまま）: 既定姿勢
        public string b4Source;            // ClipThenMirrorLeftLeg: クリップの姿勢（鏡像の前）
        public string b4Result;            // 焼く姿勢
    }

    // b4 の行は scratchpad birds/b4_reference_pose_compose_out.txt（2026-10-08 21:18）からそのまま写した（試験台でファイルの行と同じか確かめる）。
    public static readonly Spec[] Specs =
    {
        new Spec
        {
            key = "Goose", mode = Mode.MirrorLeftLeg,
            b4Def = "  DEF (runtime bind now)                       legL [-20.5, -65.3, -58.0]  legR [-34.9, -37.7, -73.7]  toe-hips -14.38 (toe y L -0.18 R -0.01) legAsym  5.11 (0.34 body) wing|z|   7.6 wingUp   2.8 head-hips  12.59 pelvis->Neck1 pitch   13.1 | Pelvis rot vs DEF   0.0 vs SKIN  17.6",
            b4Result = "  DEF + right leg = mirror(left)               legL [-20.5, -65.3, -58.0]  legR [-20.5, -65.3, -58.1]  toe-hips -14.38 (toe y L -0.18 R -0.12) legAsym  0.39 (0.03 body) wing|z|   7.6 wingUp   2.8 head-hips  12.59 pelvis->Neck1 pitch   13.1 | Pelvis rot vs DEF   0.0 vs SKIN  17.6",
        },
        new Spec
        {
            key = "Guineafowl", mode = Mode.ClipThenMirrorLeftLeg, clipSuffix = "A_StandStraight_Idle1",
            modelPath = "Assets/50+ Animated Animals/2.0/Guineafowl/Guineafowl.fbx",
            b4Def = "  DEF (runtime bind now)                       legL [-44.4, -2.3, -22.2]   legR [-50.9, 5.3, -23.0]    toe-hips -13.11 (toe y L  2.43 R  3.21) legAsym  1.53 (0.09 body) wing|z|  18.9 wingUp  23.7 head-hips   1.05 pelvis->Neck1 pitch    2.7 | Pelvis rot vs DEF   0.0 vs SKIN  17.2",
            b4Source = "  G3 IDLE all rotations (DEF translations)     legL [-40.9, -41.0, -75.8]  legR [-24.2, -72.5, -66.4]  toe-hips -25.85 (toe y L -9.93 R -10.02) legAsym 13.80 (0.78 body) wing|z|   7.8 wingUp   5.7 head-hips  19.09 pelvis->Neck1 pitch   29.5 | Pelvis rot vs DEF  29.3 vs SKIN  12.2",
            b4Result = "  G3 + right leg = mirror(left)                legL [-40.9, -41.0, -75.8]  legR [-40.9, -41.0, -75.8]  toe-hips -25.76 (toe y L -9.93 R -9.77) legAsym  0.16 (0.01 body) wing|z|   7.8 wingUp   5.7 head-hips  19.09 pelvis->Neck1 pitch   29.5 | Pelvis rot vs DEF  29.3 vs SKIN  12.2",
        },
        new Spec
        {
            key = "Pheasant", mode = Mode.MirrorLeftLeg,
            b4Def = "  DEF (runtime bind now)                       legL [-23.3, -66.8, -58.2]  legR [-33.8, -44.8, -70.2]  toe-hips -21.62 (toe y L  0.28 R  0.51) legAsym  6.80 (0.45 body) wing|z|   7.6 wingUp   4.1 head-hips  18.08 pelvis->Neck1 pitch   31.8 | Pelvis rot vs DEF   0.0 vs SKIN  10.5",
            b4Result = "  DEF + right leg = mirror(left)               legL [-23.3, -66.8, -58.2]  legR [-23.3, -66.8, -58.2]  toe-hips -21.71 (toe y L  0.28 R  0.20) legAsym  0.36 (0.02 body) wing|z|   7.6 wingUp   4.1 head-hips  18.08 pelvis->Neck1 pitch   31.8 | Pelvis rot vs DEF   0.0 vs SKIN  10.5",
        },
    };

    public const string LabelDef = "DEF (runtime bind now)";
    public const string LabelMirror = "DEF + right leg = mirror(left)";
    public const string LabelClip = "G3 IDLE all rotations (DEF translations)";
    public const string LabelClipMirror = "G3 + right leg = mirror(left)";

    // b4 との照合の許容差。Unity の取り込みと b4 の FBX の移植の差（既定姿勢で 0.1° 程度、bone_tree_*.txt の検証行）・クリップの圧縮（0.5°）・表示の丸めを見込む。
    // 試験台（birds/impl/refpose/check_refpose.py）: 棚卸しの Unity の def を使った入力でも b4 の行と文字列で一致。翼だけは、Unity の取り込みに
    // 名前が _end の葉が無かった場合に Guineafowl の既定姿勢の wing|z| が 0.8・wingUp が 0.4 動く（翼の先の葉）ので 1.0 にした。
    // 間違い（鏡像の軸・クリップの取り違え・サンプルが当たらない）は数度・数単位ずれる（例: クリップが当たらなければ Pelvis の回転 29.3° → 0°、
    // 翼 7.8 → 18.9、左右差 0.16 → 13.8）ので、これで分かる。
    public const double TolAngle = 0.6;
    public const double TolPos = 0.2;
    public const double TolAsym = 0.2;
    public const double TolWing = 1.0;
    // 鏡像のあとの左右の確かめ（b4 に依らない）: 脚の 3 区間の傾きの左右差と、左右差の体長比。
    public const double TolMirrorAngle = 0.3;
    public const double TolMirrorAsymBody = 0.05;

    public const string Meaning =
        "鳥の基準姿勢（非四足モード smalNonQuadrupedRig の smalNonQuadrupedReferencePose）の目標の局所回転 [qx,qy,qz,qw]。" +
        "形は animal_leg_skin_pose.json と同じで、skin が書く目標の局所回転、def は prefab の既定姿勢の局所回転" +
        "（runtime が今の局所回転と比べ、0.5° より違えばモデルが変わったとみて書かない）。" +
        "mirrorLeftLeg = 右脚の鎖を左脚の鏡像にしたもの（root 座標の S = diag(1,1,-1)、左右の軸の約束は skin 姿勢から）、" +
        "clipThenMirrorLeftLeg = Root より下の全部の骨を FBX のクリップの t=0 の回転にしてから右脚を鏡像にしたもの。" +
        "リグのキャッシュを作るとき、bind を控える前に親から順に一度だけ書く（位置は書かない）。キーは prefab 名から先頭の数字_を外したもの。" +
        "legAsymBefore / legAsymAfter は確かめ用（runtime は読まない）: 鏡像の前・後の脚の左右差（右の骨の位置を S で写したものと左の骨の位置の差の最大、" +
        "root 座標）を体長（Pelvis→Neck1 の長さ）で割った比。";

    public sealed class Node
    {
        public string n;         // Transform の名前（J を通したもの）
        public string parent;    // 親の名前（root の子なら root の名前。入力に無い名前は root 座標の子とみなす）
        public bool smr;         // SkinnedMeshRenderer の骨か
        public string[] def;     // [px,py,pz,qx,qy,qz,qw]（root 座標、float.ToString("0.######") の文字列）
        public string[] skin;    // 同じ形。SMR の骨でなければ null
        public string[] pose;    // 同じ形。クリップの t=0（ClipThenMirrorLeftLeg のときだけ。位置は使わない）
    }

    public sealed class Model
    {
        public string model;     // model_index.txt の "Models/Animal/" の後ろ
        public string clip;      // サンプルしたクリップの名前（無ければ null）
        public string source;    // クリップを読んだ FBX（無ければ null）
        public readonly List<Node> nodes = new List<Node>();
    }

    // b4 の measures() と同じ数（角度は度、長さは root 座標の単位）。脚の傾きは丸める前の値（b4 は 0.1 に丸めてから表示）。
    public sealed class Measures
    {
        public readonly double[] legL = new double[3];
        public readonly double[] legR = new double[3];
        public double toeMinusHips, toeL, toeR, legAsym, body, wingZ, wingUp, headMinusHips, pelvisNeckPitch, pelvisVsDef, pelvisVsSkin;
        public int toeLCount, toeRCount, wingCount, asymPairs;
        public string missing;   // 測れなかった名前（null なら全部ある）
    }

    public sealed class Result
    {
        public string json;
        public string report;
        public int modelCount;
        public int excludedCount;
        public int validationFailures;
        public bool b4Ok;                                              // Specs の全部を測って b4 と照合し、不一致 0（2026-10-09。要約の "b4 照合 ok" はこれだけ）
        public readonly List<string> unmeasured = new List<string>();  // 照合できなかった（除外・入力に無い）Specs のキー
        public readonly List<string> problems = new List<string>();   // Assets へ書くのを止める理由（除外・b4 との照合・左右の確かめ）
        public readonly List<string> notes = new List<string>();
        public readonly Dictionary<string, Measures> final = new Dictionary<string, Measures>(StringComparer.Ordinal);   // 表に入れたモデルだけ
        public readonly Dictionary<string, List<string>> measureLines = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    }

    // 要約の "b4 照合 …"。ok は 3 体とも測って照合し、不一致が 0 のときだけ（2026-10-09、査読の指摘 1。前は不一致の数だけを見ていたので、
    // 測れずに除外したモデルがあっても ok と出た）。
    public static string B4Summary(Result r)
    {
        if (r.b4Ok)
        {
            return "ok（" + Specs.Length.ToString(CultureInfo.InvariantCulture) + " 体）";
        }

        return "NG（不一致 " + r.validationFailures.ToString(CultureInfo.InvariantCulture) + " 件" +
               (r.unmeasured.Count > 0 ? "、照合できなかった " + AnimalLegSkinPoseBakeCore.PyListRepr(r.unmeasured) : "") + "）";
    }

    public static Spec FindSpec(string key)
    {
        foreach (Spec s in Specs)
        {
            if (string.Equals(s.key, key, StringComparison.Ordinal))
            {
                return s;
            }
        }

        return null;
    }

    public static string ModeName(Mode mode)
    {
        return mode == Mode.MirrorLeftLeg ? "mirrorLeftLeg" : "clipThenMirrorLeftLeg";
    }

    private sealed class Built
    {
        public readonly List<string> order = new List<string>();   // 親が先（深さ優先の前順、兄弟は入力の順 = GetComponentsInChildren の順）
        public readonly Dictionary<string, string> parentOf = new Dictionary<string, string>(StringComparer.Ordinal);   // root の子は null
        public readonly Dictionary<string, double[]> pdef = new Dictionary<string, double[]>(StringComparer.Ordinal);
        public readonly Dictionary<string, double[]> wdef = new Dictionary<string, double[]>(StringComparer.Ordinal);
        public readonly Dictionary<string, double[]> pskin = new Dictionary<string, double[]>(StringComparer.Ordinal);
        public readonly Dictionary<string, double[]> wskin = new Dictionary<string, double[]>(StringComparer.Ordinal);
        public readonly Dictionary<string, double[]> wpose = new Dictionary<string, double[]>(StringComparer.Ordinal);
        public readonly HashSet<string> smr = new HashSet<string>(StringComparer.Ordinal);
    }

    private sealed class OutBone
    {
        public string n;
        public string p;
        public double[] skin;
        public double[] def;
    }

    private sealed class OutChain
    {
        public string leg;
        public string root;
        public string girdle;
        public readonly List<OutBone> bones = new List<OutBone>();
    }

    private sealed class OutModel
    {
        public string prefab;
        public string mode;
        public string clip;
        public double asymBefore;
        public double asymAfter;
        public readonly List<OutChain> chains = new List<OutChain>();
    }

    public static Result Bake(List<Model> inputs, Func<string, string> key, string sourceText)
    {
        var r = new Result();
        var models = new Dictionary<string, OutModel>(StringComparer.Ordinal);
        var excluded = new Dictionary<string, string>(StringComparer.Ordinal);
        var rep = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Model m in inputs)
        {
            string prefab = AnimalLegSkinPoseBakeCore.LastPathSegment(m.model);
            string k = key(prefab);
            Spec spec = FindSpec(k);
            if (spec == null)
            {
                excluded[prefab] = "基準姿勢の決め方（AnimalReferencePoseBakeCore.Specs）が無いモデル";
                r.problems.Add(prefab + ": " + excluded[prefab]);
                continue;
            }

            if (!seen.Add(k))
            {
                r.problems.Add(prefab + ": キー " + k + " が入力に 2 回ある（後のほうを使う）");
            }

            string problem;
            OutModel om;
            try
            {
                problem = BakeOne(spec, m, prefab, rep, r, out om);
            }
            catch (FormatException ex)
            {
                problem = "入力が読めない（" + ex.Message + "）";
                om = null;
            }

            if (problem != null)
            {
                excluded[prefab] = problem;
                r.problems.Add(prefab + ": " + problem);
                rep.Append(prefab).Append(" excluded: ").Append(problem).Append('\n');
                continue;
            }

            models[k] = om;
        }

        foreach (Spec s in Specs)
        {
            if (!seen.Contains(s.key))
            {
                r.problems.Add(s.key + ": 入力に無い（prefab が読めない・model_index.txt に無い）");
            }

            if (!models.ContainsKey(s.key) || !r.final.ContainsKey(s.key))
            {
                r.unmeasured.Add(s.key);
            }
        }

        r.b4Ok = r.validationFailures == 0 && r.unmeasured.Count == 0;
        r.json = Write(models, excluded, sourceText);
        r.modelCount = models.Count;
        r.excludedCount = excluded.Count;
        rep.Append("baked ").Append(models.Count.ToString(CultureInfo.InvariantCulture)).Append(" models, excluded ")
           .Append(excluded.Count.ToString(CultureInfo.InvariantCulture)).Append(", b4 照合の不一致 ")
           .Append(r.validationFailures.ToString(CultureInfo.InvariantCulture))
           .Append(r.unmeasured.Count > 0 ? "、照合できなかった " + AnimalLegSkinPoseBakeCore.PyListRepr(r.unmeasured) : "").Append('\n');
        rep.Append("json bytes ").Append(new UTF8Encoding(false).GetByteCount(r.json).ToString(CultureInfo.InvariantCulture)).Append('\n');
        r.report = rep.ToString();
        return r;
    }

    // 1 体分。問題があれば理由を返す（そのモデルは _excluded）。
    private static string BakeOne(Spec spec, Model m, string prefab, StringBuilder rep, Result r, out OutModel om)
    {
        om = null;
        string L = spec.leftPrefix;
        string R = spec.rightPrefix;
        bool clipMode = spec.mode == Mode.ClipThenMirrorLeftLeg;

        // 1. 名前と親子（名前は重複しない前提。runtime は名前と親の名前で骨を探す）
        var byName = new Dictionary<string, Node>(StringComparer.Ordinal);
        var dups = new List<string>();
        foreach (Node nd in m.nodes)
        {
            if (byName.ContainsKey(nd.n))
            {
                dups.Add(nd.n);
            }
            else
            {
                byName[nd.n] = nd;
            }
        }

        if (dups.Count > 0)
        {
            return "Transform の名前が重複 " + AnimalLegSkinPoseBakeCore.PyListRepr(dups);
        }

        var b = new Built();
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var tops = new List<string>();
        foreach (Node nd in m.nodes)
        {
            if (nd.parent == nd.n)
            {
                return "親子が輪になっている（" + nd.n + " の親が自分）";
            }

            if (byName.ContainsKey(nd.parent))
            {
                b.parentOf[nd.n] = nd.parent;
                if (!children.TryGetValue(nd.parent, out List<string> list))
                {
                    list = new List<string>();
                    children[nd.parent] = list;
                }

                list.Add(nd.n);
            }
            else
            {
                b.parentOf[nd.n] = null;
                tops.Add(nd.n);
            }
        }

        var stack = new List<string>();
        foreach (string t in tops)
        {
            stack.Add(t);
            while (stack.Count > 0)
            {
                string x = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                b.order.Add(x);
                if (children.TryGetValue(x, out List<string> ch))
                {
                    for (int i = ch.Count - 1; i >= 0; i--)
                    {
                        stack.Add(ch[i]);
                    }
                }
            }
        }

        if (b.order.Count != m.nodes.Count)
        {
            return "親子が輪になっている（root からたどれない Transform が " + (m.nodes.Count - b.order.Count) + "）";
        }

        // 2. 数（Python の json と同じ読み方で double に）
        foreach (Node nd in m.nodes)
        {
            double[] d = AnimalLegSkinPoseBakeCore.ParseTokens(nd.def);
            if (d == null)
            {
                return nd.n + " に def が無い";
            }

            b.pdef[nd.n] = new[] { d[0], d[1], d[2] };
            b.wdef[nd.n] = AnimalLegSkinPoseBakeCore.Norm(AnimalLegSkinPoseBakeCore.Quat(d));
            double[] s = AnimalLegSkinPoseBakeCore.ParseTokens(nd.skin);
            if (s != null)
            {
                b.pskin[nd.n] = new[] { s[0], s[1], s[2] };
                b.wskin[nd.n] = AnimalLegSkinPoseBakeCore.Norm(AnimalLegSkinPoseBakeCore.Quat(s));
            }

            if (nd.smr)
            {
                b.smr.Add(nd.n);
            }

            if (clipMode)
            {
                double[] q = AnimalLegSkinPoseBakeCore.ParseTokens(nd.pose);
                if (q == null)
                {
                    return nd.n + " にクリップの姿勢が無い";
                }

                b.wpose[nd.n] = AnimalLegSkinPoseBakeCore.Norm(AnimalLegSkinPoseBakeCore.Quat(q));
            }
        }

        // 3. 左右の軸（S = diag(1,1,-1) が使えるか）: skin 姿勢で左の付け根が −Z、右が +Z、右を S で写すと左に重なる
        foreach (string need in new[] { L + "1", R + "1", "Pelvis", "Neck1" })
        {
            if (!b.pskin.ContainsKey(need))
            {
                return need + " に skin が無い（左右の軸を確かめられない）";
            }
        }

        double skinBody = Norm3(Sub(b.pskin["Neck1"], b.pskin["Pelvis"]));
        double[] pl = b.pskin[L + "1"];
        double[] pr = b.pskin[R + "1"];
        double sideErr = Norm3(new[] { pr[0] - pl[0], pr[1] - pl[1], -pr[2] - pl[2] }) / skinBody;
        if (!(pl[2] < 0 && pr[2] > 0) || !(sideErr <= 0.05))
        {
            return "左右の軸が root の Z でない（skin の " + L + "1 z=" + Fx(pl[2], 3, 0) + "、" + R + "1 z=" + Fx(pr[2], 3, 0) +
                   "、|S·右−左| = 体長の " + Fx(sideErr, 3, 0) + "）。S = diag(1,1,-1) の鏡像は使えない";
        }

        // 4. 局所回転と、書く骨
        Dictionary<string, double[]> ldef = Locals(b, b.wdef);
        var lsrc = new Dictionary<string, double[]>(ldef, StringComparer.Ordinal);
        var written = new HashSet<string>(StringComparer.Ordinal);
        double topMoved = 0.0;
        if (clipMode)
        {
            Dictionary<string, double[]> lpose = Locals(b, b.wpose);
            foreach (string n in b.order)
            {
                if (b.parentOf[n] == null)
                {
                    topMoved = Math.Max(topMoved, AnimalLegSkinPoseBakeCore.AngleDeg(b.wpose[n], b.wdef[n]));
                    continue;
                }

                if (n.EndsWith("_end", StringComparison.Ordinal))
                {
                    continue;
                }

                written.Add(n);
                lsrc[n] = lpose[n];
            }

            foreach (string n in b.smr)
            {
                if (!written.Contains(n))
                {
                    return "SkinnedMeshRenderer の骨 " + n + " が書く骨に入らない（root の子か _end）";
                }
            }
        }

        Fk(b, lsrc, out Dictionary<string, double[]> wsrc, out Dictionary<string, double[]> psrc);

        // 5. 鏡像（b4 の mirror_side）: 右の骨 d と左の骨 l、C = (S·skinW_l·S)⁻¹·skinW_d、目標 T_d = S·W_l·S·C（W_l は鏡像の前の姿勢）
        var targets = new List<string>();
        var skipped = new List<string>();
        foreach (string n in b.order)
        {
            if (!n.StartsWith(R, StringComparison.Ordinal) || !b.wskin.ContainsKey(n))
            {
                continue;
            }

            string ln = L + n.Substring(R.Length);
            if (b.wskin.ContainsKey(ln))
            {
                targets.Add(n);
            }
            else
            {
                skipped.Add(n);
            }
        }

        if (targets.Count == 0)
        {
            return "鏡像にする右脚の骨（" + R + "… で skin のあるもの）が無い";
        }

        var targetSet = new HashSet<string>(targets, StringComparer.Ordinal);
        var targetRoots = new List<string>();
        foreach (string d in targets)
        {
            string par = b.parentOf[d];
            if (par == null || !targetSet.Contains(par))
            {
                targetRoots.Add(d);
            }
        }

        if (targetRoots.Count != 1 || b.parentOf[targetRoots[0]] == null)
        {
            return "鏡像にする骨が 1 本の鎖にならない（鎖の根 " + AnimalLegSkinPoseBakeCore.PyListRepr(targetRoots) + "）";
        }

        string targetRoot = targetRoots[0];
        var T = new Dictionary<string, double[]>(StringComparer.Ordinal);
        double selfCheck = 0.0;
        foreach (string d in targets)
        {
            string ln = L + d.Substring(R.Length);
            double[] c = AnimalLegSkinPoseBakeCore.Mul(AnimalLegSkinPoseBakeCore.Inv(Sq(b.wskin[ln])), b.wskin[d]);
            T[d] = AnimalLegSkinPoseBakeCore.Mul(Sq(wsrc[ln]), c);
            selfCheck = Math.Max(selfCheck, AnimalLegSkinPoseBakeCore.AngleDeg(AnimalLegSkinPoseBakeCore.Mul(Sq(b.wskin[ln]), c), b.wskin[d]));
            if (!clipMode)
            {
                written.Add(d);
            }
        }

        // 6. 焼く局所回転: 親から順に、鏡像の骨は inv(この時点の親の world) × T、ほかは鏡像の前のまま
        var lfin = new Dictionary<string, double[]>(lsrc, StringComparer.Ordinal);
        var wfin = new Dictionary<string, double[]>(StringComparer.Ordinal);
        double residual = 0.0;
        foreach (string n in b.order)
        {
            string par = b.parentOf[n];
            double[] wp = par == null ? null : wfin[par];
            if (T.TryGetValue(n, out double[] t))
            {
                lfin[n] = AnimalLegSkinPoseBakeCore.CanonQ(wp == null ? t : AnimalLegSkinPoseBakeCore.Mul(AnimalLegSkinPoseBakeCore.Inv(wp), t));
            }

            wfin[n] = wp == null ? AnimalLegSkinPoseBakeCore.Norm(lfin[n]) : AnimalLegSkinPoseBakeCore.Mul(wp, lfin[n]);
            if (t != null)
            {
                residual = Math.Max(residual, AnimalLegSkinPoseBakeCore.AngleDeg(wfin[n], t));
            }
        }

        // 7. 測る（b4 の measures）
        foreach (string need in new[] { "Pelvis" })
        {
            if (!b.wskin.ContainsKey(need))
            {
                return need + " に skin が無い";
            }
        }

        Fk(b, ldef, out Dictionary<string, double[]> wdefFk, out Dictionary<string, double[]> pdefFk);
        double fkErr = 0.0;
        foreach (string n in b.order)
        {
            fkErr = Math.Max(fkErr, Norm3(Sub(pdefFk[n], b.pdef[n])));
        }

        Fk(b, lfin, out Dictionary<string, double[]> wfinFk, out Dictionary<string, double[]> pfin);
        Measures mDef = Measure(spec, pdefFk, wdefFk, b.wdef["Pelvis"], b.wskin["Pelvis"]);
        Measures mSrc = clipMode ? Measure(spec, psrc, wsrc, b.wdef["Pelvis"], b.wskin["Pelvis"]) : null;
        Measures mFin = Measure(spec, pfin, wfinFk, b.wdef["Pelvis"], b.wskin["Pelvis"]);
        string k = spec.key;
        var lines = new List<string> { FormatMeasures(LabelDef, mDef) };
        if (mSrc != null)
        {
            lines.Add(FormatMeasures(LabelClip, mSrc));
        }

        lines.Add(FormatMeasures(clipMode ? LabelClipMirror : LabelMirror, mFin));
        r.measureLines[k] = lines;

        // 2026-10-09（査読の指摘 1）: 測れない（b4 の数に要る名前が無い）モデル、左右差の体長比が有限でない（体長 0）モデル、書く回転に有限でない数が
        // あるモデルは表に入れない（_excluded）。前は測れなくても表に入れ、体長 0 で "legAsymBefore": NaN を書いた。runtime の MiniJson は NaN の上で
        // 先へ進めず、Parse が戻らなくなる（書く前の確かめで Editor・バッチも固まった）。
        string miss = mDef.missing ?? (mSrc != null ? mSrc.missing : null) ?? mFin.missing;
        if (miss != null)
        {
            foreach (string line in lines)
            {
                rep.Append(prefab).Append(':').Append(line).Append('\n');
            }

            return "測れない（" + miss + " が無い）。b4 と照合できないので表に入れない";
        }

        double asymBefore = (mSrc ?? mDef).legAsym / (mSrc ?? mDef).body;
        double asymAfter = mFin.legAsym / mFin.body;
        if (!AnimalLegSkinPoseBakeCore.IsFinite(asymBefore) || !AnimalLegSkinPoseBakeCore.IsFinite(asymAfter))
        {
            return "左右差の体長比が有限でない（体長 " + Fx((mSrc ?? mDef).body, 4, 0) + " / " + Fx(mFin.body, 4, 0) + "）";
        }

        foreach (string n in b.order)
        {
            if (written.Contains(n) && (!AnimalLegSkinPoseBakeCore.AllFinite(lfin[n]) || !AnimalLegSkinPoseBakeCore.AllFinite(ldef[n])))
            {
                return "書く回転に有限でない数がある（" + n + "）";
            }
        }

        r.final[k] = mFin;

        // 8. 書く鎖: 鏡像の鎖（targetRoot の下の書く骨）と、それ以外の書く骨（clipMode の胴・翼・首・頭・尾・左脚）
        var rightSub = new HashSet<string>(StringComparer.Ordinal);
        foreach (string n in b.order)
        {
            string par = b.parentOf[n];
            if (n == targetRoot || (par != null && rightSub.Contains(par)))
            {
                rightSub.Add(n);
            }
        }

        var body = new OutChain { leg = "body" };
        var right = new OutChain { leg = "rearR", root = targetRoot, girdle = b.parentOf[targetRoot] };
        foreach (string n in b.order)
        {
            if (!written.Contains(n))
            {
                continue;
            }

            var ob = new OutBone { n = n, p = b.parentOf[n], skin = lfin[n], def = ldef[n] };
            if (rightSub.Contains(n))
            {
                right.bones.Add(ob);
            }
            else
            {
                if (body.bones.Count == 0)
                {
                    body.root = n;
                    body.girdle = b.parentOf[n];
                }

                body.bones.Add(ob);
            }
        }

        om = new OutModel
        {
            prefab = prefab,
            mode = ModeName(spec.mode),
            clip = clipMode ? m.clip : null,
            asymBefore = asymBefore,
            asymAfter = asymAfter,
        };
        if (body.bones.Count > 0)
        {
            om.chains.Add(body);
        }

        om.chains.Add(right);

        // 9. 照合（b4 の数と、b4 に依らない左右の確かめ）
        var bad = new List<string>();
        bad.AddRange(CheckAgainstB4(spec.b4Def, mDef, k + " " + LabelDef));
        if (mSrc != null)
        {
            bad.AddRange(CheckAgainstB4(spec.b4Source, mSrc, k + " " + LabelClip));
        }

        bad.AddRange(CheckAgainstB4(spec.b4Result, mFin, k + " " + (clipMode ? LabelClipMirror : LabelMirror)));
        if (mFin.missing == null)
        {
            for (int i = 0; i < 3; i++)
            {
                if (!(Math.Abs(mFin.legL[i] - mFin.legR[i]) <= TolMirrorAngle))
                {
                    bad.Add(k + " 鏡像のあとの脚の傾きの左右差 区間" + i + " " + Fx(mFin.legL[i] - mFin.legR[i], 2, 0) + "°（許容 ±" + TolMirrorAngle + "）");
                }
            }

            if (!(mFin.legAsym / mFin.body <= TolMirrorAsymBody))
            {
                bad.Add(k + " 鏡像のあとの左右差 体長の " + Fx(mFin.legAsym / mFin.body, 3, 0) + "（許容 " + TolMirrorAsymBody + "）");
            }
        }

        r.validationFailures += bad.Count;
        r.problems.AddRange(bad);

        // 10. 報告
        rep.Append(prefab).Append(" key=").Append(k).Append(" mode=").Append(om.mode)
           .Append(" transforms=").Append(m.nodes.Count).Append(" smr=").Append(b.smr.Count)
           .Append(" written=").Append(written.Count).Append(" (body ").Append(body.bones.Count).Append(" + rearR ").Append(right.bones.Count)
           .Append(" ").Append(targetRoot).Append("(<-").Append(right.girdle).Append(")) mirrored=").Append(targets.Count)
           .Append(skipped.Count > 0 ? " skipped(no left / no skin)=" + AnimalLegSkinPoseBakeCore.PyListRepr(skipped) : "").Append('\n');
        if (clipMode)
        {
            rep.Append("  clip ").Append(m.clip ?? "?").Append(" from ").Append(m.source ?? "?")
               .Append("; root の子（書かない）がクリップで回った角 max ").Append(Fx(topMoved, 4, 0)).Append("°\n");
            if (topMoved > 0.01)
            {
                r.notes.Add(k + ": root の子の Transform がクリップで " + Fx(topMoved, 3, 0) + "° 回る（書かないので、焼いた姿勢には入らない。b4 の G3 と同じ）");
            }
        }

        rep.Append("  side: skin ").Append(L).Append("1 z=").Append(Fx(pl[2], 3, 0)).Append(' ').Append(R).Append("1 z=").Append(Fx(pr[2], 3, 0))
           .Append(" |S·R-L|/body=").Append(Fx(sideErr, 4, 0))
           .Append("; mirror self-check (S·skinW_L·S·C = skinW_R) max ").Append(Fx(selfCheck, 6, 0))
           .Append("°; written world vs target max ").Append(Fx(residual, 6, 0)).Append("°\n");
        rep.Append("  sanity: FK of DEF locals vs def, max pos err ").Append(Fx(fkErr, 5, 0)).Append('\n');
        rep.Append(lines[0]).Append('\n').Append("    b4:").Append(spec.b4Def.Substring(1)).Append('\n');
        if (mSrc != null)
        {
            rep.Append(lines[1]).Append('\n').Append("    b4:").Append(spec.b4Source.Substring(1)).Append('\n');
        }

        rep.Append(lines[lines.Count - 1]).Append('\n').Append("    b4:").Append(spec.b4Result.Substring(1)).Append('\n');
        int ends = 0;
        foreach (string n in b.order)
        {
            if (n.EndsWith("_end", StringComparison.Ordinal))
            {
                ends++;
            }
        }

        rep.Append("  measure sets (final): toe L ").Append(mFin.toeLCount).Append(" R ").Append(mFin.toeRCount)
           .Append(", wing ").Append(mFin.wingCount).Append(", asym pairs ").Append(mFin.asymPairs)
           .Append("; _end leaves in the input ").Append(ends).Append("（b4 は _end を入れて測った。無ければ指・翼・左右差が少し動く）\n");
        rep.Append("  check: ").Append(bad.Count == 0 ? "ok（b4 と許容差以内、鏡像の左右も揃う）" : "NG " + bad.Count + " 件").Append('\n');
        foreach (string x in bad)
        {
            rep.Append("    NG ").Append(x).Append('\n');
        }

        return null;
    }

    private static Dictionary<string, double[]> Locals(Built b, Dictionary<string, double[]> world)
    {
        var l = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (string n in b.order)
        {
            string par = b.parentOf[n];
            l[n] = AnimalLegSkinPoseBakeCore.CanonQ(par == null ? world[n] : AnimalLegSkinPoseBakeCore.Mul(AnimalLegSkinPoseBakeCore.Inv(world[par]), world[n]));
        }

        return l;
    }

    // 局所回転から root 座標の回転と位置（位置 = 既定姿勢の親からのずらしを、親の回転の変化だけ回す）。
    private static void Fk(Built b, Dictionary<string, double[]> local, out Dictionary<string, double[]> w, out Dictionary<string, double[]> p)
    {
        w = new Dictionary<string, double[]>(StringComparer.Ordinal);
        p = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (string n in b.order)
        {
            string par = b.parentOf[n];
            if (par == null)
            {
                w[n] = AnimalLegSkinPoseBakeCore.Norm(local[n]);
                p[n] = b.pdef[n];
                continue;
            }

            double[] wp = w[par];
            w[n] = AnimalLegSkinPoseBakeCore.Mul(wp, local[n]);
            double[] off = Rotate(AnimalLegSkinPoseBakeCore.Inv(b.wdef[par]), Sub(b.pdef[n], b.pdef[par]));
            double[] moved = Rotate(wp, off);
            p[n] = new[] { p[par][0] + moved[0], p[par][1] + moved[1], p[par][2] + moved[2] };
        }
    }

    // S·R·S の四元数（S = diag(1,1,-1)）: (x, y, z, w) → (−x, −y, z, w)
    private static double[] Sq(double[] q)
    {
        return new[] { -q[0], -q[1], q[2], q[3] };
    }

    // 単位四元数で v を回す: v + w·t + u × t、t = 2 u × v
    private static double[] Rotate(double[] q, double[] v)
    {
        double x = q[0], y = q[1], z = q[2], w = q[3];
        double tx = 2 * (y * v[2] - z * v[1]);
        double ty = 2 * (z * v[0] - x * v[2]);
        double tz = 2 * (x * v[1] - y * v[0]);
        return new[] { v[0] + w * tx + (y * tz - z * ty), v[1] + w * ty + (z * tx - x * tz), v[2] + w * tz + (x * ty - y * tx) };
    }

    private static double[] Sub(double[] a, double[] b)
    {
        return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
    }

    private static double Norm3(double[] v)
    {
        return Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
    }

    // b4 の pitch(v) = degrees(atan2(v.y, hypot(v.x, v.z)))
    private static double Pitch(double[] v)
    {
        return Math.Atan2(v[1], Math.Sqrt(v[0] * v[0] + v[2] * v[2])) * (180.0 / Math.PI);
    }

    public static double[] QuatOfTokens(string[] pq)
    {
        double[] v = AnimalLegSkinPoseBakeCore.ParseTokens(pq);
        return v == null ? null : AnimalLegSkinPoseBakeCore.Norm(AnimalLegSkinPoseBakeCore.Quat(v));
    }

    // b4 の measures()（birds/b4_reference_pose_compose.py 99〜122 行）と同じ数。P・W は全 Transform の root 座標の位置と回転（_end の葉も入れる）。
    public static Measures Measure(Spec spec, IDictionary<string, double[]> P, IDictionary<string, double[]> W, double[] wdefPelvis, double[] wskinPelvis)
    {
        var m = new Measures();
        string L = spec.leftPrefix;
        string R = spec.rightPrefix;
        foreach (string n in new[] { L + "1", L + "2", L + "3", L + "Ankle", R + "1", R + "2", R + "3", R + "Ankle", "Chest", "Head", "Neck1", "Pelvis" })
        {
            if (!P.ContainsKey(n))
            {
                m.missing = n;
                return m;
            }
        }

        if (!W.ContainsKey("Pelvis") || wdefPelvis == null || wskinPelvis == null)
        {
            m.missing = "Pelvis の回転（今・def・skin）";
            return m;
        }

        double[] hips = { (P[L + "1"][0] + P[R + "1"][0]) / 2, (P[L + "1"][1] + P[R + "1"][1]) / 2, (P[L + "1"][2] + P[R + "1"][2]) / 2 };
        for (int s = 0; s < 2; s++)
        {
            string side = s == 0 ? L : R;
            string[] ch = { side + "1", side + "2", side + "3", side + "Ankle" };
            double[] leg = s == 0 ? m.legL : m.legR;
            for (int k = 0; k < 3; k++)
            {
                leg[k] = Pitch(Sub(P[ch[k + 1]], P[ch[k]]));
            }

            double toe = double.PositiveInfinity;
            int count = 0;
            foreach (KeyValuePair<string, double[]> kv in P)
            {
                if (kv.Key.StartsWith(side + "Digit", StringComparison.Ordinal))
                {
                    toe = Math.Min(toe, kv.Value[1]);
                    count++;
                }
            }

            if (count == 0)
            {
                m.missing = side + "Digit…";
                return m;
            }

            if (s == 0)
            {
                m.toeL = toe;
                m.toeLCount = count;
            }
            else
            {
                m.toeR = toe;
                m.toeRCount = count;
            }
        }

        m.toeMinusHips = Math.Min(m.toeL, m.toeR) - hips[1];
        double asym = 0.0;
        foreach (KeyValuePair<string, double[]> kv in P)
        {
            if (!kv.Key.StartsWith(R, StringComparison.Ordinal) || !P.TryGetValue(kv.Key.Replace(R, L), out double[] lp))
            {
                continue;
            }

            double[] v = kv.Value;
            asym = Math.Max(asym, Norm3(new[] { v[0] - lp[0], v[1] - lp[1], -v[2] - lp[2] }));
            m.asymPairs++;
        }

        m.legAsym = asym;
        double wingZ = 0.0;
        double wingY = double.NegativeInfinity;
        foreach (KeyValuePair<string, double[]> kv in P)
        {
            if (kv.Key.StartsWith("Wing", StringComparison.Ordinal) || kv.Key.StartsWith("Arm", StringComparison.Ordinal))
            {
                wingZ = Math.Max(wingZ, Math.Abs(kv.Value[2]));
                wingY = Math.Max(wingY, kv.Value[1]);
                m.wingCount++;
            }
        }

        if (m.wingCount == 0)
        {
            m.missing = "Wing… / Arm…";
            return m;
        }

        m.wingZ = wingZ;
        m.wingUp = wingY - P["Chest"][1];
        m.headMinusHips = P["Head"][1] - hips[1];
        double[] pn = Sub(P["Neck1"], P["Pelvis"]);
        m.pelvisNeckPitch = Pitch(pn);
        m.body = Norm3(pn);
        m.pelvisVsDef = AnimalLegSkinPoseBakeCore.AngleDeg(W["Pelvis"], wdefPelvis);
        m.pelvisVsSkin = AnimalLegSkinPoseBakeCore.AngleDeg(W["Pelvis"], wskinPelvis);
        return m;
    }

    // b4 の fmt() と同じ書式（"%-44s" "%-22s" "%6.2f" …、脚は round(v, 1) の list の str）。
    public static string FormatMeasures(string label, Measures m)
    {
        if (m.missing != null)
        {
            return "  " + label.PadRight(44) + " 測れない（" + m.missing + " が無い）";
        }

        return "  " + label.PadRight(44) + " legL " + LegRepr(m.legL).PadRight(22) + " legR " + LegRepr(m.legR).PadRight(22) +
               " toe-hips " + Fx(m.toeMinusHips, 2, 6) + " (toe y L " + Fx(m.toeL, 2, 5) + " R " + Fx(m.toeR, 2, 5) + ")" +
               " legAsym " + Fx(m.legAsym, 2, 5) + " (" + Fx(m.legAsym / m.body, 2, 0) + " body)" +
               " wing|z| " + Fx(m.wingZ, 1, 5) + " wingUp " + Fx(m.wingUp, 1, 5) +
               " head-hips " + Fx(m.headMinusHips, 2, 6) + " pelvis->Neck1 pitch " + Fx(m.pelvisNeckPitch, 1, 6) +
               " | Pelvis rot vs DEF " + Fx(m.pelvisVsDef, 1, 5) + " vs SKIN " + Fx(m.pelvisVsSkin, 1, 5);
    }

    private static string LegRepr(double[] v)
    {
        var parts = new string[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            parts[i] = AnimalLegSkinPoseBakeCore.PyFloatRepr(AnimalLegSkinPoseBakeCore.PyRound(v[i], 1));
        }

        return "[" + string.Join(", ", parts) + "]";
    }

    private static string Fx(double v, int decimals, int width)
    {
        return AnimalLegSkinPoseBakeCore.FormatFixed(v, decimals, false).PadLeft(width);
    }

    private static readonly Regex B4Line = new Regex(
        @"legL \[([^\]]*)\]\s+legR \[([^\]]*)\]\s+toe-hips\s+(\S+) \(toe y L\s+(\S+) R\s+(\S+)\) legAsym\s+(\S+) \((\S+) body\) " +
        @"wing\|z\|\s+(\S+) wingUp\s+(\S+) head-hips\s+(\S+) pelvis->Neck1 pitch\s+(\S+) \| Pelvis rot vs DEF\s+(\S+) vs SKIN\s+(\S+)");

    // b4 の行を数に戻す（読めなければ null）。
    public static Measures ParseB4Line(string line)
    {
        Match mt = line == null ? null : B4Line.Match(line);
        if (mt == null || !mt.Success)
        {
            return null;
        }

        var m = new Measures();
        string[] l = mt.Groups[1].Value.Split(new[] { ',' });
        string[] r = mt.Groups[2].Value.Split(new[] { ',' });
        if (l.Length != 3 || r.Length != 3)
        {
            return null;
        }

        for (int i = 0; i < 3; i++)
        {
            m.legL[i] = D(l[i]);
            m.legR[i] = D(r[i]);
        }

        m.toeMinusHips = D(mt.Groups[3].Value);
        m.toeL = D(mt.Groups[4].Value);
        m.toeR = D(mt.Groups[5].Value);
        m.legAsym = D(mt.Groups[6].Value);
        double ratio = D(mt.Groups[7].Value);
        m.body = ratio > 0 ? m.legAsym / ratio : double.NaN;
        m.wingZ = D(mt.Groups[8].Value);
        m.wingUp = D(mt.Groups[9].Value);
        m.headMinusHips = D(mt.Groups[10].Value);
        m.pelvisNeckPitch = D(mt.Groups[11].Value);
        m.pelvisVsDef = D(mt.Groups[12].Value);
        m.pelvisVsSkin = D(mt.Groups[13].Value);
        return m;
    }

    private static double D(string s)
    {
        return double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    // b4 の行と比べ、許容差を超えたものを返す。
    public static List<string> CheckAgainstB4(string b4Line, Measures ours, string what)
    {
        var bad = new List<string>();
        Measures e = ParseB4Line(b4Line);
        if (e == null)
        {
            bad.Add(what + ": b4 の行が読めない");
            return bad;
        }

        if (ours.missing != null)
        {
            bad.Add(what + ": 測れない（" + ours.missing + " が無い）");
            return bad;
        }

        void C(string name, double got, double want, double tol)
        {
            if (!(Math.Abs(got - want) <= tol))
            {
                bad.Add(what + " " + name + " " + Fx(got, 3, 0) + "（b4 " + Fx(want, 2, 0) + "、許容 ±" + tol.ToString(CultureInfo.InvariantCulture) + "）");
            }
        }

        for (int i = 0; i < 3; i++)
        {
            C("legL[" + i + "]", ours.legL[i], e.legL[i], TolAngle);
            C("legR[" + i + "]", ours.legR[i], e.legR[i], TolAngle);
        }

        C("toe-hips", ours.toeMinusHips, e.toeMinusHips, TolPos);
        C("toe y L", ours.toeL, e.toeL, TolPos);
        C("toe y R", ours.toeR, e.toeR, TolPos);
        C("legAsym", ours.legAsym, e.legAsym, TolAsym);
        C("wing|z|", ours.wingZ, e.wingZ, TolWing);
        C("wingUp", ours.wingUp, e.wingUp, TolWing);
        C("head-hips", ours.headMinusHips, e.headMinusHips, TolPos);
        C("pelvis->Neck1 pitch", ours.pelvisNeckPitch, e.pelvisNeckPitch, TolAngle);
        C("Pelvis rot vs DEF", ours.pelvisVsDef, e.pelvisVsDef, TolAngle);
        C("Pelvis rot vs SKIN", ours.pelvisVsSkin, e.pelvisVsSkin, TolAngle);
        return bad;
    }

    // 2 つの測った数の違い（Unity の複製に runtime の規則で書いた結果と、焼いた計算の比べ）。
    public static List<string> CompareMeasures(Measures a, Measures b, double tolPos, double tolAngle)
    {
        var bad = new List<string>();
        if (a.missing != null || b.missing != null)
        {
            bad.Add("測れない（" + (a.missing ?? b.missing) + " が無い）");
            return bad;
        }

        void C(string name, double x, double y, double tol)
        {
            if (!(Math.Abs(x - y) <= tol))
            {
                bad.Add(name + " " + Fx(x, 4, 0) + " と " + Fx(y, 4, 0));
            }
        }

        for (int i = 0; i < 3; i++)
        {
            C("legL[" + i + "]", a.legL[i], b.legL[i], tolAngle);
            C("legR[" + i + "]", a.legR[i], b.legR[i], tolAngle);
        }

        C("toe-hips", a.toeMinusHips, b.toeMinusHips, tolPos);
        C("toe y L", a.toeL, b.toeL, tolPos);
        C("toe y R", a.toeR, b.toeR, tolPos);
        C("legAsym", a.legAsym, b.legAsym, tolPos);
        C("wing|z|", a.wingZ, b.wingZ, tolPos);
        C("wingUp", a.wingUp, b.wingUp, tolPos);
        C("head-hips", a.headMinusHips, b.headMinusHips, tolPos);
        C("pelvis->Neck1 pitch", a.pelvisNeckPitch, b.pelvisNeckPitch, tolAngle);
        C("Pelvis rot vs DEF", a.pelvisVsDef, b.pelvisVsDef, tolAngle);
        C("Pelvis rot vs SKIN", a.pelvisVsSkin, b.pelvisVsSkin, tolAngle);
        return bad;
    }

    // animal_leg_skin_pose.json と同じ形（1 骨 1 行の手書きの JSON、キーは序数順、LF、末尾に改行 1 つ）。
    private static string Write(Dictionary<string, OutModel> models, Dictionary<string, string> excluded, string sourceText)
    {
        var lines = new List<string>
        {
            "{",
            "  \"_meaning\": " + AnimalLegSkinPoseBakeCore.PyJsonString(Meaning, false) + ",",
            "  \"_source\": " + AnimalLegSkinPoseBakeCore.PyJsonString(sourceText ?? "", false) + ",",
            "  \"_excluded\": {",
        };
        var ex = new List<string>(excluded.Keys);
        ex.Sort(StringComparer.Ordinal);
        for (int i = 0; i < ex.Count; i++)
        {
            lines.Add("    " + AnimalLegSkinPoseBakeCore.PyJsonString(ex[i], false) + ": " + AnimalLegSkinPoseBakeCore.PyJsonString(excluded[ex[i]], false) +
                      (i + 1 < ex.Count ? "," : ""));
        }

        lines.Add("  },");
        lines.Add("  \"models\": {");
        var mk = new List<string>(models.Keys);
        mk.Sort(StringComparer.Ordinal);
        for (int mi = 0; mi < mk.Count; mi++)
        {
            OutModel e = models[mk[mi]];
            // 2026-10-09（査読の指摘 1）: 有限でない数は書かない（runtime の MiniJson が止まらなくなる）。BakeOne が除外するので来ないはずの最後の守り。
            if (!AnimalLegSkinPoseBakeCore.IsFinite(e.asymBefore) || !AnimalLegSkinPoseBakeCore.IsFinite(e.asymAfter))
            {
                throw new InvalidOperationException(mk[mi] + ": legAsymBefore / legAsymAfter が有限でない（NaN / Infinity は書かない）");
            }

            foreach (OutChain c in e.chains)
            {
                foreach (OutBone bb in c.bones)
                {
                    if (!AnimalLegSkinPoseBakeCore.AllFinite(bb.skin) || !AnimalLegSkinPoseBakeCore.AllFinite(bb.def))
                    {
                        throw new InvalidOperationException(mk[mi] + ": 骨 " + bb.n + " の回転が有限でない（NaN / Infinity は書かない）");
                    }
                }
            }

            lines.Add("    " + AnimalLegSkinPoseBakeCore.PyJsonString(mk[mi], true) + ": {");
            lines.Add("      \"prefab\": " + AnimalLegSkinPoseBakeCore.PyJsonString(e.prefab, true) + ", \"mode\": " + AnimalLegSkinPoseBakeCore.PyJsonString(e.mode, true) +
                      (e.clip != null ? ", \"clip\": " + AnimalLegSkinPoseBakeCore.PyJsonString(e.clip, true) : "") +
                      ", \"legAsymBefore\": " + AnimalLegSkinPoseBakeCore.PyJsonRound3(e.asymBefore) +
                      ", \"legAsymAfter\": " + AnimalLegSkinPoseBakeCore.PyJsonRound3(e.asymAfter) + ",");
            lines.Add("      \"chains\": [");
            for (int ci = 0; ci < e.chains.Count; ci++)
            {
                OutChain c = e.chains[ci];
                lines.Add("        { \"leg\": " + AnimalLegSkinPoseBakeCore.PyJsonString(c.leg, true) + ", \"root\": " + AnimalLegSkinPoseBakeCore.PyJsonString(c.root, true) +
                          ", \"girdle\": " + AnimalLegSkinPoseBakeCore.PyJsonString(c.girdle, true) + ", \"bones\": [");
                for (int bi = 0; bi < c.bones.Count; bi++)
                {
                    OutBone bb = c.bones[bi];
                    lines.Add("          { \"n\": " + AnimalLegSkinPoseBakeCore.PyJsonString(bb.n, true) + ", \"p\": " + AnimalLegSkinPoseBakeCore.PyJsonString(bb.p, true) +
                              ", \"skin\": [" + AnimalLegSkinPoseBakeCore.Join7(bb.skin) + "], \"def\": [" + AnimalLegSkinPoseBakeCore.Join7(bb.def) + "] }" +
                              (bi + 1 < c.bones.Count ? "," : ""));
                }

                lines.Add("        ] }" + (ci + 1 < e.chains.Count ? "," : ""));
            }

            lines.Add("      ]");
            lines.Add("    }" + (mi + 1 < mk.Count ? "," : ""));
        }

        lines.Add("  }");
        lines.Add("}");
        string text = string.Join("\n", lines.ToArray()) + "\n";
        // 書く前に JSON として読めること、runtime の MiniJson が読めない数（NaN / Infinity）が無いことを確かめる
        if (AnimalLegSkinPoseBakeCore.NonFiniteNumbers(text).Count > 0)
        {
            throw new InvalidOperationException("書く JSON に有限でない数がある");
        }

        return text;
    }

    // 保存した入力（-referencePoseInputIn）の鳥と model_index.txt の行の突き合わせ（2026-10-09、査読の指摘 3）。Specs の 3 体だけを見る:
    // model_index.txt にそのキーの行がちょうど 1 つあり、入力のそのキーの prefab 名がその行と同じか（番号の振り直し・消えたモデル = 入力が古い）。
    // 入力にそのキーが無いことは Bake が "入力に無い" として出すので、ここでは出さない。問題の文の列（空なら良い）。
    public static List<string> CompareInputWithIndex(List<Model> inputs, IEnumerable<string> indexLines, Func<string, string> key)
    {
        var problems = new List<string>();
        var lines = new List<string>(indexLines);
        foreach (Spec s in Specs)
        {
            var idx = new List<string>();
            foreach (string l in lines)
            {
                if (key(AnimalLegSkinPoseBakeCore.LastPathSegment(l)) == s.key)
                {
                    idx.Add(AnimalLegSkinPoseBakeCore.LastPathSegment(l));
                }
            }

            if (idx.Count == 0)
            {
                problems.Add(s.key + ": model_index.txt の Models/Animal に無い（入力にあっても今の prefab が無い）");
                continue;
            }

            if (idx.Count > 1)
            {
                problems.Add(s.key + ": model_index.txt に " + idx.Count.ToString(CultureInfo.InvariantCulture) + " 行ある " + AnimalLegSkinPoseBakeCore.PyListRepr(idx));
                continue;
            }

            foreach (Model m in inputs)
            {
                string prefab = AnimalLegSkinPoseBakeCore.LastPathSegment(m.model ?? "");
                if (key(prefab) == s.key && prefab != idx[0])
                {
                    problems.Add(s.key + ": 入力の prefab は " + prefab + "、model_index.txt は " + idx[0] + "（入力が古い? prefab を開いて取り出し直す）");
                }
            }
        }

        return problems;
    }

    // ---- 入力の JSON（-referencePoseInputOut / -referencePoseInputIn。試験台もこの形で FBX の移植から作った入力を渡す） ----
    public static string WriteInputJson(List<Model> models)
    {
        var sb = new StringBuilder();
        sb.Append("[\n");
        for (int mi = 0; mi < models.Count; mi++)
        {
            Model m = models[mi];
            sb.Append(mi == 0 ? "" : ",\n");
            sb.Append("{\"model\":").Append(AnimalLegSkinPoseBakeCore.PyJsonString(m.model ?? "", false))
              .Append(",\"clip\":").Append(m.clip == null ? "null" : AnimalLegSkinPoseBakeCore.PyJsonString(m.clip, false))
              .Append(",\"source\":").Append(m.source == null ? "null" : AnimalLegSkinPoseBakeCore.PyJsonString(m.source, false))
              .Append(",\"nodes\":[");
            for (int ni = 0; ni < m.nodes.Count; ni++)
            {
                Node nd = m.nodes[ni];
                sb.Append(ni == 0 ? "\n" : ",\n");
                sb.Append("{\"n\":").Append(AnimalLegSkinPoseBakeCore.PyJsonString(nd.n ?? "", false))
                  .Append(",\"parent\":").Append(AnimalLegSkinPoseBakeCore.PyJsonString(nd.parent ?? "", false))
                  .Append(",\"smr\":").Append(nd.smr ? "true" : "false")
                  .Append(",\"def\":").Append(TokensText(nd.def))
                  .Append(",\"skin\":").Append(TokensText(nd.skin))
                  .Append(",\"pose\":").Append(TokensText(nd.pose)).Append('}');
            }

            sb.Append("\n]}");
        }

        sb.Append("\n]\n");
        return sb.ToString();
    }

    private static string TokensText(string[] tokens)
    {
        return tokens == null ? "null" : "[" + string.Join(",", tokens) + "]";
    }

    public static List<Model> ReadInputJson(string text)
    {
        if (!(new AnimalLegSkinPoseBakeCore.JsonParser(text).ParseDocument() is List<object> arr))
        {
            throw new FormatException("入力の JSON の一番外が配列でない");
        }

        var list = new List<Model>();
        foreach (object mo in arr)
        {
            if (!(mo is Dictionary<string, object> md) || !md.TryGetValue("model", out object mn) || !(mn is string modelName))
            {
                throw new FormatException("入力のモデルに model が無い");
            }

            var m = new Model
            {
                model = modelName,
                clip = md.TryGetValue("clip", out object co) ? co as string : null,
                source = md.TryGetValue("source", out object so) ? so as string : null,
            };
            if (!md.TryGetValue("nodes", out object no) || !(no is List<object> nodes))
            {
                throw new FormatException(modelName + ": nodes が無い");
            }

            foreach (object o in nodes)
            {
                if (!(o is Dictionary<string, object> nd) || !nd.TryGetValue("n", out object nv) || !(nv is string name) ||
                    !nd.TryGetValue("parent", out object pv) || !(pv is string parent))
                {
                    throw new FormatException(modelName + ": node に n / parent が無い");
                }

                m.nodes.Add(new Node
                {
                    n = name,
                    parent = parent,
                    smr = nd.TryGetValue("smr", out object sv) && sv is bool sb && sb,
                    def = nd.TryGetValue("def", out object dv) ? Tokens(dv, modelName + "/" + name + " def") : null,
                    skin = nd.TryGetValue("skin", out object kv) ? Tokens(kv, modelName + "/" + name + " skin") : null,
                    pose = nd.TryGetValue("pose", out object qv) ? Tokens(qv, modelName + "/" + name + " pose") : null,
                });
            }

            list.Add(m);
        }

        return list;
    }

    private static string[] Tokens(object v, string what)
    {
        if (v == null)
        {
            return null;
        }

        if (!(v is List<object> a))
        {
            throw new FormatException(what + " が配列でない");
        }

        var t = new string[a.Count];
        for (int i = 0; i < a.Count; i++)
        {
            t[i] = a[i] is AnimalLegSkinPoseBakeCore.RawNumber rn ? rn.token : throw new FormatException(what + " に数でないものがある");
        }

        return t;
    }
}
