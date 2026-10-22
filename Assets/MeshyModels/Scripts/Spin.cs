// 確認用: 再生中、ゆっくり回して全方向から見られるようにする（回転台）
using UnityEngine;

namespace MeshyModels
{
    public class Spin : MonoBehaviour
    {
        public float degreesPerSecond = 20f;

        void Update()
        {
            transform.Rotate(0f, degreesPerSecond * Time.deltaTime, 0f, Space.World);
        }
    }
}
