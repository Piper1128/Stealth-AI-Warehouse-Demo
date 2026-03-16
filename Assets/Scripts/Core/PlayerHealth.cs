using UnityEngine;
using UnityEngine.UI;

namespace StealthHuntAI.Demo
{
    /// <summary>
    /// Player health system with UI health bar.
    /// Attach to PlayerCapsule.
    /// </summary>
    public class PlayerHealth : MonoBehaviour
    {
        [Header("Health")]
        [Range(1, 500)] public int maxHealth = 100;

        [Header("UI")]
        [Tooltip("Slider used as health bar. Assign in inspector.")]
        public Slider healthSlider;

        [Tooltip("Image that flashes red when taking damage.")]
        public Image damageFlash;

        [Tooltip("How long the damage flash lasts.")]
        [Range(0.05f, 0.5f)] public float flashDuration = 0.15f;

        [Header("Respawn")]
        [Tooltip("Seconds before game over screen after death.")]
        [Range(0.5f, 5f)] public float deathDelay = 2f;

        // ---------- Runtime ---------------------------------------------------

        public int CurrentHealth { get; private set; }
        public bool IsDead { get; private set; }

        // ---------- Internal --------------------------------------------------

        private float _flashTimer;
        private GameManager _gameManager;

        // ---------- Unity lifecycle -------------------------------------------

        private void Awake()
        {
            CurrentHealth = maxHealth;
            _gameManager = FindFirstObjectByType<GameManager>();

            UpdateUI();
        }

        private void Update()
        {
            if (_flashTimer > 0f)
            {
                _flashTimer -= Time.deltaTime;

                if (damageFlash != null)
                {
                    float alpha = _flashTimer / flashDuration;
                    Color col = damageFlash.color;
                    col.a = alpha * 0.5f;
                    damageFlash.color = col;
                }

                if (_flashTimer <= 0f && damageFlash != null)
                {
                    Color col = damageFlash.color;
                    col.a = 0f;
                    damageFlash.color = col;
                }
            }
        }

        // ---------- Public API ------------------------------------------------

        public void TakeDamage(int amount)
        {
            if (IsDead) return;

            CurrentHealth = Mathf.Max(0, CurrentHealth - amount);
            UpdateUI();

            // Flash damage indicator
            _flashTimer = flashDuration;

            if (CurrentHealth <= 0)
                Die();
        }

        public void Heal(int amount)
        {
            if (IsDead) return;
            CurrentHealth = Mathf.Min(maxHealth, CurrentHealth + amount);
            UpdateUI();
        }

        // ---------- Internal --------------------------------------------------

        private void UpdateUI()
        {
            if (healthSlider != null)
                healthSlider.value = (float)CurrentHealth / maxHealth;
        }

        private void Die()
        {
            IsDead = true;

            // Disable player movement
            var cc = GetComponent<UnityEngine.CharacterController>();
            if (cc != null) cc.enabled = false;

            // Tell game manager
            if (_gameManager != null)
                Invoke(nameof(NotifyDeath), deathDelay);
        }

        private void NotifyDeath()
        {
            if (_gameManager != null)
                _gameManager.OnPlayerDied();
        }
    }
}