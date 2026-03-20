using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI
{
    public partial class StealthHuntAI
    {
        // ---------- Passive sub-states ----------------------------------------

        private void TickIdle()
        {
            StopMoving();
            ApplySpeedToMovement(_baseAgentSpeed * patrolSpeedMultiplier);

            switch (behaviourMode)
            {
                case BehaviourMode.Patrol:
                    if (patrolPoints == null || patrolPoints.Length == 0)
                        GenerateAutoPatrol();
                    if (patrolPoints != null && patrolPoints.Length > 0)
                        TransitionSubState(SubState.Patrolling);
                    break;

                case BehaviourMode.GuardZone:
                    if (guardZoneWaypointCount > 0 && _guardZonePoints.Count > 0)
                        TransitionSubState(SubState.GuardZonePatrol);
                    break;

                default:
                    break;
            }
        }

        // ---------- Patrol ----------------------------------------------------

        /// <summary>
        /// Auto-generates patrol points on NavMesh around spawn position.
        /// Called when no patrol points are manually assigned.
        /// </summary>
        private void GenerateAutoPatrol()
        {
            int count = 4;
            float radius = autoPatrolRadius > 0f ? autoPatrolRadius : 8f;
            var pts = new System.Collections.Generic.List<Transform>();

            for (int i = 0; i < count; i++)
            {
                float angle = i * (360f / count) * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
                Vector3 pos = _spawnPosition + dir * radius;

                if (NavMeshHelper.Sample(pos, radius * 0.5f, out Vector3 snapped))
                {
                    var go = new GameObject("AutoPatrol_" + i);
                    go.transform.position = snapped;
                    pts.Add(go.transform);
                }
            }

            if (pts.Count >= 2)
                patrolPoints = pts.ToArray();
        }

        private void TickPatrolling()
        {
            if (patrolPoints == null || patrolPoints.Length == 0) return;

            if (_waitingAtWaypoint)
            {
                _waypointWaitTimer += Time.deltaTime;
                if (_waypointWaitTimer >= waypointWaitTime)
                {
                    _waitingAtWaypoint = false;
                    _waypointWaitTimer = 0f;
                    AdvancePatrolIndex();
                    MoveTo(patrolPoints[_patrolIndex].position);
                }
                return;
            }

            if (!_movement.HasPath || _movement.RemainingDistance < 0.5f)
            {
                // Apply pending nudge when waypoint is reached -- never mid-patrol
                if (ConsumePendingNudge()) return;

                if (waypointWaitTime > 0f)
                {
                    _waitingAtWaypoint = true;
                    StopMoving();
                }
                else
                {
                    AdvancePatrolIndex();
                    MoveTo(patrolPoints[_patrolIndex].position);
                }
            }
        }

        private void AdvancePatrolIndex()
        {
            if (patrolPattern == PatrolPattern.Loop)
            {
                _patrolIndex = (_patrolIndex + 1) % patrolPoints.Length;
                return;
            }

            // PingPong
            if (_pingPongForward)
            {
                _patrolIndex++;
                if (_patrolIndex >= patrolPoints.Length - 1)
                {
                    _patrolIndex = patrolPoints.Length - 1;
                    _pingPongForward = false;
                }
            }
            else
            {
                _patrolIndex--;
                if (_patrolIndex <= 0)
                {
                    _patrolIndex = 0;
                    _pingPongForward = true;
                }
            }
        }

        // ---------- Guard Zone ------------------------------------------------

        private void GenerateGuardZonePoints()
        {
            _guardZonePoints.Clear();
            _guardZoneIndex = 0;

            Vector3 center = guardZoneCenter != null
                ? guardZoneCenter.position
                : _spawnPosition;

            float angleStep = 360f / guardZoneWaypointCount;
            float angle = Random.Range(0f, 360f);
            float hRange = searchHeightRange > 0f ? searchHeightRange : -1f;

            for (int i = 0; i < guardZoneWaypointCount; i++)
            {
                float dist = guardZoneRadius * Random.Range(0.35f, 0.85f);

                if (NavMeshHelper.SampleOffset(center, angle, dist,
                                                guardZoneRadius, out Vector3 pt,
                                                hRange))
                    _guardZonePoints.Add(pt);

                angle += angleStep + Random.Range(-20f, 20f);
            }
        }

        private void TickGuardZonePatrol()
        {
            if (_guardZonePoints.Count == 0)
            {
                GenerateGuardZonePoints();
                return;
            }

            if (_guardZoneWaiting)
            {
                _guardZoneWaitTimer += Time.deltaTime;
                if (_guardZoneWaitTimer >= guardZoneWaitTime)
                {
                    _guardZoneWaiting = false;
                    _guardZoneWaitTimer = 0f;
                    _guardZoneIndex = (_guardZoneIndex + 1) % _guardZonePoints.Count;
                    MoveTo(_guardZonePoints[_guardZoneIndex]);
                }
                return;
            }

            if (!_movement.HasPath || _movement.RemainingDistance < 0.6f)
            {
                if (guardZoneWaitTime > 0f)
                {
                    _guardZoneWaiting = true;
                    StopMoving();
                }
                else
                {
                    _guardZoneIndex = (_guardZoneIndex + 1) % _guardZonePoints.Count;
                    MoveTo(_guardZonePoints[_guardZoneIndex]);
                }
            }
        }

        // ---------- Returning -------------------------------------------------

        private void TickReturning()
        {
            ApplySpeedToMovement(_baseAgentSpeed * patrolSpeedMultiplier);
            MoveTo(GetHomePosition());

            // Use both RemainingDistance and direct distance as fallback
            float distToHome = Vector3.Distance(transform.position, GetHomePosition());
            bool nearHome = _movement.RemainingDistance < 0.8f || distToHome < 0.8f;

            if (nearHome)
            {
                // Disable agent rotation so we can rotate manually
                if (_agent != null) _agent.updateRotation = false;

                Quaternion targetRot = GetHomeRotation();
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation, targetRot, 180f * Time.deltaTime);

                if (Quaternion.Angle(transform.rotation, targetRot) < 3f)
                {
                    transform.rotation = targetRot;

                    // Re-enable agent rotation before leaving state
                    if (_agent != null) _agent.updateRotation = true;

                    switch (behaviourMode)
                    {
                        case BehaviourMode.Patrol:
                            TransitionSubState(SubState.Patrolling);
                            break;
                        case BehaviourMode.GuardZone:
                            TransitionSubState(guardZoneWaypointCount > 0
                                ? SubState.GuardZonePatrol
                                : SubState.Idle);
                            break;
                        default:
                            TransitionSubState(SubState.Idle);
                            break;
                    }
                }
            }
            else if (_agent != null && !_agent.updateRotation)
            {
                _agent.updateRotation = true;
            }
        }

        private Quaternion GetHomeRotation()
        {
            // Patrol -- face toward next patrol point
            if (behaviourMode == BehaviourMode.Patrol
             && patrolPoints != null && patrolPoints.Length > 1)
            {
                int next = (_patrolIndex + 1) % patrolPoints.Length;
                Vector3 dir = patrolPoints[next].position
                            - patrolPoints[_patrolIndex].position;
                if (dir.magnitude > 0.1f)
                    return Quaternion.LookRotation(dir.normalized, Vector3.up);
            }

            // Guard zone -- face zone center
            if (behaviourMode == BehaviourMode.GuardZone
             && guardZoneCenter != null)
            {
                Vector3 dir = guardZoneCenter.position - transform.position;
                dir.y = 0f;
                if (dir.magnitude > 0.1f)
                    return Quaternion.LookRotation(dir.normalized, Vector3.up);
            }

            // Default -- return to spawn rotation
            return _spawnRotation;
        }

        private Vector3 GetHomePosition()
        {
            if (behaviourMode == BehaviourMode.GuardZone)
                return guardZoneCenter != null ? guardZoneCenter.position : _spawnPosition;

            if (behaviourMode == BehaviourMode.Patrol &&
                patrolPoints != null && patrolPoints.Length > 0)
                return patrolPoints[_patrolIndex].position;

            return _spawnPosition;
        }

    }
}