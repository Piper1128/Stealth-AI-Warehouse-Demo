using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Squad-level strategies. TacticalBrain selects one based on world state
    /// and applies cost modifiers to unit GOAP actions so units coordinate
    /// toward a common tactical goal without a second planning layer.
    /// </summary>
    public enum SquadStrategy
    {
        None,
        Bounding,   // standard leap-frog advance -- pairs alternate suppress/advance
        Pincer,     // two units flank from opposite sides while one suppresses
        Suppress,   // hold player down -- one unit suppresses, others reposition
        Withdraw,   // all units fall back to defensive positions
        Overwatch,  // one unit takes high ground, others advance below
    }

    /// <summary>
    /// Per-action cost modifier applied by squad strategy.
    /// Multiplied onto GetCost() result -- < 1 = cheaper, > 1 = more expensive.
    /// </summary>
    public struct StrategyCostModifier
    {
        public float Advance;
        public float Flank;
        public float Suppress;
        public float TakeCover;
        public float HighGround;
        public float Withdraw;

        public static StrategyCostModifier Neutral => new StrategyCostModifier
        {
            Advance = 1f,
            Flank = 1f,
            Suppress = 1f,
            TakeCover = 1f,
            HighGround = 1f,
            Withdraw = 1f,
        };

        public static StrategyCostModifier For(SquadStrategy s, BuddyRole role)
            => s switch
            {
                SquadStrategy.Bounding => role == BuddyRole.Tracker
                    ? new StrategyCostModifier { Advance = 0.5f, Flank = 0.7f, Suppress = 2f, TakeCover = 1f, HighGround = 1.5f, Withdraw = 3f }
                    : new StrategyCostModifier { Advance = 2f, Flank = 1.5f, Suppress = 0.3f, TakeCover = 0.5f, HighGround = 1f, Withdraw = 3f },

                SquadStrategy.Pincer => role == BuddyRole.Tracker
                    ? new StrategyCostModifier { Advance = 0.6f, Flank = 0.3f, Suppress = 2f, TakeCover = 1.5f, HighGround = 1f, Withdraw = 3f }
                    : new StrategyCostModifier { Advance = 1.5f, Flank = 0.3f, Suppress = 0.5f, TakeCover = 1f, HighGround = 1f, Withdraw = 3f },

                SquadStrategy.Suppress => role == BuddyRole.Suppressor
                    ? new StrategyCostModifier { Advance = 3f, Flank = 2f, Suppress = 0.2f, TakeCover = 0.4f, HighGround = 1.5f, Withdraw = 3f }
                    : new StrategyCostModifier { Advance = 0.6f, Flank = 0.4f, Suppress = 2f, TakeCover = 1f, HighGround = 0.8f, Withdraw = 3f },

                SquadStrategy.Withdraw =>
                    new StrategyCostModifier { Advance = 5f, Flank = 5f, Suppress = 2f, TakeCover = 0.3f, HighGround = 2f, Withdraw = 0.2f },

                SquadStrategy.Overwatch => role == BuddyRole.Suppressor
                    ? new StrategyCostModifier { Advance = 3f, Flank = 2f, Suppress = 0.5f, TakeCover = 1f, HighGround = 0.2f, Withdraw = 3f }
                    : new StrategyCostModifier { Advance = 0.5f, Flank = 0.6f, Suppress = 1.5f, TakeCover = 1f, HighGround = 2f, Withdraw = 3f },

                _ => Neutral,
            };
    }

    /// <summary>
    /// Strategy selector -- runs in TacticalBrain, picks best strategy
    /// based on squad world state and switches when strategy fails.
    /// </summary>
    public class SquadStrategySelector
    {
        public SquadStrategy Current { get; private set; } = SquadStrategy.Bounding;
        public float StrategyTimer { get; private set; }
        public string FailReason { get; private set; }

        private const float MinStrategyDuration = 4f;
        private const float MaxStrategyDuration = 15f;

        private readonly List<SquadStrategy> _failedStrategies = new List<SquadStrategy>();

        // ---------- Update ---------------------------------------------------

        public void Update(float dt, WorldState squadState, TacticalBrain brain)
        {
            StrategyTimer += dt;

            // Dont switch strategy too fast
            if (StrategyTimer < MinStrategyDuration) return;

            // Evaluate if current strategy is working
            if (!IsStrategyWorking(squadState))
            {
                _failedStrategies.Add(Current);
                if (_failedStrategies.Count > 3) _failedStrategies.RemoveAt(0);
                SwitchStrategy(squadState, brain);
            }

            // Periodic re-evaluation
            if (StrategyTimer > MaxStrategyDuration)
                SwitchStrategy(squadState, brain);
        }

        private bool IsStrategyWorking(WorldState state)
        {
            return Current switch
            {
                SquadStrategy.Bounding => state.ThreatConfidence > 0.2f,
                SquadStrategy.Pincer => state.FlankRouteOpen,
                SquadStrategy.Suppress => state.SquadmateSuppressing,
                SquadStrategy.Withdraw => state.SquadStrength < 0.3f,
                SquadStrategy.Overwatch => state.HighGroundNearby,
                _ => true,
            };
        }

        private void SwitchStrategy(WorldState state, TacticalBrain brain)
        {
            var candidate = ChooseBestStrategy(state);
            if (candidate != Current)
            {
                Current = candidate;
                StrategyTimer = 0f;
                _failedStrategies.Clear();
            }
        }

        private SquadStrategy ChooseBestStrategy(WorldState state)
        {
            // Critical -- always withdraw if squad collapsing
            if (state.SquadStrength < 0.25f || state.Health < 0.2f)
                return SquadStrategy.Withdraw;

            // Score each strategy
            float bestScore = -1f;
            SquadStrategy best = SquadStrategy.Bounding;

            var candidates = new[]
            {
                SquadStrategy.Bounding,
                SquadStrategy.Pincer,
                SquadStrategy.Suppress,
                SquadStrategy.Overwatch,
            };

            foreach (var s in candidates)
            {
                if (_failedStrategies.Contains(s)) continue;
                float score = ScoreStrategy(s, state);
                if (score > bestScore) { bestScore = score; best = s; }
            }

            return best;
        }

        private float ScoreStrategy(SquadStrategy s, WorldState state)
        {
            return s switch
            {
                SquadStrategy.Bounding =>
                    0.5f + state.ThreatConfidence * 0.5f,

                SquadStrategy.Pincer =>
                    state.FlankRouteOpen
                        ? 0.7f + (1f - state.ThreatConfidence) * 0.3f
                        : 0f,

                SquadStrategy.Suppress =>
                    state.SquadmateAdvancing ? 0.8f : 0.3f,

                SquadStrategy.Overwatch =>
                    state.HighGroundNearby
                        ? 0.6f + (1f - state.DistToThreat / 30f) * 0.4f
                        : 0f,

                _ => 0f,
            };
        }

        public override string ToString()
            => Current + " t=" + StrategyTimer.ToString("F1") + "s";
    }
}