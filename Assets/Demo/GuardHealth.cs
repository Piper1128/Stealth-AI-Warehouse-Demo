using UnityEngine;
using UnityEngine.Events;
using StealthHuntAI.Combat;

namespace StealthHuntAI.Demo
{
    [RequireComponent(typeof(StealthHuntAI))]
    public class GuardHealth : MonoBehaviour, ISuppressionHandler, IHealthProvider
    {
        [Header("Health")]
        public float maxHealth = 100f;
        public float startHealth = 100f;

        [Header("Armor")]
        public ArmorType armorType = ArmorType.None;
        public float armorPoints = 100f;

        [Header("Suppression")]
        [Tooltip("Level at which guard is considered suppressed (0-1).")]
        public float suppressThreshold = 0.4f;
        [Tooltip("How fast suppression decays per second.")]
        public float suppressDecay = 0.8f;
        [Tooltip("Accuracy penalty at full suppression (0-1).")]
        public float suppressAccuracyPenalty = 0.6f;
        [Tooltip("Speed penalty at full suppression (0-1).")]
        public float suppressSpeedPenalty = 0.4f;
        [Tooltip("Awareness rise speed multiplier when suppressed.")]
        public float suppressAwarenessPenalty = 0.5f;

        [Header("Events")]
        public UnityEvent<DamageInfo> onHit;
        public UnityEvent onDied;

        public float CurrentHealth { get; private set; }
        public float CurrentArmor { get; private set; }
        public float SuppressLevel { get; private set; }
        public bool IsDead { get; private set; }
        public bool IsSuppressed => SuppressLevel >= suppressThreshold;
        public float HealthPercent => CurrentHealth / maxHealth;

        private StealthHuntAI _ai;

        private static float ArmorReduction(ArmorType t) => t switch
        {
            ArmorType.Light => 0.30f,
            ArmorType.Medium => 0.50f,
            ArmorType.Heavy => 0.70f,
            _ => 0f
        };

        private void Awake()
        {
            _ai = GetComponent<StealthHuntAI>();
            CurrentHealth = startHealth;
            CurrentArmor = armorPoints;
        }

        private void Update()
        {
            if (SuppressLevel > 0f)
            {
                SuppressLevel = Mathf.Max(0f,
                    SuppressLevel - suppressDecay * Time.deltaTime);
            }

            // Apply suppression effects
            var sensor = GetComponent<AwarenessSensor>();
            if (IsSuppressed)
            {
                // Reduce awareness rise speed
                if (sensor != null)
                    sensor.sightAccumulatorMultiplier = suppressAwarenessPenalty;

                // Reduce movement speed
                var ai = GetComponent<StealthHuntAI>();
                if (ai != null)
                {
                    ai.patrolSpeedMultiplier = ai.patrolSpeedMultiplier
                        * (1f - SuppressLevel * suppressSpeedPenalty);
                    ai.chaseSpeedMultiplier = ai.chaseSpeedMultiplier
                        * (1f - SuppressLevel * suppressSpeedPenalty);
                }
            }
            else
            {
                // Restore normal values
                if (sensor != null)
                    sensor.sightAccumulatorMultiplier = 1f;
            }
        }

        public void TakeDamage(DamageInfo info)
        {
            if (IsDead) return;

            if (info.isSuppression)
            {
                AddSuppression(info.suppressAmount);
                return;
            }

            float dmg = CalcDamage(info);
            CurrentHealth = Mathf.Max(0f, CurrentHealth - dmg);

            // Hit reaction anim
            _ai?.PlayAnimState("HitReaction");

            // Become hostile immediately when hit
            if (_ai != null && !IsDead)
            {
                _ai.ForceHostile();
                AddSuppression(0.2f);

                // Broadcast pain sound -- nearby guards hear it and become suspicious
                HuntDirector.BroadcastSound(transform.position, 0.7f, 20f);

                // Share shooter position via squad blackboard
                if (info.direction != Vector3.zero)
                {
                    // Estimate shooter position from bullet direction
                    Vector3 shooterDir = -info.direction.normalized;
                    Vector3 estimatedShooterPos = transform.position + shooterDir * 15f;
                    var board = SquadBlackboard.Get(_ai.squadID);
                    if (board != null)
                        board.ShareIntel(estimatedShooterPos, 0.6f);
                }
            }

            onHit?.Invoke(info);

            if (CurrentHealth <= 0f) Die();
        }

        public void AddSuppression(float amount)
        {
            SuppressLevel = Mathf.Clamp01(SuppressLevel + amount);
        }

        private float CalcDamage(DamageInfo info)
        {
            float dmg = info.damage;
            float red = ArmorReduction(armorType);
            if (red > 0f && CurrentArmor > 0f)
            {
                float effRed = red * (1f - info.penetration);
                dmg *= (1f - effRed);
                CurrentArmor = Mathf.Max(0f,
                    CurrentArmor - info.damage * (1f - info.penetration) * 0.3f);
                if (CurrentArmor <= 0f) armorType = ArmorType.None;
            }
            return dmg;
        }

        private void Die()
        {
            IsDead = true;
            // Remove from HuntDirector unit list so dead guards dont count
            HuntDirector.UnregisterUnit(_ai);
            _ai?.Die();
            onDied?.Invoke();
        }
    }
}