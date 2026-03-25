using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI
{
    /// <summary>
    /// Markov flight prediction -- tracks player movement patterns
    /// to predict where they will flee next.
    /// Extracted from HuntDirector.
    /// </summary>
    public static class FlightMemory
    {
        private const int Sectors = 8;
        private const int MaxHistory = 12;
        private const float MarkovPrior = 0.1f;

        private static readonly float[,] _markov = new float[Sectors, Sectors];
        private static int _lastSector = -1;
        private static readonly Queue<Vector3> _history = new Queue<Vector3>();

        public static Vector3 PredictedFlightDir { get; private set; }
        public static int Observations { get; private set; }

        public static void RecordFlight(Vector3 flightVec)
        {
            if (flightVec.magnitude < 0.1f) return;

            int sector = ToSector(flightVec);
            if (_lastSector >= 0)
                _markov[_lastSector, sector] += 1f;
            _lastSector = sector;
            Observations++;

            _history.Enqueue(flightVec.normalized);
            while (_history.Count > MaxHistory) _history.Dequeue();

            UpdatePrediction();
        }

        public static void Reset()
        {
            System.Array.Clear(_markov, 0, _markov.Length);
            _history.Clear();
            _lastSector = -1;
            Observations = 0;
            PredictedFlightDir = Vector3.zero;
        }

        private static void UpdatePrediction()
        {
            if (Observations == 0) { PredictedFlightDir = Vector3.zero; return; }

            Vector3 markovPred = Vector3.zero;
            if (_lastSector >= 0)
            {
                float bestScore = -1f;
                int bestSec = 0;
                for (int i = 0; i < Sectors; i++)
                {
                    float score = _markov[_lastSector, i] + MarkovPrior;
                    if (score > bestScore) { bestScore = score; bestSec = i; }
                }
                markovPred = FromSector(bestSec);
            }

            Vector3 histPred = Vector3.zero;
            if (_history.Count > 0)
            {
                Vector3 sum = Vector3.zero; float total = 0f;
                var arr = _history.ToArray();
                for (int i = 0; i < arr.Length; i++)
                { float w = 1f + i; sum += arr[i] * w; total += w; }
                histPred = total > 0f ? (sum / total).normalized : Vector3.zero;
            }

            float markovW = Mathf.Clamp01(Observations / 10f) * 0.8f;
            Vector3 blended = markovPred * markovW + histPred * (1f - markovW);
            PredictedFlightDir = blended.magnitude > 0.01f
                ? blended.normalized : Vector3.zero;
        }

        private static int ToSector(Vector3 dir)
        {
            dir.y = 0f;
            float angle = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            if (angle < 0) angle += 360f;
            return Mathf.RoundToInt(angle / 45f) % Sectors;
        }

        private static Vector3 FromSector(int sector)
        {
            float angle = sector * 45f * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
        }
    }
}