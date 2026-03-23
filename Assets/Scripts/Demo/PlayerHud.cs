using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace StealthHuntAI.Demo
{
    /// <summary>
    /// FPS HUD with CS-style crosshair, ammo count, armor bar and reload progress bar.
    /// All elements drawn via Unity UI -- assign refs in inspector or let AutoFind locate them.
    /// </summary>
    public class PlayerHUD : MonoBehaviour
    {
        [Header("Refs -- leave empty to auto-find")]
        public PlayerCombat combat;
        public PlayerHealth health;

        [Header("Crosshair")]
        public RectTransform crosshairTop;
        public RectTransform crosshairBottom;
        public RectTransform crosshairLeft;
        public RectTransform crosshairRight;
        public RectTransform crosshairDot;

        [Header("Crosshair Settings")]
        public float crosshairGap = 6f;    // base gap from center
        public float crosshairLength = 8f;    // line length
        public float crosshairThickness = 2f;
        public Color crosshairColor = new Color(1f, 1f, 1f, 0.9f);
        public Color crosshairLowAmmo = new Color(1f, 0.3f, 0.2f, 0.9f);
        public float spreadMultiplier = 40f;   // how much spread affects crosshair

        [Header("Ammo")]
        public TMP_Text ammoCurrentText;
        public TMP_Text ammoMaxText;
        public TMP_Text fireModeText;

        [Header("Armor & Health")]
        public Slider healthBar;
        public Slider armorBar;
        public TMP_Text healthText;
        public TMP_Text armorText;

        [Header("Reload Bar")]
        public GameObject reloadBarRoot;
        public Slider reloadBar;
        public TMP_Text reloadText;

        [Header("Hit Vignette")]
        public Image hitVignette;
        public Image deathVignette;

        // ---------- Private --------------------------------------------------

        private float _crosshairSpread;
        private float _vignetteAlpha;

        // ---------- Unity lifecycle ------------------------------------------

        private void Start()
        {
            if (combat == null) combat = FindFirstObjectByType<PlayerCombat>();
            if (health == null) health = FindFirstObjectByType<PlayerHealth>();

            if (health != null)
                health.onHit.AddListener(OnHit);

            if (reloadBarRoot != null)
                reloadBarRoot.SetActive(false);

            // Auto-find crosshair children by name if not assigned
            AutoFindCrosshair();
        }

        private void AutoFindCrosshair()
        {
            if (crosshairTop == null) crosshairTop = FindChild("Top");
            if (crosshairBottom == null) crosshairBottom = FindChild("Bot");
            if (crosshairLeft == null) crosshairLeft = FindChild("Left");
            if (crosshairRight == null) crosshairRight = FindChild("Right");
            if (crosshairDot == null) crosshairDot = FindChild("Dot");
        }

        private RectTransform FindChild(string childName)
        {
            var t = transform.Find(childName)
                 ?? transform.Find("Crosshair/" + childName);
            return t != null ? t.GetComponent<RectTransform>() : null;
        }

        private void Update()
        {
            UpdateCrosshair();
            UpdateAmmo();
            UpdateHealthArmor();
            UpdateReloadBar();
            UpdateVignette();
        }

        // ---------- Crosshair ------------------------------------------------

        private void UpdateCrosshair()
        {
            if (combat == null) return;

            // Spread grows when moving/sprinting, shrinks when still/ADS
            float targetSpread = 0f;

            var controller = combat.GetComponent<PlayerController>();
            if (controller != null)
            {
                if (controller.IsSprinting) targetSpread = combat.sprintSpread;
                else if (controller.IsMoving) targetSpread = combat.moveSpread;
                else if (controller.IsCrouching
                      || controller.IsProne) targetSpread = combat.baseSpread * 0.3f;
                else targetSpread = combat.baseSpread;
            }

            if (combat.IsADS) targetSpread *= (1f - combat.adsAccuracyBonus);

            _crosshairSpread = Mathf.Lerp(_crosshairSpread, targetSpread,
                Time.deltaTime * 12f);

            float offset = crosshairGap + _crosshairSpread * spreadMultiplier;

            // Color -- red when low ammo
            Color col = combat.CurrentAmmo <= 5
                ? crosshairLowAmmo : crosshairColor;

            // Hide crosshair dot when ADS
            if (crosshairDot != null)
                crosshairDot.gameObject.SetActive(!combat.IsADS);

            SetLine(crosshairTop, 0f, offset, crosshairThickness, crosshairLength, col);
            SetLine(crosshairBottom, 0f, -offset, crosshairThickness, crosshairLength, col);
            SetLine(crosshairLeft, -offset, 0f, crosshairLength, crosshairThickness, col);
            SetLine(crosshairRight, offset, 0f, crosshairLength, crosshairThickness, col);
        }

        private void SetLine(RectTransform rt, float x, float y, float w, float h, Color col)
        {
            if (rt == null) return;
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
            var img = rt.GetComponent<Image>();
            if (img != null) img.color = col;
        }

        // ---------- Ammo -----------------------------------------------------

        private void UpdateAmmo()
        {
            if (combat == null) return;

            if (ammoCurrentText != null)
            {
                ammoCurrentText.text = combat.CurrentAmmo.ToString();
                ammoCurrentText.color = combat.CurrentAmmo <= 5
                    ? crosshairLowAmmo : Color.white;
            }

            if (ammoMaxText != null)
                ammoMaxText.text = "/ " + combat.magazineSize;

            if (fireModeText != null)
                fireModeText.text = combat.CurrentFireMode switch
                {
                    PlayerCombat.FireMode.Single => "SINGLE",
                    PlayerCombat.FireMode.Burst => "BURST",
                    _ => "AUTO"
                };
        }

        // ---------- Health & Armor -------------------------------------------

        private void UpdateHealthArmor()
        {
            if (health == null) return;

            if (healthBar != null) healthBar.value = health.HealthPercent;
            if (armorBar != null) armorBar.value = health.ArmorPercent;

            if (healthText != null)
                healthText.text = Mathf.CeilToInt(health.CurrentHealth).ToString();

            if (armorText != null)
                armorText.text = health.armorType == ArmorType.None
                    ? "--"
                    : Mathf.CeilToInt(health.CurrentArmor).ToString();
        }

        // ---------- Reload bar -----------------------------------------------

        private void UpdateReloadBar()
        {
            if (combat == null || reloadBarRoot == null) return;

            reloadBarRoot.SetActive(combat.IsReloading);

            if (!combat.IsReloading) return;

            // Access reload timer via reflection-free approach -- expose from PlayerCombat
            float progress = combat.ReloadProgress;
            if (reloadBar != null) reloadBar.value = progress;
            if (reloadText != null) reloadText.text = "RELOADING";
        }

        // ---------- Vignette -------------------------------------------------

        private void OnHit(DamageInfo info)
        {
            if (!info.isSuppression) _vignetteAlpha = 0.7f;
        }

        private void UpdateVignette()
        {
            if (hitVignette != null)
            {
                float lowHp = health != null
                    ? Mathf.Clamp01(1f - health.HealthPercent * 2f) * 0.4f
                    : 0f;

                _vignetteAlpha = Mathf.Max(0f, _vignetteAlpha - Time.deltaTime * 2.5f);
                float alpha = Mathf.Max(lowHp, _vignetteAlpha);

                var c = hitVignette.color; c.a = alpha;
                hitVignette.color = c;
            }

            if (deathVignette != null && health != null)
            {
                var c = deathVignette.color;
                c.a = health.IsDead ? 1f : 0f;
                deathVignette.color = c;
            }
        }
    }
}