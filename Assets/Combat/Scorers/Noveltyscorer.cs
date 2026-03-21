using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Prefers positions this specific unit has not recently visited.
    /// Prevents guards from repeatedly cycling through the same two cover spots.
    /// Each unit maintains its own visited position history.
    /// </summary>
    public class NoveltyScorer : TacticalScorerBase
    {
        public override string Name => "NoveltyScorer";
        public override float Weight { get; set; } = 0.6f;

        [Range(2f, 8f)] public float VisitRadius = 3f;
        [Range(3, 12)] public int HistorySize = 6;
        [Range(10f, 60f)] public float ForgetTime = 30f;

        // Per-unit visit history
        private readonly Dictionary<StealthHuntAI, List<(Vector3 pos, float time)>> _history
            = new Dictionary<StealthHuntAI, List<(Vector3, float)>>();

        public override float Score(TacticalSpot spot, TacticalContext ctx)
        {
            if (!_history.TryGetValue(ctx.Unit, out var visits))
                return 1f; // never visited anywhere -- all novel

            float now = Time.time;
            float score = 1f;

            for (int i = visits.Count - 1; i >= 0; i--)
            {
                var (pos, t) = visits[i];

                // Forget old visits
                if (now - t > ForgetTime) { visits.RemoveAt(i); continue; }

                float dist = Vector3.Distance(spot.Position, pos);
                if (dist < VisitRadius)
                {
                    // Recently visited -- penalize based on recency
                    float recency = 1f - (now - t) / ForgetTime;
                    float proximity = 1f - dist / VisitRadius;
                    score -= recency * proximity * 0.4f;
                }
            }

            return Mathf.Clamp01(score);
        }

        /// <summary>Record that a unit visited a position.</summary>
        public void RecordVisit(StealthHuntAI unit, Vector3 position)
        {
            if (!_history.TryGetValue(unit, out var visits))
            {
                visits = new List<(Vector3, float)>();
                _history[unit] = visits;
            }

            visits.Add((position, Time.time));

            // Trim history
            while (visits.Count > HistorySize)
                visits.RemoveAt(0);
        }

        public void ClearHistory(StealthHuntAI unit)
        {
            _history.Remove(unit);
        }
    }
}