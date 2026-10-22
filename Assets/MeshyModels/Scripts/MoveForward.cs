// 前（+Z）へまっすぐ進める。人・動物のプレハブ（一番上）に付けると、走る・歩く動きのまま前へ進む
//  走る・歩く動きはその場で動くので（Apply Root Motion では進まない）、位置はこれで動かす
using UnityEngine;

namespace MeshyModels
{
    public class MoveForward : MonoBehaviour
    {
        [Tooltip("速さ（m/秒）。足がすべって見えない目安: 人（走る）3.5、犬（歩く）0.6、猫（歩く）0.27")]
        public float speed = 1f;
        [Tooltip("この距離（m）進んだら、はじめの位置に戻る。0 なら戻らない")]
        public float loopDistance = 0f;

        Vector3 start;
        float traveled;

        void OnEnable()
        {
            start = transform.position;
            traveled = 0f;
        }

        void Update()
        {
            float d = speed * Time.deltaTime;
            transform.position += transform.forward * d;
            traveled += d;
            if (loopDistance > 0f && traveled >= loopDistance) { transform.position = start; traveled = 0f; }
        }
    }
}
