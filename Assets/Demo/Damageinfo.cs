using UnityEngine;

namespace StealthHuntAI.Demo
{
    /// <summary>
    /// Shared damage information passed between weapons and health systems.
    /// </summary>
    public struct DamageInfo
    {
        public float damage;
        public float penetration;     // 0-1, how well bullet penetrates armor
        public Vector3 direction;       // for hit indicator and ragdoll
        public Vector3 hitPoint;        // world space hit position
        public bool isHeadshot;
        public bool isSuppression;   // near miss -- no damage, suppression only
        public float suppressAmount;  // how much suppression to apply 0-1
        public string sourceTag;       // "Player" or "Guard"

        public static DamageInfo FromBullet(float dmg, float pen,
            Vector3 dir, Vector3 point, bool headshot = false)
        {
            return new DamageInfo
            {
                damage = dmg,
                penetration = pen,
                direction = dir,
                hitPoint = point,
                isHeadshot = headshot,
                isSuppression = false,
                suppressAmount = 0f,
                sourceTag = "Player"
            };
        }

        public static DamageInfo Suppression(Vector3 dir, float amount = 0.3f)
        {
            return new DamageInfo
            {
                damage = 0f,
                penetration = 0f,
                direction = dir,
                hitPoint = Vector3.zero,
                isHeadshot = false,
                isSuppression = true,
                suppressAmount = amount,
                sourceTag = "Player"
            };
        }
    }

    /// <summary>Armor types with different damage reduction values.</summary>
    public enum ArmorType { None, Light, Medium, Heavy }
}