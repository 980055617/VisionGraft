using UnityEngine;

public sealed class RuntimeOneEuroVector3Filter
{
    private readonly OneEuroFilter1D x;
    private readonly OneEuroFilter1D y;
    private readonly OneEuroFilter1D z;

    public RuntimeOneEuroVector3Filter(float minCutoffHz, float betaValue, float derivativeCutoffHz)
    {
        x = new OneEuroFilter1D(minCutoffHz, betaValue, derivativeCutoffHz);
        y = new OneEuroFilter1D(minCutoffHz, betaValue, derivativeCutoffHz);
        z = new OneEuroFilter1D(minCutoffHz, betaValue, derivativeCutoffHz);
    }


    public Vector3 Filter(Vector3 value, float deltaTime)
    {
        return new Vector3(
            x.Filter(value.x, deltaTime),
            y.Filter(value.y, deltaTime),
            z.Filter(value.z, deltaTime));
    }


    // S（2026-10-16、StreamingStereoVideoPlayer.stereoDepthMatchVideo）: 速さの項（beta × |平滑した微分|）に掛ける倍率。
    // 目のまわりに k 倍した入力に 1/k を渡すと遮断周波数が掛ける前と同じになり、出力も厳密に k 倍になる。betaScale = 1 は上の Filter と同じ値。
    public Vector3 Filter(Vector3 value, float deltaTime, float betaScale)
    {
        return new Vector3(
            x.Filter(value.x, deltaTime, betaScale),
            y.Filter(value.y, deltaTime, betaScale),
            z.Filter(value.z, deltaTime, betaScale));
    }


    public void Reset(Vector3 value)
    {
        x.Reset(value.x);
        y.Reset(value.y);
        z.Reset(value.z);
    }


    // 状態（直前の入力と直前の出力）を delta だけずらす。微分の平滑は触らない（2026-10-13、exactScaleRefine）。
    // 入力の系列が一定量ずれたとき（倍率の測り直しで root の目標が動いた）、ずれる前からその系列だったのと同じ出力を返すようにする:
    // 次の Filter の微分は「ずらした直前の入力」との差なので変わらず、出力は「ずらした直前の出力」から同じ割合で寄る。初期化前なら何もしない。
    public void Shift(Vector3 delta)
    {
        x.Shift(delta.x);
        y.Shift(delta.y);
        z.Shift(delta.z);
    }


    // S（2026-10-16）: 状態を origin のまわりに factor 倍し、微分の平滑を factor 倍する（配置を目のまわりに倍率し直したとき）。初期化前なら何もしない。
    public void ScaleAbout(Vector3 origin, float factor)
    {
        x.ScaleAbout(origin.x, factor);
        y.ScaleAbout(origin.y, factor);
        z.ScaleAbout(origin.z, factor);
    }


    // 直前の出力 − 直前の入力（平滑が今どれだけ遅れているか）。初期化前なら false。状態は変えない（2026-10-14、animalBottomClippedHeadPin）。
    public bool TryGetLag(out Vector3 lag)
    {
        lag = Vector3.zero;
        if (!x.TryGetLag(out float lx) || !y.TryGetLag(out float ly) || !z.TryGetLag(out float lz))
        {
            return false;
        }

        lag = new Vector3(lx, ly, lz);
        return true;
    }


    private sealed class LowPassFilter1D
    {
        private bool initialized;
        private float previousValue;

        public float Value => previousValue;

        public float Filter(float value, float alpha)
        {
            if (!initialized)
            {
                initialized = true;
                previousValue = value;
                return value;
            }

            previousValue = alpha * value + (1f - alpha) * previousValue;
            return previousValue;
        }


        public void Reset(float value)
        {
            initialized = true;
            previousValue = value;
        }


        public void Shift(float delta)
        {
            if (initialized)
            {
                previousValue += delta;
            }
        }


        public void ScaleAbout(float origin, float factor)
        {
            if (initialized)
            {
                previousValue = origin + (previousValue - origin) * factor;
            }
        }


        public void Scale(float factor)
        {
            if (initialized)
            {
                previousValue *= factor;
            }
        }
    }


    private sealed class OneEuroFilter1D
    {
        private readonly LowPassFilter1D valueFilter = new LowPassFilter1D();
        private readonly LowPassFilter1D derivativeFilter = new LowPassFilter1D();
        private readonly float minCutoff;
        private readonly float beta;
        private readonly float derivativeCutoff;
        private bool initialized;
        private float previousRawValue;

        public OneEuroFilter1D(float minCutoffHz, float betaValue, float derivativeCutoffHz)
        {
            minCutoff = Mathf.Max(0.0001f, minCutoffHz);
            beta = Mathf.Max(0f, betaValue);
            derivativeCutoff = Mathf.Max(0.0001f, derivativeCutoffHz);
        }


        public float Filter(float value, float deltaTime)
        {
            return Filter(value, deltaTime, 1f);
        }


        public float Filter(float value, float deltaTime, float betaScale)
        {
            float dt = Mathf.Max(0.0001f, deltaTime);
            if (!initialized)
            {
                initialized = true;
                previousRawValue = value;
                valueFilter.Reset(value);
                derivativeFilter.Reset(0f);
                return value;
            }

            float derivative = (value - previousRawValue) / dt;
            previousRawValue = value;
            float filteredDerivative = derivativeFilter.Filter(derivative, ComputeAlpha(derivativeCutoff, dt));
            float cutoff = minCutoff + beta * betaScale * Mathf.Abs(filteredDerivative);
            return valueFilter.Filter(value, ComputeAlpha(cutoff, dt));
        }


        public void Reset(float value)
        {
            initialized = true;
            previousRawValue = value;
            valueFilter.Reset(value);
            derivativeFilter.Reset(0f);
        }


        public void Shift(float delta)
        {
            if (!initialized)
            {
                return;
            }

            previousRawValue += delta;
            valueFilter.Shift(delta);
        }


        public void ScaleAbout(float origin, float factor)
        {
            if (!initialized)
            {
                return;
            }

            previousRawValue = origin + (previousRawValue - origin) * factor;
            valueFilter.ScaleAbout(origin, factor);
            derivativeFilter.Scale(factor);
        }


        public bool TryGetLag(out float lag)
        {
            lag = 0f;
            if (!initialized)
            {
                return false;
            }

            lag = valueFilter.Value - previousRawValue;
            return true;
        }


        private static float ComputeAlpha(float cutoff, float deltaTime)
        {
            float tau = 1f / (2f * Mathf.PI * Mathf.Max(0.0001f, cutoff));
            return 1f / (1f + tau / Mathf.Max(0.0001f, deltaTime));
        }
    }
}
