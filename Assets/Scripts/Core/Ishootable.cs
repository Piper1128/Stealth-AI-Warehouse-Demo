using UnityEngine;

namespace StealthHuntAI
{
    /// <summary>
    /// Interface for weapon components that can be triggered to shoot.
    /// Allows Core TickShooting to fire without depending on Demo assembly.
    /// Implement on GuardWeapon or any custom weapon script.
    /// </summary>
    public interface IShootable
    {
        void TryShoot(Vector3 targetPos);
    }
}