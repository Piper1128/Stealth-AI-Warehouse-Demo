using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Shared threat intel for a squad.
    /// Extracted from TacticalBrain -- owns shared threat model only.
    /// Guards report sightings here; guards without LOS pull from here.
    /// </summary>
    public class SquadIntel
    {
        public ThreatModel Threat { get; } = new ThreatModel();

        public void Report(Vector3 playerPos, Vector3 playerVel, float confidence)
            => Threat.ReceiveIntel(playerPos, playerVel, confidence);

        public void UpdateNoSight() => Threat.UpdateWithoutSight();

        // Convenience passthrough
        public bool HasIntel => Threat.HasIntel;
        public Vector3 EstimatedPos => Threat.EstimatedPosition;
        public float Confidence => Threat.Confidence;
    }
}