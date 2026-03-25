using System.Collections.Generic;
using UnityEngine;
using StealthHuntAI.Combat.CQB;
using UnityEngine.AI;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Squad-level tactical coordination for combat.
    /// One TacticalBrain per squad -- shared between all StandardCombat instances in the squad.
    /// Coordinates bounding overwatch, pincer movement and role assignment.
    /// </summary>
    public class TacticalBrain
    {
        // ---------- Subsystems -----------------------------------------------

        /// <summary>Shared threat intel -- best known position from any squad member.</summary>
        public SquadIntel Intel { get; } = new SquadIntel();
        public SquadCoordinator Coordinator { get; } = new SquadCoordinator();
        public SquadStrategySelector Strategy => Coordinator.Strategy;
        public CQBController CQB { get; } = new CQBController();

        // ---------- Backward-compat passthroughs ----------------------------

        /// <summary>Backward compat: returns Intel.Threat.</summary>
        public ThreatModel SharedThreat => Intel.Threat;

        public void RegisterMember(StealthHuntAI unit) => Coordinator.Register(unit);
        public void UnregisterMember(StealthHuntAI unit) => Coordinator.Unregister(unit);

        public void ReportThreat(StealthHuntAI reporter, Vector3 pos,
                                  Vector3 vel, float confidence)
            => Intel.Report(pos, vel, confidence);

        public void UpdateNoSight() => Intel.UpdateNoSight();

        public bool ShouldFlank(StealthHuntAI unit) => Coordinator.ShouldFlank(unit);

        public Vector3? GetFlankPosition(StealthHuntAI unit)
        {
            if (!Intel.HasIntel) return null;
            return Coordinator.GetFlankPosition(unit, Intel.EstimatedPos);
        }

        public void OnAdvancerReachedCover(StealthHuntAI unit)
            => Coordinator.OnAdvancerReachedCover(unit);

        // ---------- Committed goal -------------------------------------------

        public class CommittedGoalData
        {
            public enum GoalType { ClearRoom, PursueTarget }
            public GoalType Type;
            public Vector3 Position;
            public float Timeout;
            public float StartTime;
            public bool IsExpired => Time.time - StartTime > Timeout;
        }

        public CommittedGoalData CommittedGoal { get; private set; }

        /// <summary>Commit entire squad to a goal. Guards wont replann away from it.</summary>
        public void SetCommittedGoal(CommittedGoalData.GoalType type,
                                      Vector3 position, float timeout = 120f)
        {
            CommittedGoal = new CommittedGoalData
            {
                Type = type,
                Position = position,
                Timeout = timeout,
                StartTime = Time.time,
            };
        }

        /// <summary>Clear committed goal -- squad can now pick new objectives.</summary>
        public void ClearCommittedGoal() => CommittedGoal = null;

        /// <summary>Interrupt committed goal -- new intel or ambush.</summary>
        public void InterruptCommittedGoal(string reason)
        {
            CommittedGoal = null;
        }

        /// <summary>Tick committed goal -- expire if timeout reached.</summary>
        public void TickCommittedGoal()
        {
            if (CommittedGoal != null && CommittedGoal.IsExpired)
                CommittedGoal = null;
        }

        // ---------- Static registry ------------------------------------------

        private static readonly Dictionary<int, TacticalBrain> _brains
            = new Dictionary<int, TacticalBrain>();

        public static TacticalBrain GetOrCreate(int squadID)
        {
            if (!_brains.TryGetValue(squadID, out var brain))
            {
                brain = new TacticalBrain();
                _brains[squadID] = brain;
            }
            return brain;
        }

        public static void Clear()
        {
            _brains.Clear();
        }
    }
}