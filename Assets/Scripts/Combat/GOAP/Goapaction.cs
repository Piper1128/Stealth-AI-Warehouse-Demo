using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// A single GOAP action. Actions have:
    ///   - Preconditions: what world state is required to execute
    ///   - Effects: how the world state changes after execution
    ///   - Cost: how expensive this action is (lower = preferred)
    ///   - Execute: what the unit actually does
    ///
    /// The planner chains actions together to reach a goal state.
    /// </summary>
    public abstract class GoapAction
    {
        /// <summary>Display name shown in Tactical Inspector.</summary>
        public abstract string Name { get; }

        /// <summary>
        /// If true this action can be interrupted by CombatEventBus events.
        /// Set false for critical actions like TakeCover that must complete.
        /// </summary>
        public virtual bool IsInterruptible => true;

        /// <summary>Action priority -- higher interrupts lower when planning.</summary>
        public virtual int Priority => 0;

        /// <summary>Formation this action prefers.</summary>
        public virtual FormationType PreferredFormation => FormationType.None;

        // ---------- Planning -------------------------------------------------

        /// <summary>
        /// Returns true if this action can be executed given the current world state.
        /// </summary>
        public abstract bool CheckPreconditions(WorldState state);

        /// <summary>
        /// Apply this action's effects to a world state copy and return it.
        /// Used by the planner to simulate future states.
        /// </summary>
        public abstract WorldState ApplyEffects(WorldState state);

        /// <summary>
        /// Cost of this action. Lower cost = preferred by planner.
        /// Can be dynamic based on world state.
        /// </summary>
        public abstract float GetCost(WorldState state, StealthHuntAI unit);

        // ---------- Execution ------------------------------------------------

        /// <summary>
        /// Called when this action becomes the active action.
        /// Set up any state needed.
        /// </summary>
        public virtual void OnEnter(StealthHuntAI unit, ThreatModel threat) { }

        /// <summary>
        /// Called every frame while this action is active.
        /// Returns true when the action is complete.
        /// </summary>
        public abstract bool Execute(StealthHuntAI unit, ThreatModel threat,
                                      TacticalBrain brain, float deltaTime);

        /// <summary>
        /// Called when this action is interrupted or completed.
        /// Clean up any state.
        /// </summary>
        public virtual void OnExit(StealthHuntAI unit) { }

        // ---------- Helpers --------------------------------------------------

        private IShootable _cachedShootable;

        protected void FireAt(StealthHuntAI unit, Vector3 targetPos)
        {
            if (_cachedShootable == null)
                _cachedShootable = unit.GetComponent<IShootable>();
            _cachedShootable?.TryShoot(targetPos);
        }

        protected void FaceToward(StealthHuntAI unit, Vector3 pos, float speed = 150f)
        {
            unit.CombatFaceToward(pos, speed);
        }

        private Vector3 _lastDest;
        private const float DestChangeThreshold = 1.5f;

        /// <summary>
        /// Best known position of threat -- never returns zero.
        /// Uses LOS position, then last known, then squad blackboard, then forward guess.
        /// All actions should use this instead of threat.EstimatedPosition directly.
        /// </summary>
        protected Vector3 GetBestKnownPosition(StealthHuntAI unit, ThreatModel threat)
        {
            // 1. Direct LOS
            if (threat.HasLOS)
                return threat.EstimatedPosition;

            // 2. Recent last known (within 30s)
            if (threat.LastSeenTime > -999f)
                return threat.LastKnownPosition;

            // 3. Squad blackboard
            var board = SquadBlackboard.Get(unit.squadID);
            if (board != null && board.SharedConfidence > 0.05f)
                return board.SharedLastKnown;

            // 4. Forward guess -- last known velocity extrapolation
            if (threat.LastSeenTime > -999f && threat.LastKnownVelocity.magnitude > 0.1f)
                return threat.LastKnownPosition
                    + threat.LastKnownVelocity.normalized * 8f;

            // 5. Fallback -- look forward
            return unit.transform.position + unit.transform.forward * 10f;
        }

        protected bool MoveTo(StealthHuntAI unit, Vector3 dest)
        {
            // Only update destination when it changes significantly
            // Prevents resetting NavMesh path every frame
            float destDelta = Vector3.Distance(dest, _lastDest);
            if (destDelta > DestChangeThreshold || _lastDest == Vector3.zero)
            {
                _lastDest = dest;
                unit.CombatMoveTo(dest);
            }
            unit.CombatRestoreRotation();
            float dist = Vector3.Distance(unit.transform.position, dest);
            return dist < 2f;
        }

        protected void ResetMoveDest() => _lastDest = Vector3.zero;

        protected bool IsPathBlocked(StealthHuntAI unit, Vector3 dest)
        {
            var path = new UnityEngine.AI.NavMeshPath();
            if (!UnityEngine.AI.NavMesh.CalculatePath(
                unit.transform.position, dest,
                UnityEngine.AI.NavMesh.AllAreas, path))
                return true;
            return path.status != UnityEngine.AI.NavMeshPathStatus.PathComplete;
        }

        public override string ToString() => Name;
    }
}