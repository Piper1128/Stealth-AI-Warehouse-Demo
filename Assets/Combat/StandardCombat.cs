using System.Collections.Generic;
using UnityEngine;
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
        [Range(0.5f, 8f)] public float ReplanInterval = 1.5f;
        [Range(5f, 40f)] public float CoverSearchRange = 25f;

        [Header("Animation")]
        public List<CombatAnimSlot> animSlots = new List<CombatAnimSlot>();
        [Range(0f, 0.5f)] public float animTransitionDuration = 0.12f;

        // ---------- Public state (read by WorldState and TacticalInspector) ---

        public bool WantsControl { get; private set; }
        public string CurrentStateName => _currentAction?.Name ?? "Idle";
        public string CurrentPlanName => _plan != null ? _plan.ToString() : "none";

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
            _buddy = BuddySystem.GetOrCreate(ai.squadID);
            RebuildBuddyPairs(ai);
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
            _buddy?.Update(Time.deltaTime);

            // Replan periodically or when plan is done
            if (_replanTimer >= ReplanInterval || _plan == null || _plan.IsComplete)
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
        private BuddySystem _buddy;
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
            _actions.Add(new WithdrawAction());
            _actions.Add(new SearchAction());
        }

        private void RequestReplan()
        {
            _replanTimer = 0f;

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
                if (_threat.HasIntel)
                {
                    // Has intel -- advance aggressively
                    _currentAction?.OnExit(_ai);
                    _currentAction = new AdvanceAggressivelyAction();
                    _currentAction.OnEnter(_ai, _threat);
                }
                else
                {
                    // No intel -- yield to Core stealth AI search
                    // Core runs TickLostTarget with full ReachabilitySearch
                    // When Core finds player again OnEnterCombat re-triggers
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
            bool noIntel = !_threat.HasIntel && !(_currentAction is SearchAction);
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
        private void YieldToCore()
        {
            _currentAction?.OnExit(_ai);
            _currentAction = null;
            WantsControl = false; // Core takes over
            // WantsControl will be set true again via OnEnterCombat
            // when Core transitions back to Hostile after finding player
        }

        private void RebuildBuddyPairs(StealthHuntAI ai)
        {
            var members = new System.Collections.Generic.List<StealthHuntAI>();
            var units = HuntDirector.AllUnits;
            for (int i = 0; i < units.Count; i++)
                if (units[i] != null && units[i].squadID == ai.squadID)
                    members.Add(units[i]);
            _buddy.RebuildPairs(members);
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