3D モデル 10 個（MeshyModels）
==============================

■ 読み込み方
  Unity（URP のプロジェクト。Unity 6 / URP 17 で作成）で Assets > Import Package > Custom Package… からこのパッケージを選ぶ。
  全部 Assets/MeshyModels に入ります（約 280 MB。読み込みに数分かかります）。
  ※ 前に「車のライト_CarLightsKit」を読み込んだプロジェクトでは、先に Project ウィンドウで Assets/CarLightsKit を削除してから読み込んでください
    （中身が同じものなので、削除しないと車の部品が CarLightsKit のほうに残ります。シーンに置いた車は、そのままつながります）。

■ すぐ使える部品（Prefabs フォルダ）… シーンにドラッグするだけ。大きさは実物大（m）、前は +Z
  人   man_run（男性・身長 約1.72 m）, woman_run（女性・約1.60 m） … 走る動き（再生 ▶ でくり返し）。Humanoid 設定済み
  動物 dog_walk（犬・高さ 約0.78 m、長さ 約1.18 m）, cat_walk（猫・高さ 約0.37 m） … 歩く動き（再生 ▶ でくり返し）
  物   balloon（風船・約0.63 x 0.80 m）, soccer_ball（直径 0.22 m）, tennis_ball（直径 0.067 m） … 底の真ん中が原点（置くと地面に乗る）
  車   car_red_lights（長さ 4.03 m）, car_van_lights（軽バン・3.13 m）, car_navy_lights（4.00 m） … ライト付き
       車（一番上）を選ぶと Inspector に「Car Lights」が出ます:
         Headlights / Tail Lights … ヘッドライト・テールのオン・オフ
         Blinker … Off / Left / Right / Hazard（点滅。Blink Period = 1回の秒数）。赤いセダンは初めから Hazard
         ※ 紺の車にはウィンカーがありません（ヘッドライト・テールのみ）
         Head Glow / Amber Glow / Tail Glow … ランプの光の明るさ、Head Beam … 道を照らす光の明るさ
       点滅は再生中に動きます。ライトを消すときは、部品ごと止めずに Headlights などのチェックを外してください。
       車1台あたり、GPU のメモリを約 45 MB 使います（たくさん並べるときは注意）。
  CarLights_Bloom … ランプの光のにじみ（Bloom だけ。シーンの色や明るさは変えません）
       シーンに1つ置き、Camera の Inspector > Rendering > Post Processing にチェック（URP のもの。Post Processing v2 の部品は不要）。
       すでに Global Volume があるシーンでは、これを置かずに、その Volume に Add Override > Post-processing > Bloom を足しても同じです。
       にじまないときは、URP Asset の HDR と、Renderer の Post-processing が入っているかを確認。

■ 動かし方のメモ
  - 走る・歩く動きはその場で動きます（Apply Root Motion では前に進みません）。
    前に進ませるには、部品の一番上に Scripts/MoveForward を付けて Speed を入れる。
    足がすべって見えない速さの目安: 人 3.5 m/秒、犬 0.6 m/秒、猫 0.27 m/秒（Loop Distance を入れると、その距離で元の位置に戻る）
  - Timeline で動かすときは、空の GameObject を親にして、親の位置を録画する（部品の Animator は走り・歩きのまま）。
    Timeline が無いときは Window > Package Manager で Timeline を入れる。
  - 動かす・回す・大きさを変えるのは一番上（man_run、car_red_lights など）だけ。中の子の Transform は触らないでください
    （モデルの向きと大きさの調整が入っています）。
  - 人は Humanoid なので、ほかの Humanoid の動き（Mixamo など）も、コントローラーを替えれば使えます。

■ 見本のシーン（Demo フォルダ）… 開いて再生 ▶
  Motion_Demo     … 男性・女性が走り、犬・猫が歩く
  Objects_Demo    … 車・風船・ボールが回転台で回る（大きさ比べの 1 m の立方体つき）
  CarLights_Demo  … 夕暮れの道で、車のライトとハザード

■ そのほか
  Models/      … 元の 3D モデル（FBX）と、取り出した画像（Textures。metallic / roughness の画像は参考用で、材質にはつないでいません）
  Animations/  … 走る・歩く動き（犬・猫は、つなぎ目を直した dog_walk_loop / cat_walk_loop）とコントローラー
  CarLights/   … 車のライトの画像・材質・空など、Scripts/ … CarLights（車のライト）、MoveForward（前へ進める）、Spin（見本の回転台）
  明るさは Linear の色空間（Unity 6 の初期設定）に合わせてあります。
