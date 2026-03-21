using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Penalizes positions with high heat from HuntDirector's heat map.
    /// Heat accumulates where guards have been recently.
    /// Encourages guards to spread out and not repeatedly use the same cover.
    /// </summary>
    public class HeatMapScorer : TacticalScorerBase
    {
        public override string Name => "HeatMapScorer";
        public override float Weight { get; set; } = 1.0f;

        public override float Score(TacticalSpot spot, TacticalContext ctx)
        {
            float heat = HuntDirector.GetHeat(spot.Position);
            return Mathf.Clamp01(1f - heat);
        }
    }
}