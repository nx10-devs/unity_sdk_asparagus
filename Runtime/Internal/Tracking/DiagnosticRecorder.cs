using System;
using System.Collections.Generic;
using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Encoding;

namespace Asparagus.Sdk.Internal.Tracking
{
    /// <summary>
    /// Bounded, deduplicating, pooled per-window <see cref="Diagnostic"/> collector.
    /// Repeated <c>(Severity, Code)</c> entries increment <see cref="Diagnostic.Count"/> in place
    /// instead of allocating duplicate records.
    /// </summary>
    public sealed class DiagnosticRecorder
    {
        public const int MaxDistinctDiagnosticsPerWindow = 64;
        private const int MaxCodeBytes = 128;
        private const int MaxDetailBytes = 1024;

        private readonly List<Diagnostic> _active = new List<Diagnostic>(16);
        private readonly Stack<Diagnostic> _pool = new Stack<Diagnostic>(16);

        public int ActiveCount
        {
            get { return _active.Count; }
        }

        public IReadOnlyList<Diagnostic> ActiveDiagnostics
        {
            get { return _active; }
        }

        public void RecordWarning(string code, string detail, long unityTimeUs)
        {
            Record(Severity.Warning, code, detail, unityTimeUs);
        }

        public void RecordError(string code, string detail, long unityTimeUs)
        {
            Record(Severity.Error, code, detail, unityTimeUs);
        }

        public void Record(Severity severity, string code, string detail, long unityTimeUs)
        {
            try
            {
                string safeCode = Utf8StringBounds.TruncateUtf8(string.IsNullOrEmpty(code) ? "unknown" : code, MaxCodeBytes);

                for (int i = 0; i < _active.Count; i++)
                {
                    Diagnostic existing = _active[i];
                    if (existing.Severity == severity && string.Equals(existing.Code, safeCode, StringComparison.Ordinal))
                    {
                        if (existing.Count < uint.MaxValue)
                        {
                            existing.Count++;
                        }
                        return;
                    }
                }

                if (_active.Count >= MaxDistinctDiagnosticsPerWindow)
                {
                    return;
                }

                Diagnostic diag = Rent();
                diag.TimeUs = unityTimeUs;
                diag.Severity = severity;
                diag.Code = safeCode;
                diag.Detail = Utf8StringBounds.TruncateUtf8(detail ?? string.Empty, MaxDetailBytes);
                diag.Count = 1u;
                _active.Add(diag);
            }
            catch
            {
                // Total fault containment: diagnostics never throw.
            }
        }

        /// <summary>
        /// Moves all active window diagnostics into <paramref name="target"/> and resets the active list.
        /// Callers may return instances via <see cref="ReturnToPool"/> once batch encoding finishes.
        /// </summary>
        public void DrainTo(List<Diagnostic> target)
        {
            if (target == null)
            {
                ResetWindow();
                return;
            }

            for (int i = 0; i < _active.Count; i++)
            {
                target.Add(_active[i]);
            }

            _active.Clear();
        }

        public void ReturnToPool(List<Diagnostic> diagnostics)
        {
            if (diagnostics == null)
            {
                return;
            }

            for (int i = 0; i < diagnostics.Count; i++)
            {
                Diagnostic diag = diagnostics[i];
                if (diag != null && _pool.Count < MaxDistinctDiagnosticsPerWindow)
                {
                    diag.Clear();
                    _pool.Push(diag);
                }
            }

            diagnostics.Clear();
        }

        public void ResetWindow()
        {
            for (int i = 0; i < _active.Count; i++)
            {
                Diagnostic diag = _active[i];
                if (diag != null && _pool.Count < MaxDistinctDiagnosticsPerWindow)
                {
                    diag.Clear();
                    _pool.Push(diag);
                }
            }

            _active.Clear();
        }

        private Diagnostic Rent()
        {
            if (_pool.Count > 0)
            {
                return _pool.Pop();
            }

            return new Diagnostic();
        }
    }
}
