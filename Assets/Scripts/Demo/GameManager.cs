using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using TMPro;
using UnityEngine.SceneManagement;

namespace StealthHuntAI.Demo
{
    /// <summary>
    /// Manages win/lose/respawn and HUD for the demo scene.
    /// Connect PlayerHealth.onDied -> GameManager.OnPlayerDied
    /// Connect PlayerHealth.onRespawned -> GameManager.OnPlayerRespawned
    /// Connect ExitTrigger -> GameManager.OnPlayerReachedExit
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        [Header("UI Panels")]
        public GameObject winPanel;
        public GameObject losePanel;
        public GameObject hudPanel;
        public GameObject deathPanel;  // brief death screen before respawn

        [Header("HUD Elements")]
        public Slider healthBar;
        public Slider armorBar;
        public TMP_Text ammoText;
        public TMP_Text fireModeText;
        public Image hitVignette;   // red vignette on hit
        public Image deathVignette; // full black on death

        [Header("Respawn")]
        public float deathFadeDuration = 1.0f;
        public float respawnFadeDuration = 0.5f;

        [Header("Restart")]
        public float restartDelay = 2f;

        // ---------- Runtime --------------------------------------------------

        public bool GameOver { get; private set; }

        private PlayerHealth _playerHealth;
        private PlayerCombat _playerCombat;
        private float _vignetteAlpha;
        private float _deathFadeAlpha;
        private bool _fading;
        private bool _fadingIn;

        // ---------- Unity lifecycle ------------------------------------------

        private void Start()
        {
            if (winPanel != null) winPanel.SetActive(false);
            if (losePanel != null) losePanel.SetActive(false);
            if (deathPanel != null) deathPanel.SetActive(false);
            if (hudPanel != null) hudPanel.SetActive(true);

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            _playerHealth = FindFirstObjectByType<PlayerHealth>();
            _playerCombat = FindFirstObjectByType<PlayerCombat>();

            // Auto-connect events if not done in inspector
            if (_playerHealth != null)
            {
                _playerHealth.onDied.AddListener(OnPlayerDied);
                _playerHealth.onRespawned.AddListener(OnPlayerRespawned);
                _playerHealth.onHit.AddListener(OnPlayerHit);
            }
        }

        private void Update()
        {
            UpdateHUD();
            UpdateVignette();
            UpdateDeathFade();

            if (GameOver && Keyboard.current != null
             && (Keyboard.current.rKey.wasPressedThisFrame
              || Keyboard.current.spaceKey.wasPressedThisFrame))
                Restart();
        }

        // ---------- HUD ------------------------------------------------------

        private void UpdateHUD()
        {
            if (_playerHealth == null) return;

            if (healthBar != null)
                healthBar.value = _playerHealth.HealthPercent;

            if (armorBar != null)
                armorBar.value = _playerHealth.ArmorPercent;

            if (_playerCombat != null)
            {
                if (ammoText != null)
                    ammoText.text = _playerCombat.IsReloading
                        ? "RELOADING..."
                        : _playerCombat.CurrentAmmo + " / " + "30";

                if (fireModeText != null)
                    fireModeText.text = _playerCombat.CurrentFireMode.ToString().ToUpper();
            }
        }

        // ---------- Hit vignette ---------------------------------------------

        private void OnPlayerHit(DamageInfo info)
        {
            if (info.isSuppression) return;
            _vignetteAlpha = 0.6f; // flash red
        }

        private void UpdateVignette()
        {
            if (hitVignette == null) return;

            // Low HP -- persistent red edge
            float lowHpAlpha = _playerHealth != null
                ? Mathf.Clamp01(1f - _playerHealth.HealthPercent * 2f) * 0.5f
                : 0f;

            // Hit flash -- fades out
            _vignetteAlpha = Mathf.Max(0f, _vignetteAlpha - Time.deltaTime * 2f);

            float alpha = Mathf.Max(lowHpAlpha, _vignetteAlpha);
            var col = hitVignette.color;
            col.a = alpha;
            hitVignette.color = col;
        }

        // ---------- Death / Respawn ------------------------------------------

        public void OnPlayerDied()
        {
            if (GameOver) return;
            GameOver = true;
            _fading = true;
            _fadingIn = true;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (hudPanel != null) hudPanel.SetActive(false);
            if (deathPanel != null) deathPanel.SetActive(true);
        }

        public void OnPlayerRespawned() { }

        private void UpdateDeathFade()
        {
            if (deathVignette == null || !_fading) return;

            if (_fadingIn)
            {
                _deathFadeAlpha = Mathf.MoveTowards(
                    _deathFadeAlpha, 1f, Time.deltaTime / deathFadeDuration);
            }
            else
            {
                _deathFadeAlpha = Mathf.MoveTowards(
                    _deathFadeAlpha, 0f, Time.deltaTime / respawnFadeDuration);
                if (_deathFadeAlpha <= 0f) _fading = false;
            }

            var col = deathVignette.color;
            col.a = _deathFadeAlpha;
            deathVignette.color = col;
        }

        // ---------- Win ------------------------------------------------------

        public void OnPlayerReachedExit()
        {
            if (GameOver) return;
            GameOver = true;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (hudPanel != null) hudPanel.SetActive(false);
            if (winPanel != null) winPanel.SetActive(true);
        }

        // ---------- Restart --------------------------------------------------

        public void Restart()
        {
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        public void QuitToMenu()
        {
            SceneManager.LoadScene(0);
        }
    }
}