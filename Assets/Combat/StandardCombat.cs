using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Goal-based combat behaviour. Guard commits to a goal and executes it fully
    /// before reassessing. No frame-by-frame state oscillation.
    ///
    /// Goals:
    ///   AdvanceTo(pos)   -- move to position, shoot on the way if LOS
    ///   HoldAndFire      -- in cover, fire at target
    ///   Suppress         -- fire at estimated position to cover teammate
    ///   Flank(pos)       -- move to flank position
    ///   Search           -- move to last known, look around
    /// </summary>
    [AddComponentMenu("StealthHuntAI/Standard Combat")]
    [RequireComponent(typeof(StealthHuntAI))]
    public class StandardCombat : MonoBehaviour, ICombatBehaviour
    {
        // ---------- Inspector -------------------------------------------------

        [Header("Engagement")]
        [Range(3f, 20f)] public float advanceStopDistance = 7f;
        [Range(1f, 10f)] public float coverWaitTime = 1.0f;
        [Range(0.3f, 3f)] public float peekDuration = 0.8f;
        [Range(2f, 15f)] public float repositionThreshold = 5f;
        [Range(5f, 40f)] public float suppressionRange = 18f;
        [Range(1f, 8f)] public float suppressionDuration = 2.5f;
        [Range(5f, 40f)] public float coverSearchRange = 20f;
        [Range(0.5f, 3f)] public float coverArrivalThreshold = 1.2f;

        [Header("Cover Weights")]
        public CoverWeights weights = new CoverWeights();

        [Header("Animation")]
        public List<CombatAnimSlot> animSlots = new List<CombatAnimSlot>();
        [Range(0f, 0.5f)] public float animTransitionDuration = 0.12f;

        // ---------- ICombatBehaviour -----------------------------------------

        public bool WantsControl { get; private set; }
        public string CurrentStateName => _goal.ToString();

        public void OnEnterCombat(StealthHuntAI ai)
        {
            WantsControl = true;
            _ai = ai;
            _brain = TacticalBrain.GetOrCreate(ai.squadID);
            _brain.RegisterMember(ai);
            _threat = new ThreatModel();
            _goal = Goal.Idle;
            _goalTimer = 0f;
            _currentCover = null;
            _inCover = false;
            _cachedRole = ai.ActiveRole;
            EnsureDefaultSlots();

            // Bootstrap threat from squad intel
            var board = SquadBlackboard.Get(ai.squadID);
            if (board != null && board.SharedConfidence > 0.1f)
                _threat.ReceiveIntel(board.SharedLastKnown, Vector3.zero,
                                     board.SharedConfidence);

            if (ai.Sensor?.CanSeeTarget == true)
            {
                var t = ai.GetTarget();
                if (t != null) _threat.UpdateWithSight(t.Position, t.Velocity);
            }

            PickGoal();
        }

        public void Tick(StealthHuntAI ai)
        {
            _ai = ai;
            _goalTimer += Time.deltaTime;

            UpdateThreat();

            ExecuteGoal();

            // Only pick a new goal when current one is DONE
            if (_goal == Goal.Idle) PickGoal();

            HuntDirector.RegisterHeat(ai.transform.position, 0.04f * Time.deltaTime);
        }

        public void OnExitCombat(StealthHuntAI ai)
        {
            WantsControl = false;
            _brain?.UnregisterMember(ai);
            ReleaseCover();
            ai.CombatRestoreRotation();
        }

        // ---------- Goals ----------------------------------------------------

        private enum Goal { Idle, AdvanceTo, HoldAndFire, Suppress, Flank, Search }

        private StealthHuntAI _ai;
        private TacticalBrain _brain;
        private ThreatModel _threat;
        private Goal _goal;
        private float _goalTimer;
        private Vector3 _goalDestination;
        private CoverPoint _currentCover;
        private SquadRole _cachedRole;
        private bool _inCover;
        private float _faceTimer;
        private Vector3 _cachedFaceTarget;

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

        // ---------- Goal selection -------------------------------------------

        private void PickGoal()
        {
            _goalTimer = 0f;

            // No intel -- search last known
            if (!_threat.HasIntel && _threat.LastSeenTime < 0f)
            {
                SetGoal(Goal.Search, _ai.transform.position);
                return;
            }

            Vector3 dest = _threat.HasIntel
                ? _threat.EstimatedPosition
                : _threat.LastKnownPosition;

            // Has LOS -- hold and fire from cover
            if (_threat.HasLOS)
            {
                SetGoal(Goal.HoldAndFire, dest);
                return;
            }

            // Bounding role -- coverer suppresses
            if (_brain.GetBoundingRole(_ai) == TacticalBrain.BoundingRole.Covering)
            {
                SetGoal(Goal.Suppress, dest);
                return;
            }

            // Flanker
            if (_brain.ShouldFlank(_ai) && _threat.Confidence > 0.3f)
            {
                Vector3 flank = _brain.GetFlankPosition(_ai) ?? dest;
                SetGoal(Goal.Flank, flank);
                return;
            }

            // Default -- advance to estimated position
            SetGoal(Goal.AdvanceTo, GetSpreadPosition(dest));
        }

        private void SetGoal(Goal g, Vector3 destination)
        {
            if (_goal == g) return;
            _goal = g;
            _goalTimer = 0f;
            _goalDestination = destination;

            // Clean up cover when leaving HoldAndFire
            if (g != Goal.HoldAndFire) ReleaseCover();
        }

        // ---------- Goal execution -------------------------------------------

        private void ExecuteGoal()
        {
            switch (_goal)
            {
                case Goal.AdvanceTo: ExecuteAdvanceTo(); break;
                case Goal.HoldAndFire: ExecuteHoldAndFire(); break;
                case Goal.Suppress: ExecuteSuppress(); break;
                case Goal.Flank: ExecuteFlank(); break;
                case Goal.Search: ExecuteSearch(); break;
            }
        }

        // AdvanceTo -- move to destination, shoot on the way, done when arrived
        private void ExecuteAdvanceTo()
        {
            float dist = Vector3.Distance(_ai.transform.position, _goalDestination);

            _ai.CombatMoveTo(_goalDestination);
            _ai.CombatRestoreRotation();
            PlayAnim(CombatAnimTrigger.Advance);

            if (_threat.HasLOS) FireAt(_threat.EstimatedPosition);

            // Arrived -- pick new goal
            if (dist < advanceStopDistance)
            {
                CompleteGoal();
                return;
            }

            // Got LOS while advancing -- switch to HoldAndFire
            if (_threat.HasLOS)
            {
                SetGoal(Goal.HoldAndFire, _threat.EstimatedPosition);
                return;
            }

            // Timeout -- pick fresh goal
            if (_goalTimer > 12f) CompleteGoal();
        }

        // HoldAndFire -- find cover, get in it, peek and fire
        private void ExecuteHoldAndFire()
        {
            Vector3 targetPos = _threat.HasIntel
                ? _threat.EstimatedPosition
                : _goalDestination;

            // Find cover if not in it
            if (!_inCover)
            {
                if (_currentCover == null)
                {
                    // Find nearest cover
                    var scored = CoverEvaluator.Evaluate(_ai, targetPos, _cachedRole,
                                                          weights, coverSearchRange);
                    if (scored.Count > 0)
                    {
                        _currentCover = scored[0].Point;
                        _currentCover.Occupy(_ai);
                    }
                    else
                    {
                        // No cover -- stand and fire
                        FaceToward(targetPos);
                        if (_threat.HasLOS) FireAt(targetPos);
                        if (_goalTimer > repositionThreshold) CompleteGoal();
                        return;
                    }
                }

                // Move to cover
                _ai.CombatMoveTo(_currentCover.transform.position);
                _ai.CombatRestoreRotation();
                PlayAnim(CombatAnimTrigger.MoveToCover);

                float dist = Vector3.Distance(
                    _ai.transform.position, _currentCover.transform.position);
                var agent = _ai.GetComponent<NavMeshAgent>();
                float rem = (agent != null && !float.IsInfinity(agent.remainingDistance)
                             && agent.remainingDistance > 0.01f)
                    ? agent.remainingDistance : dist;

                if (rem <= coverArrivalThreshold || dist <= coverArrivalThreshold)
                {
                    _ai.CombatStop();
                    _inCover = true;
                    _brain.OnAdvancerReachedCover(_ai);
                    PlayAnim(CombatAnimTrigger.TakeCover);
                    _goalTimer = 0f;
                }
                return;
            }

            // In cover
            FaceToward(targetPos);
            PlayAnim(CombatAnimTrigger.CoverIdle);

            if (_goalTimer < coverWaitTime) return;

            if (_threat.HasLOS)
            {
                // Peek and fire
                PlayAnim(DeterminePeekSide(targetPos)
                    ? CombatAnimTrigger.PeekLeft
                    : CombatAnimTrigger.PeekRight);
                PlayAnim(CombatAnimTrigger.CoverFire);
                FireAt(targetPos);
            }
            else if (_goalTimer > repositionThreshold)
            {
                // Player moved -- abandon cover and advance
                ReleaseCover();
                CompleteGoal();
            }
        }

        // Suppress -- fire at estimated position to cover bounding partner
        private void ExecuteSuppress()
        {
            Vector3 targetPos = _threat.HasIntel
                ? _threat.EstimatedPosition
                : _goalDestination;

            FaceToward(targetPos, 140f);
            PlayAnim(CombatAnimTrigger.Suppressing);

            if (_threat.Confidence > 0.1f) FireAt(targetPos);

            if (_goalTimer >= suppressionDuration)
            {
                _brain.OnAdvancerReachedCover(_ai);
                CompleteGoal();
            }

            // If we get LOS switch to HoldAndFire
            if (_threat.HasLOS)
                SetGoal(Goal.HoldAndFire, targetPos);
        }

        // Flank -- move to flank position then switch to HoldAndFire
        private void ExecuteFlank()
        {
            float dist = Vector3.Distance(_ai.transform.position, _goalDestination);
            _ai.CombatMoveTo(_goalDestination);
            _ai.CombatRestoreRotation();
            PlayAnim(CombatAnimTrigger.Flank);

            if (dist < 2f || _threat.HasLOS)
            {
                SetGoal(Goal.HoldAndFire,
                    _threat.HasIntel ? _threat.EstimatedPosition : _goalDestination);
                return;
            }

            if (_goalTimer > 10f) CompleteGoal();
        }

        // Search -- move to last known, look around
        private void ExecuteSearch()
        {
            Vector3 searchPos = _threat.LastSeenTime > -999f
                ? _threat.LastKnownPosition
                : _ai.transform.position;

            float dist = Vector3.Distance(_ai.transform.position, searchPos);

            if (dist > 3f)
            {
                _ai.CombatMoveTo(searchPos);
                _ai.CombatRestoreRotation();
                PlayAnim(CombatAnimTrigger.Advance);
            }
            else
            {
                _ai.CombatStop();
                PlayAnim(CombatAnimTrigger.CoverIdle);
                LookAround();
            }

            // Got intel -- stop searching
            if (_threat.Confidence > 0.15f)
            {
                CompleteGoal();
                return;
            }

            // Give up after 20s
            if (_goalTimer > 20f)
            {
                WantsControl = false;
            }
        }

        private float _lookTimer;
        private void LookAround()
        {
            _lookTimer += Time.deltaTime;
            if (_lookTimer > 1.2f)
            {
                _lookTimer = 0f;
                Vector3 dir = GetOpenDirection();
                _cachedFaceTarget = _ai.transform.position + dir * 5f;
            }
            if (_cachedFaceTarget != Vector3.zero)
                _ai.CombatFaceToward(_cachedFaceTarget, 60f);
        }

        private void CompleteGoal()
        {
            _goal = Goal.Idle;
            _goalTimer = 0f;
        }

        // ---------- Helpers --------------------------------------------------

        private void FaceToward(Vector3 pos, float speed = 150f)
        {
            _faceTimer += Time.deltaTime;
            if (_faceTimer >= 0.35f)
            {
                _faceTimer = 0f;
                _cachedFaceTarget = pos;
            }
            if (_cachedFaceTarget != Vector3.zero)
                _ai.CombatFaceToward(_cachedFaceTarget, speed);
        }

        private void FireAt(Vector3 pos)
        {
            _ai.GetComponent<IShootable>()?.TryShoot(pos);
        }

        private bool DeterminePeekSide(Vector3 targetPos)
        {
            if (_currentCover == null) return true;
            float dot = Vector3.Dot(
                _currentCover.transform.right,
                (targetPos - _currentCover.transform.position).normalized);
            return dot < 0f;
        }

        private void ReleaseCover()
        {
            if (_currentCover != null) { _currentCover.Release(_ai); _currentCover = null; }
            _inCover = false;
            _ai?.CombatRestoreRotation();
        }

        private Vector3 GetSpreadPosition(Vector3 center)
        {
            var units = HuntDirector.AllUnits;
            int idx = 0;
            for (int i = 0; i < units.Count; i++)
                if (units[i] == _ai) { idx = i; break; }

            if (idx == 0) return center;

            float angle = idx * 75f;
            float radius = 4f + idx * 1.5f;
            Vector3 dest = center + Quaternion.Euler(0, angle, 0) * Vector3.forward * radius;

            if (NavMesh.SamplePosition(dest, out NavMeshHit hit, radius, NavMesh.AllAreas))
                return hit.position;
            return center;
        }

        private Vector3 GetOpenDirection()
        {
            Vector3 origin = _ai.transform.position + Vector3.up * 1.4f;
            Vector3 baseDir = _threat.LastKnownVelocity.magnitude > 0.1f
                ? _threat.LastKnownVelocity.normalized
                : _ai.transform.forward;
            baseDir.y = 0f;
            if (baseDir.magnitude < 0.1f) baseDir = _ai.transform.forward;

            float[] angles = { 0f, -45f, 45f, -90f, 90f, -135f, 135f, 180f };
            foreach (float a in angles)
            {
                Vector3 dir = Quaternion.Euler(0, a + Random.Range(-15f, 15f), 0) * baseDir;
                if (!Physics.Raycast(origin, dir, 3f)) return dir;
            }
            return baseDir;
        }

        // ---------- Animation ------------------------------------------------

        public string CurrentCombatState => _goal.ToString();

        public void PlayCombatAnim(CombatAnimTrigger trigger)
        {
            if (_ai?.animator == null) return;
            string clip = GetClip(trigger);
            if (string.IsNullOrEmpty(clip)) return;
            try { _ai.animator.CrossFade(clip, animTransitionDuration); } catch { }
        }

        private void PlayAnim(CombatAnimTrigger t) => PlayCombatAnim(t);

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
}