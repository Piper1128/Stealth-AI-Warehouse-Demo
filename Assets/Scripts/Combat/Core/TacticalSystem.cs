using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Central orchestrator for the tactical pipeline.
    /// Manages providers, scorers, and processes TacticalRequests.
    ///
    /// Flow:
    ///   1. Unit submits TacticalRequest with context
    ///   2. TacticalSystem runs all providers to gather candidates
    ///   3. All scorers evaluate each candidate (coroutine -- spread over frames)
    ///   4. Best spot selected and callback fired
    ///   5. Unit executes chosen spot
    ///
    /// One TacticalSystem per scene -- attach to HuntDirector GameObject.
    /// </summary>
    [AddComponentMenu("StealthHuntAI/Tactical System")]
    public class TacticalSystem : MonoBehaviour
    {
        // ---------- Singleton ------------------------------------------------

        public static TacticalSystem Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            BuildDefaultPipeline();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ---------- Inspector ------------------------------------------------

        [Header("Providers")]
        public bool UseCoverProvider = true;
        public bool UseFlankProvider = true;
        public bool UseVantageProvider = true;
        public bool UseCornerEdgeProvider = true;
        public bool UsePincerProvider = true;
        public bool UseTacticalZoneProvider = true;

        [Header("Performance")]
        [Range(1, 8)] public int MaxRequestsPerFrame = 1; // 1 per frame -- spread load across 14 guards
        [Range(0f, 1f)] public float ScoreThreshold = 0.1f; // discard spots below this

        [Header("Debug")]
        public bool ShowCandidateGizmos = true;
        public bool LogDecisions = false;

        // ---------- Pipeline -------------------------------------------------

        private readonly List<ITacticalProvider> _providers = new List<ITacticalProvider>();
        private readonly List<ITacticalScorer> _scorers = new List<ITacticalScorer>();
        private readonly Queue<TacticalRequest> _queue = new Queue<TacticalRequest>();

        // Inspector access
        public IReadOnlyList<ITacticalProvider> Providers => _providers;
        public IReadOnlyList<ITacticalScorer> Scorers => _scorers;
        public int QueueDepth => _queue.Count;
        public TacticalRequest LastRequest { get; private set; }
        public List<TacticalSpot> LastCandidates { get; private set; }
        public TacticalSpot LastBestSpot { get; private set; }

        // Scorer instances -- shared across all requests
        public CoverQualityScorer CoverQuality = new CoverQualityScorer();
        public AdvanceScorer Advance = new AdvanceScorer();
        public FlankAngleScorer FlankAngle = new FlankAngleScorer();
        public HighGroundScorer HighGround = new HighGroundScorer();
        public CrossfireScorer Crossfire = new CrossfireScorer();
        public SquadSeparationScorer SquadSeparation = new SquadSeparationScorer();
        public PlayerGazeScorer PlayerGaze = new PlayerGazeScorer();
        public ShadowScorer Shadow = new ShadowScorer();
        public HeatMapScorer HeatMap = new HeatMapScorer();
        public NoveltyScorer Novelty = new NoveltyScorer();

        public void BuildDefaultPipeline()
        {
            _providers.Clear();
            if (UseCoverProvider) _providers.Add(new CoverProvider());
            if (UseFlankProvider) _providers.Add(new FlankProvider());
            if (UseVantageProvider) _providers.Add(new VantageProvider());
            if (UseCornerEdgeProvider) _providers.Add(new CornerEdgeProvider());
            if (UsePincerProvider) _providers.Add(new PincerProvider());
            if (UseTacticalZoneProvider) _providers.Add(new TacticalZoneProvider());

            _scorers.Clear();
            _scorers.Add(CoverQuality);
            _scorers.Add(Advance);
            _scorers.Add(FlankAngle);
            _scorers.Add(HighGround);
            _scorers.Add(Crossfire);
            _scorers.Add(SquadSeparation);
            _scorers.Add(PlayerGaze);
            _scorers.Add(Shadow);
            _scorers.Add(HeatMap);
            _scorers.Add(Novelty);
        }

        // ---------- Request queue --------------------------------------------

        internal void Enqueue(TacticalRequest request)
            => _queue.Enqueue(request);

        private void Update()
        {
            int processed = 0;
            while (_queue.Count > 0 && processed < MaxRequestsPerFrame)
            {
                var req = _queue.Dequeue();
                if (req.State == TacticalRequest.RequestState.Cancelled) continue;
                StartCoroutine(ProcessRequest(req));
                processed++;
            }
        }

        // ---------- Processing -----------------------------------------------

        private IEnumerator ProcessRequest(TacticalRequest req)
        {
            req.SetScoring();

            var ctx = req.Context;
            var candidates = GatherCandidates(ctx);

            if (candidates.Count == 0)
            {
                req.Complete(candidates, null);
                yield break;
            }

            // Score all candidates
            ScoreAll(candidates, ctx);

            // Sort by score descending
            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

            // Pick best valid spot
            TacticalSpot best = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Score >= ScoreThreshold)
                {
                    best = candidates[i];
                    break;
                }
            }

            // Reserve spot
            if (best != null)
            {
                best.Reserve(ctx.Unit);
                Novelty.RecordVisit(ctx.Unit, best.Position);

                if (LogDecisions)
                    Debug.Log("[TacticalSystem] " + ctx.Unit.name + " -> " + best.ProviderTag +
                              " score=" + best.Score.ToString("F2") + " pos=" + best.Position);
            }

            LastRequest = req;
            LastCandidates = candidates;
            LastBestSpot = best;
            req.Complete(candidates, best);
            yield break;
        }

        // ---------- Gather candidates ----------------------------------------

        private List<TacticalSpot> GatherCandidates(TacticalContext ctx)
        {
            var all = new List<TacticalSpot>();

            for (int p = 0; p < _providers.Count; p++)
            {
                var provider = _providers[p];
                if (!provider.IsEnabled) continue;

                var spots = provider.GetSpots(ctx);
                if (spots == null) continue;

                // Filter reserved spots
                for (int i = 0; i < spots.Count; i++)
                {
                    var spot = spots[i];
                    if (IsReservedByOther(spot, ctx.Unit)) continue;
                    all.Add(spot);
                }
            }

            return all;
        }

        private bool IsReservedByOther(TacticalSpot spot, StealthHuntAI unit)
        {
            if (!spot.IsReserved) return false;
            return spot.ReservedBy != unit;
        }

        // ---------- Score all ------------------------------------------------

        private void ScoreAll(List<TacticalSpot> candidates, TacticalContext ctx)
        {
            float totalWeight = 0f;
            for (int s = 0; s < _scorers.Count; s++)
                if (_scorers[s].IsEnabled) totalWeight += _scorers[s].Weight;

            if (totalWeight <= 0f) return;

            for (int i = 0; i < candidates.Count; i++)
            {
                var spot = candidates[i];
                float weightedSum = 0f;

                for (int s = 0; s < _scorers.Count; s++)
                {
                    var scorer = _scorers[s];
                    if (!scorer.IsEnabled) continue;

                    float raw = scorer.Score(spot, ctx);
                    float weighted = raw * scorer.Weight;
                    weightedSum += weighted;

                    // Store breakdown for inspector
                    spot.ScoreBreakdown[scorer.Name] = raw;
                }

                spot.Score = weightedSum / totalWeight;

                // Mark rejection reason for inspector
                if (spot.Score < ScoreThreshold)
                    spot.RejectionReason = FindWorstScorer(spot);
            }
        }

        private string FindWorstScorer(TacticalSpot spot)
        {
            string worst = "";
            float worstVal = float.MaxValue;
            foreach (var kv in spot.ScoreBreakdown)
                if (kv.Value < worstVal) { worstVal = kv.Value; worst = kv.Key; }
            return worst;
        }

        // ---------- Public API -----------------------------------------------

        /// <summary>Add a custom provider to the pipeline.</summary>
        public void AddProvider(ITacticalProvider provider)
            => _providers.Add(provider);

        /// <summary>Add a custom scorer to the pipeline.</summary>
        public void AddScorer(ITacticalScorer scorer)
            => _scorers.Add(scorer);

        /// <summary>Remove a provider by tag.</summary>
        public void RemoveProvider(string tag)
            => _providers.RemoveAll(p => p.Tag == tag);

        /// <summary>
        /// Synchronous fallback -- runs entire pipeline on main thread.
        /// Use for simple projects or when async timing doesn't matter.
        /// </summary>
        // Cache last sync result per unit -- avoid full evaluation every replan
        private readonly Dictionary<StealthHuntAI, (TacticalSpot spot, float time)>
            _syncCache = new Dictionary<StealthHuntAI, (TacticalSpot, float)>();
        private const float SyncCacheInterval = 1.2f;

        public TacticalSpot EvaluateSync(TacticalContext ctx)
        {
            var candidates = GatherCandidates(ctx);
            if (candidates.Count == 0) return null;

            ScoreAll(candidates, ctx);
            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

            var best = candidates.Count > 0 && candidates[0].Score >= ScoreThreshold
                ? candidates[0] : null;

            if (best != null)
            {
                best.Reserve(ctx.Unit);
                Novelty.RecordVisit(ctx.Unit, best.Position);
            }

            return best;
        }

        // ---------- Gizmos ---------------------------------------------------

        private void OnDrawGizmos()
        {
            if (!ShowCandidateGizmos || !Application.isPlaying) return;
            // Gizmo drawing handled by TacticalInspector overlay
        }
    }
}