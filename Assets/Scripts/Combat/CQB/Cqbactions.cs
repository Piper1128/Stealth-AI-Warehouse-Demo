using System.Collections.Generic;
using UnityEngine;
using StealthHuntAI.Combat.CQB;

namespace StealthHuntAI.Combat
{
    // =========================================================================
    // StackAction -- move to stack position and wait for buddy
    // =========================================================================

    /// <summary>
    /// Move to assigned stack position beside door and wait for buddy.
    /// Signals CQBController when in position.
    /// Not interruptible -- must reach stack.
    /// </summary>
    public class StackAction : GoapAction
    {
        public override string Name => "Stack";
        public override bool IsInterruptible => false;
        public override int Priority => 9;

        private Vector3 _stackPos;
        private bool _destSet;
        private float _waitTimer;
        private const float MaxWait = 4f; // abort if buddy never arrives

        public override bool CheckPreconditions(WorldState s)
            => s.NearEntryPoint && !s.RoomCleared;

        public override WorldState ApplyEffects(WorldState s)
        {
            s.AtStackPosition = true;
            return s;
        }

        public override float GetCost(WorldState s, StealthHuntAI unit)
            => 0.5f; // very cheap -- CQB has high priority

        public override void OnEnter(StealthHuntAI unit, ThreatModel threat)
        {
            _destSet = false;
            _waitTimer = 0f;

            var brain = TacticalBrain.GetOrCreate(unit.squadID);
            var role = brain.CQB.GetRole(unit);
            if (role.HasValue)
            {
                _stackPos = role.Value.StackPos;
                _destSet = true;
            }
        }

        public override bool Execute(StealthHuntAI unit, ThreatModel threat,
                                      TacticalBrain brain, float dt)
        {
            if (!_destSet) return true;

            float dist = Vector3.Distance(unit.transform.position, _stackPos);
            if (dist > 0.8f)
            {
                unit.CombatMoveTo(_stackPos);
                return false;
            }

            // In position -- signal and wait
            unit.CombatStop();
            brain.CQB.SignalStackReady(unit);

            _waitTimer += dt;

            // Dynamic -- both enter simultaneously when both ready
            if (brain.CQB.CurrentEntry == CQBController.EntryType.Dynamic)
                return brain.CQB.BothReady || _waitTimer > MaxWait;

            // Deliberate -- breacher goes first, follower waits for signal
            if (brain.CQB.IsFollower(unit))
                return brain.CQB.BreacherReady || _waitTimer > MaxWait;

            return brain.CQB.BreacherReady || _waitTimer > MaxWait;
        }

        public override void OnExit(StealthHuntAI unit)
            => unit.CombatStop();
    }

    // =========================================================================
    // BreachAction -- sprint through fatal funnel to point of domination
    // =========================================================================

    /// <summary>
    /// Sprint through the door and reach assigned point of domination.
    /// Skips the fatal funnel as fast as possible.
    /// Shoots immediately on LOS.
    /// </summary>
    public class BreachAction : GoapAction
    {
        public override string Name => "Breach";
        public override bool IsInterruptible => false;
        public override int Priority => 10; // highest -- never interrupted

        private Vector3 _domTarget;
        private bool _destSet;

        public override bool CheckPreconditions(WorldState s)
            => s.AtStackPosition && !s.RoomCleared;

        public override WorldState ApplyEffects(WorldState s)
        {
            s.InCover = true;
            s.AtDomPoint = true;
            return s;
        }

        public override float GetCost(WorldState s, StealthHuntAI unit)
            => 0.3f;

        public override void OnEnter(StealthHuntAI unit, ThreatModel threat)
        {
            _destSet = false;

            var brain = TacticalBrain.GetOrCreate(unit.squadID);
            var role = brain.CQB.GetRole(unit);
            if (!role.HasValue) return;

            _domTarget = role.Value.DomTarget;
            _destSet = true;
        }

        public override bool Execute(StealthHuntAI unit, ThreatModel threat,
                                      TacticalBrain brain, float dt)
        {
            if (!_destSet) return true;

            // Sprint -- override normal speed
            unit.CombatMoveTo(_domTarget, 1.3f);

            // Shoot immediately if LOS during breach
            if (threat.HasLOS)
            {
                unit.CombatFaceToward(threat.EstimatedPosition, 500f);
                FireAt(unit, threat.EstimatedPosition);
            }

            float dist = Vector3.Distance(unit.transform.position, _domTarget);
            return dist < 1f;
        }

        public override void OnExit(StealthHuntAI unit)
        {
            unit.CombatStop();
            // Signal CQBController we are in position
            TacticalBrain.GetOrCreate(unit.squadID).CQB.SignalStackReady(unit);
        }
    }

    // =========================================================================
    // ClearCornerAction -- sweep corners from point of domination
    // =========================================================================

    /// <summary>
    /// Systematically sweep dead space from the point of domination.
    /// Checks hard corners -- the areas not visible from the fatal funnel.
    /// Signals room clear when done.
    /// </summary>
    public class ClearCornerAction : GoapAction
    {
        public override string Name => "ClearCorner";
        public override bool IsInterruptible => true;
        public override int Priority => 7;

        private float _sweepTimer;
        private int _sweepStep;
        private Vector3 _basePos;
        private const float SweepDuration = 0.8f;
        private const int SweepSteps = 3;

        public override bool CheckPreconditions(WorldState s)
            => s.AtDomPoint && !s.RoomCleared;

        public override WorldState ApplyEffects(WorldState s)
        {
            s.RoomCleared = true;
            return s;
        }

        public override float GetCost(WorldState s, StealthHuntAI unit)
            => 1f;

        public override void OnEnter(StealthHuntAI unit, ThreatModel threat)
        {
            _sweepTimer = 0f;
            _sweepStep = 0;
            _basePos = unit.transform.position;
        }

        public override bool Execute(StealthHuntAI unit, ThreatModel threat,
                                      TacticalBrain brain, float dt)
        {
            _sweepTimer += dt;

            // Systematic sweep -- face different angles to check corners
            float[] sweepAngles = { 0f, 45f, 90f, 135f, 180f };
            int angleIdx = Mathf.Min(_sweepStep, sweepAngles.Length - 1);

            Vector3 sweepDir = Quaternion.Euler(0, sweepAngles[angleIdx], 0)
                * unit.transform.forward;
            unit.CombatFaceToward(unit.transform.position + sweepDir * 3f, 120f);

            // Engage if LOS during sweep
            if (threat.HasLOS)
            {
                FireAt(unit, threat.EstimatedPosition);
                unit.CombatFaceToward(threat.EstimatedPosition, 400f);
            }

            if (_sweepTimer >= SweepDuration)
            {
                _sweepTimer = 0f;
                _sweepStep++;
            }

            if (_sweepStep >= SweepSteps)
            {
                // Room cleared -- signal squad
                brain.CQB.SignalRoomCleared(unit);
                CombatEventBus.RaiseSquad(unit.squadID,
                    CombatEventType.ThreatFound, unit,
                    unit.transform.position);
                return true;
            }

            return false;
        }
    }

    // =========================================================================
    // HoldFatalFunnelAction -- cover the doorway from outside
    // =========================================================================

    /// <summary>
    /// Hold position outside the door and shoot anything that comes out.
    /// Used by holder guards and singletons who can't breach safely.
    /// </summary>
    public class HoldFatalFunnelAction : GoapAction
    {
        public override string Name => "HoldFatalFunnel";
        public override bool IsInterruptible => false;
        public override int Priority => 8;

        private Vector3 _holdPos;
        private bool _destSet;
        private float _holdTimer;
        private const float MaxHoldTime = 10f;

        public override bool CheckPreconditions(WorldState s)
            => s.NearEntryPoint && !s.AtDomPoint;

        public override WorldState ApplyEffects(WorldState s)
        {
            s.InCover = true;
            return s;
        }

        public override float GetCost(WorldState s, StealthHuntAI unit)
            => 1.5f;

        public override void OnEnter(StealthHuntAI unit, ThreatModel threat)
        {
            _destSet = false;
            _holdTimer = 0f;

            var brain = TacticalBrain.GetOrCreate(unit.squadID);
            var role = brain.CQB.GetRole(unit);

            if (role.HasValue)
            {
                _holdPos = role.Value.StackPos;
                _destSet = true;
            }
            else if (brain.CQB.ActiveEntry != null)
            {
                // Solo guard -- hold left stack
                _holdPos = brain.CQB.ActiveEntry.StackLeftPos;
                _destSet = true;
            }
        }

        public override bool Execute(StealthHuntAI unit, ThreatModel threat,
                                      TacticalBrain brain, float dt)
        {
            _holdTimer += dt;

            if (!_destSet) return true;

            float dist = Vector3.Distance(unit.transform.position, _holdPos);
            if (dist > 0.8f)
            {
                unit.CombatMoveTo(_holdPos);
                return false;
            }

            unit.CombatStop();

            // Face the fatal funnel
            if (brain.CQB.ActiveEntry != null)
            {
                Vector3 funnelCenter = brain.CQB.ActiveEntry.transform.position
                    + brain.CQB.ActiveEntry.transform.forward;
                unit.CombatFaceToward(funnelCenter, 160f);
            }

            // Shoot threat if visible
            if (threat.HasLOS || threat.Confidence > 0.3f)
                FireAt(unit, GetBestKnownPosition(unit, threat));

            // Room cleared or timeout -- done
            return brain.CQB.RoomCleared || _holdTimer > MaxHoldTime;
        }

        public override void OnExit(StealthHuntAI unit)
            => unit.CombatStop();
    }
}