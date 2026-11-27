using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Asparagus.Sdk.Contracts;

namespace Asparagus.Sdk.Internal.Storage
{
    public readonly struct QueuedBatchMetadata
    {
        public readonly string FilePath;
        public readonly ulong Sequence;
        public readonly string IdOrg;
        public readonly string IdDeployment;
        public readonly string IdSource;
        public readonly string IdSession;
        public readonly long WindowStartUs;
        public readonly long ClosedUs;
        public readonly long EnqueuedUtcMs;
        public readonly int PayloadBytes;
        public readonly long TotalFileBytes;
        public readonly bool IsCompressed;
        public readonly uint CarriedDroppedCount;
        public readonly ulong CarriedFirstDroppedSequence;
        public readonly long CarriedDropStartUs;
        public readonly long CarriedDropEndUs;

        public QueuedBatchMetadata(
            string filePath,
            ulong sequence,
            string idOrg,
            string idDeployment,
            string idSource,
            string idSession,
            long windowStartUs,
            long closedUs,
            long enqueuedUtcMs,
            int payloadBytes,
            long totalFileBytes,
            bool isCompressed,
            uint carriedDroppedCount = 0u,
            ulong carriedFirstDroppedSequence = 0UL,
            long carriedDropStartUs = 0L,
            long carriedDropEndUs = 0L)
        {
            FilePath = filePath;
            Sequence = sequence;
            IdOrg = idOrg ?? string.Empty;
            IdDeployment = idDeployment ?? string.Empty;
            IdSource = idSource ?? string.Empty;
            IdSession = idSession ?? string.Empty;
            WindowStartUs = windowStartUs;
            ClosedUs = closedUs;
            EnqueuedUtcMs = enqueuedUtcMs;
            PayloadBytes = payloadBytes;
            TotalFileBytes = totalFileBytes;
            IsCompressed = isCompressed;
            CarriedDroppedCount = carriedDroppedCount;
            CarriedFirstDroppedSequence = carriedFirstDroppedSequence;
            CarriedDropStartUs = carriedDropStartUs;
            CarriedDropEndUs = carriedDropEndUs;
        }
    }

    /// <summary>
    /// Persistent write-before-upload disk queue for closed batches.
    /// Keeps only lightweight file metadata in memory (never batch payload bytes), enforces
    /// <c>queue_max_bytes</c> and <c>queue_max_age_s</c> FIFO eviction, survives restarts across
    /// sessions, and tracks dropped batches atomically with rollback support for
    /// <see cref="Header.BatchesDropped"/> and <see cref="GapCause.BatchesDropped"/> <see cref="GapRecord"/>s.
    /// </summary>
    public sealed class DiskBatchQueue
    {
        public const string SubdirectoryName = "asparagus_batches";
        private const string BatchFileExtension = ".abq";
        private const string TempFileExtension = ".tmp";

        private static readonly byte[] MagicBytes = { (byte)'A', (byte)'B', (byte)'Q', (byte)'1' };
        private const byte EnvelopeVersion = 2;
        private const byte FlagCompressed = 1;
        private const int MaxStringFieldBytes = 1024;

        private readonly string _queueDirectory;
        private readonly object _sync = new object();
        private readonly List<QueuedBatchMetadata> _entries = new List<QueuedBatchMetadata>(32);

        private uint _queueMaxBytes = SessionSettings.DefaultQueueMaxBytes;
        private uint _queueMaxAgeS = SessionSettings.DefaultQueueMaxAgeS;
        private long _totalQueuedBytes;
        private int _fileCounter;

        // Atomic pending dropped-batch report accumulator (batch.proto lines 92-94, 531-533)
        private uint _pendingDroppedCount;
        private ulong _pendingFirstDroppedSequence;
        private long _pendingDropStartUs;
        private long _pendingDropEndUs;

        public DiskBatchQueue(string baseDirectory)
        {
            string root = string.IsNullOrEmpty(baseDirectory) ? Path.GetTempPath() : baseDirectory;
            _queueDirectory = Path.Combine(root, SubdirectoryName);
        }

        public string QueueDirectory
        {
            get { return _queueDirectory; }
        }

        public bool IsDiskQueueDisabled
        {
            get
            {
                lock (_sync)
                {
                    return _queueMaxBytes == 0u || _queueMaxAgeS == 0u;
                }
            }
        }

        public int QueuedCount
        {
            get
            {
                lock (_sync)
                {
                    return _entries.Count;
                }
            }
        }

        public long TotalQueuedBytes
        {
            get
            {
                lock (_sync)
                {
                    return _totalQueuedBytes;
                }
            }
        }

        public uint PendingBatchesDroppedCount
        {
            get
            {
                lock (_sync)
                {
                    return _pendingDroppedCount;
                }
            }
        }

        public bool HasPendingDroppedBatchReport
        {
            get
            {
                lock (_sync)
                {
                    return _pendingDroppedCount > 0u;
                }
            }
        }

        public void ConfigureLimits(uint queueMaxBytes, uint queueMaxAgeS)
        {
            lock (_sync)
            {
                _queueMaxBytes = queueMaxBytes;
                _queueMaxAgeS = queueMaxAgeS;
            }
        }

        /// <summary>
        /// Scans the queue directory on startup, removes stray temp/corrupt files and entries
        /// belonging to a different org/deployment/source, sorts valid entries FIFO, and enforces
        /// age and size limits. Leftover entries from an earlier session keep their original
        /// <c>IdSession</c> and remain queued for upload under the new session's token.
        /// </summary>
        public void LoadFromDisk(
            long nowUtcMs,
            string activeOrg = null,
            string activeDeployment = null,
            string activeSource = null)
        {
            lock (_sync)
            {
                _entries.Clear();
                _totalQueuedBytes = 0L;

                try
                {
                    if (!Directory.Exists(_queueDirectory))
                    {
                        return;
                    }

                    string[] files = Directory.GetFiles(_queueDirectory);
                    for (int i = 0; i < files.Length; i++)
                    {
                        string path = files[i];
                        if (path.EndsWith(TempFileExtension, StringComparison.OrdinalIgnoreCase))
                        {
                            SafeDeleteFile(path);
                            continue;
                        }

                        if (!path.EndsWith(BatchFileExtension, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!TryReadEnvelopeMetadata(path, out QueuedBatchMetadata meta))
                        {
                            SafeDeleteFile(path);
                            continue;
                        }

                        if (IsMismatchedScope(in meta, activeOrg, activeDeployment, activeSource))
                        {
                            SafeDeleteFile(path);
                            continue;
                        }

                        _entries.Add(meta);
                        _totalQueuedBytes += meta.TotalFileBytes;
                    }

                    _entries.Sort(CompareFifo);
                    PruneExpiredLocked(nowUtcMs);
                    EnforceMaxBytesLocked(0L);
                }
                catch
                {
                    // Fault containment: unreadable storage never crashes the host game.
                }
            }
        }

        /// <summary>
        /// Discards any queued entries whose (IdOrg, IdDeployment, IdSource) do not match the
        /// active session (preventing 403 refusals when switching deployments), while preserving
        /// leftover batches from earlier sessions of the same org/deployment/device.
        /// </summary>
        public void DiscardMismatchedScope(string activeOrg, string activeDeployment, string activeSource)
        {
            lock (_sync)
            {
                for (int i = _entries.Count - 1; i >= 0; i--)
                {
                    QueuedBatchMetadata meta = _entries[i];
                    if (IsMismatchedScope(in meta, activeOrg, activeDeployment, activeSource))
                    {
                        _entries.RemoveAt(i);
                        _totalQueuedBytes = Math.Max(0L, _totalQueuedBytes - meta.TotalFileBytes);
                        SafeDeleteFile(meta.FilePath);
                    }
                }
            }
        }

        /// <summary>
        /// Persists a closed batch payload atomically to disk. Returns <c>true</c> if persisted;
        /// returns <c>false</c> and records dropped-batch accounting if disk queuing is disabled,
        /// payload exceeds <c>queue_max_bytes</c>, or disk I/O fails.
        /// </summary>
        public bool Enqueue(
            ulong sequence,
            string idOrg,
            string idDeployment,
            string idSource,
            string idSession,
            long windowStartUs,
            long closedUs,
            long enqueuedUtcMs,
            bool isCompressed,
            byte[] payloadBuffer,
            int payloadOffset,
            int payloadLength,
            out QueuedBatchMetadata metadata,
            uint carriedDroppedCount = 0u,
            ulong carriedFirstDroppedSequence = 0UL,
            long carriedDropStartUs = 0L,
            long carriedDropEndUs = 0L)
        {
            metadata = default;

            if (payloadBuffer == null || payloadOffset < 0 || payloadLength <= 0 || payloadOffset + payloadLength > payloadBuffer.Length)
            {
                return false;
            }

            lock (_sync)
            {
                if (_queueMaxBytes == 0u || _queueMaxAgeS == 0u)
                {
                    RecordDroppedWithCarriedLocked(
                        sequence, windowStartUs, closedUs,
                        carriedDroppedCount, carriedFirstDroppedSequence, carriedDropStartUs, carriedDropEndUs);
                    return false;
                }

                PruneExpiredLocked(enqueuedUtcMs);

                string safeOrg = idOrg ?? string.Empty;
                string safeDep = idDeployment ?? string.Empty;
                string safeSrc = idSource ?? string.Empty;
                string safeSes = idSession ?? string.Empty;

                byte[] orgBytes = System.Text.Encoding.UTF8.GetBytes(safeOrg);
                byte[] depBytes = System.Text.Encoding.UTF8.GetBytes(safeDep);
                byte[] srcBytes = System.Text.Encoding.UTF8.GetBytes(safeSrc);
                byte[] sesBytes = System.Text.Encoding.UTF8.GetBytes(safeSes);

                long estimatedTotalBytes = EstimateEnvelopeSize(orgBytes.Length, depBytes.Length, srcBytes.Length, sesBytes.Length, payloadLength);
                if (estimatedTotalBytes > _queueMaxBytes)
                {
                    RecordDroppedWithCarriedLocked(
                        sequence, windowStartUs, closedUs,
                        carriedDroppedCount, carriedFirstDroppedSequence, carriedDropStartUs, carriedDropEndUs);
                    return false;
                }

                EnforceMaxBytesLocked(estimatedTotalBytes);

                string tmpPath = null;
                try
                {
                    if (!Directory.Exists(_queueDirectory))
                    {
                        Directory.CreateDirectory(_queueDirectory);
                    }

                    int counter = ++_fileCounter;
                    string baseName = enqueuedUtcMs.ToString("D16", CultureInfo.InvariantCulture)
                        + "_" + sequence.ToString("D10", CultureInfo.InvariantCulture)
                        + "_" + counter.ToString("D6", CultureInfo.InvariantCulture);

                    tmpPath = Path.Combine(_queueDirectory, baseName + TempFileExtension);
                    string finalPath = Path.Combine(_queueDirectory, baseName + BatchFileExtension);

                    using (FileStream fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096))
                    using (BinaryWriter bw = new BinaryWriter(fs, System.Text.Encoding.UTF8, false))
                    {
                        bw.Write(MagicBytes);
                        bw.Write(EnvelopeVersion);
                        bw.Write(isCompressed ? FlagCompressed : (byte)0);
                        bw.Write(sequence);
                        bw.Write(windowStartUs);
                        bw.Write(closedUs);
                        bw.Write(enqueuedUtcMs);
                        bw.Write(carriedDroppedCount);
                        bw.Write(carriedFirstDroppedSequence);
                        bw.Write(carriedDropStartUs);
                        bw.Write(carriedDropEndUs);
                        WriteLengthPrefixedBytes(bw, orgBytes);
                        WriteLengthPrefixedBytes(bw, depBytes);
                        WriteLengthPrefixedBytes(bw, srcBytes);
                        WriteLengthPrefixedBytes(bw, sesBytes);
                        bw.Write(payloadLength);
                        bw.Write(payloadBuffer, payloadOffset, payloadLength);
                        bw.Flush();
                    }

                    if (File.Exists(finalPath))
                    {
                        SafeDeleteFile(finalPath);
                    }

                    File.Move(tmpPath, finalPath);
                    tmpPath = null;

                    long actualFileBytes = estimatedTotalBytes;
                    try
                    {
                        actualFileBytes = new FileInfo(finalPath).Length;
                    }
                    catch
                    {
                    }

                    metadata = new QueuedBatchMetadata(
                        finalPath,
                        sequence,
                        safeOrg,
                        safeDep,
                        safeSrc,
                        safeSes,
                        windowStartUs,
                        closedUs,
                        enqueuedUtcMs,
                        payloadLength,
                        actualFileBytes,
                        isCompressed,
                        carriedDroppedCount,
                        carriedFirstDroppedSequence,
                        carriedDropStartUs,
                        carriedDropEndUs);

                    _entries.Add(metadata);
                    _totalQueuedBytes += actualFileBytes;
                    return true;
                }
                catch
                {
                    if (!string.IsNullOrEmpty(tmpPath))
                    {
                        SafeDeleteFile(tmpPath);
                    }

                    RecordDroppedWithCarriedLocked(
                        sequence, windowStartUs, closedUs,
                        carriedDroppedCount, carriedFirstDroppedSequence, carriedDropStartUs, carriedDropEndUs);
                    return false;
                }
            }
        }

        public void RecordDroppedBatch(
            ulong sequence,
            long windowStartUs,
            long closedUs,
            uint carriedDroppedCount = 0u,
            ulong carriedFirstDroppedSequence = 0UL,
            long carriedDropStartUs = 0L,
            long carriedDropEndUs = 0L)
        {
            lock (_sync)
            {
                RecordDroppedWithCarriedLocked(
                    sequence, windowStartUs, closedUs,
                    carriedDroppedCount, carriedFirstDroppedSequence, carriedDropStartUs, carriedDropEndUs);
            }
        }

        public void RestoreCarriedDropReport(
            uint carriedDroppedCount,
            ulong carriedFirstDroppedSequence,
            long carriedDropStartUs,
            long carriedDropEndUs)
        {
            if (carriedDroppedCount == 0u)
            {
                return;
            }

            lock (_sync)
            {
                MergeDroppedRangeLocked(
                    carriedDroppedCount,
                    carriedFirstDroppedSequence,
                    carriedDropStartUs,
                    carriedDropEndUs);
            }
        }

        public int PruneExpired(long nowUtcMs)
        {
            lock (_sync)
            {
                return PruneExpiredLocked(nowUtcMs);
            }
        }

        public bool TryPeekOldest(long nowUtcMs, out QueuedBatchMetadata metadata)
        {
            lock (_sync)
            {
                PruneExpiredLocked(nowUtcMs);
                if (_entries.Count == 0)
                {
                    metadata = default;
                    return false;
                }

                metadata = _entries[0];
                return true;
            }
        }

        public bool TryReadPayload(in QueuedBatchMetadata metadata, out byte[] payload)
        {
            payload = null;
            if (string.IsNullOrEmpty(metadata.FilePath))
            {
                return false;
            }

            try
            {
                if (!File.Exists(metadata.FilePath))
                {
                    RemoveEntryByPath(metadata.FilePath);
                    return false;
                }

                using (FileStream fs = new FileStream(metadata.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (BinaryReader br = new BinaryReader(fs, System.Text.Encoding.UTF8, false))
                {
                    if (!TryReadEnvelopeHeader(br, fs.Length, out _, out int payloadLen))
                    {
                        fs.Close();
                        RemoveEntryByPath(metadata.FilePath);
                        SafeDeleteFile(metadata.FilePath);
                        return false;
                    }

                    byte[] bytes = br.ReadBytes(payloadLen);
                    if (bytes == null || bytes.Length != payloadLen)
                    {
                        fs.Close();
                        RemoveEntryByPath(metadata.FilePath);
                        SafeDeleteFile(metadata.FilePath);
                        return false;
                    }

                    payload = bytes;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public void Delete(in QueuedBatchMetadata metadata)
        {
            if (string.IsNullOrEmpty(metadata.FilePath))
            {
                return;
            }

            RemoveEntryByPath(metadata.FilePath);
            SafeDeleteFile(metadata.FilePath);
        }

        public void NotifyUploadSucceeded()
        {
            // Pending drop reports are claimed atomically per closing batch and only rolled back
            // if a carrying batch fails/evicts before reaching the server; no state is wiped here.
        }

        /// <summary>
        /// Atomically claims the pending dropped-batch report (clearing the pending accumulator)
        /// so <see cref="Header.BatchesDropped"/> and the matching <see cref="GapCause.BatchesDropped"/>
        /// <see cref="GapRecord"/> are emitted together in lockstep. If the batch carrying this report
        /// is later evicted, fails to encode, or is permanently refused before a 2xx upload, its carried
        /// report is rolled back into the accumulator via <see cref="RecordDroppedBatch"/> or
        /// <see cref="RestoreCarriedDropReport"/>.
        /// </summary>
        public bool TryClaimDroppedBatchReport(out GapRecord gapRecord, out uint batchesDropped)
        {
            lock (_sync)
            {
                if (_pendingDroppedCount == 0u)
                {
                    gapRecord = default;
                    batchesDropped = 0u;
                    return false;
                }

                batchesDropped = _pendingDroppedCount;
                gapRecord = new GapRecord
                {
                    Signal = GapSignal.Everything,
                    Clock = Clock.Session,
                    StartTimeUs = _pendingDropStartUs,
                    EndTimeUs = _pendingDropEndUs,
                    Cause = GapCause.BatchesDropped,
                    BatchesDropped = _pendingDroppedCount,
                    FirstDroppedSequence = _pendingFirstDroppedSequence
                };

                _pendingDroppedCount = 0u;
                _pendingFirstDroppedSequence = 0UL;
                _pendingDropStartUs = 0L;
                _pendingDropEndUs = 0L;
                return true;
            }
        }

        private int PruneExpiredLocked(long nowUtcMs)
        {
            if (_entries.Count == 0)
            {
                return 0;
            }

            int removed = 0;
            if (_queueMaxAgeS == 0u)
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    QueuedBatchMetadata entry = _entries[i];
                    RecordDroppedEntryLocked(in entry);
                    SafeDeleteFile(entry.FilePath);
                    removed++;
                }
                _entries.Clear();
                _totalQueuedBytes = 0L;
                return removed;
            }

            long maxAgeMs = (long)_queueMaxAgeS * 1000L;
            for (int i = 0; i < _entries.Count; )
            {
                QueuedBatchMetadata entry = _entries[i];
                if ((nowUtcMs - entry.EnqueuedUtcMs) > maxAgeMs)
                {
                    _entries.RemoveAt(i);
                    _totalQueuedBytes = Math.Max(0L, _totalQueuedBytes - entry.TotalFileBytes);
                    RecordDroppedEntryLocked(in entry);
                    SafeDeleteFile(entry.FilePath);
                    removed++;
                }
                else
                {
                    i++;
                }
            }

            return removed;
        }

        private void EnforceMaxBytesLocked(long incomingBytes)
        {
            while (_entries.Count > 0 && (_totalQueuedBytes + incomingBytes) > _queueMaxBytes)
            {
                QueuedBatchMetadata oldest = _entries[0];
                _entries.RemoveAt(0);
                _totalQueuedBytes = Math.Max(0L, _totalQueuedBytes - oldest.TotalFileBytes);
                RecordDroppedEntryLocked(in oldest);
                SafeDeleteFile(oldest.FilePath);
            }
        }

        private void RecordDroppedEntryLocked(in QueuedBatchMetadata entry)
        {
            RecordDroppedWithCarriedLocked(
                entry.Sequence,
                entry.WindowStartUs,
                entry.ClosedUs,
                entry.CarriedDroppedCount,
                entry.CarriedFirstDroppedSequence,
                entry.CarriedDropStartUs,
                entry.CarriedDropEndUs);
        }

        private void RecordDroppedWithCarriedLocked(
            ulong sequence,
            long windowStartUs,
            long closedUs,
            uint carriedDroppedCount,
            ulong carriedFirstDroppedSequence,
            long carriedDropStartUs,
            long carriedDropEndUs)
        {
            if (carriedDroppedCount > 0u)
            {
                MergeDroppedRangeLocked(
                    carriedDroppedCount,
                    carriedFirstDroppedSequence,
                    carriedDropStartUs,
                    carriedDropEndUs);
            }

            MergeDroppedRangeLocked(1u, sequence, windowStartUs, closedUs);
        }

        private void MergeDroppedRangeLocked(uint count, ulong firstSequence, long startUs, long endUs)
        {
            if (count == 0u)
            {
                return;
            }

            long safeEnd = endUs >= startUs ? endUs : startUs;
            if (_pendingDroppedCount == 0u)
            {
                _pendingDroppedCount = count;
                _pendingFirstDroppedSequence = firstSequence;
                _pendingDropStartUs = startUs;
                _pendingDropEndUs = safeEnd;
            }
            else
            {
                ulong sum = (ulong)_pendingDroppedCount + (ulong)count;
                _pendingDroppedCount = sum > uint.MaxValue ? uint.MaxValue : (uint)sum;
                if (firstSequence > 0UL && (_pendingFirstDroppedSequence == 0UL || firstSequence < _pendingFirstDroppedSequence))
                {
                    _pendingFirstDroppedSequence = firstSequence;
                }
                if (startUs < _pendingDropStartUs)
                {
                    _pendingDropStartUs = startUs;
                }
                if (safeEnd > _pendingDropEndUs)
                {
                    _pendingDropEndUs = safeEnd;
                }
            }
        }

        private void RemoveEntryByPath(string filePath)
        {
            lock (_sync)
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (string.Equals(_entries[i].FilePath, filePath, StringComparison.Ordinal))
                    {
                        _totalQueuedBytes = Math.Max(0L, _totalQueuedBytes - _entries[i].TotalFileBytes);
                        _entries.RemoveAt(i);
                        return;
                    }
                }
            }
        }

        private static bool TryReadEnvelopeMetadata(string path, out QueuedBatchMetadata metadata)
        {
            metadata = default;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (BinaryReader br = new BinaryReader(fs, System.Text.Encoding.UTF8, false))
                {
                    if (!TryReadEnvelopeHeader(br, fs.Length, out QueuedBatchMetadata partial, out int payloadLen))
                    {
                        return false;
                    }

                    metadata = new QueuedBatchMetadata(
                        path,
                        partial.Sequence,
                        partial.IdOrg,
                        partial.IdDeployment,
                        partial.IdSource,
                        partial.IdSession,
                        partial.WindowStartUs,
                        partial.ClosedUs,
                        partial.EnqueuedUtcMs,
                        payloadLen,
                        fs.Length,
                        partial.IsCompressed,
                        partial.CarriedDroppedCount,
                        partial.CarriedFirstDroppedSequence,
                        partial.CarriedDropStartUs,
                        partial.CarriedDropEndUs);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadEnvelopeHeader(
            BinaryReader br,
            long fileLength,
            out QueuedBatchMetadata headerMeta,
            out int payloadLength)
        {
            headerMeta = default;
            payloadLength = 0;

            if (fileLength < 70)
            {
                return false;
            }

            byte[] magic = br.ReadBytes(4);
            if (magic.Length != 4
                || magic[0] != MagicBytes[0]
                || magic[1] != MagicBytes[1]
                || magic[2] != MagicBytes[2]
                || magic[3] != MagicBytes[3])
            {
                return false;
            }

            byte version = br.ReadByte();
            if (version != EnvelopeVersion)
            {
                return false;
            }

            byte flags = br.ReadByte();
            bool isCompressed = (flags & FlagCompressed) != 0;

            ulong seq = br.ReadUInt64();
            long windowStartUs = br.ReadInt64();
            long closedUs = br.ReadInt64();
            long enqueuedUtcMs = br.ReadInt64();
            uint carriedDropCount = br.ReadUInt32();
            ulong carriedFirstSeq = br.ReadUInt64();
            long carriedDropStartUs = br.ReadInt64();
            long carriedDropEndUs = br.ReadInt64();

            if (!TryReadLengthPrefixedString(br, out string idOrg)) return false;
            if (!TryReadLengthPrefixedString(br, out string idDep)) return false;
            if (!TryReadLengthPrefixedString(br, out string idSrc)) return false;
            if (!TryReadLengthPrefixedString(br, out string idSes)) return false;

            if (br.BaseStream.Position + 4 > fileLength)
            {
                return false;
            }

            int pLen = br.ReadInt32();
            if (pLen <= 0 || br.BaseStream.Position + pLen != fileLength)
            {
                return false;
            }

            payloadLength = pLen;
            headerMeta = new QueuedBatchMetadata(
                null,
                seq,
                idOrg,
                idDep,
                idSrc,
                idSes,
                windowStartUs,
                closedUs,
                enqueuedUtcMs,
                pLen,
                fileLength,
                isCompressed,
                carriedDropCount,
                carriedFirstSeq,
                carriedDropStartUs,
                carriedDropEndUs);
            return true;
        }

        private static void WriteLengthPrefixedBytes(BinaryWriter bw, byte[] utf8Bytes)
        {
            int len = utf8Bytes.Length > MaxStringFieldBytes ? MaxStringFieldBytes : utf8Bytes.Length;
            bw.Write((ushort)len);
            if (len > 0)
            {
                bw.Write(utf8Bytes, 0, len);
            }
        }

        private static bool TryReadLengthPrefixedString(BinaryReader br, out string value)
        {
            value = string.Empty;
            if (br.BaseStream.Position + 2 > br.BaseStream.Length)
            {
                return false;
            }

            ushort len = br.ReadUInt16();
            if (len > MaxStringFieldBytes || br.BaseStream.Position + len > br.BaseStream.Length)
            {
                return false;
            }

            if (len == 0)
            {
                return true;
            }

            byte[] bytes = br.ReadBytes(len);
            if (bytes.Length != len)
            {
                return false;
            }

            value = System.Text.Encoding.UTF8.GetString(bytes);
            return true;
        }

        private static long EstimateEnvelopeSize(int orgLen, int depLen, int srcLen, int sesLen, int payloadLen)
        {
            // 4 magic + 1 ver + 1 flags + 8 seq + 8 start + 8 closed + 8 enqueued
            // + 4 carriedCount + 8 carriedFirstSeq + 8 carriedStart + 8 carriedEnd
            // + 4*2 str len + 4 payloadLen = 78 bytes + strings + payload
            return 78L
                + Math.Min(orgLen, MaxStringFieldBytes)
                + Math.Min(depLen, MaxStringFieldBytes)
                + Math.Min(srcLen, MaxStringFieldBytes)
                + Math.Min(sesLen, MaxStringFieldBytes)
                + payloadLen;
        }

        private static bool IsMismatchedScope(
            in QueuedBatchMetadata meta,
            string activeOrg,
            string activeDeployment,
            string activeSource)
        {
            if (!string.IsNullOrEmpty(activeOrg) && !string.Equals(meta.IdOrg, activeOrg, StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(activeDeployment) && !string.Equals(meta.IdDeployment, activeDeployment, StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(activeSource) && !string.Equals(meta.IdSource, activeSource, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static int CompareFifo(QueuedBatchMetadata a, QueuedBatchMetadata b)
        {
            int c = a.EnqueuedUtcMs.CompareTo(b.EnqueuedUtcMs);
            if (c != 0) return c;
            c = a.Sequence.CompareTo(b.Sequence);
            if (c != 0) return c;
            return string.CompareOrdinal(a.FilePath, b.FilePath);
        }

        private static void SafeDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
