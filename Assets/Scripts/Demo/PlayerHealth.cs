using UnityEngine;
using UnityEngine.Events;

namespace StealthHuntAI.Demo
{
    public class PlayerHealth : MonoBehaviour
    {
        [Header("Health")]
        public float maxHealth = 100f;
        public float startHealth = 100f;

        [Header("Armor")]
        public ArmorType armorType = ArmorType.None;
        public float armorPoints = 100f;

        [Header("Regeneration")]
        public bool enableRegen = true;
        [Tooltip("Seconds after damage before regen starts.")]
        public float regenDelay = 5f;
        [Tooltip("HP per second.")]
        public float regenRate = 8f;
        [Tooltip("Max HP regen can reach. Set to maxHealth for full regen.")]
        public float regenMax = 70f;

        [Header("Death")]
        public float respawnDelay = 3f;

        [Header("Events")]
        public UnityEvent<float> onDamaged;
        public UnityEvent<DamageInfo> onHit;
        public UnityEvent onDied;
        public UnityEvent onRespawned;

        public float CurrentHealth { get; private set; }
        public float CurrentArmor { get; private set; }
        public bool IsDead { get; private set; }
        public float HealthPercent => CurrentHealth / maxHealth;
        public float ArmorPercent => armorPoints > 0 ? CurrentArmor / armorPoints : 0f;

        private float _regenTimer;
        private float _respawnTimer;

        private static float ArmorReduction(ArmorType t) => t switch
        {
            ArmorType.Light => 0.30f,
            ArmorType.Medium => 0.50f,
            ArmorType.Heavy => 0.70f,
            _ => 0f
        };

        private void Awake()
        {
            CurrentHealth = startHealth;
            CurrentArmor = armorPoints;
        }

        private void Update()
        {
            if (IsDead)
            {
                _respawnTimer += Time.deltaTime;
                if (_respawnTimer >= respawnDelay) Respawn();
                return;
            }

            if (enableRegen && CurrentHealth < regenMax)
            {
                _regenTimer += Time.deltaTime;
                if (_regenTimer >= regenDelay)
                    CurrentHealth = Mathf.Min(regenMax,
                        CurrentHealth + regenRate * Time.deltaTime);
            }
        }

        public void TakeDamage(DamageInfo info)
        {
            if (IsDead) return;
            float dmg = CalcDamage(info);
            if (dmg <= 0f) return;
            CurrentHealth = Mathf.Max(0f, CurrentHealth - dmg);
            _regenTimer = 0f;
            onDamaged?.Invoke(CurrentHealth);
            onHit?.Invoke(info);
            if (CurrentHealth <= 0f) Die();
        }

        public void TakeDamage(float damage) =>
            TakeDamage(new DamageInfo { damage = damage });

        public void Heal(float amount)
        {
            if (IsDead) return;
            CurrentHealth = Mathf.Min(maxHealth, CurrentHealth + amount);
        }

        private float CalcDamage(DamageInfo info)
        {
            if (info.isSuppression) return 0f;
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
            IsDead = true; _respawnTimer = 0f;
            // Disable player controls
            var controller = GetComponent<PlayerController>();
            if (controller != null) controller.enabled = false;
            var combat = GetComponent<PlayerCombat>();
            if (combat != null) combat.enabled = false;
            onDied?.Invoke();
        }

        private void Respawn()
        {
            // Re-enable player controls
            var controller = GetComponent<PlayerController>();
            if (controller != null) controller.enabled = true;
            var combat = GetComponent<PlayerCombat>();
            if (combat != null) combat.enabled = true;

            IsDead = false;
            CurrentHealth = startHealth;
            CurrentArmor = armorPoints;
            _regenTimer = _respawnTimer = 0f;
            onRespawned?.Invoke();
        }
    }
}