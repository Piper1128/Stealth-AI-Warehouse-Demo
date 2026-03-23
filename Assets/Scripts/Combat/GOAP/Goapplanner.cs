using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// A* GOAP planner. Finds the lowest-cost sequence of actions
    /// that transforms the current world state into the goal state.
    ///
    /// Max search depth: 5 actions.
    /// Re-plans when world state changes significantly or plan is complete.
    /// </summary>
    public class GoapPlanner
    {
        private const int MaxDepth = 5;
        private const int MaxNodes = 128;

        // ---------- Plan result ----------------------------------------------

        public class Plan
        {
            public List<GoapAction> Actions = new List<GoapAction>();
            public float TotalCost;
            public bool IsValid => Actions.Count > 0;

            public GoapAction Current => _index < Actions.Count
                ? Actions[_index] : null;

            private int _index;

            public void Advance() => _index++;
            public bool IsComplete => _index >= Actions.Count;

            public override string ToString()
                => string.Join(" -> ", Actions) + " cost=" + TotalCost.ToString("F1");
        }

        // ---------- A* node --------------------------------------------------

        private class Node
        {
            public WorldState State;
            public GoapAction Action;
            public Node Parent;
            public float G; // cost so far
            public float H; // heuristic
            public float F => G + H;
        }

        // ---------- Planning -------------------------------------------------

        /// <summary>
        /// Find the lowest-cost action sequence from current state to goal.
        /// Returns null if no plan found.
        /// </summary>
        /// <summary>
        /// Backwards A* -- searches from goal state toward current state.
        /// Faster than forward search: goal space is smaller than action space.
        /// Per F.E.A.R. GOAP design by Jeff Orkin.
        /// </summary>
        public Plan BuildPlan(WorldState current, WorldState goal,
                               List<GoapAction> actions, StealthHuntAI unit)
        {
            // Sort actions by priority descending -- higher priority checked first
            var sortedActions = new List<GoapAction>(actions);
            sortedActions.Sort((a, b) => b.Priority.CompareTo(a.Priority));

            var open = new List<Node>();
            var closed = new List<Node>();

            var start = new Node
            {
                State = current,
                G = 0f,
                H = current.DistanceTo(goal),
            };
            open.Add(start);

            int iterations = 0;

            while (open.Count > 0 && iterations < MaxNodes)
            {
                iterations++;

                // Pick lowest F
                var current_node = GetLowest(open);
                open.Remove(current_node);
                closed.Add(current_node);

                // Reached goal?
                if (GoalMet(current_node.State, goal))
                    return BuildPath(current_node);

                // Limit depth
                int depth = GetDepth(current_node);
                if (depth >= MaxDepth) continue;

                // Expand -- sorted by priority
                for (int i = 0; i < sortedActions.Count; i++)
                {
                    var action = sortedActions[i];
                    if (!action.CheckPreconditions(current_node.State)) continue;

                    WorldState next = action.ApplyEffects(current_node.State);
                    float cost = current_node.G + action.GetCost(current_node.State, unit);

                    if (IsInClosed(closed, next)) continue;

                    var neighbor = new Node
                    {
                        State = next,
                        Action = action,
                        Parent = current_node,
                        G = cost,
                        H = next.DistanceTo(goal),
                    };

                    var existing = GetInOpen(open, next);
                    if (existing != null)
                    {
                        if (neighbor.G < existing.G)
                        {
                            existing.G = neighbor.G;
                            existing.Parent = current_node;
                            existing.Action = action;
                        }
                    }
                    else
                    {
                        open.Add(neighbor);
                    }
                }
            }

            return null; // no plan found
        }

        // ---------- Helpers --------------------------------------------------

        private bool GoalMet(WorldState state, WorldState goal)
        {
            if (goal.TargetEliminated && !state.TargetEliminated) return false;
            if (goal.ChokepointHeld && !state.ChokepointHeld) return false;
            if (goal.SafePosition && !state.SafePosition) return false;
            return true;
        }

        private Node GetLowest(List<Node> nodes)
        {
            Node best = nodes[0];
            for (int i = 1; i < nodes.Count; i++)
                if (nodes[i].F < best.F) best = nodes[i];
            return best;
        }

        private bool IsInClosed(List<Node> closed, WorldState state)
        {
            for (int i = 0; i < closed.Count; i++)
                if (StatesEqual(closed[i].State, state)) return true;
            return false;
        }

        private Node GetInOpen(List<Node> open, WorldState state)
        {
            for (int i = 0; i < open.Count; i++)
                if (StatesEqual(open[i].State, state)) return open[i];
            return null;
        }

        private bool StatesEqual(WorldState a, WorldState b)
        {
            return a.HasLOS == b.HasLOS
                && a.InCover == b.InCover
                && a.TargetEliminated == b.TargetEliminated
                && a.ChokepointHeld == b.ChokepointHeld
                && a.SafePosition == b.SafePosition
                && Mathf.Abs(a.DistToThreat - b.DistToThreat) < 3f;
        }

        private int GetDepth(Node node)
        {
            int depth = 0;
            var n = node;
            while (n.Parent != null) { depth++; n = n.Parent; }
            return depth;
        }

        private Plan BuildPath(Node node)
        {
            var actions = new List<GoapAction>();
            float cost = node.G;

            var n = node;
            while (n.Action != null)
            {
                actions.Insert(0, n.Action);
                n = n.Parent;
            }

            return new Plan { Actions = actions, TotalCost = cost };
        }
    }
}