using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Zone types that level designers can place in the scene.
    /// </summary>
    public enum TacticalZoneType
    {
        Ambush,     // guards prefer to wait here for player
        Defend,     // guards hold this area
        FlankRoute, // preferred flank corridor
        Avoid,      // guards avoid this area (fire, open ground etc)
    }

    /// <summary>
    /// Designer-placed volume that biases guard tactical decisions.
    /// Place in scene to create ambush points, defensive lines, or hazard zones.
    /// Guards with TacticalZoneProvider will prefer or avoid these zones.
    /// </summary>
    [AddComponentMenu("StealthHuntAI/Tactical Zone")]
    public class TacticalZone : MonoBehaviour
    {
        public TacticalZoneType ZoneType = TacticalZoneType.Ambush;
        [Range(0.1f, 5f)] public float Priority = 1f;
        public Color GizmoColor = new Color(0.2f, 0.8f, 0.4f, 0.3f);

        private static readonly List<TacticalZone> _all = new List<TacticalZone>();
        public static IReadOnlyList<TacticalZone> All => _all;

        private void OnEnable() => _all.Add(this);
        private void OnDisable() => _all.Remove(this);

        public bool Contains(Vector3 pos)
            => GetComponent<Collider>()?.bounds.Contains(pos) ?? false;

        private void OnDrawGizmos()
        {
            Gizmos.color = GizmoColor;
            var col = GetComponent<Collider>();
            if (col != null) Gizmos.DrawCube(col.bounds.center, col.bounds.size);
            Gizmos.color = new Color(GizmoColor.r, GizmoColor.g, GizmoColor.b, 1f);
            Gizmos.DrawWireCube(transform.position, transform.localScale);

#if UNITY_EDITOR
            UnityEditor.Handles.Label(
                transform.position + Vector3.up * 0.5f,
                ZoneType.ToString(),
                UnityEditor.EditorStyles.miniLabel);
#endif
        }
    }

    /// <summary>
    /// Provider that samples positions from designer-placed TacticalZones.
    /// Ambush/Defend/FlankRoute zones generate candidate spots.
    /// Avoid zones are handled by a scorer (not a provider).
    /// </summary>
    public class TacticalZoneProvider : ITacticalProvider
    {
        public string Tag => "TacticalZoneProvider";
        public bool IsEnabled { get; set; } = true;

        [Range(2, 8)] public int SamplesPerZone = 3;

        public List<TacticalSpot> GetSpots(TacticalContext ctx)
        {
            var spots = new List<TacticalSpot>();
            var zones = TacticalZone.All;

            for (int z = 0; z < zones.Count; z++)
            {
                var zone = zones[z];
                if (zone == null) continue;
                if (zone.ZoneType == TacticalZoneType.Avoid) continue;

                var col = zone.GetComponent<Collider>();
                if (col == null) continue;

                float distToUnit = Vector3.Distance(ctx.UnitPosition, zone.transform.position);
                if (distToUnit > ctx.SearchRadius * 1.5f) continue;

                // Sample random positions within zone bounds
                Bounds b = col.bounds;
                for (int i = 0; i < SamplesPerZone; i++)
                {
                    Vector3 candidate = new Vector3(
                        Random.Range(b.min.x, b.max.x),
                        b.center.y,
                        Random.Range(b.min.z, b.max.z));

                    if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit,
                        2f, ctx.NavMeshMask)) continue;

                    float dist = Vector3.Distance(ctx.UnitPosition, hit.position);
                    if (dist > ctx.SearchRadius) continue;

                    var spot = TacticalSpot.FromPosition(hit.position, Tag);
                    spot.DistanceToThreat = Vector3.Distance(hit.position, ctx.EstimatedThreatPos);
                    spot.FacingDirection = (ctx.EstimatedThreatPos - hit.position).normalized;

                    // Store zone type in provider tag for scorer access
                    spot.ProviderTag = Tag + ":" + zone.ZoneType;

                    spots.Add(spot);
                }
            }

            return spots;
        }
    }
}