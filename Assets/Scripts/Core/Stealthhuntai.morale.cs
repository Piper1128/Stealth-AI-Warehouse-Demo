using UnityEngine;

namespace StealthHuntAI
{
    public partial class StealthHuntAI
    {
        // ---------- Morale system ---------------------------------------------

        private void LoadMorale()
        {
            MoraleLevel = startingMorale;

            if (persistMorale)
            {
                string key = MoralePrefsPrefix + gameObject.name;
                if (PlayerPrefs.HasKey(key))
                    MoraleLevel = PlayerPrefs.GetFloat(key, startingMorale);
            }

            ApplyMoraleModifiers();
        }

        private void SaveMorale()
        {
            if (!persistMorale) return;
            string key = MoralePrefsPrefix + gameObject.name;
            PlayerPrefs.SetFloat(key, MoraleLevel);
            PlayerPrefs.Save();
        }

        private void ModifyMorale(float delta)
        {
            MoraleLevel = Mathf.Clamp01(MoraleLevel + delta);
            ApplyMoraleModifiers();
            SaveMorale();
        }

        /// <summary>
        /// Apply morale level to runtime sensor and agent parameters.
        /// Scales from base values stored at startup -- never writes to
        /// inspector-visible serialized fields, so inspector stays clean.
        /// </summary>
        private void ApplyMoraleModifiers()
        {
            if (_sensor == null || _movement == null) return;

            // Base values must exist before we can scale them
            if (_baseAgentSpeed <= 0f) return;

            bool isCautious = personality == Personality.Cautious
                           || personality == Personality.Balanced;

            switch (CurrentMorale)
            {
                case MoraleState.High:
                    _sensor.riseSpeed = _baseSensorRiseSpeed;
                    _sensor.decaySpeed = _baseSensorDecaySpeed;
                    ApplySpeedToMovement(_baseAgentSpeed);
                    _sensor.suspicionThresh = _baseSuspicionThreshold;
                    _sensor.hostileThresh = _baseHostileThreshold;
                    _sensor.searchDur = _baseSearchDuration;
                    break;

                case MoraleState.Medium:
                    if (isCautious)
                    {
                        _sensor.riseSpeed = _baseSensorRiseSpeed * 0.75f;
                        _sensor.decaySpeed = _baseSensorDecaySpeed * 1.30f;
                        ApplySpeedToMovement(_baseAgentSpeed * 0.85f);
                        _sensor.searchDur = _baseSearchDuration * 1.20f;
                    }
                    else
                    {
                        _sensor.riseSpeed = _baseSensorRiseSpeed * 1.20f;
                        ApplySpeedToMovement(_baseAgentSpeed * 1.10f);
                        _sensor.searchDur = _baseSearchDuration;
                    }
                    _sensor.suspicionThresh = _baseSuspicionThreshold;
                    _sensor.hostileThresh = _baseHostileThreshold;
                    break;

                case MoraleState.Low:
                    if (isCautious)
                    {
                        _sensor.riseSpeed = _baseSensorRiseSpeed * 0.50f;
                        _sensor.decaySpeed = _baseSensorDecaySpeed * 1.80f;
                        ApplySpeedToMovement(_baseAgentSpeed * 0.70f);
                        _sensor.searchDur = _baseSearchDuration * 0.70f;
                        _sensor.hostileThresh = Mathf.Min(
                            _baseHostileThreshold * 1.3f, 0.95f);
                        _sensor.suspicionThresh = _baseSuspicionThreshold;
                    }
                    else
                    {
                        _sensor.riseSpeed = _baseSensorRiseSpeed * 1.50f;
                        _sensor.decaySpeed = _baseSensorDecaySpeed * 0.60f;
                        ApplySpeedToMovement(_baseAgentSpeed * 1.25f);
                        _sensor.searchDur = _baseSearchDuration * 1.50f;
                        _sensor.hostileThresh = Mathf.Max(
                            _baseHostileThreshold * 0.7f, 0.30f);
                        _sensor.suspicionThresh = _baseSuspicionThreshold;
                    }
                    break;
            }
        }

        private void TickMoraleRecovery()
        {
            if (CurrentAlertState != AlertState.Passive) return;

            // Slowly recover morale while calm and uncontested
            _passiveTimer += Time.deltaTime;
            if (_passiveTimer >= 5f)
            {
                _passiveTimer = 0f;
                if (MoraleLevel < startingMorale)
                    ModifyMorale(0.02f);
            }
        }

        private float GetBaseSpeed()
        {
            // Return speed based on personality only
            // so ApplyMoraleModifiers always scales from a clean base
            switch (personality)
            {
                case Personality.Cautious: return 3.5f;
                case Personality.Aggressive: return 6.0f;
                default: return 4.5f;
            }
        }

        private void ApplySpeedToMovement(float speed)
        {
            if (_movement == null || !_movement.CanOverrideSpeed) return;
            _movement.Speed = speed;
        }

        /// <summary>Manually reset morale to starting value (e.g. new game).</summary>
        public void ResetMorale()
        {
            MoraleLevel = startingMorale;
            _timesLostTarget = 0;
            ApplyMoraleModifiers();

            if (persistMorale)
            {
                PlayerPrefs.DeleteKey(MoralePrefsPrefix + gameObject.name);
                PlayerPrefs.Save();
            }
        }

    }
}