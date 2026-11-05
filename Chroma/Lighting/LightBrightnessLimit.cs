using UnityEngine;

namespace Chroma.Lighting;

// Light colors are emitted as rgb scaled by alpha, and both feed the bloom pass. Maps can push
// either far past 1, so the product is capped while hue and saturation are kept.
internal static class LightBrightnessLimit
{
    internal const float Unlimited = 10f;

    internal static float Value { get; set; } = 3f;

    internal static Color Apply(Color color)
    {
        float limit = Value;
        if (limit >= Unlimited)
        {
            return color;
        }

        float alpha = Mathf.Max(color.a, 0f);
        float peak = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
        if (peak * alpha <= limit)
        {
            return color;
        }

        float cappedAlpha = Mathf.Min(alpha, limit);
        float rgbScale = Mathf.Min(1f, limit / (peak * cappedAlpha));
        return new Color(color.r * rgbScale, color.g * rgbScale, color.b * rgbScale, cappedAlpha);
    }
}
