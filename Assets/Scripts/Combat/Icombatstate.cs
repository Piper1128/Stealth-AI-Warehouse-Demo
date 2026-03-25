using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Interface for individual combat states in StandardCombat.
    /// Each state owns its own destination, waypoints and timing.
    /// StandardCombat selects states -- states execute behaviour.
    /// </summary>
    public interface ICombatState
    {
        /// <summary>Called once when state is entered.</summary>
        void OnEnter(StandardCombat ctx, ThreatModel threat, TacticalBrain brain);

        /// <summary>Called every frame. Returns true when state is done.</summary>
        bool Tick(StandardCombat ctx, ThreatModel threat, TacticalBrain brain, float dt);

        /// <summary>Called when state is exited -- clean up NavMeshAgent etc.</summary>
        void OnExit(StandardCombat ctx);

        /// <summary>Display name for TacticalInspector.</summary>
        string Name { get; }
    }
}