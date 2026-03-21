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
        public override FormationType PreferredFormation => FormationType.None;

        private Vector3 _coverDest;
        private bool _destSet;

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
        }

        public override bool Execute(StealthHuntAI unit, ThreatModel threat,
                                      TacticalBrain brain, float dt)
        {
            if (!_destSet)
            {
                // Find cover via TacticalSystem
                if (TacticalSystem.Instance != null)
                {
                    var ctx = TacticalContext.Build(unit, threat, brain);
                    var best = TacticalSystem.Instance.EvaluateSync(ctx);
                    if (best?.CoverPoint != null)
                    {
                        _coverDest = best.Position;
                        _destSet = true;
                    }
                }
                if (!_destSet)
                {
                    // No cover found -- advance toward best known position
                    var best = GetBestKnownPosition(unit, threat);
                    unit.CombatMoveTo(best);
                    return false;
                }
            }

            bool arrived = MoveTo(unit, _coverDest);
            return arrived;
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

        private float _suppressTimer;
        private float _pathCheckTimer;
        private bool _lastPathValid = true;

        public override bool CheckPreconditions(WorldState s)
            => s.ThreatConfidence > 0.2f
            && s.SquadStrength > 0.2f
            && s.Health > 0.15f;

        public override WorldState ApplyEffects(WorldState s)
        {
            s.DistToThreat = Mathf.Max(0f, s.DistToThreat - 8f);
            s.HasLOS = s.DistToThreat < 10f;
            return s;
        }

        public override float GetCost(WorldState s, StealthHuntAI unit)
        {
            float base_cost = 1f;
            float confBonus = (1f - s.ThreatConfidence) * 1.5f;
            float squadPenalty = s.SquadStrength < 0.5f ? 1f : 0f;
            return base_cost + confBonus + squadPenalty;
        }

        public override void OnEnter(StealthHuntAI unit, ThreatModel threat)
        {
            _suppressTimer = 0f;
            _pathCheckTimer = 0f;
            _lastPathValid = true;
        }

        public override bool Execute(StealthHuntAI unit, ThreatModel threat,
                                      TacticalBrain brain, float dt)
        {
            // Spread advance destination -- each unit targets slightly different position
            Vector3 raw = GetBestKnownPosition(unit, threat);
            var units = HuntDirector.AllUnits;
            int idx = 0;
            for (int i = 0; i < units.Count; i++)
                if (units[i] == unit) { idx = i; break; }

            // Offset perpendicular to threat direction based on index
            Vector3 toThreat = (raw - unit.transform.position);
            toThreat.y = 0f;
            Vector3 perpDir = Vector3.Cross(toThreat.normalized, Vector3.up);
            float offset = (idx % 2 == 0 ? 1f : -1f) * (2f + idx * 0.5f);
            Vector3 spread = raw + perpDir * offset;

            Vector3 dest = spread;
            if (UnityEngine.AI.NavMesh.SamplePosition(spread, out var hit, 4f,
                UnityEngine.AI.NavMesh.AllAreas))
                dest = hit.position;

            float dist = Vector3.Distance(unit.transform.position, dest);

            // Check path validity every 0.5s
            _pathCheckTimer += dt;
            if (_pathCheckTimer >= 0.5f)
            {
                _pathCheckTimer = 0f;
                _lastPathValid = !IsPathBlocked(unit, dest);
            }
            if (!_lastPathValid) return true; // replanner will find alternative

            unit.CombatMoveTo(dest);
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
                _suppressTimer += dt;
                if (_suppressTimer > 1.2f && threat.Confidence > 0.2f)
                {
                    FireAt(unit, GetBestKnownPosition(unit, threat));
                    _suppressTimer = 0f;
                }
            }

            // Signal buddy when arrived -- triggers role swap
            if (dist < 6f || threat.HasLOS)
            {
                BuddySystem.GetOrCreate(unit.squadID).SignalArrived(unit);
                return true;
            }
            return false;
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

        private Vector3 _flankDest;
        private bool _destSet;
        private float _suppressTimer;
        private float _pathCheckTimer;
        private bool _pathBlocked;

        public override bool CheckPreconditions(WorldState s)
            => s.ThreatConfidence > 0.25f
            && s.FlankRouteOpen
            && s.SquadStrength > 0.25f;

        public override WorldState ApplyEffects(WorldState s)
        {
            s.HasLOS = true;
            s.DistToThreat -= 4f;
            return s;
        }

        public override float GetCost(WorldState s, StealthHuntAI unit)
            => s.SquadmateSuppressing ? 0.8f : 2.0f; // cheap when teammate suppresses

        public override void OnEnter(StealthHuntAI unit, ThreatModel threat)
        {
            _destSet = false;
            _suppressTimer = 0f;
        }

        public override bool Execute(StealthHuntAI unit, ThreatModel threat,
                                      TacticalBrain brain, float dt)
        {
            if (!_destSet)
            {
                var pos = brain.GetFlankPosition(unit);
                if (!pos.HasValue) return true;
                _flankDest = pos.Value;
                _destSet = true;
            }

            // Re-verify path each frame -- abort if blocked
            if (IsPathBlocked(unit, _flankDest))
            {
                _destSet = false; // force recalculation
                return true;     // abort this action
            }

            float dist = Vector3.Distance(unit.transform.position, _flankDest);
            unit.CombatMoveTo(_flankDest);
            unit.CombatRestoreRotation();

            _suppressTimer += dt;
            Vector3 fireTarget = GetBestKnownPosition(unit, threat);
            if (threat.HasLOS)
            {
                FireAt(unit, fireTarget);
                _suppressTimer = 0f;
            }
            else if (_suppressTimer > 1.8f)
            {
                FireAt(unit, fireTarget);
                _suppressTimer = 0f;
            }

            return dist < 2f;
        }
    }

    // =========================================================================
    // SuppressAction
    // =========================================================================

    /// <summary>Fire suppression to pin threat and cover advancing teammate.</summary>
    public class SuppressAction : GoapAction
    {
        public override string Name => "Suppress";
        public override FormationType PreferredFormation => FormationType.Overwatch;

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
            => 0.5f; // very cheap -- supports teammate

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
            if (threat.HasLOS || threat.Confidence > 0.1f)
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

        private Vector3 _withdrawDest;
        private bool _destSet;

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
            // Find withdraw point -- away from threat, preferably behind cover
            if (threat.HasIntel)
            {
                Vector3 awayDir = (unit.transform.position
                    - threat.EstimatedPosition).normalized;
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