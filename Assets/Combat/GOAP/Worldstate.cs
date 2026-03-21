using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Snapshot of the tactical world state used by the GOAP planner.
    /// Built fresh each planning cycle from the unit and squad context.
    /// Immutable after construction.
    /// </summary>
    public struct WorldState
    {
        // Threat
        public bool HasLOS;
        public float ThreatConfidence;
        public float DistToThreat;
        public bool ThreatInChokepoint;

        // Unit
        public float Health;
        public bool InCover;
        public bool IsSuppressed;
        public float Awareness;

        // Squad
        public float SquadStrength;       // 0-1 alive/total
        public int SquadSize;
        public bool SquadmateSuppressing;
        public bool SquadmateAdvancing;

        // World
        public bool ChokepointNearby;
        public bool FlankRouteOpen;
        public bool HighGroundNearby;
        public bool WithdrawRouteOpen;

        // Buddy system
        public BuddyRole BuddyRole;
        public bool BuddyIsSuppressing; // my buddy is currently suppressing
        public bool BuddyArrived;       // my buddy signalled arrived

        // Goal state flags (set by planner)
        public bool TargetEliminated;
        public bool ChokepointHeld;
        public bool SafePosition;

        // ---------- Builder --------------------------------------------------

        public static WorldState Build(StealthHuntAI unit, ThreatModel threat,
                                        TacticalBrain brain)
        {
            var units = HuntDirector.AllUnits;
            int alive = 0;
            int total = 0;
            bool squadSuppressing = false;
            bool squadAdvancing = false;

            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.squadID != unit.squadID) continue;
                total++;
                if (u.CurrentAlertState != AlertState.Passive) alive++;

                var sc = u.GetComponent<StandardCombat>();
                if (sc == null || u == unit) continue;
                if (sc.CurrentGoal == StandardCombat.Goal.Suppress) squadSuppressing = true;
                if (sc.CurrentGoal == StandardCombat.Goal.AdvanceTo) squadAdvancing = true;
            }

            float health = 1f;
            var gh = unit.GetComponent<IHealthProvider>();
            if (gh != null) health = gh.HealthPercent;

            bool inCover = false;
            var sc2 = unit.GetComponent<StandardCombat>();
            if (sc2 != null) inCover = sc2.IsInCover;

            var buddySys = BuddySystem.GetOrCreate(unit.squadID);
            var buddy = buddySys.GetBuddy(unit);
            var buddySC = buddy?.GetComponent<StandardCombat>();
            bool buddySuppressing = buddySC?.CurrentGoal == StandardCombat.Goal.Suppress;

            return new WorldState
            {
                HasLOS = threat.HasLOS,
                ThreatConfidence = threat.Confidence,
                DistToThreat = threat.HasIntel
                                      ? Vector3.Distance(unit.transform.position,
                                                         threat.EstimatedPosition)
                                      : 999f,
                Health = health,
                InCover = inCover,
                IsSuppressed = unit.IsSuppressed,
                Awareness = unit.AwarenessLevel,
                SquadStrength = total > 0 ? (float)alive / total : 1f,
                SquadSize = total,
                SquadmateSuppressing = squadSuppressing,
                SquadmateAdvancing = squadAdvancing,
                BuddyRole = buddySys.GetRole(unit),
                BuddyIsSuppressing = buddySuppressing,
                BuddyArrived = false,
                ChokepointNearby = IsChokepointNearby(unit),
                FlankRouteOpen = brain.GetFlankPosition(unit).HasValue,
                HighGroundNearby = false, // evaluated by VantageProvider
                WithdrawRouteOpen = true,  // always assume withdraw is possible
                TargetEliminated = false,
                ChokepointHeld = false,
                SafePosition = false,
            };
        }

        private static bool IsChokepointNearby(StealthHuntAI unit)
        {
            // Simple heuristic -- narrow NavMesh passage nearby
            // Full implementation uses ChokePoint registry
            return false; // extended by TacticalZone with type Defend
        }

        // ---------- Distance -------------------------------------------------

        public float DistanceTo(WorldState other)
        {
            // Heuristic distance for A* -- count differing flags
            int diff = 0;
            if (HasLOS != other.HasLOS) diff++;
            if (InCover != other.InCover) diff++;
            if (TargetEliminated != other.TargetEliminated) diff += 3;
            if (ChokepointHeld != other.ChokepointHeld) diff += 2;
            if (SafePosition != other.SafePosition) diff += 2;
            diff += (int)(Mathf.Abs(ThreatConfidence - other.ThreatConfidence) * 3f);
            return diff;
        }

        public override string ToString()
            => "LOS=" + HasLOS
             + " conf=" + ThreatConfidence.ToString("F2")
             + " dist=" + DistToThreat.ToString("F0")
             + " health=" + Health.ToString("F2")
             + " squadStr=" + SquadStrength.ToString("F2");
    }

    /// <summary>
    /// Thin proxy so WorldState can read health without Combat depending on Demo.
    /// Add this component alongside GuardHealth, or implement in your own health system.
    /// </summary>
    public interface IHealthProvider
    {
        float HealthPercent { get; }
    }

    /// <summary>Proxy component -- attach alongside GuardHealth.</summary>
    [UnityEngine.AddComponentMenu("")]
    public class GuardHealthProxy : UnityEngine.MonoBehaviour, IHealthProvider
    {
        public float HealthPercent { get; set; } = 1f;
    }
}