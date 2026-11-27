using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Encoding;
using Asparagus.Sdk.Internal.Storage;
using Asparagus.Sdk.Internal.Tracking;

namespace Asparagus.Sdk.Internal.Pipeline
{
    public readonly struct PreparedUpload
    {
        public readonly QueuedBatchMetadata Metadata;
        public readonly byte[] Payload;
        public readonly bool IsEphemeral;

        public PreparedUpload(QueuedBatchMetadata metadata, byte[] payload, bool isEphemeral)
        {
            Metadata = metadata;
            Payload = payload;
            IsEphemeral = isEphemeral;
        }
    }

    /// <summary>
    /// Dedicated background worker thread (<see cref="AutoResetEvent"/>-driven, zero busy-spin)
    /// executing all Protobuf serialization, GZip compression, and <see cref="DiskBatchQueue"/>
    /// file I/O off Unity's main thread. Supports <c>runSynchronouslyForTests = true</c> for
    /// deterministic EditMode unit testing.
    /// </summary>
    public sealed class AsparagusWorker
    {
        private enum AckKind : byte
        {
            Success = 0,
            PermanentRefusal = 1,
            RetryableFailure = 2
        }

        private struct EncodeJob
        {
            public Batch Batch;
            public bool Compress;
            public long EnqueuedUtcMs;
        }

        private struct AckJob
        {
            public AckKind Kind;
            public QueuedBatchMetadata Metadata;
            public bool IsEphemeral;
        }

        private struct StartupLoadJob
        {
            public long NowUtcMs;
            public string ActiveOrg;
            public string ActiveDeployment;
            public string ActiveSource;
        }

        private struct ScopeFilterJob
        {
            public string ActiveOrg;
            public string ActiveDeployment;
            public string ActiveSource;
        }

        private struct WorkerDiagnostic
        {
            public string Code;
            public string Detail;
        }

        private readonly DiskBatchQueue _diskQueue;
        private readonly bool _runSynchronouslyForTests;
        private readonly object _sync = new object();

        private readonly Queue<EncodeJob> _encodeQueue = new Queue<EncodeJob>(8);
        private readonly Queue<AckJob> _ackQueue = new Queue<AckJob>(8);
        private readonly Queue<StartupLoadJob> _startupLoadQueue = new Queue<StartupLoadJob>(2);
        private readonly Queue<ScopeFilterJob> _scopeFilterQueue = new Queue<ScopeFilterJob>(2);
        private readonly Queue<Batch> _recycledBatches = new Queue<Batch>(8);
        private readonly Queue<WorkerDiagnostic> _workerDiagnostics = new Queue<WorkerDiagnostic>(8);

        // Worker-owned reusable encode buffers (never accessed from main thread)
        private readonly ProtobufWireWriter _wireWriter = new ProtobufWireWriter(16384);
        private readonly MemoryStream _compressStream = new MemoryStream(8192);

        private bool _prepareUploadRequested;
        private long _prepareUploadNowUtcMs;
        private bool _hasPreparedUpload;
        private PreparedUpload _preparedUpload;
        private bool _uploadInFlight;
        private bool _canAcceptImmediateUpload;

        private volatile bool _shutdownRequested;
        private readonly AutoResetEvent _wakeSignal;
        private readonly Thread _thread;

        public AsparagusWorker(DiskBatchQueue diskQueue, bool runSynchronouslyForTests = false)
        {
            _diskQueue = diskQueue ?? throw new ArgumentNullException(nameof(diskQueue));
            _runSynchronouslyForTests = runSynchronouslyForTests;

            if (!_runSynchronouslyForTests)
            {
                _wakeSignal = new AutoResetEvent(false);
                _thread = new Thread(WorkerLoop)
                {
                    Name = "AsparagusWorker",
                    IsBackground = true
                };
                _thread.Start();
            }
        }

        public DiskBatchQueue DiskQueue
        {
            get { return _diskQueue; }
        }

        public bool IsRunningSynchronously
        {
            get { return _runSynchronouslyForTests; }
        }

        public bool CanAcceptImmediateUpload
        {
            get
            {
                lock (_sync)
                {
                    return _canAcceptImmediateUpload;
                }
            }
        }

        public bool HasPreparedUpload
        {
            get
            {
                lock (_sync)
                {
                    return _hasPreparedUpload;
                }
            }
        }

        public void SetCanAcceptImmediateUpload(bool canAccept)
        {
            QueuedBatchMetadata droppedMeta = default;
            bool dropEphemeral = false;

            lock (_sync)
            {
                _canAcceptImmediateUpload = canAccept;
                if (!canAccept && _hasPreparedUpload && _preparedUpload.IsEphemeral)
                {
                    droppedMeta = _preparedUpload.Metadata;
                    _preparedUpload = default;
                    _hasPreparedUpload = false;
                    dropEphemeral = true;
                }
            }

            if (dropEphemeral)
            {
                _diskQueue.RecordDroppedBatch(
                    droppedMeta.Sequence,
                    droppedMeta.WindowStartUs,
                    droppedMeta.ClosedUs,
                    droppedMeta.CarriedDroppedCount,
                    droppedMeta.CarriedFirstDroppedSequence,
                    droppedMeta.CarriedDropStartUs,
                    droppedMeta.CarriedDropEndUs);
            }
        }

        public void EnqueueStartupLoad(long nowUtcMs, string activeOrg = null, string activeDeployment = null, string activeSource = null)
        {
            if (_shutdownRequested) return;

            lock (_sync)
            {
                _startupLoadQueue.Enqueue(new StartupLoadJob
                {
                    NowUtcMs = nowUtcMs,
                    ActiveOrg = activeOrg,
                    ActiveDeployment = activeDeployment,
                    ActiveSource = activeSource
                });
            }

            SignalOrRun();
        }

        public void EnqueueDiscardMismatchedScope(string activeOrg, string activeDeployment, string activeSource)
        {
            if (_shutdownRequested) return;

            lock (_sync)
            {
                _scopeFilterQueue.Enqueue(new ScopeFilterJob
                {
                    ActiveOrg = activeOrg,
                    ActiveDeployment = activeDeployment,
                    ActiveSource = activeSource
                });
            }

            SignalOrRun();
        }

        public void EnqueueClosedBatch(Batch batch, bool compress, long enqueuedUtcMs)
        {
            if (batch == null || _shutdownRequested)
            {
                return;
            }

            lock (_sync)
            {
                _encodeQueue.Enqueue(new EncodeJob
                {
                    Batch = batch,
                    Compress = compress,
                    EnqueuedUtcMs = enqueuedUtcMs
                });
            }

            SignalOrRun();
        }

        public void RequestNextUploadPayload(long nowUtcMs)
        {
            if (_shutdownRequested) return;

            lock (_sync)
            {
                if (_hasPreparedUpload || _uploadInFlight)
                {
                    return;
                }

                _prepareUploadRequested = true;
                _prepareUploadNowUtcMs = nowUtcMs;
            }

            SignalOrRun();
        }

        public bool TryTakePreparedUpload(out PreparedUpload upload)
        {
            lock (_sync)
            {
                if (!_hasPreparedUpload)
                {
                    upload = default;
                    return false;
                }

                upload = _preparedUpload;
                _preparedUpload = default;
                _hasPreparedUpload = false;
                _uploadInFlight = true;
                return true;
            }
        }

        public void AcknowledgeSuccess(in PreparedUpload upload)
        {
            EnqueueAck(AckKind.Success, in upload);
        }

        public void AcknowledgePermanentRefusal(in PreparedUpload upload)
        {
            EnqueueAck(AckKind.PermanentRefusal, in upload);
        }

        public void AcknowledgeRetryableFailure(in PreparedUpload upload)
        {
            EnqueueAck(AckKind.RetryableFailure, in upload);
        }

        public void DrainRecycledBatches(Action<Batch> recycleAction)
        {
            if (recycleAction == null)
            {
                return;
            }

            while (true)
            {
                Batch b;
                lock (_sync)
                {
                    if (_recycledBatches.Count == 0)
                    {
                        break;
                    }
                    b = _recycledBatches.Dequeue();
                }

                try
                {
                    recycleAction(b);
                }
                catch
                {
                }
            }
        }

        public void DrainWorkerDiagnostics(DiagnosticRecorder recorder, long currentUnityTimeUs)
        {
            if (recorder == null)
            {
                return;
            }

            while (true)
            {
                WorkerDiagnostic diag;
                lock (_sync)
                {
                    if (_workerDiagnostics.Count == 0)
                    {
                        break;
                    }
                    diag = _workerDiagnostics.Dequeue();
                }

                recorder.RecordWarning(diag.Code, diag.Detail, currentUnityTimeUs);
            }
        }

        /// <summary>
        /// In synchronous test mode, executes all queued worker jobs inline deterministically.
        /// </summary>
        public void FlushPendingWork()
        {
            if (_runSynchronouslyForTests)
            {
                ProcessAllPendingJobs();
            }
        }

        public void Shutdown(int timeoutMs = 250)
        {
            if (_shutdownRequested)
            {
                return;
            }

            _shutdownRequested = true;

            if (_runSynchronouslyForTests)
            {
                ProcessAllPendingJobs();
                DropUnsentEphemeralUpload();
                return;
            }

            try
            {
                if (_wakeSignal != null)
                {
                    _wakeSignal.Set();
                }
            }
            catch
            {
            }

            bool exited = true;
            if (_thread != null && _thread.IsAlive)
            {
                try
                {
                    exited = _thread.Join(timeoutMs > 0 ? timeoutMs : 250);
                }
                catch
                {
                    exited = false;
                }
            }

            DropUnsentEphemeralUpload();

            if (exited && _wakeSignal != null)
            {
                try
                {
                    _wakeSignal.Dispose();
                }
                catch
                {
                }
            }
        }

        private void DropUnsentEphemeralUpload()
        {
            QueuedBatchMetadata droppedMeta = default;
            bool hadEphemeral = false;

            lock (_sync)
            {
                if (_hasPreparedUpload && _preparedUpload.IsEphemeral)
                {
                    droppedMeta = _preparedUpload.Metadata;
                    _preparedUpload = default;
                    _hasPreparedUpload = false;
                    hadEphemeral = true;
                }
            }

            if (hadEphemeral)
            {
                _diskQueue.RecordDroppedBatch(
                    droppedMeta.Sequence,
                    droppedMeta.WindowStartUs,
                    droppedMeta.ClosedUs,
                    droppedMeta.CarriedDroppedCount,
                    droppedMeta.CarriedFirstDroppedSequence,
                    droppedMeta.CarriedDropStartUs,
                    droppedMeta.CarriedDropEndUs);
            }
        }

        private void EnqueueAck(AckKind kind, in PreparedUpload upload)
        {
            lock (_sync)
            {
                _ackQueue.Enqueue(new AckJob
                {
                    Kind = kind,
                    Metadata = upload.Metadata,
                    IsEphemeral = upload.IsEphemeral
                });
            }

            SignalOrRun();
        }

        private void SignalOrRun()
        {
            if (_runSynchronouslyForTests)
            {
                ProcessAllPendingJobs();
                return;
            }

            try
            {
                if (_wakeSignal != null && !_shutdownRequested)
                {
                    _wakeSignal.Set();
                }
            }
            catch
            {
            }
        }

        private void WorkerLoop()
        {
            while (true)
            {
                try
                {
                    ProcessAllPendingJobs();
                }
                catch
                {
                    // Total fault containment: worker loop never terminates unexpectedly on transient errors.
                }

                if (_shutdownRequested)
                {
                    // Drain any final batches queued right before shutdown.
                    try { ProcessAllPendingJobs(); } catch { }
                    return;
                }

                try
                {
                    _wakeSignal.WaitOne(500);
                }
                catch
                {
                    if (_shutdownRequested) return;
                }
            }
        }

        private void ProcessAllPendingJobs()
        {
            // 1. Startup disk loads
            while (TryDequeueStartupLoad(out StartupLoadJob loadJob))
            {
                _diskQueue.LoadFromDisk(loadJob.NowUtcMs, loadJob.ActiveOrg, loadJob.ActiveDeployment, loadJob.ActiveSource);
            }

            // 2. Scope filtering (org/deployment/source)
            while (TryDequeueScopeFilter(out ScopeFilterJob scopeJob))
            {
                _diskQueue.DiscardMismatchedScope(scopeJob.ActiveOrg, scopeJob.ActiveDeployment, scopeJob.ActiveSource);
            }

            // 3. Upload acknowledgements (delete or keep on disk)
            while (TryDequeueAck(out AckJob ack))
            {
                ProcessAck(in ack);
            }

            // 4. Encode & persist closed batches
            while (TryDequeueEncode(out EncodeJob job))
            {
                ProcessEncodeJob(in job);
            }

            // 5. Prepare next upload payload off disk if requested
            if (TryClaimPrepareRequest(out long nowUtcMs))
            {
                ProcessPrepareUpload(nowUtcMs);
            }
        }

        private void ProcessAck(in AckJob ack)
        {
            try
            {
                if (ack.Kind == AckKind.Success)
                {
                    if (!ack.IsEphemeral)
                    {
                        _diskQueue.Delete(in ack.Metadata);
                    }
                    _diskQueue.NotifyUploadSucceeded();
                }
                else if (ack.Kind == AckKind.PermanentRefusal)
                {
                    if (!ack.IsEphemeral)
                    {
                        _diskQueue.Delete(in ack.Metadata);
                    }

                    // Roll back any dropped-batch report carried by a permanently refused batch
                    // so the next batch that succeeds delivers the cumulative drop accounting.
                    if (ack.Metadata.CarriedDroppedCount > 0u)
                    {
                        _diskQueue.RestoreCarriedDropReport(
                            ack.Metadata.CarriedDroppedCount,
                            ack.Metadata.CarriedFirstDroppedSequence,
                            ack.Metadata.CarriedDropStartUs,
                            ack.Metadata.CarriedDropEndUs);
                    }
                }
                else if (ack.Kind == AckKind.RetryableFailure)
                {
                    if (ack.IsEphemeral)
                    {
                        // Disk queue disabled (queue_max_bytes == 0 || queue_max_age_s == 0):
                        // failed immediate send cannot be queued, so drop and count it (session.proto line 208).
                        _diskQueue.RecordDroppedBatch(
                            ack.Metadata.Sequence,
                            ack.Metadata.WindowStartUs,
                            ack.Metadata.ClosedUs,
                            ack.Metadata.CarriedDroppedCount,
                            ack.Metadata.CarriedFirstDroppedSequence,
                            ack.Metadata.CarriedDropStartUs,
                            ack.Metadata.CarriedDropEndUs);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                lock (_sync)
                {
                    _uploadInFlight = false;
                }
            }
        }

        private void ProcessEncodeJob(in EncodeJob job)
        {
            Batch batch = job.Batch;
            if (batch == null)
            {
                return;
            }

            ulong seq = batch.Header != null ? batch.Header.Sequence : 0UL;
            long windowStartUs = batch.Header != null ? batch.Header.WindowStartUs : 0L;
            long closedUs = batch.Header != null ? batch.Header.ClosedUs : 0L;
            string idOrg = batch.Header != null ? batch.Header.IdOrg : string.Empty;
            string idDep = batch.Header != null ? batch.Header.IdDeployment : string.Empty;
            string idSrc = batch.Header != null ? batch.Header.IdSource : string.Empty;
            string idSes = batch.Header != null ? batch.Header.IdSession : string.Empty;

            // Extract carried drop-report metadata before batch is recycled so eviction/failure can roll it back.
            uint carriedDropCount = 0u;
            ulong carriedFirstSeq = 0UL;
            long carriedDropStartUs = 0L;
            long carriedDropEndUs = 0L;
            if (batch.Header != null && batch.Header.BatchesDropped > 0u)
            {
                carriedDropCount = batch.Header.BatchesDropped;
                for (int i = 0; i < batch.Gaps.Count; i++)
                {
                    GapRecord g = batch.Gaps[i];
                    if (g.Cause == GapCause.BatchesDropped)
                    {
                        if (g.BatchesDropped > 0u)
                        {
                            carriedDropCount = g.BatchesDropped;
                        }
                        carriedFirstSeq = g.FirstDroppedSequence;
                        carriedDropStartUs = g.StartTimeUs;
                        carriedDropEndUs = g.EndTimeUs;
                        break;
                    }
                }
            }

            try
            {
                if (!BatchProtobufCodec.TryEncodeBatch(batch, _wireWriter, out string encodeErr))
                {
                    _diskQueue.RecordDroppedBatch(
                        seq, windowStartUs, closedUs,
                        carriedDropCount, carriedFirstSeq, carriedDropStartUs, carriedDropEndUs);
                    RecordWorkerDiagnostic("batch_encode_failed", encodeErr ?? "Protobuf encoding failed.");
                    return;
                }

                int unzippedBytes = _wireWriter.Length;
                if (unzippedBytes > BatchConstants.MaxUnzippedBytes)
                {
                    _diskQueue.RecordDroppedBatch(
                        seq, windowStartUs, closedUs,
                        carriedDropCount, carriedFirstSeq, carriedDropStartUs, carriedDropEndUs);
                    RecordWorkerDiagnostic(
                        "batch_too_large_unzipped",
                        "Encoded batch (" + unzippedBytes + " bytes) exceeds W8-26 262144-byte unzipped limit; dropped.");
                    return;
                }

                byte[] payloadBuf;
                int payloadLen;

                if (job.Compress)
                {
                    _compressStream.SetLength(0);
                    _compressStream.Position = 0;
                    using (GZipStream gzip = new GZipStream(_compressStream, CompressionLevel.Fastest, true))
                    {
                        if (unzippedBytes > 0)
                        {
                            gzip.Write(_wireWriter.Buffer, 0, unzippedBytes);
                        }
                    }

                    payloadBuf = _compressStream.GetBuffer();
                    payloadLen = (int)_compressStream.Length;
                }
                else
                {
                    payloadBuf = _wireWriter.Buffer;
                    payloadLen = unzippedBytes;
                }

                if (payloadLen > BatchConstants.MaxUploadBytes)
                {
                    _diskQueue.RecordDroppedBatch(
                        seq, windowStartUs, closedUs,
                        carriedDropCount, carriedFirstSeq, carriedDropStartUs, carriedDropEndUs);
                    RecordWorkerDiagnostic(
                        "batch_too_large_upload",
                        "Upload payload (" + payloadLen + " bytes) exceeds W8-26 262144-byte upload limit; dropped.");
                    return;
                }

                if (_diskQueue.IsDiskQueueDisabled)
                {
                    // Zero queue limit (session.proto line 208): only hand off if transport can send immediately
                    // right now (no backoff, no re-auth pause, active endpoint, no in-flight upload).
                    // Otherwise drop immediately without allocating or holding closed batch bytes in RAM.
                    bool handedOff = false;
                    lock (_sync)
                    {
                        if (_canAcceptImmediateUpload && !_uploadInFlight && !_hasPreparedUpload)
                        {
                            byte[] copy = new byte[payloadLen];
                            Buffer.BlockCopy(payloadBuf, 0, copy, 0, payloadLen);
                            QueuedBatchMetadata ephemeralMeta = new QueuedBatchMetadata(
                                null,
                                seq,
                                idOrg,
                                idDep,
                                idSrc,
                                idSes,
                                windowStartUs,
                                closedUs,
                                job.EnqueuedUtcMs,
                                payloadLen,
                                payloadLen,
                                job.Compress,
                                carriedDropCount,
                                carriedFirstSeq,
                                carriedDropStartUs,
                                carriedDropEndUs);
                            _preparedUpload = new PreparedUpload(ephemeralMeta, copy, true);
                            _hasPreparedUpload = true;
                            _prepareUploadRequested = false;
                            handedOff = true;
                        }
                    }

                    if (!handedOff)
                    {
                        _diskQueue.RecordDroppedBatch(
                            seq, windowStartUs, closedUs,
                            carriedDropCount, carriedFirstSeq, carriedDropStartUs, carriedDropEndUs);
                    }
                    return;
                }

                _diskQueue.Enqueue(
                    seq,
                    idOrg,
                    idDep,
                    idSrc,
                    idSes,
                    windowStartUs,
                    closedUs,
                    job.EnqueuedUtcMs,
                    job.Compress,
                    payloadBuf,
                    0,
                    payloadLen,
                    out _,
                    carriedDropCount,
                    carriedFirstSeq,
                    carriedDropStartUs,
                    carriedDropEndUs);
            }
            catch (Exception ex)
            {
                _diskQueue.RecordDroppedBatch(
                    seq, windowStartUs, closedUs,
                    carriedDropCount, carriedFirstSeq, carriedDropStartUs, carriedDropEndUs);
                RecordWorkerDiagnostic("worker_encode_exception", ex.Message);
            }
            finally
            {
                // Closed batch is immediately released from worker back to main-thread pool!
                lock (_sync)
                {
                    _recycledBatches.Enqueue(batch);
                }
            }
        }

        private void ProcessPrepareUpload(long nowUtcMs)
        {
            string lastFailedPath = null;
            while (_diskQueue.TryPeekOldest(nowUtcMs, out QueuedBatchMetadata meta))
            {
                if (_diskQueue.TryReadPayload(in meta, out byte[] payload))
                {
                    lock (_sync)
                    {
                        if (!_hasPreparedUpload && !_uploadInFlight)
                        {
                            _preparedUpload = new PreparedUpload(meta, payload, false);
                            _hasPreparedUpload = true;
                        }
                    }
                    return;
                }

                // Guard against spinning if a transient I/O error leaves the same file at queue head.
                if (string.Equals(meta.FilePath, lastFailedPath, StringComparison.Ordinal))
                {
                    return;
                }
                lastFailedPath = meta.FilePath;
            }
        }

        private bool TryDequeueStartupLoad(out StartupLoadJob job)
        {
            lock (_sync)
            {
                if (_startupLoadQueue.Count == 0)
                {
                    job = default;
                    return false;
                }
                job = _startupLoadQueue.Dequeue();
                return true;
            }
        }

        private bool TryDequeueScopeFilter(out ScopeFilterJob job)
        {
            lock (_sync)
            {
                if (_scopeFilterQueue.Count == 0)
                {
                    job = default;
                    return false;
                }
                job = _scopeFilterQueue.Dequeue();
                return true;
            }
        }

        private bool TryDequeueAck(out AckJob job)
        {
            lock (_sync)
            {
                if (_ackQueue.Count == 0)
                {
                    job = default;
                    return false;
                }
                job = _ackQueue.Dequeue();
                return true;
            }
        }

        private bool TryDequeueEncode(out EncodeJob job)
        {
            lock (_sync)
            {
                if (_encodeQueue.Count == 0)
                {
                    job = default;
                    return false;
                }
                job = _encodeQueue.Dequeue();
                return true;
            }
        }

        private bool TryClaimPrepareRequest(out long nowUtcMs)
        {
            lock (_sync)
            {
                if (!_prepareUploadRequested || _hasPreparedUpload || _uploadInFlight)
                {
                    nowUtcMs = 0L;
                    return false;
                }

                _prepareUploadRequested = false;
                nowUtcMs = _prepareUploadNowUtcMs;
                return true;
            }
        }

        private void RecordWorkerDiagnostic(string code, string detail)
        {
            lock (_sync)
            {
                if (_workerDiagnostics.Count < 32)
                {
                    _workerDiagnostics.Enqueue(new WorkerDiagnostic { Code = code, Detail = detail });
                }
            }
        }
    }
}
