using StealthHuntAI.Combat.CQB;
using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI.Combat
{
    // =========================================================================
    // TakeCoverAction
    // =========================================================================

    /// <summary>Find and move to the best available cover.</summary>
    public class TakeCoverAction : GoapAction
    {
        public override string Name => "TakeCover";
        public override bool IsInterruptible => true;
        public override int Priority => 8;
        public override FormationType PreferredFormation => FormationType.None;

        private Vector3 _coverDest;
        private bool _destSet;
        private float _phaseTimer;
        private int _shotsFired;
        private int _repositionCount;

        public override bool CheckPreconditions(WorldState s)
            => !s.InCover;

        public override WorldState ApplyEffects(WorldState s)
        {
            s.InCover = true;
            return s;
        }

        public override float GetCost(WorldState s, StealthHuntAI unit)
        {
            // Cheap when under fire, expensive when safe
            float urgency = s.IsSuppressed ? 0.2f : 1.5f;
            return urgency;
        }

        public override void OnEnter(StealthHuntAI unit, ThreatModel threat)
        {
            _destSet = false;
            _phaseTimer = 0f;
            _shotsFired = 0;
            _repositionCount = 0;
        }

        public override bool Execute(StealthHuntAI unit, ThreatModel threat,
                                      TacticalBrain brain, float dt)
        {
            if (!_destSet)
            {
                if (TacticalSystem.Instance != null)
                {
                    var ctx = TacticalContext.Build(unit, threat, brain);
                    TacticalSystem.Instance.EvaluateSync(ctx);
                    var all = TacticalSystem.Instance.LastCandidates;

                    if (all != null)
                    {
                        var squadUnits = HuntDirector.AllUnits;
                        foreach (var spot in all)
                        {
                            if (spot.CoverPoint == null) continue;
                            bool tooClose = false;
                            for (int i = 0; i < squadUnits.Count; i++)
                            {
                                var u = squadUnits[i];
                                if (u == null || u == unit || u.squadID != unit.squadID) continue;
                                if (Vector3.Distance(spot.Position, u.transform.position) < 2.5f)
                                { tooClose = true; break; }
                            }
                            if (!tooClose)
                            {
                                _coverDest = spot.Position;
                                _destSet = true;
                                break;
                            }
                        }
                    }
                }
                if (!_destSet)
                {
                    unit.CombatMoveTo(GetBestKnownPosition(unit, threat));
                    return false;
                }
            }

            _phaseTimer += dt;

            // Move to cover
            bool arrived = MoveTo(unit, _coverDest);
            if (!arrived) return false;

            // In cover -- peek and shoot
            unit.CombatStop();
            Vector3 target = GetBestKnownPosition(unit, threat);
            FaceToward(unit, target, 200f);

            // Shoot immediately on arrival, then again after 0.3s
            if (_phaseTimer >= 0.3f && threat.Confidence > 0.05f)
            {
                FireAt(unit, target);
                _shotsFired++;
                _phaseTimer = 0f;
            }

            if (_shotsFired >= 2)
            {
                _repositionCount++;
                // After 3 repositions complete action -- let planner pick next
                if (_repositionCount >= 3) return true;

                _destSet = false;
                _shotsFired = 0;
                ResetMoveDest();
                FindNewCover(unit, threat, brain);
                if (!_destSet)
                {
                    _coverDest = GetBestKnownPosition(unit, threat);
                    _destSet = true;
                }
            }

            return false;
        }

        private void FindNewCover(StealthHuntAI unit, ThreatModel threat, TacticalBrain brain)
        {
            _destSet = false;
            if (TacticalSystem.Instance == null) return;

            var ctx = TacticalContext.Build(unit, threat, brain);
            TacticalSystem.Instance.EvaluateSync(ctx);
            var all = TacticalSystem.Instance.LastCandidates;
            if (all == null) return;

            var squadUnits = HuntDirector.AllUnits;
            foreach (var spot in all)
            {
                if (spot.CoverPoint == null) continue;
                if (Vector3.Distance(spot.Position, _coverDest) < 1.5f) continue;
                bool tooClose = false;
                for (int i = 0; i < squadUnits.Count; i++)
                {
                    var u = squadUnits[i];
                    if (u == null || u == unit || u.squadID != unit.squadID) continue;
                    if (Vector3.Distance(spot.Position, u.transform.position) < 2.5f)
                    { tooClose = true; break; }
                }
                if (!tooClose)
                {
                    _coverDest = spot.Position;
                    _destSet = true;
                    ResetMoveDest();
                    return;
                }
            }
        }
    }

    // =========================================================================
    // AdvanceAggressivelyAction
    // =========================================================================

    /// <summary>Move aggressively toward estimated threat, fire on the way.</summary>
    public class AdvanceAggressivelyAction : GoapAction
    {
        public override string Name => "AdvanceAggressively";
        public override FormationType PreferredFormation => FormationType.Wedge;
        public override bool IsInterruptible => true;
        public override int Priority => 3;

        private float _suppressTimer;
        private float _pathCheckTimer;
        private bool _lastPathValid = true;
        private List<Vector3> _waypoints = null;
        private int _waypointIdx = 0;

        public override bool CheckPreconditions(WorldState s)
            => s.ThreatConfidence > 0.2f
            && s.SquadStrength > 0.2f
            && s.Health > 0.15f;

        public override WorldState ApplyEffects(WorldState s)
        {
            s.DistToThreat = Mathf.Max(0f, s.DistToThreat - 8f);
            s.HasLOS = s.DistToThreat < 10f;
            s.TargetEliminated = s.DistToThreat < 6f; // reaching threat = engaging
            return s;
        }

        public override float GetCost(WorldState s, StealthHuntAI unit)
        {
            float base_cost = 1f + (1f - s.ThreatConfidence) * 1.5f
                            + (s.SquadStrength < 0.5f ? 1f : 0f);
            base_cost += Mathf.Max(0, s.SquadAdvancingCount - 1) * 2.0f;
            if (s.SquadAdvancingCount > 0 && s.SquadInCoverCount == 0)
                base_cost += 2.0f;
            return base_cost * StrategyMultiplier(unit, "advance");
        }

        public bool Immediate; // skip stagger -- used by nudge on LOS loss

        public override void OnEnter(StealthHuntAI unit, ThreatModel threat)
        {
            _suppressTimer = 0f;
            _pathCheckTimer = 0f;
            _lastPathValid = true;

            // Nudge -- skip stagger entirely, move immediately
            _startDelay = 0f;
            if (Immediate) return;

            // Stagger advance -- if squad member already advancing, wait
            var units = HuntDirector.AllUnits;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u == unit || u.squadID != unit.squadID) continue;
                var sc = u.GetComponent<StandardCombat>();
                if (sc != null && sc.CurrentStateName == "AdvanceAggressively")
                {
                    _startDelay = UnityEngine.Random.Range(0.1f, 0.4f);
                    break;
                }
            }
        }

        private float _startDelay;

        public override bool Execute(StealthHuntAI unit, ThreatModel threat,
                                      TacticalBrain brain, float dt)
        {
            // Use LastKnownPosition -- EstimatedPosition extrapolates and causes oscillation
            Vector3 raw = threat.LastSeenTime > -999f
                ? threat.LastKnownPosition
                : threat.EstimatedPosition;
            var allUnits = HuntDirector.AllUnits;
            int squadIdx = 0;
            int squadCount = 0;
            for (int i = 0; i < allUnits.Count; i++)
            {
                var u = allUnits[i];
                if (u == null || u.squadID != unit.squadID) continue;
                if (u == unit) squadIdx = squadCount;
                squadCount++;
            }

            // Offset perpendicular to threat direction based on squad index
            // Spreads guards 3m apart so they dont stack on same point
            Vector3 toThreat = (raw - unit.transform.position);
            toThreat.y = 0f;
            Vector3 perpDir = Vector3.Cross(toThreat.normalized, Vector3.up);
            float spread = (squadIdx - squadCount * 0.5f) * 3f;
            Vector3 dest = raw + perpDir * spread;

            // Build safe waypoint route if not set
            if (_waypoints == null || _waypointIdx >= _waypoints.Count)
            {
                _waypoints = TacticalPathfinder.BuildAdvanceRoute(unit, dest);
                _waypointIdx = 0;
            }
            if (_waypoints != null && _waypoints.Count > 0)
                TacticalPathfinder.FollowWaypoints(unit, _waypoints, ref _waypointIdx);
            else
            {
                Vector3 fallbackDest = dest;
                if (UnityEngine.AI.NavMesh.SamplePosition(dest, out var hit, 4f,
                    UnityEngine.AI.NavMesh.AllAreas))
                    fallbackDest = hit.position;
                unit.CombatMoveTo(fallbackDest);
            }

            float dist = Vector3.Distance(unit.transform.position, raw);

            // Check path validity every 0.5s
            _pathCheckTimer += dt;
            if (_pathCheckTimer >= 0.5f)
            {
                _pathCheckTimer = 0f;
                var cur = _waypoints != null && _waypointIdx < _waypoints.Count
                    ? _waypoints[_waypointIdx] : raw;
                _lastPathValid = !IsPathBlocked(unit, cur);
            }
            if (!_lastPathValid) { _waypoints = null; return true; }

            // Abort on stale intel -- dont advance toward 12s old position
            if (threat.Confidence < 0.1f || threat.TimeSinceSeen > 12f) return true;

            // Start delay -- wait if squad member already advancing
            if (_startDelay > 0f)
            {
                _startDelay -= dt;
                unit.CombatStop();
                // Peek toward threat while waiting
                unit.CombatFaceToward(threat.EstimatedPosition, 80f);
                return false;
            }

            // Movement handled above by TacticalPathfinder.FollowWaypoints
            unit.CombatRestoreRotation();

            // Fire immediately on LOS -- no delay when rounding a corner
            if (threat.HasLOS)
            {
                // Fast face toward -- override smooth rotation for instant threat
                unit.CombatFaceToward(threat.EstimatedPosition, 400f);
                FireAt(unit, threat.EstimatedPosition);
                _suppressTimer = 0f;
            }
            else
            {
                // Suppression fire -- only if path to target isnt fully blocked
                _suppressTimer += dt;
                if (_suppressTimer > 1.2f && threat.Confidence > 0.2f)
                {
                    Vector3 suppressTarget = GetBestKnownPosition(unit, threat);
                    Vector3 toTarget = suppressTarget - unit.transform.position;
                    // Only suppress if not completely walled off
                    if (!Physics.Raycast(
                        unit.transform.position + Vector3.up * 1.5f,
                        toTarget.normalized, toTarget.magnitude * 0.5f,
                        LayerMask.GetMask("Default", "Environment")))
                    {
                        FireAt(unit, suppressTarget);
                    }
                    _suppressTimer = 0f;
                }
            }

            return dist < 6f || threat.HasLOS;
        }
    }

    // =========================================================================
    // FlankAction
    // =========================================================================

    /// <summary>Move to a flanking position to attack from an angle.</summary>
    public class FlankAction : GoapAction
    {
        public override string Name => "Flank";
        public override FormationType PreferredFormation => FormationType.File;
        public override bool IsInterruptible => true;
        public override int Priority => 4;

        private Vector3 _flankDest;
        private bool _destSet;
        private float _suppressTimer;
        private float _pathCheckTimer;
        private bool _pathBlocked;
        private List<Vector3> _waypoints = null;
        private int _waypointIdx = 0;

        public override bool CheckPreconditions(WorldState s)
            => s.ThreatConfidence > 0.25f
            && s.FlankRouteOpen
            && s.SquadStrength > 0.25f;

        public override WorldState ApplyEffects(WorldState s)
        {
            s.HasLOS = true;
            s.DistToThreat -= 4f;
            s.TargetEliminated = true;
            return s;
        }

        public override float GetCost(WorldState s, StealthHuntAI unit)
        {
            float base_cost = s.SquadmateSuppressing ? 0.8f : 2.0f;
            return base_cost;
        }

        public override void OnEnter(StealthHuntAI unit, ThreatModel threat)
        {
            _destSet = false;
            _suppressTimer = 0f;
            _waypointIdx = 0;

            // Use LastKnownPosition for routing -- EstimatedPosition extrapolates
            // and moves constantly making pathfinding unstable
            Vector3 threatPos = threat.LastSeenTime > -999f
                ? threat.LastKnownPosition
                : threat.EstimatedPosition;
            var brain2 = TacticalBrain.GetOrCreate(unit.squadID);

            // If CQB is active or an entry point is nearby -- route toward it
            // Flanking through a door is more effective than flanking around a building
            EntryPoint targetEntry = null;
            if (brain2.CQB.IsActive && brain2.CQB.ActiveEntry != null)
                targetEntry = brain2.CQB.ActiveEntry;
            else
            {
                var ep = EntryPointRegistry.FindBest(unit.transform.position, threatPos);
                if (ep != null)
                {
                    float distEp = Vector3.Distance(ep.transform.position, threatPos);
                    if (distEp < 20f) targetEntry = ep;
                }
            }

            if (targetEntry != null)
            {
                // Route toward stack position at entry point
                Vector3 stackPos = unit.squadID % 2 == 0
                    ? targetEntry.StackLeftPos
                    : targetEntry.StackRightPos;
                _waypoints = TacticalPathfinder.BuildAdvanceRoute(unit, stackPos);
                if (_waypoints != null && _waypoints.Count > 0)
                {
                    _flankDest = stackPos;
                    _destSet = true;
                }
            }

            // Fallback -- regular flank route
            if (!_destSet)
            {
                _waypoints = TacticalPathfinder.BuildFlankRoute(unit, threatPos);
                if (_waypoints != null && _waypoints.Count > 0)
                {
                    _flankDest = _waypoints[_waypoints.Count - 1];
                    _destSet = true;
                }
                else
                {
                    var pos = TacticalBrain.GetOrCreate(unit.squadID)?.GetFlankPosition(unit);
                    if (pos.HasValue) { _flankDest = pos.Value; _destSet = true; }
                }
            }
        }

        public override bool Execute(StealthHuntAI unit, ThreatModel threat,
                                      TacticalBrain brain, float dt)
        {
            // Abort conditions
            if (threat.Confidence < 0.12f) return true;
            if (threat.TimeSinceSeen > 15f) return true;

            // No destination -- move directly toward last known instead of stopping
            if (!_destSet)
            {
                unit.CombatMoveTo(threat.LastSeenTime > -999f
                    ? threat.LastKnownPosition
                    : threat.EstimatedPosition, 1.0f);
                return false;
            }

            bool arrived = TacticalPathfinder.FollowWaypoints(
                unit, _waypoints, ref _waypointIdx);
            float dist = Vector3.Distance(unit.transform.position, _flankDest);
            unit.CombatRestoreRotation();

            Vector3 fireTarget = GetBestKnownPosition(unit, threat);

            if (threat.HasLOS)
            {
                // Got LOS while flanking -- shoot but keep moving
                unit.CombatFaceToward(fireTarget, 400f);
                FireAt(unit, fireTarget);
                _suppressTimer = 0f;
            }
            else
            {
                // No LOS -- fire suppression every 0.8s while moving
                _suppressTimer += dt;
                if (_suppressTimer > 0.8f)
                {
                    FireAt(unit, fireTarget);
                    _suppressTimer = 0f;
                }
            }

            return arrived || dist < 2f;
        }
    }

    // =========================================================================
    // SuppressAction
    // =========================================================================

    /// <summary>
    /// Fire suppression in coordinated bursts to cover buddy advance.
    /// Uses three-phase timing: Firing -> Pause -> Firing -> Complete
    /// Not interruptible mid-burst so buddy gets full cover window.
    /// </summary>
    public class SuppressAction : GoapAction
    {
        public override string Name => "Suppress";
        public override FormationType PreferredFormation => FormationType.Overwatch;
        public override bool IsInterruptible => false; // complete burst
        public override int Priority => 5;

        private enum SuppressPhase { Firing, Pausing, Done }
        private SuppressPhase _phase;
        private float _phaseTimer;
        private int _burstCount;
        private const float BurstDuration = 0.8f;
        private const float PauseDuration = 0.4f;
        private const int BurstsRequired = 3;
        private float _duration;
        private const float SuppressDuration = 2.5f;

        public override bool CheckPreconditions(WorldState s)
            => s.ThreatConfidence > 0.15f
            && s.SquadmateAdvancing;

        public override WorldState ApplyEffects(WorldState s)
        {
            s.SquadmateSuppressing = true;
            return s;
        }

        public override float GetCost(WorldState s, StealthHuntAI unit)
        {
            // Suppress is cheap when squad is advancing -- essential role
            if (s.SquadmateAdvancing || s.SquadFlankingCount > 0) return 0.4f;
            // Already suppressing -- slightly more expensive
            if (s.SquadmateSuppressing) return 1.5f;
            return 0.8f;
        }

        public override void OnEnter(StealthHuntAI unit, ThreatModel threat)
            => _duration = 0f;

        public override bool Execute(StealthHuntAI unit, ThreatModel threat,
                                      TacticalBrain brain, float dt)
        {
            _duration += dt;
            Vector3 target = GetBestKnownPosition(unit, threat);

            FaceToward(unit, target, 140f);

            if (threat.Confidence > 0.1f)
                FireAt(unit, target);

            if (threat.HasLOS)
            {
                FireAt(unit, target);
                _duration += dt; // complete faster when we have LOS
            }

            return _duration >= SuppressDuration;
        }
    }

    // =========================================================================
    // HoldChokepointAction
    // =========================================================================

    /// <summary>Move to and hold a chokepoint to prevent enemy advance.</summary>
    public class HoldChokepointAction : GoapAction
    {
        public override string Name => "HoldChokepoint";
        public override FormationType PreferredFormation => FormationType.Line;
        public override bool IsInterruptible => false;
        public override int Priority => 6;

        private Vector3 _holdPos;
        private bool _atPosition;
        private float _holdTimer;
        private const float HoldDuration = 8f;

        public override bool CheckPreconditions(WorldState s)
            => s.ChokepointNearby
            && (s.SquadStrength < 0.6f || s.ThreatConfidence < 0.3f);

        public override WorldState ApplyEffects(WorldState s)
        {
            s.ChokepointHeld = true;
            s.InCover = true;
            return s;
        }

        public override float GetCost(WorldState s, StealthHuntAI unit)
            => s.SquadStrength < 0.4f ? 0.3f : 2.5f; // cheap when squad is weak

        public override void OnEnter(StealthHuntAI unit, ThreatModel threat)
        {
            _atPosition = false;
            _holdTimer = 0f;

            // Find nearest TacticalZone with type Defend
            var zones = TacticalZone.All;
            float best = float.MaxValue;
            _holdPos = unit.transform.position;
            for (int i = 0; i < zones.Count; i++)
            {
                if (zones[i].ZoneType != TacticalZoneType.Defend) continue;
                float d = Vector3.Distance(unit.transform.position,
                    zones[i].transform.position);
                if (d < best) { best = d; _holdPos = zones[i].transform.position; }
            }
        }

        public override bool Execute(StealthHuntAI unit, ThreatModel threat,
                                      TacticalBrain brain, float dt)
        {
            if (!_atPosition)
            {
                _atPosition = MoveTo(unit, _holdPos);
                return false;
            }

            // At position -- hold and fire
            _holdTimer += dt;
            unit.CombatStop();

            Vector3 holdTarget = GetBestKnownPosition(unit, threat);
            FaceToward(unit, holdTarget);
            // Only shoot with direct LOS -- never through walls
            if (threat.HasLOS)
                FireAt(unit, holdTarget);

            // Abandon hold if threat gets too close or squad recovers
            return _holdTimer > HoldDuration || threat.DistanceTo(unit) < 4f;
        }
    }

    // =========================================================================
    // WithdrawAction
    // =========================================================================

    /// <summary>Fall back to a safe position when squad takes heavy casualties.</summary>
    public class WithdrawAction : GoapAction
    {
        public override string Name => "Withdraw";
        public override FormationType PreferredFormation => FormationType.File;
        public override bool IsInterruptible => true;
        public override int Priority => 2;

        private Vector3 _withdrawDest;
        private bool _destSet;
        private List<Vector3> _waypoints;
        private int _waypointIdx;

        public override bool CheckPreconditions(WorldState s)
            => s.SquadStrength < 0.15f || s.Health < 0.15f;

        public override WorldState ApplyEffects(WorldState s)
        {
            s.SafePosition = true;
            return s;
        }

        public override float GetCost(WorldState s, StealthHuntAI unit)
            => 3.0f; // expensive -- last resort

        public override void OnEnter(StealthHuntAI unit, ThreatModel threat)
        {
            _destSet = false;
            _waypointIdx = 0;
            Vector3 threatPos = GetBestKnownPosition(unit, threat);
            _waypoints = TacticalPathfinder.BuildWithdrawRoute(unit, threatPos);

            // Fallback -- direct withdraw
            if ((_waypoints == null || _waypoints.Count == 0) && threat.HasIntel)
            {
                Vector3 awayDir = (unit.transform.position - threatPos).normalized;
                Vector3 candidate = unit.transform.position + awayDir * 15f;
                if (UnityEngine.AI.NavMesh.SamplePosition(candidate,
                    out var hit, 5f, UnityEngine.AI.NavMesh.AllAreas))
                {
                    _withdrawDest = hit.position;
                    _destSet = true;
                }
            }
        }

        public override bool Execute(StealthHuntAI unit, ThreatModel threat,
                                      TacticalBrain brain, float dt)
        {
            // Follow safe waypoint route
            if (_waypoints != null && _waypoints.Count > 0)
                return TacticalPathfinder.FollowWaypoints(unit, _waypoints, ref _waypointIdx);

            // Fallback -- direct move
            if (!_destSet) return true;
            return MoveTo(unit, _withdrawDest);
        }

        public override void OnExit(StealthHuntAI unit)
            => unit.CombatStop();
    }

    // =========================================================================
    // SearchAction
    // =========================================================================

    /// <summary>Move to last known position and search systematically.</summary>
    public class SearchAction : GoapAction
    {
        public override string Name => "Search";
        public override FormationType PreferredFormation => FormationType.Wedge;
        public override bool IsInterruptible => true;
        public override int Priority => 1;

        private float _timer;

        public override bool CheckPreconditions(WorldState s)
            => true; // always available as fallback

        public override WorldState ApplyEffects(WorldState s)
        {
            s.ThreatConfidence = Mathf.Min(1f, s.ThreatConfidence + 0.3f);
            return s;
        }

        public override float GetCost(WorldState s, StealthHuntAI unit)
            => s.ThreatConfidence < 0.1f ? 0.5f : 3.0f;

        public override void OnEnter(StealthHuntAI unit, ThreatModel threat)
        {
            _timer = 0f;
        }

        public override bool Execute(StealthHuntAI unit, ThreatModel threat,
                                      TacticalBrain brain, float dt)
        {
            _timer += dt;

            // Hand off to Core stealth AI search -- it uses ReachabilitySearch,
            // SearchContext with stimulus history, Markov prediction and cone search.
            // We return WantsControl=false temporarily so Core's TickLostTarget runs.
            // When Core finds threat again, GOAP regains control via OnEnterCombat.
            return true; // complete immediately -- StandardCombat will yield to Core
        }
    }
}