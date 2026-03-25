using UnityEngine;
using StealthHuntAI.Combat.CQB;

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
        public bool HasIntel => ThreatConfidence > 0.05f;
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
        public int SquadAdvancingCount;  // how many already advancing
        public int SquadFlankingCount;   // how many already flanking
        public int SquadInCoverCount;    // how many in cover/suppressing
        public bool HasShotFrom;          // guard was shot at without LOS
        public bool HasCommittedGoal;     // squad has a committed goal
        public Vector3 CommittedPosition;   // position of committed goal

        // World
        public bool ChokepointNearby;
        public bool FlankRouteOpen;
        public bool HighGroundNearby;
        public bool WithdrawRouteOpen;

        // CQB
        public bool NearEntryPoint;
        public bool AtStackPosition;
        public bool AtDomPoint;
        public bool RoomCleared;

        // Goal state flags (set by planner)
        public bool TargetEliminated;
        public bool ChokepointHeld;
        public bool SafePosition;

        // ---------- Builder --------------------------------------------------

        // ---------- Per-unit cache ------------------------------------------

        /// <summary>Public so WorldStateCache component can return it.</summary>
        public struct CachedChecks
        {
            public bool HighGround;
            public bool NearEntry;
            public bool Chokepoint;
            public float Time;
        }

        // Expensive checks (high ground, entry point) cached on a per-unit
        // component to avoid stale static dictionary on scene reload.

        private const float CacheInterval = 0.5f;

        private static CachedChecks GetChecks(StealthHuntAI unit)
        {
            var cache = unit.GetComponent<WorldStateCache>();
            if (cache == null) cache = unit.gameObject.AddComponent<WorldStateCache>();
            return cache.Get(unit);
        }

        public static WorldState Build(StealthHuntAI unit, ThreatModel threat,
                                        TacticalBrain brain)
        {
            var units = HuntDirector.AllUnits;
            int alive = 0;
            int total = 0;
            bool squadSuppressing = false;
            bool squadAdvancing = false;
            int advancingCount = 0;
            int flankingCount = 0;
            int inCoverCount = 0;

            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.squadID != unit.squadID) continue;
                total++;
                if (u.CurrentAlertState != AlertState.Passive) alive++;

                var sc = u.GetComponent<StandardCombat>();
                if (sc == null || u == unit) continue;
                if (sc.CurrentGoal == StandardCombat.Goal.Suppress) { squadSuppressing = true; inCoverCount++; }
                if (sc.CurrentGoal == StandardCombat.Goal.HoldAndFire) inCoverCount++;
                if (sc.CurrentGoal == StandardCombat.Goal.AdvanceTo) { squadAdvancing = true; advancingCount++; }
                if (sc.CurrentGoal == StandardCombat.Goal.Flank) flankingCount++;
            }

            float health = 1f;
            var gh = unit.GetComponent<IHealthProvider>();
            if (gh != null) health = gh.HealthPercent;

            bool inCover = false;
            var sc2 = unit.GetComponent<StandardCombat>();
            if (sc2 != null) inCover = sc2.IsInCover;

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
                HasShotFrom = threat.HasShotFrom,
                HasCommittedGoal = brain?.CommittedGoal != null,
                CommittedPosition = brain?.CommittedGoal?.Position ?? Vector3.zero,
                SquadmateSuppressing = squadSuppressing,
                SquadmateAdvancing = squadAdvancing,
                SquadAdvancingCount = advancingCount,
                SquadFlankingCount = flankingCount,
                SquadInCoverCount = inCoverCount,
                NearEntryPoint = GetChecks(unit).NearEntry,
                AtStackPosition = false,
                AtDomPoint = false,
                RoomCleared = brain?.CQB?.RoomCleared ?? false,
                ChokepointNearby = GetChecks(unit).Chokepoint,
                FlankRouteOpen = brain.GetFlankPosition(unit).HasValue,
                HighGroundNearby = GetChecks(unit).HighGround,
                WithdrawRouteOpen = true,  // always assume withdraw is possible
                TargetEliminated = false,
                ChokepointHeld = false,
                SafePosition = false,
            };
        }

        private static bool CheckNearEntryPoint(StealthHuntAI unit)
        {
            // If CQB is active for this squad -- all guards can Stack
            var brain = TacticalBrain.GetOrCreate(unit.squadID);
            if (brain.CQB.IsActive) return true;

            // Otherwise check proximity to nearest entry point
            var ep = EntryPointRegistry.FindNearest(unit.transform.position, unit);
            return ep != null && ep.DistToStack(unit.transform.position) < 12f;
        }

        private static bool CheckHighGroundNearby(StealthHuntAI unit)
        {
            Vector3 pos = unit.transform.position;
            // Sample NavMesh at various heights -- if we find elevated reachable spot it counts
            float[] heights = { 2f, 3f, 4f, 5f };
            float[] angles = { 0f, 90f, 180f, 270f };
            foreach (float h in heights)
                foreach (float a in angles)
                {
                    Vector3 dir = Quaternion.Euler(0, a, 0) * Vector3.forward;
                    Vector3 candidate = pos + dir * 8f + Vector3.up * h;
                    if (!UnityEngine.AI.NavMesh.SamplePosition(candidate,
                        out var hit, 1.5f, UnityEngine.AI.NavMesh.AllAreas)) continue;
                    if (hit.position.y - pos.y < 1.5f) continue;
                    // Verify reachable via OffMeshLink
                    var path = new UnityEngine.AI.NavMeshPath();
                    if (!UnityEngine.AI.NavMesh.CalculatePath(pos, hit.position,
                        UnityEngine.AI.NavMesh.AllAreas, path)) continue;
                    if (path.status == UnityEngine.AI.NavMeshPathStatus.PathComplete)
                        return true;
                }
            return false;
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