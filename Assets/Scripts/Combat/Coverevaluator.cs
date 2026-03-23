using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Scores cover points for a given unit using a multi-factor weighted system.
    /// Inspired by tactical AI scoring systems used in commercial games.
    ///
    /// Scoring factors:
    ///   Protection    -- does cover block incoming fire?
    ///   Proximity     -- distance from unit to cover
    ///   Visibility    -- can unit engage target from this cover?
    ///   Crossfire     -- does position enable crossfire with other units?
    ///   Formation     -- maintains tactical spacing from squad mates
    ///   PathPredict   -- positions along player's predicted escape route
    ///   HeatPenalty   -- avoids positions other units are using
    ///   Novelty       -- avoids reusing same cover repeatedly
    /// </summary>
    [System.Serializable]
    public class CoverWeights
    {
        [Range(0f, 2f)] public float protection = 1.0f;
        [Range(0f, 2f)] public float proximity = 0.8f;
        [Range(0f, 2f)] public float visibility = 0.9f;
        [Range(0f, 2f)] public float crossfire = 0.7f;
        [Range(0f, 2f)] public float formation = 0.5f;
        [Range(0f, 2f)] public float pathPredict = 0.6f;
        [Range(0f, 2f)] public float heatPenalty = 0.8f;
        [Range(0f, 2f)] public float novelty = 0.4f;
    }

    /// <summary>
    /// Burst-compiled job for parallel cover point scoring.
    /// Handles the pure math portion -- protection, proximity, heat, novelty.
    /// Physics-dependent factors (visibility, crossfire) run on main thread.
    /// </summary>
    [BurstCompile]
    public struct CoverScoreJob : IJobParallelFor
    {
        // Input
        [ReadOnly] public NativeArray<float3> CoverPositions;
        [ReadOnly] public NativeArray<float> HeatValues;
        [ReadOnly] public NativeArray<float> TimeSinceUsed;
        [ReadOnly] public float3 UnitPosition;
        [ReadOnly] public float3 TargetPosition;
        [ReadOnly] public float3 PredictedFlightDir;
        [ReadOnly] public float MaxRange;

        // Weights
        [ReadOnly] public float WeightProximity;
        [ReadOnly] public float WeightHeat;
        [ReadOnly] public float WeightNovelty;
        [ReadOnly] public float WeightPathPredict;

        // Output
        public NativeArray<float> Scores;

        public void Execute(int i)
        {
            float3 pos = CoverPositions[i];

            // Proximity score
            float dist = math.distance(UnitPosition, pos);
            float proximity = 1f - math.clamp(dist / 20f, 0f, 1f);

            // Target distance -- not too close
            float targetDist = math.distance(TargetPosition, pos);
            float notTooClose = math.clamp((targetDist - 3f) / 10f, 0f, 1f);

            // Heat penalty
            float heat = 1f - math.clamp(HeatValues[i] * 0.5f, 0f, 1f);

            // Novelty -- muscle overuse penalty
            float novelty = math.clamp(TimeSinceUsed[i] / 20f, 0f, 1f);

            // Path prediction -- along player's predicted escape route
            float3 toPoint = math.normalizesafe(pos - TargetPosition);
            float dot = math.dot(math.normalizesafe(PredictedFlightDir), toPoint);
            float pathPred = math.clamp(dot * 0.5f + 0.5f, 0f, 1f);

            Scores[i] = proximity * WeightProximity
                      + notTooClose * 0.1f
                      + heat * WeightHeat
                      + novelty * WeightNovelty
                      + pathPred * WeightPathPredict;
        }
    }

    public static class CoverEvaluator
    {
        /// <summary>
        /// Find and score all available cover points for a unit.
        /// Base scores computed in parallel via Burst-compiled job.
        /// Physics-dependent scores (protection, visibility, crossfire) added on main thread.
        /// Returns sorted list -- best cover first.
        /// </summary>
        public static List<ScoredCover> Evaluate(
            StealthHuntAI unit,
            Vector3 targetPos,
            SquadRole role,
            CoverWeights weights,
            float maxRange = 25f)
        {
            var rawPoints = HuntDirector.AllCoverPoints;
            int count = rawPoints.Count;
            if (count == 0) return new List<ScoredCover>();

            // Native arrays for Burst job
            var positions = new NativeArray<float3>(count, Allocator.TempJob);
            var heatVals = new NativeArray<float>(count, Allocator.TempJob);
            var timeSince = new NativeArray<float>(count, Allocator.TempJob);
            var baseScores = new NativeArray<float>(count, Allocator.TempJob);

            for (int i = 0; i < count; i++)
            {
                var cp = rawPoints[i] as CoverPoint;
                if (cp == null) continue;
                Vector3 p = cp.transform.position;
                positions[i] = new float3(p.x, p.y, p.z);
                heatVals[i] = HuntDirector.GetHeat(p);
                timeSince[i] = cp.TimeSinceUsed;
            }

            Vector3 fp = HuntDirector.PredictedFlightDir;
            var job = new CoverScoreJob
            {
                CoverPositions = positions,
                HeatValues = heatVals,
                TimeSinceUsed = timeSince,
                UnitPosition = new float3(unit.transform.position.x,
                                                unit.transform.position.y,
                                                unit.transform.position.z),
                TargetPosition = new float3(targetPos.x, targetPos.y, targetPos.z),
                PredictedFlightDir = new float3(fp.x, fp.y, fp.z),
                MaxRange = maxRange,
                WeightProximity = weights.proximity,
                WeightHeat = weights.heatPenalty,
                WeightNovelty = weights.novelty,
                WeightPathPredict = weights.pathPredict,
                Scores = baseScores
            };

            JobHandle handle = job.Schedule(count, 8);
            handle.Complete();

            // Add physics-dependent scores on main thread
            var results = new List<ScoredCover>();
            for (int i = 0; i < count; i++)
            {
                var cp = rawPoints[i] as CoverPoint;
                if (cp == null) continue;
                if (cp.IsOccupied && cp.Occupant != unit) continue;

                float dist = Vector3.Distance(unit.transform.position,
                                               cp.transform.position);
                if (dist > maxRange) continue;

                float score = baseScores[i]
                    + ScoreProtection(cp.transform.position, targetPos) * weights.protection
                    + ScoreVisibility(cp, targetPos) * weights.visibility
                    + ScoreCrossfire(cp.transform.position, unit, targetPos) * weights.crossfire
                    + ScoreFormation(cp.transform.position, unit) * weights.formation
                    + ScoreRole(cp.transform.position, cp, unit, targetPos, role);

                score *= cp.scoreBias;
                if (score > 0.01f)
                    results.Add(new ScoredCover(cp, score));
            }

            positions.Dispose();
            heatVals.Dispose();
            timeSince.Dispose();
            baseScores.Dispose();

            results.Sort((a, b) => b.Score.CompareTo(a.Score));
            return results;
        }

        // ---------- Scoring --------------------------------------------------

        private static float ScoreCoverPoint(
            CoverPoint cp,
            StealthHuntAI unit,
            Vector3 targetPos,
            SquadRole role,
            CoverWeights w)
        {
            Vector3 pos = cp.transform.position;

            float protection = ScoreProtection(pos, targetPos) * w.protection;
            float proximity = ScoreProximity(pos, unit) * w.proximity;
            float visibility = ScoreVisibility(cp, targetPos) * w.visibility;
            float crossfire = ScoreCrossfire(pos, unit, targetPos) * w.crossfire;
            float formation = ScoreFormation(pos, unit) * w.formation;
            float pathPredict = ScorePathPredict(pos, targetPos) * w.pathPredict;
            float heat = (1f - HuntDirector.GetHeat(pos)) * w.heatPenalty;
            float novelty = ScoreNovelty(cp) * w.novelty;

            float base_score = protection + proximity + visibility
                             + crossfire + formation + pathPredict
                             + heat + novelty;

            // Role modifier
            float roleBonus = ScoreRole(pos, cp, unit, targetPos, role);

            return (base_score + roleBonus) * cp.scoreBias;
        }

        private static float ScoreProtection(Vector3 coverPos, Vector3 threatPos)
        {
            Vector3 dir = coverPos - threatPos;
            float dist = dir.magnitude;
            if (dist < 0.1f) return 0f;

            // Cast from threat toward cover -- blocked = good protection
            Vector3 origin = threatPos + Vector3.up * 1.0f;
            if (Physics.Raycast(origin, dir.normalized, dist - 0.4f))
                return 1f;

            return 0.1f;
        }

        private static float ScoreProximity(Vector3 coverPos, StealthHuntAI unit)
        {
            float dist = Vector3.Distance(unit.transform.position, coverPos);
            return 1f - Mathf.Clamp01(dist / 20f);
        }

        private static float ScoreVisibility(CoverPoint cp, Vector3 targetPos)
        {
            Vector3 peekPos = cp.transform.position + cp.PeekDir * 0.6f
                             + Vector3.up * 1.4f;
            Vector3 toTarget = targetPos + Vector3.up * 0.8f - peekPos;

            if (!Physics.Raycast(peekPos, toTarget.normalized, toTarget.magnitude))
                return 1f;
            return 0f;
        }

        private static float ScoreCrossfire(Vector3 coverPos, StealthHuntAI unit,
                                             Vector3 targetPos)
        {
            // Find other Hostile units and check if this position creates crossfire
            float bestAngle = 0f;
            var units = HuntDirector.AllUnits;

            for (int i = 0; i < units.Count; i++)
            {
                var other = units[i];
                if (other == null || other == unit) continue;
                if (other.CurrentAlertState != AlertState.Hostile) continue;

                // Angle between this position and other unit around target
                Vector3 dirA = (coverPos - targetPos).normalized;
                Vector3 dirB = (other.transform.position - targetPos).normalized;
                float dot = Vector3.Dot(dirA, dirB);
                float angle = Mathf.Acos(Mathf.Clamp(dot, -1f, 1f)) * Mathf.Rad2Deg;

                // 90-180 degree separation is ideal crossfire
                float crossScore = Mathf.Clamp01((angle - 45f) / 90f);
                bestAngle = Mathf.Max(bestAngle, crossScore);
            }

            return bestAngle;
        }

        private static float ScoreFormation(Vector3 coverPos, StealthHuntAI unit)
        {
            // Penalize positions too close to other units
            float minDist = float.MaxValue;
            var units = HuntDirector.AllUnits;

            for (int i = 0; i < units.Count; i++)
            {
                var other = units[i];
                if (other == null || other == unit) continue;
                float d = Vector3.Distance(coverPos, other.transform.position);
                if (d < minDist) minDist = d;
            }

            // Ideal spacing 5-15m -- too close or too far both bad
            if (minDist < 3f) return 0f;
            if (minDist < 6f) return Mathf.Clamp01((minDist - 3f) / 3f);
            if (minDist > 20f) return 0.5f;
            return 1f;
        }

        private static float ScorePathPredict(Vector3 coverPos, Vector3 targetPos)
        {
            // Bonus if position is along player's predicted escape route
            Vector3 predictedDir = HuntDirector.PredictedFlightDir;
            if (predictedDir.magnitude < 0.1f) return 0.5f;

            Vector3 toPoint = (coverPos - targetPos).normalized;
            float dot = Vector3.Dot(predictedDir, toPoint);
            return Mathf.Clamp01(dot * 0.5f + 0.5f);
        }

        private static float ScoreNovelty(CoverPoint cp)
        {
            // Muscle overuse penalty -- avoid reusing same cover
            return Mathf.Clamp01(cp.TimeSinceUsed / 20f);
        }

        private static float ScoreRole(Vector3 coverPos, CoverPoint cp,
                                        StealthHuntAI unit,
                                        Vector3 targetPos, SquadRole role)
        {
            switch (role)
            {
                case SquadRole.Tracker:
                    // Prefer closer cover with good visibility
                    float tDist = Vector3.Distance(coverPos, targetPos);
                    return Mathf.Clamp01(1f - tDist / 15f) * 0.5f;

                case SquadRole.Flanker:
                    // Prefer cover to the sides of the target
                    Vector3 toUnit = (unit.transform.position - targetPos).normalized;
                    Vector3 toPoint = (coverPos - targetPos).normalized;
                    float side = Mathf.Abs(Vector3.Cross(toUnit, toPoint).y);
                    return side * 0.5f;

                case SquadRole.Overwatch:
                    // Prefer high cover with maximum visibility
                    float visBonus = ScoreVisibility(cp, targetPos);
                    float highBonus = cp.type == CoverPoint.CoverType.High ? 0.3f : 0f;
                    return (visBonus + highBonus) * 0.4f;

                case SquadRole.Blocker:
                    // Prefer cover along predicted player path
                    return ScorePathPredict(coverPos, targetPos) * 0.5f;

                default:
                    return 0f;
            }
        }
    }

    /// <summary>Cover point with evaluated score.</summary>
    public struct ScoredCover
    {
        public CoverPoint Point;
        public float Score;

        public ScoredCover(CoverPoint pt, float score)
        {
            Point = pt;
            Score = score;
        }
    }
}