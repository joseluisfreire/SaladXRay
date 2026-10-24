using System;
using System.Collections.Generic;
using System.Linq;

namespace SaladXRayPanel.Services
{
    public sealed class PollScheduler
    {
        private const int Period = 300;
        private const double PeriodTol = 10;     // s de jitter por ciclo
        private const int MaxSkip = 3;           // ciclos perdidos tolerados na mesma fase
        private const double ValueTol = 0.30;    // diferença relativa máxima de valor
        private const int MaxPhases = 2;
        private const int AliveMinutes = 15;

        private int _errors;
        private TimeSpan? _retryAfter;

        public void OnOk() { _errors = 0; _retryAfter = null; }
        public void OnError(TimeSpan? ra) { _errors++; _retryAfter = ra; }

        private sealed class Phase
        {
            public DateTime Last;
            public double Value;
            public int Count;
        }

        private static List<Phase> DetectPhases(IEnumerable<(DateTime Ts, double Value)> pulses)
        {
            var chains = new List<Phase>();

            foreach (var (ts, v) in pulses.OrderBy(p => p.Ts))
            {
                int bi = -1;
                double bestErr = double.MaxValue;

                for (int i = 0; i < chains.Count; i++)
                {
                    var c = chains[i];
                    double gap = (ts - c.Last).TotalSeconds;
                    int k = (int)Math.Round(gap / Period);
                    if (k < 1 || k > MaxSkip) continue;

                    double err = Math.Abs(gap - k * Period);
                    if (err > PeriodTol) continue;
                    if (Math.Abs(v - c.Value) > ValueTol * Math.Max(v, c.Value)) continue;

                    if (err < bestErr) { bestErr = err; bi = i; }
                }

                if (bi >= 0)
                {
                    var c = chains[bi];
                    c.Last = ts; c.Value = v; c.Count++;
                }
                else
                {
                    chains.Add(new Phase { Last = ts, Value = v, Count = 1 });
                }
            }

            if (chains.Count == 0) return chains;

            var newest = chains.Max(c => c.Last);
            return chains
                .OrderByDescending(c => (newest - c.Last).TotalMinutes <= AliveMinutes)
                .ThenByDescending(c => c.Count)
                .ThenByDescending(c => c.Last)
                .Take(MaxPhases)
                .ToList();
        }

        private static TimeSpan PhaseDelay(Phase p, DateTime now, DateTime newest)
        {
            double elapsed = (now - p.Last).TotalSeconds;
            int k = Math.Max(1, (int)Math.Ceiling((elapsed - 180) / Period));
            var next = p.Last.AddSeconds(k * Period);

            double until = (next - now).TotalSeconds;
            double j = Random.Shared.NextDouble() * 2 - 1;

            if (until > 20) return TimeSpan.FromSeconds(Math.Min(until - 15, 60) + j);
            if (until > -90) return TimeSpan.FromSeconds(10 + j);
            return TimeSpan.FromSeconds((now - newest).TotalMinutes > 8 ? 60 : 30);
        }

        public TimeSpan NextDelay(DateTime now, IEnumerable<(DateTime Ts, double Value)> pulses)
        {
            if (_errors > 0)
            {
                var b = TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, _errors - 1)));
                return _retryAfter is { } ra && ra > b ? ra : b;
            }

            var phases = DetectPhases(pulses);
            if (phases.Count == 0) return TimeSpan.FromSeconds(60);

            var newest = phases.Max(p => p.Last);
            return phases.Select(p => PhaseDelay(p, now, newest)).Min();
        }
    }
}
