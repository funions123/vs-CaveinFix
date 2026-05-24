namespace CaveinFix;

public class ModConfig
{
    /// <summary>
    /// Multiplies the instability of all unstable rock blocks.
    /// 1.0 = default behaviour. Lower values make blocks more stable (fewer cave-ins);
    /// higher values make blocks less stable (more cave-ins). Clamped to [0, 1] after multiplication.
    /// </summary>
    public float InstabilityMultiplier { get; set; } = 1.0f;

    /// <summary>
    /// Maximum number of face-adjacent steps the cave-in epicenter search walks through
    /// connected unstable rock before giving up. Higher values allow cave-ins to trigger
    /// from blocks further away from the break point. Default is 20.
    /// Only has an effect when InterestingOreGen is not installed.
    /// </summary>
    public int MaxCaveinSearchSteps { get; set; } = 20;
}
