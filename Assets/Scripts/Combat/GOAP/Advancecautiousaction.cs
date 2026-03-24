using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI.Combat
{
    // =========================================================================
    // AdvanceCautiousAction
    // =========================================================================

    /// <summary>
    /// Cover-to-cover advance at full speed but with mandatory peek at each position.
    /// Used when guard is alone, squad is weakened, or shot at without LOS.
    ///
    /// Behavior:
    ///   1. Find cover outside the danger cone (line of fire from shooter)
    ///   2. Sprint to it at full speed
    ///   3. Stop and peek 1-2s -- scan for threat
    ///   4. LOS gained -> return true (hand off to planner)
    ///   5. No LOS -> advance to next cover point closer to estimated position
    /// </summary>
    public class AdvanceCautiousAction : GoapAction
    {
        public override string Name => "AdvanceCautious";
        public override bool IsInterruptible => true;
        public override int Priority => 3;

        /// <summary>Triggered directly by shot event -- skip precondition check.</summary>
        public bool TriggeredByShot;

        private enum Phase { Move, Peek }

        private Phase _phase;
        private Vector3 _currentDest;
        private float _peekTimer;
        private float _totalTimer;
        private float _peekDuration;
        private bool _destSet;
        private int _stepCount;

        private const float MaxDuration = 30f;
        private const float MaxSteps = 6;
        private const float MinPeek = 1.0f;
        private const float MaxPeek = 2.0f;
        private const float StepDistance = 8f;  // max distance per step

        public override bool CheckPreconditions(WorldState s)
            => s.HasShotFrom
            || s.SquadStrength < 0.4f
            || (s.ThreatConfidence > 0.05f && s.ThreatConfidence < 0.35f);

        public override WorldState ApplyEffects(WorldState s)
        {
            s.InCover = true;
            s.TargetEliminated = true;
            return s;
        }

        public override float GetCost(WorldState s, StealthHuntAI unit)
        {
            float cost = 2.0f;
            if (s.HasShotFrom) cost -= 0.8f;
            if (s.SquadStrength < 0.4f) cost -= 0.4f;
            // More expensive than aggressive when full squad and fresh intel
            if (s.SquadStrength > 0.7f && s.ThreatConfidence > 0.5f) cost += 1.5f;
            return Mathf.Max(0.5f, cost);
        }

        public override void OnEnter(StealthHuntAI unit, ThreatModel threat)
        {
            _phase = Phase.Move;
            _peekTimer = 0f;
            _totalTimer = 0f;
            _destSet = false;
            _stepCount = 0;
            _peekDuration = Random.Range(MinPeek, MaxPeek);

            PickNextDest(unit, threat);
        }

        public override bool Execute(StealthHuntAI unit, ThreatModel threat,
                                      TacticalBrain brain, float dt)
        {
            _totalTimer += dt;
            if (_totalTimer > MaxDuration) return true;

            // LOS gained -- hand off immediately
            if (threat.HasLOS) return true;

            // Too many steps without finding threat -- give up
            if (_stepCount >= MaxSteps) return true;

            switch (_phase)
            {
                case Phase.Move: return TickMove(unit, threat, dt);
                case Phase.Peek: return TickPeek(unit, threat, dt);
            }
            return true;
        }

        // ---------- Move phase -----------------------------------------------

        private bool TickMove(StealthHuntAI unit, ThreatModel threat, float dt)
        {
            if (!_destSet)
            {
                PickNextDest(unit, threat);
                if (!_destSet) return true; // nowhere to go
            }

            // Sprint to destination at full speed
            unit.CombatMoveTo(_currentDest, 1.0f);

            float dist = Vector3.Distance(unit.transform.position, _currentDest);
            if (dist < 0.8f)
            {
                // Arrived -- stop and peek
                unit.CombatStop();
                _phase = Phase.Peek;
                _peekTimer = 0f;
                _peekDuration = Random.Range(MinPeek, MaxPeek);
                _stepCount++;
            }

            return false;
        }

        // ---------- Peek phase -----------------------------------------------

        private bool TickPeek(StealthHuntAI unit, ThreatModel threat, float dt)
        {
            _peekTimer += dt;

            // Face toward estimated threat position while peeking
            Vector3 lookTarget = threat.HasShotFrom
                ? threat.ShotFromPosition
                : threat.EstimatedPosition;

            unit.CombatFaceToward(lookTarget, 80f);

            // Shoot if LOS gained during peek
            if (threat.HasLOS)
            {
                FireAt(unit, threat.EstimatedPosition);
                return true; // hand off to planner
            }

            if (_peekTimer >= _peekDuration)
            {
                // Peek done -- advance to next position
                _phase = Phase.Move;
                _destSet = false;
            }

            return false;
        }

        // ---------- Destination selection ------------------------------------

        private void PickNextDest(StealthHuntAI unit, ThreatModel threat)
        {
            _destSet = false;

            Vector3 origin = unit.transform.position;
            Vector3 target = threat.HasShotFrom
                ? threat.ShotFromPosition
                : threat.HasIntel ? threat.EstimatedPosition
                : origin + unit.transform.forward * StepDistance;

            Vector3 toTarget = (target - origin);
            toTarget.y = 0f;
            float totalDist = toTarget.magnitude;

            if (totalDist < 1f) return;

            // Direction toward target
            Vector3 dir = toTarget.normalized;

            // Danger cone -- shooter's line of fire
            // Advance perpendicular to danger cone where possible
            Vector3 safeDir = GetSafeApproachDir(unit, threat, dir);

            // Sample several positions along safe direction
            float[] distances = { StepDistance * 0.5f, StepDistance, StepDistance * 1.5f };
            float[] angles = { 0f, 20f, -20f, 40f, -40f };

            float bestScore = float.MinValue;
            Vector3 bestPos = Vector3.zero;

            foreach (float d in distances)
                foreach (float a in angles)
                {
                    Vector3 candidate = origin
                        + Quaternion.Euler(0, a, 0) * safeDir * Mathf.Min(d, totalDist * 0.7f);

                    // Must be on NavMesh
                    if (!NavMesh.SamplePosition(candidate, out var hit, 2f, NavMesh.AllAreas))
                        continue;

                    Vector3 pos = hit.position;

                    // Must be reachable
                    var path = new NavMeshPath();
                    if (!NavMesh.CalculatePath(origin, pos, NavMesh.AllAreas, path)
                     || path.status != NavMeshPathStatus.PathComplete) continue;

                    // Score -- closer to target is better, but not in danger cone
                    float progress = Vector3.Dot(pos - origin, dir);
                    float inDanger = IsInDangerCone(pos, threat) ? -3f : 0f;
                    float score = progress + inDanger;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPos = pos;
                    }
                }

            if (bestScore > float.MinValue + 1f)
            {
                _currentDest = bestPos;
                _destSet = true;
            }
        }

        // ---------- Danger cone helpers --------------------------------------

        private Vector3 GetSafeApproachDir(StealthHuntAI unit,
                                            ThreatModel threat, Vector3 toTarget)
        {
            if (!threat.HasShotFrom) return toTarget;

            // Rotate approach direction to avoid line of fire
            // Try approaching from the side rather than head-on
            Vector3 dangerDir = threat.ShotFromDirection;
            float dot = Vector3.Dot(toTarget, dangerDir);

            // If heading into danger cone -- offset to the side
            if (dot > 0.5f)
            {
                Vector3 side = Vector3.Cross(dangerDir, Vector3.up).normalized;
                // Pick whichever side gets us closer to target
                float dotLeft = Vector3.Dot(toTarget, side);
                return dotLeft > 0 ? side + toTarget * 0.5f
                                   : -side + toTarget * 0.5f;
            }

            return toTarget;
        }

        private bool IsInDangerCone(Vector3 pos, ThreatModel threat)
        {
            if (!threat.HasShotFrom) return false;

            Vector3 toPos = pos - threat.ShotFromPosition;
            toPos.y = 0f;
            float angle = Vector3.Angle(threat.ShotFromDirection, toPos.normalized);
            return angle < 35f;
        }

        public override void OnExit(StealthHuntAI unit)
        {
            unit.CombatStop();
        }
    }
}