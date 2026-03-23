using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.AI;
using static StealthHuntAI.Combat.CoverPoint;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Auto-detects cover positions in the scene using NavMesh sampling and raycasts.
    /// Runs as a coroutine at scene start so it doesn't block the main thread.
    ///
    /// Manual CoverPoint components placed in the scene always take priority.
    /// Add this component to the HuntDirector GameObject.
    /// </summary>
    [AddComponentMenu("StealthHuntAI/Cover Scanner")]
    public class CoverScanner : MonoBehaviour
    {
        [Header("Scanning")]
        [Tooltip("How many sample points to test per scan pass.")]
        [Range(50, 500)] public int samplesPerPass = 200;

        [Tooltip("Radius around scene center to scan.")]
        [Range(10f, 200f)] public float scanRadius = 60f;

        [Tooltip("Minimum distance between cover points.")]
        [Range(0.5f, 5f)] public float minSpacing = 2f;

        [Tooltip("Rescan interval in seconds. 0 = scan once at start only.")]
        [Range(0f, 60f)] public float rescanInterval = 0f;

        [Header("Cover Detection")]
        [Tooltip("Minimum obstacle height to count as cover.")]
        [Range(0.3f, 2f)] public float minCoverHeight = 0.6f;

        [Tooltip("Max distance to sample NavMesh from a candidate point.")]
        [Range(0.5f, 3f)] public float navMeshSampleRadius = 1.5f;

        [Tooltip("Layer mask for cover geometry raycasts.")]
        public LayerMask coverLayers = Physics.DefaultRaycastLayers;

        [Header("Debug")]
        public bool showScanGizmos = false;

        // ---------- Runtime --------------------------------------------------

        private readonly List<CoverPoint> _autoPoints = new List<CoverPoint>();
        private bool _scanning;

        public int AutoPointCount => _autoPoints.Count;
        public bool IsScanning => _scanning;

        // ---------- Unity lifecycle ------------------------------------------

        private void Start()
        {
            StartCoroutine(ScanRoutine());
        }

        private void OnDestroy()
        {
            ClearAutoPoints();
        }

        // ---------- Scanning -------------------------------------------------

        private IEnumerator ScanRoutine()
        {
            while (true)
            {
                yield return StartCoroutine(Scan());

                if (rescanInterval <= 0f) yield break;
                yield return new WaitForSeconds(rescanInterval);

                // Clear old auto-points before rescan
                ClearAutoPoints();
            }
        }

        // 8 compass directions precomputed
        private static readonly Vector3[] Dirs8 = new Vector3[8];

        private void Awake()
        {
            for (int i = 0; i < 8; i++)
            {
                float a = i * 45f * Mathf.Deg2Rad;
                Dirs8[i] = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            }
        }

        private IEnumerator Scan()
        {
            _scanning = true;

            Vector3 origin = transform.position;
            int batchSize = 20; // candidates per batch
            int tested = 0;

            // Pre-allocate candidates for this pass
            var candidates = new List<Vector3>(batchSize);

            while (tested < samplesPerPass)
            {
                candidates.Clear();

                // Collect a batch of valid NavMesh candidates
                int batchTarget = Mathf.Min(batchSize, samplesPerPass - tested);
                int attempts = 0;

                while (candidates.Count < batchTarget && attempts < batchTarget * 4)
                {
                    attempts++;
                    Vector2 rand = Random.insideUnitCircle * scanRadius;
                    Vector3 sample = origin + new Vector3(rand.x, 0f, rand.y);

                    if (!NavMesh.SamplePosition(sample, out NavMeshHit hit,
                        navMeshSampleRadius, NavMesh.AllAreas)) continue;

                    if (TooClose(hit.position)) continue;

                    candidates.Add(hit.position);
                }

                tested += batchTarget;

                if (candidates.Count == 0) { yield return null; continue; }

                // Build batched raycasts for all candidates
                // Per candidate: 8 low rays + 8 high rays + 8 peek rays = 24 rays
                int rayCount = candidates.Count * 24;
                var commands = new NativeArray<RaycastCommand>(rayCount, Allocator.TempJob);
                var results = new NativeArray<RaycastHit>(rayCount, Allocator.TempJob);

                QueryParameters qp = QueryParameters.Default;
                qp.layerMask = coverLayers;

                for (int c = 0; c < candidates.Count; c++)
                {
                    Vector3 pos = candidates[c];
                    int base0 = c * 24;

                    for (int d = 0; d < 8; d++)
                    {
                        // Low rays (cover height 0.1m)
                        commands[base0 + d] = new RaycastCommand(
                            pos + Vector3.up * 0.1f, Dirs8[d], qp, 1.2f);
                        // High rays (stand height 1.6m)
                        commands[base0 + 8 + d] = new RaycastCommand(
                            pos + Vector3.up * 1.6f, Dirs8[d], qp, 1.2f);
                        // Peek rays (1.0m height, 10m distance)
                        commands[base0 + 16 + d] = new RaycastCommand(
                            pos + Vector3.up * 1.0f, Dirs8[d], qp, 10f);
                    }
                }

                // Schedule batch -- all raycasts run in parallel on job threads
                var handle = RaycastCommand.ScheduleBatch(commands, results,
                    minCommandsPerJob: 4);

                // Yield until job completes -- no frame spike
                while (!handle.IsCompleted)
                    yield return null;
                handle.Complete();

                // Process results
                for (int c = 0; c < candidates.Count; c++)
                {
                    Vector3 pos = candidates[c];
                    int base0 = c * 24;

                    // Analyse low and high ray results
                    int wallCount = 0;
                    int cornerCount = 0;
                    float bestDist = -1f;
                    int bestDir = 0;

                    for (int d = 0; d < 8; d++)
                    {
                        bool lowHit = results[base0 + d].collider != null;
                        bool highHit = results[base0 + 8 + d].collider != null;
                        float peekD = results[base0 + 16 + d].collider != null
                            ? results[base0 + 16 + d].distance : 10f;

                        if (lowHit) wallCount++;
                        if (highHit) cornerCount++;
                        if (peekD > bestDist) { bestDist = peekD; bestDir = d; }
                    }

                    // Determine cover type
                    if (wallCount == 0 || wallCount >= 6) continue;

                    CoverType ct = cornerCount >= 2 ? CoverType.High
                                 : wallCount == 2 ? CoverType.Corner
                                 : CoverType.Low;

                    CreateCoverPoint(pos, ct, Dirs8[bestDir]);
                }

                commands.Dispose();
                results.Dispose();

                yield return null; // one frame between batches
            }

            _scanning = false;
        }

        private bool TooClose(Vector3 pos)
        {
            var points = HuntDirector.AllCoverPoints;
            for (int i = 0; i < points.Count; i++)
            {
                var cp = points[i] as CoverPoint;
                if (cp == null) continue;
                if (Vector3.Distance(pos, cp.transform.position) < minSpacing)
                    return true;
            }
            return false;
        }

        // ---------- Point management -----------------------------------------

        private void CreateCoverPoint(Vector3 pos, CoverType type, Vector3 peekDir)
        {
            var go = new GameObject("CoverPoint_Auto");
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(peekDir);

            var cp = go.AddComponent<CoverPoint>();
            cp.type = type;
            cp.isAutoGenerated = true;
            cp.peekDirection = Vector3.zero; // use transform.forward

            _autoPoints.Add(cp);
        }

        private void ClearAutoPoints()
        {
            for (int i = 0; i < _autoPoints.Count; i++)
                if (_autoPoints[i] != null)
                    Destroy(_autoPoints[i].gameObject);
            _autoPoints.Clear();
        }

        // ---------- Gizmos ---------------------------------------------------

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!showScanGizmos) return;

            Gizmos.color = new Color(0.5f, 1f, 0.5f, 0.1f);
            Gizmos.DrawWireSphere(transform.position, scanRadius);
        }
#endif
    }
}