using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// A candidate tactical position found by a provider and scored by scorers.
    /// Passed through the Provider ? Scorer ? Executor pipeline.
    /// </summary>
    public class TacticalSpot
    {
        // ---------- Position -------------------------------------------------

        /// <summary>World position of this spot.</summary>
        public Vector3 Position;

        /// <summary>Normal direction of the cover at this spot (away from wall).</summary>
        public Vector3 CoverNormal;

        /// <summary>Preferred facing direction when occupying this spot.</summary>
        public Vector3 FacingDirection;

        // ---------- Metadata -------------------------------------------------

        /// <summary>Which provider found this spot.</summary>
        public string ProviderTag;

        /// <summary>Optional cover point associated with this spot.</summary>
        public CoverPoint CoverPoint;

        /// <summary>True if this spot has hard cover (wall, crate etc).</summary>
        public bool HasHardCover;

        /// <summary>Approximate height above ground -- used by HighGroundScorer.</summary>
        public float Height;

        /// <summary>True if spot is in shadow (from LightRegistry).</summary>
        public bool IsInShadow;

        /// <summary>Distance to threat estimated position.</summary>
        public float DistanceToThreat;

        /// <summary>Angle of approach relative to threat facing -- used by FlankAngleScorer.</summary>
        public float FlankAngle;

        /// <summary>True if NavMesh path to this spot is valid.</summary>
        public bool IsReachable;

        /// <summary>NavMesh path cost to this spot.</summary>
        public float PathCost;

        // ---------- Scoring --------------------------------------------------

        /// <summary>Final weighted score. Higher is better.</summary>
        public float Score;

        /// <summary>Per-scorer breakdown for inspector display.</summary>
        public Dictionary<string, float> ScoreBreakdown = new Dictionary<string, float>();

        /// <summary>Why this spot was rejected (if Score is very low).</summary>
        public string RejectionReason;

        // ---------- Reservation ----------------------------------------------

        /// <summary>Unit that has reserved this spot. Null if available.</summary>
        public StealthHuntAI ReservedBy;

        public bool IsReserved => ReservedBy != null;

        public void Reserve(StealthHuntAI unit) => ReservedBy = unit;
        public void Release() => ReservedBy = null;

        // ---------- Factory --------------------------------------------------

        public static TacticalSpot FromPosition(Vector3 pos, string providerTag)
            => new TacticalSpot
            {
                Position = pos,
                ProviderTag = providerTag,
                IsReachable = true,
            };

        public static TacticalSpot FromCoverPoint(CoverPoint cp, string providerTag)
            => new TacticalSpot
            {
                Position = cp.transform.position,
                CoverNormal = cp.transform.forward,
                FacingDirection = -cp.transform.forward,
                ProviderTag = providerTag,
                CoverPoint = cp,
                HasHardCover = true,
                IsReachable = true,
            };

        public override string ToString()
            => $"[{ProviderTag}] pos={Position} score={Score:F2}";
    }
}