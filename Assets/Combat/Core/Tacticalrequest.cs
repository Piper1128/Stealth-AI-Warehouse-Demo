using System;
using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Formation types for squad movement coordination.
    /// </summary>
    public enum FormationType
    {
        None,
        Wedge,      // leader front, two flankers behind
        Line,       // side by side
        File,       // single file behind leader
        Diamond,    // leader, two sides, rear
        Vee,        // two forward, one rear
        Overwatch,  // one advances, one covers
    }

    /// <summary>
    /// An async tactical request. Units submit a request and get a callback
    /// when the best spot has been scored. This prevents blocking the main thread
    /// during heavy scoring calculations.
    ///
    /// Usage:
    ///   var req = TacticalRequest.Submit(context, OnSpotReady);
    ///
    ///   void OnSpotReady(TacticalRequest req) {
    ///       MoveToSpot(req.BestSpot);
    ///   }
    /// </summary>
    public class TacticalRequest
    {
        // ---------- State ----------------------------------------------------

        public enum RequestState { Pending, Scoring, Complete, Cancelled }

        public RequestState State { get; private set; } = RequestState.Pending;
        public bool IsComplete => State == RequestState.Complete;
        public bool IsPending => State == RequestState.Pending
                                       || State == RequestState.Scoring;

        // ---------- Data -----------------------------------------------------

        public TacticalContext Context { get; }
        public List<TacticalSpot> Candidates { get; private set; }
        public TacticalSpot BestSpot { get; private set; }
        public float SubmitTime { get; }
        public float CompleteTime { get; private set; }
        public float ElapsedMs => (CompleteTime - SubmitTime) * 1000f;

        // ---------- Callback -------------------------------------------------

        private readonly Action<TacticalRequest> _callback;

        // ---------- Constructor ----------------------------------------------

        private TacticalRequest(TacticalContext context,
                                 Action<TacticalRequest> callback)
        {
            Context = context;
            _callback = callback;
            SubmitTime = Time.time;
        }

        // ---------- Lifecycle ------------------------------------------------

        public static TacticalRequest Submit(TacticalContext context,
                                              Action<TacticalRequest> callback)
        {
            var req = new TacticalRequest(context, callback);
            TacticalSystem.Instance?.Enqueue(req);
            return req;
        }

        internal void SetScoring()
            => State = RequestState.Scoring;

        internal void Complete(List<TacticalSpot> candidates, TacticalSpot best)
        {
            Candidates = candidates;
            BestSpot = best;
            CompleteTime = Time.time;
            State = RequestState.Complete;
            _callback?.Invoke(this);
        }

        internal void Cancel()
            => State = RequestState.Cancelled;

        // ---------- Debug ----------------------------------------------------

        public override string ToString()
            => $"TacticalRequest [{State}] unit={Context.Unit?.name} " +
               $"candidates={Candidates?.Count ?? 0} " +
               $"best={BestSpot?.Score:F2} elapsed={ElapsedMs:F1}ms";
    }
}