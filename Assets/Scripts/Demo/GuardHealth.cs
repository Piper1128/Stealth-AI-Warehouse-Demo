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

            // Strong hit reaction -- suppression, stagger, intel
            if (!IsDead)
            {
                // Heavy suppression on hit -- guard flinches and loses accuracy
                AddSuppression(0.5f);

                // Stagger -- briefly stop movement
                StartCoroutine(StaggerRoutine(info));

                // Hit reaction animation
                _ai?.PlayAnimState("HitReaction");

                // Become hostile
                _ai?.ForceHostile();

                // Raise combat event -- triggers immediate cover seek
                CombatEventBus.Get(_ai).Raise(
                    CombatEventType.DamageTaken, _ai,
                    transform.position, info.direction,
                    info.damage / maxHealth);

                // Broadcast pain -- nearby guards react
                HuntDirector.BroadcastSound(transform.position, 0.8f, 25f);

                // Estimate shooter from bullet direction
                // Use raycast to find actual range -- much more accurate
                if (info.direction != Vector3.zero && _ai != null)
                {
                    Vector3 shootDir = -info.direction.normalized;
                    Vector3 shooterPos;
                    float confidence;

                    // Raycast backwards along bullet path to find cover or wall
                    if (Physics.Raycast(transform.position + Vector3.up,
                        shootDir, out RaycastHit sourceHit, 60f))
                    {
                        // Hit something -- shooter is near there
                        shooterPos = sourceHit.point;
                        confidence = 0.5f; // moderate -- shooter may have moved
                    }
                    else
                    {
                        // No hit -- estimate at max range
                        shooterPos = transform.position + shootDir * 40f;
                        confidence = 0.25f; // low -- just a direction
                    }

                    var board = SquadBlackboard.Get(_ai.squadID);
                    board?.ShareIntel(shooterPos, confidence);
                }
            }

            onHit?.Invoke(info);

            if (CurrentHealth <= 0f) Die();
        }

        private System.Collections.IEnumerator StaggerRoutine(DamageInfo info)
        {
            var agent = GetComponent<UnityEngine.AI.NavMeshAgent>();
            var sc = GetComponent<StandardCombat>();

            // Stop movement briefly
            if (agent != null) agent.isStopped = true;

            // Push in hit direction (visual knockback via transform)
            if (info.direction != Vector3.zero)
            {
                Vector3 push = info.direction.normalized * 0.3f;
                push.y = 0f;
                float pushTime = 0f;
                while (pushTime < 0.12f)
                {
                    transform.position += push * Time.deltaTime * 8f;
                    pushTime += Time.deltaTime;
                    yield return null;
                }
            }

            // Stagger max 0.2s -- combat pack needs agent running
            float staggerDur = Mathf.Clamp(info.damage / 30f, 0.1f, 0.2f);
            yield return new UnityEngine.WaitForSeconds(staggerDur);

            // Always resume -- combat pack handles movement from here
            if (agent != null && !IsDead) agent.isStopped = false;

            // Force seek cover immediately after stagger
            if (!IsDead) ForceSeekCover();
        }

        private void ForceSeekCover()
        {
            var sc = GetComponent<StandardCombat>();
            if (sc == null || !sc.WantsControl) return;
            sc.ForceRole(StandardCombat.CombatRole.Cover);
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
            _ai?.SetDead();
            // Alert squad that a buddy is down
            if (_ai != null)
                CombatEventBus.RaiseSquad(_ai.squadID,
                    CombatEventType.BuddyDown, _ai,
                    transform.position, 1f);
            // Remove from HuntDirector unit list so dead guards dont count
            HuntDirector.UnregisterUnit(_ai);
            _ai?.Die();
            onDied?.Invoke();
        }
    }
}