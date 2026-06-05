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


    public void Reset(Vector3 value)
    {
        x.Reset(value.x);
        y.Reset(value.y);
        z.Reset(value.z);
    }


    private sealed class LowPassFilter1D
    {
        private bool initialized;
        private float previousValue;

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
            float cutoff = minCutoff + beta * Mathf.Abs(filteredDerivative);
            return valueFilter.Filter(value, ComputeAlpha(cutoff, dt));
        }


        public void Reset(float value)
        {
            initialized = true;
            previousRawValue = value;
            valueFilter.Reset(value);
            derivativeFilter.Reset(0f);
        }


        private static float ComputeAlpha(float cutoff, float deltaTime)
        {
            float tau = 1f / (2f * Mathf.PI * Mathf.Max(0.0001f, cutoff));
            return 1f / (1f + tau / Mathf.Max(0.0001f, deltaTime));
        }
    }
}
