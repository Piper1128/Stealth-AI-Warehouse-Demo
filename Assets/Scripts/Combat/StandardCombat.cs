using System.Collections.Generic;
using UnityEngine;
using StealthHuntAI.Combat.CQB;
using UnityEngine.AI;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// GOAP-driven combat behaviour for StealthHuntAI.
    /// Builds a world state, runs the planner to find the best action sequence,
    /// and executes actions one by one until the plan is complete or invalid.
    ///
    /// Goals (in priority order):
    ///   1. EliminateTarget     -- aggressive pursuit
    ///   2. SuppressAndFlank    -- coordinated attack
    ///   3. HoldChokepoint      -- defensive hold
    ///   4. Withdraw            -- fall back when casualties are high
    ///   5. Search              -- find threat when confidence is low
    /// </summary>
    [AddComponentMenu("StealthHuntAI/Standard Combat")]
    [RequireComponent(typeof(StealthHuntAI))]
    public class StandardCombat : MonoBehaviour, ICombatBehaviour
    {
        // ---------- Inspector -------------------------------------------------

        [Header("Weights")]
        public CoverWeights weights = new CoverWeights();

        [Header("Performance")]
        [Range(0.1f, 4f)] public float ReplanInterval = 0.6f;
        private float _replanOffset; // stagger offset so guards dont all replan same frame
        [Range(5f, 40f)] public float CoverSearchRange = 25f;

        [Header("Animation")]
        public List<CombatAnimSlot> animSlots = new List<CombatAnimSlot>();
        [Range(0f, 0.5f)] public float animTransitionDuration = 0.12f;

        // ---------- Public state (read by WorldState and TacticalInspector) ---

        public bool WantsControl { get; private set; }
        public string CurrentStateName => _currentAction?.Name ?? "Idle";
        public string CurrentPlanName => _plan != null ? _plan.ToString() : "none";
        public string CurrentStrategy => _brain?.Strategy.ToString() ?? "none";

        // Goal enum exposed for WorldState.Build
        public enum Goal { Idle, AdvanceTo, HoldAndFire, Suppress, Flank, Search }
        public Goal CurrentGoal { get; private set; }
        public bool IsInCover { get; private set; }
        public TacticalSpot CurrentSpot { get; private set; }

        // ---------- ICombatBehaviour -----------------------------------------

        public void OnEnterCombat(StealthHuntAI ai)
        {
            WantsControl = true;
            _ai = ai;
            _brain = TacticalBrain.GetOrCreate(ai.squadID);
            _brain.RegisterMember(ai);
            _events = CombatEventBus.Get(ai);
            _threat = new ThreatModel();
            _planner = new GoapPlanner();
            BuildActions();
            EnsureDefaultSlots();

            // Bootstrap threat from blackboard -- includes distant alerted units
            var board = SquadBlackboard.Get(ai.squadID);
            if (board != null && board.SharedConfidence > 0.05f)
                _threat.ReceiveIntel(board.SharedLastKnown, Vector3.zero,
                                     board.SharedConfidence);

            // Also bootstrap from brain shared threat
            if (_brain.SharedThreat.HasIntel
             && _brain.SharedThreat.Confidence > _threat.Confidence)
                _threat.ReceiveIntel(
                    _brain.SharedThreat.EstimatedPosition,
                    _brain.SharedThreat.LastKnownVelocity,
                    _brain.SharedThreat.Confidence * 0.9f);
            if (ai.Sensor?.CanSeeTarget == true)
            {
                var t = ai.GetTarget();
                if (t != null) _threat.UpdateWithSight(t.Position, t.Velocity);
            }

            RequestReplan();
        }

        public void Tick(StealthHuntAI ai)
        {
            _ai = ai;
            _goalTimer += Time.deltaTime;
            _replanTimer += Time.deltaTime;

            UpdateThreat();
            ProcessEvents();
            CheckStuck();

            // Update squad strategy
            if (_ai.squadID == GetSquadLeaderID())
            {
                var ws = WorldState.Build(_ai, _threat, _brain);
                _brain.Strategy.Update(Time.deltaTime, ws, _brain);
            }

            // Evaluate CQB entry when near a door and threat inside
            if (!_brain.CQB.IsActive && _threat.HasIntel)
            {
                var members = new System.Collections.Generic.List<StealthHuntAI>();
                var allUnits = HuntDirector.AllUnits;
                for (int i = 0; i < allUnits.Count; i++)
                    if (allUnits[i] != null && allUnits[i].squadID == _ai.squadID)
                        members.Add(allUnits[i]);
                _brain.CQB.EvaluateEntry(
                    _ai.transform.position,
                    _threat.EstimatedPosition,
                    _threat.Confidence,
                    members);
            }

            // Replan periodically or when plan is done
            if (_replanTimer >= ReplanInterval + _replanOffset
             || _plan == null || _plan.IsComplete)
                RequestReplan();

            ExecuteCurrentAction();

            HuntDirector.RegisterHeat(ai.transform.position, 0.04f * Time.deltaTime);
        }

        public void OnExitCombat(StealthHuntAI ai)
        {
            WantsControl = false;
            _currentAction?.OnExit(ai);
            _brain?.UnregisterMember(ai);
            ReleaseCover();
            ai.CombatRestoreRotation();
        }

        // ---------- Fields ---------------------------------------------------

        private StealthHuntAI _ai;
        private TacticalBrain _brain;
        private CombatEventBus _events;
        private float _stuckTimer;
        private Vector3 _lastStuckPos;
        private ThreatModel _threat;
        private GoapPlanner _planner;
        private GoapPlanner.Plan _plan;
        private GoapAction _currentAction;
        private List<GoapAction> _actions = new List<GoapAction>();
        private float _goalTimer;
        private float _replanTimer;
        private CoverPoint _currentCover;

        // ---------- Threat ---------------------------------------------------

        private void UpdateThreat()
        {
            var target = _ai.GetTarget();
            if (target == null) return;

            if (_ai.Sensor.CanSeeTarget)
            {
                _threat.UpdateWithSight(target.Position, target.Velocity);
                _brain.ReportThreat(_ai, target.Position, target.Velocity, 1f);
            }
            else
            {
                _threat.UpdateWithoutSight();
                if (_brain.SharedThreat.Confidence > _threat.Confidence)
                    _threat.ReceiveIntel(
                        _brain.SharedThreat.EstimatedPosition,
                        _brain.SharedThreat.LastKnownVelocity,
                        _brain.SharedThreat.Confidence * 0.85f);
            }

            if (!_ai.Sensor.CanSeeTarget)
                _brain.UpdateNoSight();
        }

        // ---------- Planning -------------------------------------------------

        private void BuildActions()
        {
            _actions.Clear();
            _actions.Add(new TakeCoverAction());
            _actions.Add(new AdvanceAggressivelyAction());
            _actions.Add(new FlankAction());
            _actions.Add(new SuppressAction());
            _actions.Add(new HoldChokepointAction());
            _actions.Add(new HighGroundAction());
            _actions.Add(new WithdrawAction());
            _actions.Add(new SearchAction());
            // CQB actions
            _actions.Add(new StackAction());
            _actions.Add(new BreachAction());
            _actions.Add(new ClearCornerAction());
            _actions.Add(new HoldFatalFunnelAction());
        }

        private void RequestReplan()
        {
            _replanTimer = 0f;
            _replanOffset = 0f; // only stagger first replan

            WorldState current = WorldState.Build(_ai, _threat, _brain);
            WorldState goal = ChooseGoal(current);

            var newPlan = _planner.BuildPlan(current, goal, _actions, _ai);

            if (newPlan != null && newPlan.IsValid)
            {
                // Only switch if new plan is meaningfully different
                if (_plan == null || _plan.IsComplete
                    || _currentAction?.Name != newPlan.Current?.Name)
                {
                    _currentAction?.OnExit(_ai);
                    _plan = newPlan;
                    _currentAction = _plan.Current;
                    _currentAction?.OnEnter(_ai, _threat);
                    _goalTimer = 0f;
                }
            }
            else if (_plan == null || _plan.IsComplete)
            {
                if (_threat.HasIntel && _threat.Confidence >= 0.1f)
                {
                    _currentAction?.OnExit(_ai);
                    _currentAction = new AdvanceAggressivelyAction();
                    _currentAction.OnEnter(_ai, _threat);
                }
                else
                {
                    YieldToCore();
                }
            }

            // Update goal label for inspector
            UpdateGoalLabel();
        }

        private WorldState ChooseGoal(WorldState current)
        {
            // Withdraw goal -- highest urgency
            if (current.SquadStrength < 0.3f || current.Health < 0.25f)
                return new WorldState { SafePosition = true };

            // Chokepoint goal -- defensive when squad is weak
            if (current.ChokepointNearby && current.SquadStrength < 0.5f)
                return new WorldState { ChokepointHeld = true };

            // Default -- eliminate target
            return new WorldState { TargetEliminated = true };
        }

        private void UpdateGoalLabel()
        {
            if (_currentAction == null) { CurrentGoal = Goal.Idle; return; }
            CurrentGoal = _currentAction.Name switch
            {
                "AdvanceAggressively" => Goal.AdvanceTo,
                "Flank" => Goal.Flank,
                "Suppress" => Goal.Suppress,
                "Search" => Goal.Search,
                "TakeCover" => Goal.HoldAndFire,
                "HoldChokepoint" => Goal.HoldAndFire,
                _ => Goal.Idle
            };
        }

        // ---------- Execution ------------------------------------------------

        private void ExecuteCurrentAction()
        {
            if (_currentAction == null) return;

            bool done = _currentAction.Execute(_ai, _threat, _brain, Time.deltaTime);

            if (done)
            {
                _currentAction.OnExit(_ai);
                _plan?.Advance();

                if (_plan != null && !_plan.IsComplete)
                {
                    _currentAction = _plan.Current;
                    _currentAction?.OnEnter(_ai, _threat);
                }
                else
                {
                    _currentAction = null;
                    RequestReplan();
                }
            }

            // Force replan on significant world state changes
            // Yield threshold -- keep fighting until confidence is very low
            // Yield to Core when intel is very stale
            bool noIntel = _threat.Confidence < 0.1f
                            && !(_currentAction is SearchAction)
                            && !(_currentAction is TakeCoverAction);
            bool gotLOS = _threat.HasLOS && _currentAction is SearchAction;
            bool shouldWithdraw = _currentAction is not WithdrawAction
                && (WorldState.Build(_ai, _threat, _brain).SquadStrength < 0.25f
                    || WorldState.Build(_ai, _threat, _brain).Health < 0.2f);

            if (gotLOS || shouldWithdraw)
            {
                _currentAction?.OnExit(_ai);
                _currentAction = null;
                RequestReplan();
            }
            else if (noIntel)
            {
                // No intel -- yield to Core search instead of replanning
                YieldToCore();
            }
        }

        // ---------- Cover helpers --------------------------------------------

        public void OccupyCover(CoverPoint cp)
        {
            ReleaseCover();
            _currentCover = cp;
            _currentCover.Occupy(_ai);
            IsInCover = true;
        }

        /// <summary>
        /// Temporarily yield control to Core stealth AI.
        /// Core runs TickLostTarget -- full ReachabilitySearch with Markov prediction.
        /// Combat regains control when Core re-enters Hostile state.
        /// </summary>
        /// <summary>
        /// Force an action immediately -- interrupts current plan.
        /// Used by GuardHealth to force cover seeking on hit.
        /// </summary>
        public void ForceAction(GoapAction action)
        {
            _currentAction?.OnExit(_ai);
            _plan = null;
            _replanTimer = 0f;
            _currentAction = action;
            _currentAction.OnEnter(_ai, _threat);
        }

        private void ProcessEvents()
        {
            if (_events == null || !_events.HasEvents) return;

            var evt = _events.ConsumeHighestPriority();
            if (evt == null) return;

            switch (evt.Value.Type)
            {
                case CombatEventType.DamageTaken:
                case CombatEventType.Ambushed:
                    // Always interrupt -- being hit overrides everything
                    ForceAction(new TakeCoverAction());
                    return;

                case CombatEventType.BuddyDown:
                    if (_currentAction == null || _currentAction.IsInterruptible)
                        ForceAction(new SuppressAction());
                    return;


                case CombatEventType.ThreatFlank:
                    // Reorient -- invalidate plan so we reassess
                    _threat.ReceiveIntel(evt.Value.Position,
                        Vector3.zero, 0.8f);
                    _currentAction?.OnExit(_ai);
                    _currentAction = null;
                    RequestReplan();
                    break;

                case CombatEventType.BuddyNeedsHelp:
                    ForceAction(new SuppressAction());
                    break;

                case CombatEventType.ThreatFound:
                    // Regained LOS -- interrupt search and engage
                    if (_currentAction is SearchAction)
                    {
                        _currentAction.OnExit(_ai);
                        _currentAction = null;
                        RequestReplan();
                    }
                    break;
            }
        }

        private void YieldToCore()
        {
            _currentAction?.OnExit(_ai);
            _currentAction = null;
            WantsControl = false; // Core takes over
            // WantsControl will be set true again via OnEnterCombat
            // when Core transitions back to Hostile after finding player
        }

        private void CheckStuck()
        {
            var agent = _ai?.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (agent == null || agent.isStopped || !agent.hasPath) return;

            // Detect partial/invalid path -- agent has path but cant complete it
            if (agent.pathStatus == UnityEngine.AI.NavMeshPathStatus.PathPartial
             || agent.pathStatus == UnityEngine.AI.NavMeshPathStatus.PathInvalid)
            {
                _currentAction?.OnExit(_ai);
                _currentAction = null;
                _plan = null;
                return;
            }

            // Detect velocity stall -- agent wants to move but isnt
            if (agent.desiredVelocity.magnitude < 0.1f) return;

            float moved = Vector3.Distance(_ai.transform.position, _lastStuckPos);
            if (moved > 0.5f)
            {
                _stuckTimer = 0f;
                _lastStuckPos = _ai.transform.position;
                return;
            }

            _stuckTimer += Time.deltaTime;
            if (_stuckTimer > 2f)
            {
                // Stuck -- invalidate action and let planner choose new destination
                // Do NOT warp -- warping to random position causes wall clipping
                _currentAction?.OnExit(_ai);
                _currentAction = null;
                _plan = null;
                _stuckTimer = 0f;
                _lastStuckPos = _ai.transform.position;

                // Stop agent cleanly
                agent.ResetPath();
            }
        }

        private int GetSquadLeaderID()
        {
            var units = HuntDirector.AllUnits;
            for (int i = 0; i < units.Count; i++)
                if (units[i] != null && units[i].squadID == _ai.squadID)
                    return units[i].GetInstanceID();
            return -1;
        }

        private void ReleaseCover()
        {
            if (_currentCover != null) { _currentCover.Release(_ai); _currentCover = null; }
            IsInCover = false;
            CurrentSpot = null;
            _ai?.CombatRestoreRotation();
        }

        // ---------- Animation ------------------------------------------------

        public void PlayCombatAnim(CombatAnimTrigger trigger)
        {
            if (_ai?.animator == null) return;
            string clip = GetClip(trigger);
            if (string.IsNullOrEmpty(clip)) return;
            try { _ai.animator.CrossFade(clip, animTransitionDuration); } catch { }
        }

        public string GetClip(CombatAnimTrigger trigger)
        {
            for (int i = 0; i < animSlots.Count; i++)
                if (animSlots[i].trigger == trigger) return animSlots[i].Pick();
            return null;
        }

        public void EnsureDefaultSlots()
        {
            var defaults = new[]
            {
                CombatAnimTrigger.MoveToCover, CombatAnimTrigger.TakeCover,
                CombatAnimTrigger.CoverIdle,   CombatAnimTrigger.Reposition,
                CombatAnimTrigger.PeekLeft,    CombatAnimTrigger.PeekRight,
                CombatAnimTrigger.CoverFire,   CombatAnimTrigger.Suppressing,
                CombatAnimTrigger.Advance,     CombatAnimTrigger.Flank,
                CombatAnimTrigger.HitReaction, CombatAnimTrigger.Reload,
                CombatAnimTrigger.StandingFire,
            };
            foreach (var t in defaults)
            {
                bool found = false;
                for (int i = 0; i < animSlots.Count; i++)
                    if (animSlots[i].trigger == t) { found = true; break; }
                if (!found)
                    animSlots.Add(new CombatAnimSlot
                    { trigger = t, clips = new List<string>() });
            }
        }

        private void Reset() => EnsureDefaultSlots();
    }

    // ---------- Extension helper on ThreatModel ------------------------------

    public static class ThreatModelHelpers
    {
        public static float DistanceTo(this ThreatModel threat, StealthHuntAI unit)
            => threat.HasIntel
               ? Vector3.Distance(unit.transform.position, threat.EstimatedPosition)
               : 999f;
    }
}