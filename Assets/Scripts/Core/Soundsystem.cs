using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI
{
    /// <summary>
    /// Handles sound propagation via raycast and NavMesh path tracing.
    /// Extracted from HuntDirector -- pure static utility, no MonoBehaviour.
    /// </summary>
    public static class SoundSystem
    {
        private const float NavMeshSoundThreshold = 0.5f;

        private static NavMeshPath _soundPath;
        private static readonly RaycastHit[] _raycastBuffer = new RaycastHit[16];
        private static LayerMask _sightBlockerMask = 0;
        private static bool _sightBlockerSet = false;

        public static void RegisterSightBlockers(LayerMask mask)
        {
            _sightBlockerMask = mask;
            _sightBlockerSet = true;
        }

        public static void Broadcast(Vector3 position, float intensity, float radius)
        {
            if (_soundPath == null) _soundPath = new NavMeshPath();

            var units = UnitRegistry.AllUnits;
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (unit == null) continue;

                Vector3 unitPos = unit.transform.position + Vector3.up * 0.5f;
                float dist = Vector3.Distance(unitPos, position);
                if (dist > radius) continue;

                float scaledIntensity;
                Vector3 arrivalDir;

                if (intensity < NavMeshSoundThreshold)
                    PropagateRaycast(position, unitPos, dist, radius, intensity,
                        out scaledIntensity, out arrivalDir);
                else
                    PropagateNavMesh(position, unitPos, dist, radius, intensity,
                        out scaledIntensity, out arrivalDir);

                if (scaledIntensity <= 0.01f) continue;

                unit.GetComponent<AwarenessSensor>()
                    ?.HearSound(position, scaledIntensity, arrivalDir);

                if (intensity >= 0.8f && dist <= 12f)
                    unit.OnNearShotHeard(position, scaledIntensity);
            }
        }

        public static void BroadcastWithCurve(Vector3 position, float intensity,
            AnimationCurve falloffCurve, float radius)
        {
            if (_soundPath == null) _soundPath = new NavMeshPath();

            var units = UnitRegistry.AllUnits;
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (unit == null) continue;

                Vector3 unitPos = unit.transform.position + Vector3.up * 0.5f;
                float dist = Vector3.Distance(unitPos, position);
                if (dist > radius) continue;

                float scaledIntensity;
                Vector3 arrivalDir;

                PropagateNavMesh(position, unitPos, dist, radius, intensity,
                    out scaledIntensity, out arrivalDir);

                if (falloffCurve != null)
                    scaledIntensity *= falloffCurve.Evaluate(dist / radius);

                if (scaledIntensity <= 0.01f) continue;

                unit.GetComponent<AwarenessSensor>()
                    ?.HearSound(position, scaledIntensity, arrivalDir);

                if (intensity >= 0.8f && dist <= 12f)
                    unit.OnNearShotHeard(position, scaledIntensity);
            }
        }

        private static void PropagateRaycast(
            Vector3 soundPos, Vector3 unitPos,
            float dist, float radius, float intensity,
            out float scaledIntensity, out Vector3 arrivalDir)
        {
            float falloff = 1f - Mathf.Clamp01(dist / radius);
            scaledIntensity = intensity * falloff;
            arrivalDir = Vector3.zero;

            Vector3 toSound = soundPos - unitPos;
            int hitCount = Physics.RaycastNonAlloc(
                unitPos, toSound.normalized, _raycastBuffer, dist, _sightBlockerMask);

            int walls = 0;
            for (int h = 0; h < hitCount; h++) walls++;

            if (walls >= 2) scaledIntensity *= 0.05f;
            else if (walls == 1) scaledIntensity *= 0.25f;
        }

        private static void PropagateNavMesh(
            Vector3 soundPos, Vector3 unitPos,
            float dist, float radius, float intensity,
            out float scaledIntensity, out Vector3 arrivalDir)
        {
            arrivalDir = Vector3.zero;

            Vector3 toSound = soundPos - unitPos;
            LayerMask mask = _sightBlockerSet
                ? _sightBlockerMask : LayerMask.GetMask("Default");
            bool hasLine = !Physics.Raycast(unitPos, toSound.normalized, dist, mask);

            if (hasLine)
            {
                scaledIntensity = intensity * (1f - Mathf.Clamp01(dist / radius));
                return;
            }

            bool pathFound = NavMesh.CalculatePath(
                unitPos, soundPos, NavMesh.AllAreas, _soundPath);

            if (!pathFound || _soundPath.status == NavMeshPathStatus.PathInvalid)
            {
                scaledIntensity = intensity * (1f - Mathf.Clamp01(dist / radius)) * 0.05f;
                return;
            }

            int corners = Mathf.Max(0, _soundPath.corners.Length - 2);

            if (_soundPath.corners.Length >= 2)
                arrivalDir = (_soundPath.corners[1] - unitPos).normalized;

            float pathLength = 0f;
            for (int c = 1; c < _soundPath.corners.Length; c++)
                pathLength += Vector3.Distance(_soundPath.corners[c - 1],
                                               _soundPath.corners[c]);

            float pathFalloff = 1f - Mathf.Clamp01(pathLength / radius);
            float cornerBase = intensity >= 0.8f ? 0.8f : 0.55f;
            float cornerPenalty = Mathf.Pow(cornerBase, corners);

            scaledIntensity = intensity * pathFalloff * cornerPenalty;
        }
    }
}