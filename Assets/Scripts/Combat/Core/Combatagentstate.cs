using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// All mutable state for one guard's combat behaviour.
    /// Owned exclusively by StandardCombat.
    /// Only modified through StandardCombat.ForceRole() and role Tick methods.
    /// No other class may write to this directly.
    /// </summary>
    public class CombatAgentState
    {
        // ---------- Role -----------------------------------------------------

        public StandardCombat.CombatRole Role = StandardCombat.CombatRole.Idle;
        public float Timer = 0f;
        public float MaxTime = 8f;
        public StandardCombat.Goal Goal = StandardCombat.Goal.Idle;

        // ---------- Scenario tracking ----------------------------------------

        public SquadTactician.TacticianScenario LastScenario
            = (SquadTactician.TacticianScenario)(-1);

        // ---------- Movement -------------------------------------------------

        public Vector3 Destination = Vector3.zero;
        public bool DestinationSet = false;
        public List<Vector3> Waypoints = null;
        public int WaypointIdx = 0;

        // ---------- Cover ----------------------------------------------------

        public bool AtCover = false;
        public Vector3 CoverPos = Vector3.zero;
        public TacticalSpot ReservedSpot = null;
        public float ExposedTimer = 0f;
        public float RepositionWindow = 2.5f;

        // ---------- Per-role timers ------------------------------------------

        public float SearchScanTimer = 0f;
        public float CombatShootTimer = 0f;
        public float SuppressBurstTimer = 0f;
        public int SuppressBurst = 0;
        public bool CautiousWaiting = false;
        public float CautiousWaitTimer = 0f;

        // ---------- Stuck detection ------------------------------------------

        public Vector3 LastPos = Vector3.zero;
        public float StuckTimer = 0f;
        public float BlockedTimer = 0f;
        public HashSet<Vector3Int> BlockedCells = new HashSet<Vector3Int>();

        // ---------- CQB ------------------------------------------------------

        public GoapAction CqbAction = null;

        // ---------- Mutation helpers -----------------------------------------

        /// <summary>
        /// Reset all movement state. The ONLY place _roleDestSet etc should be cleared.
        /// </summary>
        public void ResetMovement()
        {
            Destination = Vector3.zero;
            DestinationSet = false;
            Waypoints = null;
            WaypointIdx = 0;
        }

        /// <summary>Reset all role state. Called by ForceRole only.</summary>
        public void ResetRole(StandardCombat.CombatRole newRole, float maxTime)
        {
            ResetMovement();
            Role = newRole;
            Timer = 0f;
            MaxTime = maxTime;
            AtCover = false;
            CautiousWaiting = false;
            CqbAction = null;
            SuppressBurst = 0;
            SuppressBurstTimer = 0f;
            SearchScanTimer = 0f;
            ExposedTimer = 0f;
        }

        public bool IsMoving => Role == StandardCombat.CombatRole.Advance
                             || Role == StandardCombat.CombatRole.Flank
                             || Role == StandardCombat.CombatRole.Reposition
                             || Role == StandardCombat.CombatRole.Cautious;

        public bool IsDestinationBlocked(Vector3 dest)
        {
            var cell = new Vector3Int(
                Mathf.RoundToInt(dest.x / 2f),
                Mathf.RoundToInt(dest.y / 2f),
                Mathf.RoundToInt(dest.z / 2f));
            return BlockedCells.Contains(cell);
        }

        public void MarkBlocked(Vector3 dest)
        {
            BlockedCells.Add(new Vector3Int(
                Mathf.RoundToInt(dest.x / 2f),
                Mathf.RoundToInt(dest.y / 2f),
                Mathf.RoundToInt(dest.z / 2f)));
        }

        public void ClearBlocked() => BlockedCells.Clear();
    }

    /// <summary>
    /// Read-only context passed to role Tick methods.
    /// Roles read from here -- they never reach into StandardCombat directly.
    /// </summary>
    public readonly struct CombatContext
    {
        public readonly StealthHuntAI AI;
        public readonly ThreatModel Threat;
        public readonly TacticalBrain Brain;
        public readonly CombatEventBus Events;
        public readonly float DeltaTime;

        public CombatContext(StealthHuntAI ai, TacticalBrain brain,
                              CombatEventBus events, float dt)
        {
            AI = ai;
            Brain = brain;
            Threat = brain?.Intel?.Threat;
            Events = events;
            DeltaTime = dt;
        }

        public bool HasThreat => Threat != null;
        public bool HasLOS => Threat?.HasLOS ?? false;
        public bool HasIntel => Threat?.HasIntel ?? false;
        public Vector3 ThreatPos => Threat?.EstimatedPosition ?? Vector3.zero;
        public Vector3 LastKnown => Threat?.LastKnownPosition ?? Vector3.zero;
        public float Confidence => Threat?.Confidence ?? 0f;
        public float TimeSinceSeen => Threat?.TimeSinceSeen ?? 999f;
    }
}