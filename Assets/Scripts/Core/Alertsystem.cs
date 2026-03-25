using UnityEngine;
using UnityEngine.Events;

namespace StealthHuntAI
{
    /// <summary>
    /// Manages scene-wide alert level and tension.
    /// Driven by HuntDirector.Update -- owns the state, not the MonoBehaviour.
    /// </summary>
    public class AlertSystem
    {
        // ---------- Config (set from HuntDirector inspector) -----------------

        public float TensionDecaySpeed = 0.08f;
        public float TensionRiseSpeed = 0.15f;
        public float TensionZoneRadius = 20f;
        public float ProximityWeight = 0.6f;
        public float EvasionWeight = 0.4f;
        public float EvasionTimeToMax = 30f;
        public float AlertCooldownTime = 8f;
        public float CautionCooldownTime = 12f;
        public float CautionRiseMultiplier = 1.4f;
        public float AlertRiseMultiplier = 2.0f;
        public bool ScaleSpeedWithAlert = true;
        public float CautionSpeedMultiplier = 1.1f;
        public float AlertSpeedMultiplier = 1.25f;
        public float MinIntelConfidence = 0.3f;
        public float NudgeThreshold = 0.6f;

        // ---------- Events ---------------------------------------------------

        public UnityEvent OnNormal = new UnityEvent();
        public UnityEvent OnCaution = new UnityEvent();
        public UnityEvent OnAlert = new UnityEvent();

        // ---------- State ----------------------------------------------------

        public SceneAlertLevel Level { get; private set; } = SceneAlertLevel.Normal;
        public float Tension { get; private set; }
        public float Evasion { get; private set; }

        private float _alertCooldown;
        private float _cautionCooldown;

        // ---------- Update ---------------------------------------------------

        public void Tick(float dt, StealthTarget target,
                         System.Collections.Generic.IReadOnlyList<StealthHuntAI> units,
                         System.Collections.Generic.IReadOnlyList<SquadBlackboard> squads)
        {
            TickTension(dt, target, units);
            TickAlertLevel(dt, units, squads);
        }

        private void TickTension(float dt, StealthTarget target,
            System.Collections.Generic.IReadOnlyList<StealthHuntAI> units)
        {
            if (target == null || !target.IsActive)
            {
                Tension = Mathf.MoveTowards(Tension, 0f, TensionDecaySpeed * 2f * dt);
                return;
            }

            for (int i = 0; i < units.Count; i++)
            {
                if (units[i] == null) continue;
                if (units[i].CurrentAlertState == AlertState.Hostile)
                {
                    Evasion = 0f;
                    Tension = Mathf.MoveTowards(Tension, 0f, TensionDecaySpeed * 3f * dt);
                    return;
                }
            }

            bool anyDirectContact = false;
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i] == null) continue;
                var sensor = units[i].GetComponent<AwarenessSensor>();
                if (sensor != null && sensor.CanSeeTarget) { anyDirectContact = true; break; }
            }

            if (anyDirectContact) Evasion = 0f;
            else Evasion += dt;

            float proximityFactor = 0f;
            if (ProximityWeight > 0f)
            {
                float closest = float.MaxValue;
                for (int i = 0; i < units.Count; i++)
                {
                    if (units[i] == null) continue;
                    Vector3 u = units[i].transform.position;
                    Vector3 t = target.Position;
                    float d = Mathf.Sqrt((u.x - t.x) * (u.x - t.x) + (u.z - t.z) * (u.z - t.z));
                    if (d < closest) closest = d;
                }
                proximityFactor = 1f - Mathf.Clamp01(closest / TensionZoneRadius);
            }

            float evasionFactor = EvasionWeight > 0f
                ? Mathf.Clamp01(Evasion / EvasionTimeToMax) : 0f;

            float blended = proximityFactor * ProximityWeight + evasionFactor * EvasionWeight;
            Tension = Mathf.MoveTowards(Tension, 1f,
                TensionRiseSpeed * Mathf.Max(0.1f, blended) * dt);
        }

        private void TickAlertLevel(float dt,
            System.Collections.Generic.IReadOnlyList<StealthHuntAI> units,
            System.Collections.Generic.IReadOnlyList<SquadBlackboard> squads)
        {
            bool anyHostile = false, anySuspicious = false;
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i] == null) continue;
                if (units[i].CurrentAlertState == AlertState.Hostile) anyHostile = true;
                else if (units[i].CurrentAlertState == AlertState.Suspicious) anySuspicious = true;
            }

            SceneAlertLevel desired;
            if (anyHostile)
            {
                desired = SceneAlertLevel.Alert;
                _alertCooldown = 0f; _cautionCooldown = 0f;
            }
            else if (anySuspicious)
            {
                if (Level == SceneAlertLevel.Alert)
                {
                    _alertCooldown += dt;
                    desired = _alertCooldown >= AlertCooldownTime
                        ? SceneAlertLevel.Caution : SceneAlertLevel.Alert;
                }
                else { desired = SceneAlertLevel.Caution; _cautionCooldown = 0f; }
            }
            else
            {
                if (Level == SceneAlertLevel.Alert)
                {
                    _alertCooldown += dt;
                    if (_alertCooldown >= AlertCooldownTime) Level = SceneAlertLevel.Caution;
                    desired = Level;
                }
                else if (Level == SceneAlertLevel.Caution)
                {
                    _cautionCooldown += dt;
                    desired = _cautionCooldown >= CautionCooldownTime
                        ? SceneAlertLevel.Normal : SceneAlertLevel.Caution;
                }
                else desired = SceneAlertLevel.Normal;
            }

            if (desired != Level)
            {
                Level = desired;
                OnLevelChanged(Level, units, squads);
            }
        }

        private void OnLevelChanged(SceneAlertLevel level,
            System.Collections.Generic.IReadOnlyList<StealthHuntAI> units,
            System.Collections.Generic.IReadOnlyList<SquadBlackboard> squads)
        {
            switch (level)
            {
                case SceneAlertLevel.Normal: OnNormal.Invoke(); break;
                case SceneAlertLevel.Caution: OnCaution.Invoke(); break;
                case SceneAlertLevel.Alert: OnAlert.Invoke(); break;
            }

            for (int i = 0; i < units.Count; i++)
                ApplyToUnit(units[i], level);

            if (level == SceneAlertLevel.Alert)
            {
                for (int u = 0; u < units.Count; u++)
                {
                    if (units[u] == null) continue;
                    for (int s = 0; s < squads.Count; s++)
                    {
                        if (squads[s] == null) continue;
                        if (squads[s].SharedConfidence > MinIntelConfidence)
                            units[u].ReceiveSquadIntel(
                                squads[s].SharedLastKnown,
                                squads[s].SharedConfidence * 0.5f);
                    }
                }
            }
        }

        private void ApplyToUnit(StealthHuntAI unit, SceneAlertLevel level)
        {
            if (unit == null) return;
            float riseM = 1f, speedM = 1f;
            switch (level)
            {
                case SceneAlertLevel.Caution:
                    riseM = CautionRiseMultiplier;
                    speedM = ScaleSpeedWithAlert ? CautionSpeedMultiplier : 1f;
                    break;
                case SceneAlertLevel.Alert:
                    riseM = AlertRiseMultiplier;
                    speedM = ScaleSpeedWithAlert ? AlertSpeedMultiplier : 1f;
                    break;
            }
            unit.ApplyAlertLevelEffects(riseM, speedM);
        }
    }
}