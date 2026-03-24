using UnityEngine;

namespace StealthHuntAI
{
    /// <summary>
    /// Interface for custom combat behaviour systems.
    /// Attach a MonoBehaviour implementing this interface to a guard and assign
    /// it to StealthHuntAI.combatBehaviourOverride to replace default Hostile behaviour.
    ///
    /// Lifecycle:
    ///   OnEnterCombat  called once when AlertState transitions to Hostile
    ///   Tick           called every frame while WantsControl is true
    ///   OnExitCombat   called once when AlertState leaves Hostile
    ///
    /// Control:
    ///   Set WantsControl = true  to take over Hostile state from Core
    ///   Set WantsControl = false to let Core run Pursuing/Shooting as fallback
    ///
    /// When WantsControl is true:
    ///   - Core skips Pursuing, Shooting, LostTarget ticks
    ///   - Core skips UpdateAnimator (Combat Pack owns animation)
    ///   - Core still owns awareness, state transitions out of Hostile,
    ///     squad intel, and perception -- Combat Pack does not touch these
    ///
    /// Usage:
    ///   public class StandardCombat : MonoBehaviour, ICombatBehaviour { ... }
    ///   // Assign in inspector: guard.combatBehaviourOverride = GetComponent<StandardCombat>()
    /// </summary>
    public interface ICombatBehaviour
    {
        /// <summary>Called when a near shot is heard -- take cover immediately.</summary>
        void OnNearShot(Vector3 shotOrigin);
        /// <summary>
        /// Set to true when Combat Pack wants to control Hostile behaviour.
        /// Core checks this every frame -- Combat Pack sets it internally.
        /// </summary>
        bool WantsControl { get; }

        /// <summary>Called once when the unit enters Hostile state.</summary>
        void OnEnterCombat(StealthHuntAI ai);

        /// <summary>
        /// Called every frame while WantsControl is true and unit is Hostile.
        /// Combat Pack owns movement, animation and SubState during this time.
        /// </summary>
        void Tick(StealthHuntAI ai);

        /// <summary>Called once when the unit leaves Hostile state.</summary>
        void OnExitCombat(StealthHuntAI ai);
    }
}