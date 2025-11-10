using UnityEngine;
using UnityEngine.Video;
using System.IO;

public class StreamingStereoVideoPlayer : MonoBehaviour
{
    public string fileName = "sample.mp4";

    // ここを追加：動画のアスペクト比に合わせてサイズを変えたいスクリーン
    public Transform leftScreen;
    public Transform rightScreen;

    // サイドバイサイドのステレオかどうか（trueなら横並びで左右に2枚）
    public bool sideBySide = true;

    // 高さの基準スケール（縦方向をどれくらいの大きさにしたいか）
    public float baseHeight = 1f;

    private VideoPlayer vp;

    void Start()
    {
        vp = GetComponent<VideoPlayer>();
        if (vp == null)
        {
            Debug.LogError("VideoPlayer component not found on this GameObject.");
            return;
        }

        // StreamingAssets/sample.mp4 のフルパス
        string path = Path.Combine(Application.streamingAssetsPath, fileName);

        vp.source = VideoSource.Url;
        vp.url = path;
        vp.isLooping = true;

        // 準備完了イベントに登録してから Prepare
        vp.prepareCompleted += OnPrepared;
        vp.Prepare();
    }

    private void OnPrepared(VideoPlayer source)
    {
        // 動画の幅・高さを取得
        float w = source.width;
        float h = source.height;

        if (w <= 0 || h <= 0)
        {
            Debug.LogWarning($"Video size is invalid: {w}x{h}");
            vp.Play();
            return;
        }

        // サイドバイサイドなら片目の幅は半分
        float perEyeWidth = sideBySide ? w * 0.5f : w;

        // 片目分のアスペクト比（横/縦）
        float aspect = perEyeWidth / h;

        // 高さを baseHeight に固定して、横をアスペクト比に合わせる
        Vector3 screenScale = new Vector3(aspect * baseHeight, baseHeight, 1f);

        if (leftScreen != null)
        {
            leftScreen.localScale = screenScale;
        }

        if (rightScreen != null)
        {
            rightScreen.localScale = screenScale;
        }

        // 準備が終わったので再生
        vp.Play();

        // 一度調整したらこのイベントはいらないので解除（任意）
        vp.prepareCompleted -= OnPrepared;
    }
}
