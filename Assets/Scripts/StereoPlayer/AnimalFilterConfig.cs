public struct AnimalFilterConfig
{
    public float minCutoffHz;
    public float beta;
    public float derivativeCutoffHz;

    public static AnimalFilterConfig Default => new AnimalFilterConfig
    {
        minCutoffHz = 1.0f,
        beta = 0.15f,
        derivativeCutoffHz = 1.0f
    };
}
