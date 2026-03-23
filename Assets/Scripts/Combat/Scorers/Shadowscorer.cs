using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Prefers positions in shadow -- harder for player to see guards moving.
    /// Uses our LightRegistry for accurate light sampling.
    /// Only meaningful in Suspicious/early Hostile states.
    /// Weight should be reduced when threat confidence is high (player already knows position).
    /// </summary>
    public class ShadowScorer : TacticalScorerBase
    {
        public override string Name => "ShadowScorer";
        public override float Weight { get; set; } = 0.8f;

        public override float Score(TacticalSpot spot, TacticalContext ctx)
        {
            // Only meaningful at lower confidence
            if (ctx.ThreatConfidence > 0.85f)
                return 0.5f;

            // Sample light level at spot position using LightRegistry
            float lightLevel = SampleLightLevel(spot.Position);

            spot.IsInShadow = lightLevel < 0.3f;

            float shadowScore = 1f - lightLevel;
            float stealthWeight = 1f - ctx.ThreatConfidence;
            return Mathf.Lerp(0.5f, shadowScore, stealthWeight);
        }

        private static float SampleLightLevel(Vector3 pos)
        {
            var lights = LightRegistry.All;
            if (lights == null || lights.Count == 0) return 0.5f;

            float total = 0f;
            for (int i = 0; i < lights.Count; i++)
            {
                var light = lights[i];
                if (light == null || !light.enabled) continue;

                float dist = Vector3.Distance(pos, light.transform.position);

                if (light.type == LightType.Directional)
                {
                    // Check if in shadow via raycast
                    if (!Physics.Raycast(pos + Vector3.up * 0.5f,
                        -light.transform.forward, 50f))
                        total += light.intensity * 0.5f;
                }
                else
                {
                    float range = light.range;
                    if (dist < range)
                        total += light.intensity * (1f - dist / range);
                }
            }

            return Mathf.Clamp01(total / 3f);
        }
    }
}