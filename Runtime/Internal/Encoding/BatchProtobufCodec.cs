using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Asparagus.Sdk.Contracts;

namespace Asparagus.Sdk.Internal.Encoding
{
    /// <summary>
    /// Non-throwing Schema v3 Protobuf encoder and decoder for <see cref="Batch"/> and
    /// <see cref="UploadResponse"/>, with optional GZip framing (0x1f 0x8b auto-detected on decode).
    /// </summary>
    public static class BatchProtobufCodec
    {
        private const byte GzipMagic0 = 0x1f;
        private const byte GzipMagic1 = 0x8b;
        private const int MaxDecompressedDiagnosticBytes = 16 * 1024 * 1024;

        public static bool TryEncodeBatch(Batch batch, ProtobufWireWriter writer, out string error)
        {
            error = null;
            if (batch == null)
            {
                error = "Batch is null.";
                return false;
            }

            if (writer == null)
            {
                error = "ProtobufWireWriter is null.";
                return false;
            }

            try
            {
                writer.Reset();
                WriteBatch(batch, writer);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryEncodeUploadPayload(
            Batch batch,
            bool compress,
            ProtobufWireWriter scratchWriter,
            MemoryStream outputStream,
            out string error)
        {
            error = null;
            if (outputStream == null)
            {
                error = "Output stream is null.";
                return false;
            }

            if (!TryEncodeBatch(batch, scratchWriter, out error))
            {
                return false;
            }

            try
            {
                outputStream.SetLength(0);
                outputStream.Position = 0;

                if (!compress)
                {
                    if (scratchWriter.Length > 0)
                    {
                        outputStream.Write(scratchWriter.Buffer, 0, scratchWriter.Length);
                    }
                }
                else
                {
                    using (GZipStream gzip = new GZipStream(outputStream, CompressionLevel.Fastest, true))
                    {
                        if (scratchWriter.Length > 0)
                        {
                            gzip.Write(scratchWriter.Buffer, 0, scratchWriter.Length);
                        }
                    }
                }

                outputStream.Position = 0;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryDecodeUploadResponse(byte[] data, int offset, int count, out UploadResponse response)
        {
            response = default;
            if (data == null || offset < 0 || count < 0 || offset + count > data.Length)
            {
                return false;
            }

            try
            {
                ProtobufWireReader reader = new ProtobufWireReader(data, offset, count);
                while (!reader.IsAtEnd)
                {
                    if (!reader.TryReadTag(out int fieldNumber, out int wireType))
                    {
                        return false;
                    }

                    if (fieldNumber == 1)
                    {
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadInt32(out int statusVal))
                        {
                            return false;
                        }

                        response.Status = (UploadStatus)statusVal;
                    }
                    else
                    {
                        if (!reader.TrySkipField(wireType))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }
            catch
            {
                response = default;
                return false;
            }
        }

        public static bool TryDecodeBatch(byte[] data, int offset, int count, out Batch batch, out string error)
        {
            batch = null;
            error = null;

            if (data == null || offset < 0 || count < 0 || offset + count > data.Length)
            {
                error = "Invalid input buffer bounds.";
                return false;
            }

            try
            {
                byte[] payload = data;
                int payloadOffset = offset;
                int payloadCount = count;

                if (count >= 2 && data[offset] == GzipMagic0 && data[offset + 1] == GzipMagic1)
                {
                    if (!TryGunzip(data, offset, count, out payload, out payloadCount, out error))
                    {
                        return false;
                    }
                    payloadOffset = 0;
                }

                Batch result = new Batch();
                ProtobufWireReader reader = new ProtobufWireReader(payload, payloadOffset, payloadCount);
                if (!TryReadBatch(ref reader, result))
                {
                    error = "Malformed Batch protobuf wire payload.";
                    return false;
                }

                batch = result;
                return true;
            }
            catch (Exception ex)
            {
                batch = null;
                error = ex.Message;
                return false;
            }
        }

        private static bool TryGunzip(
            byte[] compressed,
            int offset,
            int count,
            out byte[] decompressed,
            out int decompressedLength,
            out string error)
        {
            decompressed = null;
            decompressedLength = 0;
            error = null;

            try
            {
                using (MemoryStream input = new MemoryStream(compressed, offset, count, false))
                using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
                using (MemoryStream output = new MemoryStream())
                {
                    byte[] chunk = new byte[4096];
                    int total = 0;
                    int read;
                    while ((read = gzip.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        total += read;
                        if (total > MaxDecompressedDiagnosticBytes)
                        {
                            error = "GZip payload exceeds safe decompression limit.";
                            return false;
                        }

                        output.Write(chunk, 0, read);
                    }

                    decompressed = output.GetBuffer();
                    decompressedLength = (int)output.Length;
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = "GZip decompression failed: " + ex.Message;
                return false;
            }
        }

        // ====================================================================
        // Encoding
        // ====================================================================

        private static void WriteBatch(Batch batch, ProtobufWireWriter writer)
        {
            if (batch.Header != null)
            {
                int mark = writer.BeginLengthDelimited(1);
                WriteHeader(batch.Header, writer);
                writer.EndLengthDelimited(mark);
            }

            List<SensorBlock> sensorBlocks = batch.SensorBlocks;
            for (int i = 0; i < sensorBlocks.Count; i++)
            {
                SensorBlock block = sensorBlocks[i];
                if (block != null)
                {
                    int mark = writer.BeginLengthDelimited(2);
                    WriteSensorBlock(block, writer);
                    writer.EndLengthDelimited(mark);
                }
            }

            if (batch.Touch != null)
            {
                int mark = writer.BeginLengthDelimited(3);
                WriteTouch(batch.Touch, writer);
                writer.EndLengthDelimited(mark);
            }

            List<GameEvent> events = batch.Events;
            for (int i = 0; i < events.Count; i++)
            {
                GameEvent evt = events[i];
                if (evt != null)
                {
                    int mark = writer.BeginLengthDelimited(4);
                    WriteGameEvent(evt, writer);
                    writer.EndLengthDelimited(mark);
                }
            }

            List<StateRecord> stateRecords = batch.StateRecords;
            for (int i = 0; i < stateRecords.Count; i++)
            {
                StateRecord rec = stateRecords[i];
                if (rec != null)
                {
                    int mark = writer.BeginLengthDelimited(5);
                    WriteStateRecord(rec, writer);
                    writer.EndLengthDelimited(mark);
                }
            }

            List<LifecycleRecord> lifecycle = batch.Lifecycle;
            for (int i = 0; i < lifecycle.Count; i++)
            {
                int mark = writer.BeginLengthDelimited(6);
                WriteLifecycleRecord(lifecycle[i], writer);
                writer.EndLengthDelimited(mark);
            }

            List<GapRecord> gaps = batch.Gaps;
            for (int i = 0; i < gaps.Count; i++)
            {
                int mark = writer.BeginLengthDelimited(7);
                WriteGapRecord(gaps[i], writer);
                writer.EndLengthDelimited(mark);
            }

            List<ScreenRecord> screenChanges = batch.ScreenChanges;
            for (int i = 0; i < screenChanges.Count; i++)
            {
                int mark = writer.BeginLengthDelimited(8);
                WriteScreenRecord(screenChanges[i], writer);
                writer.EndLengthDelimited(mark);
            }

            List<Diagnostic> diagnostics = batch.Diagnostics;
            for (int i = 0; i < diagnostics.Count; i++)
            {
                Diagnostic diag = diagnostics[i];
                if (diag != null)
                {
                    int mark = writer.BeginLengthDelimited(9);
                    WriteDiagnostic(diag, writer);
                    writer.EndLengthDelimited(mark);
                }
            }

            if (batch.Carried != null)
            {
                int mark = writer.BeginLengthDelimited(10);
                WriteCarried(batch.Carried, writer);
                writer.EndLengthDelimited(mark);
            }
        }

        private static void WriteHeader(Header header, ProtobufWireWriter writer)
        {
            writer.WriteString(1, Utf8StringBounds.TruncateUtf8(header.IdOrg, BatchConstants.MaxHeaderStringBytes));
            writer.WriteString(2, Utf8StringBounds.TruncateUtf8(header.IdDeployment, BatchConstants.MaxHeaderStringBytes));
            writer.WriteString(3, Utf8StringBounds.TruncateUtf8(header.IdSource, BatchConstants.MaxHeaderStringBytes));
            writer.WriteString(4, Utf8StringBounds.TruncateUtf8(header.IdSession, BatchConstants.MaxHeaderStringBytes));
            writer.WriteUInt64(5, header.Sequence);
            writer.WriteUInt32(6, header.SchemaVersion);

            if (header.HasSdkVersion || !header.SdkVersion.IsZero)
            {
                int mark = writer.BeginLengthDelimited(7);
                WriteSdkVersion(header.SdkVersion, writer);
                writer.EndLengthDelimited(mark);
            }

            writer.WriteEnum(8, (int)header.Platform);
            writer.WriteString(9, Utf8StringBounds.TruncateUtf8(header.OsVersion, BatchConstants.MaxHeaderStringBytes));
            writer.WriteString(10, Utf8StringBounds.TruncateUtf8(header.DeviceModel, BatchConstants.MaxHeaderStringBytes));
            writer.WriteString(11, Utf8StringBounds.TruncateUtf8(header.SettingsVersion, BatchConstants.MaxHeaderStringBytes));

            writer.WriteSInt64(12, header.WindowStartUs);
            writer.WriteUInt32(13, header.WindowLengthS);
            writer.WriteSInt64(14, header.ClosedUs);
            writer.WriteEnum(15, (int)header.CloseReason);

            if (header.HasClocks || !header.Clocks.IsEmpty)
            {
                int mark = writer.BeginLengthDelimited(16);
                WriteClockReading(header.Clocks, writer);
                writer.EndLengthDelimited(mark);
            }

            if (header.ServerAnchor != null)
            {
                int mark = writer.BeginLengthDelimited(17);
                WriteServerAnchor(header.ServerAnchor, writer);
                writer.EndLengthDelimited(mark);
            }

            if (header.HasScreen || !header.Screen.IsEmpty)
            {
                int mark = writer.BeginLengthDelimited(18);
                WriteScreenRecord(header.Screen, writer);
                writer.EndLengthDelimited(mark);
            }

            writer.WriteBool(19, header.HasGyroscope);
            writer.WriteUInt32(20, header.BatchesDropped);

            if (header.HasUtcOffsetMinutes)
            {
                writer.WriteSInt32Always(21, header.UtcOffsetMinutes);
            }
        }

        private static void WriteSdkVersion(SdkVersion version, ProtobufWireWriter writer)
        {
            writer.WriteUInt32(1, version.Major);
            writer.WriteUInt32(2, version.Minor);
            writer.WriteUInt32(3, version.Patch);
        }

        private static void WriteClockReading(ClockReading reading, ProtobufWireWriter writer)
        {
            writer.WriteSInt64(1, reading.SessionUs);
            writer.WriteSInt64(2, reading.UnityUs);
            if (reading.HasSensorUs)
            {
                writer.WriteSInt64Always(3, reading.SensorUs);
            }
            writer.WriteSInt64(4, reading.WallUtcMs);
        }

        private static void WriteServerAnchor(ServerAnchor anchor, ProtobufWireWriter writer)
        {
            writer.WriteSInt64(1, anchor.ServerUtcMs);
            if (anchor.HasAtReceipt || !anchor.AtReceipt.IsEmpty)
            {
                int mark = writer.BeginLengthDelimited(2);
                WriteClockReading(anchor.AtReceipt, writer);
                writer.EndLengthDelimited(mark);
            }
            writer.WriteUInt32(3, anchor.RoundTripMs);
        }

        private static void WriteSensorBlock(SensorBlock block, ProtobufWireWriter writer)
        {
            writer.WriteEnum(1, (int)block.Sensor);
            writer.WriteEnum(2, (int)block.Source);
            writer.WriteEnum(3, (int)block.FallbackReason);
            writer.WriteEnum(4, (int)block.Clock);
            writer.WriteEnum(5, (int)block.Unit);
            writer.WriteDouble(6, block.Scale);
            writer.WriteFloat(7, block.RequestedHz);
            writer.WriteUInt32(8, block.SampleCount);
            writer.WriteUInt32(9, block.LongestGapUs);
            writer.WriteSInt64(10, block.FirstTimeUs);
            writer.WritePackedSInt32(11, block.TimeStepsUs);
            writer.WritePackedSInt32(12, block.X);
            writer.WritePackedSInt32(13, block.Y);
            writer.WritePackedSInt32(14, block.Z);

            List<Hold> holds = block.Holds;
            for (int i = 0; i < holds.Count; i++)
            {
                Hold hold = holds[i];
                if (hold != null)
                {
                    int mark = writer.BeginLengthDelimited(15);
                    WriteHold(hold, writer);
                    writer.EndLengthDelimited(mark);
                }
            }
        }

        private static void WriteHold(Hold hold, ProtobufWireWriter writer)
        {
            writer.WriteSInt64(1, hold.StartTimeUs);
            writer.WriteSInt64(2, hold.EndTimeUs);
            writer.WriteUInt32(3, hold.SampleCount);
            writer.WriteSInt32(4, hold.X);
            writer.WriteSInt32(5, hold.Y);
            writer.WriteSInt32(6, hold.Z);

            List<HoldStats> stats = hold.Stats;
            for (int i = 0; i < stats.Count; i++)
            {
                int mark = writer.BeginLengthDelimited(7);
                WriteHoldStats(stats[i], writer);
                writer.EndLengthDelimited(mark);
            }
        }

        private static void WriteHoldStats(HoldStats stats, ProtobufWireWriter writer)
        {
            writer.WriteSInt64(1, stats.Sum);
            writer.WriteDouble(2, stats.SumOfSquares);
            writer.WriteSInt32(3, stats.Minimum);
            writer.WriteSInt32(4, stats.Maximum);
        }

        private static void WriteTouch(Touch touch, ProtobufWireWriter writer)
        {
            writer.WriteEnum(1, (int)touch.Mode);
            writer.WriteFloat(2, touch.RequestedHz);
            writer.WriteDouble(3, touch.PositionScale);
            writer.WriteDouble(4, touch.SizeScale);
            writer.WriteDouble(5, touch.PressureScale);

            List<TouchContact> contacts = touch.Contacts;
            for (int i = 0; i < contacts.Count; i++)
            {
                TouchContact contact = contacts[i];
                if (contact != null)
                {
                    int mark = writer.BeginLengthDelimited(6);
                    WriteTouchContact(contact, writer);
                    writer.EndLengthDelimited(mark);
                }
            }
        }

        private static void WriteTouchContact(TouchContact contact, ProtobufWireWriter writer)
        {
            writer.WriteUInt32(1, contact.FingerId);
            writer.WriteBool(2, contact.Continued);
            writer.WriteEnum(3, (int)contact.End);
            writer.WriteSInt64(4, contact.FirstTimeUs);
            writer.WritePackedSInt32(5, contact.TimeStepsUs);
            writer.WritePackedSInt32(6, contact.X);
            writer.WritePackedSInt32(7, contact.Y);
            writer.WritePackedSInt32(8, contact.SizeMajor);
            writer.WritePackedSInt32(9, contact.SizeMinor);
            writer.WritePackedSInt32(10, contact.Pressure);

            List<TouchHold> holds = contact.Holds;
            for (int i = 0; i < holds.Count; i++)
            {
                TouchHold hold = holds[i];
                if (hold != null)
                {
                    int mark = writer.BeginLengthDelimited(11);
                    WriteTouchHold(hold, writer);
                    writer.EndLengthDelimited(mark);
                }
            }

            writer.WriteUInt32(12, contact.EventsSeen);

            if (contact.Before != null)
            {
                int mark = writer.BeginLengthDelimited(13);
                WriteContactBefore(contact.Before, writer);
                writer.EndLengthDelimited(mark);
            }
        }

        private static void WriteContactBefore(ContactBefore before, ProtobufWireWriter writer)
        {
            writer.WriteSInt64(1, before.DownTimeUs);
            writer.WriteSInt32(2, before.MinX);
            writer.WriteSInt32(3, before.MaxX);
            writer.WriteSInt32(4, before.MinY);
            writer.WriteSInt32(5, before.MaxY);
        }

        private static void WriteTouchHold(TouchHold hold, ProtobufWireWriter writer)
        {
            writer.WriteSInt64(1, hold.StartTimeUs);
            writer.WriteSInt64(2, hold.EndTimeUs);
            writer.WriteUInt32(3, hold.SampleCount);
            writer.WriteSInt32(4, hold.X);
            writer.WriteSInt32(5, hold.Y);
            writer.WriteSInt32(6, hold.SizeMajor);
            writer.WriteSInt32(7, hold.SizeMinor);
            writer.WriteSInt32(8, hold.Pressure);

            List<HoldStats> stats = hold.Stats;
            for (int i = 0; i < stats.Count; i++)
            {
                int mark = writer.BeginLengthDelimited(9);
                WriteHoldStats(stats[i], writer);
                writer.EndLengthDelimited(mark);
            }
        }

        private static void WriteScreenRecord(ScreenRecord record, ProtobufWireWriter writer)
        {
            writer.WriteSInt64(1, record.TimeUs);
            writer.WriteUInt32(2, record.WidthPx);
            writer.WriteUInt32(3, record.HeightPx);
            writer.WriteFloat(4, record.DensityDpi);
            writer.WriteEnum(5, (int)record.Orientation);
            writer.WriteEnum(6, (int)record.Origin);
        }

        private static void WriteValue(Value value, ProtobufWireWriter writer)
        {
            switch (value.Kind)
            {
                case ValueKind.Text:
                    // oneof branch always written even if empty string
                    writer.WriteStringAlways(1, value.TextValue);
                    break;
                case ValueKind.Number:
                    // oneof branch always written even if 0.0
                    writer.WriteDoubleAlways(2, value.NumberValue);
                    break;
                case ValueKind.Flag:
                    // oneof branch always written even if false
                    writer.WriteBoolAlways(3, value.FlagValue);
                    break;
                case ValueKind.None:
                default:
                    // Empty Value {} writes zero bytes
                    break;
            }
        }

        private static void WriteValueMap(int fieldNumber, Dictionary<string, Value> map, ProtobufWireWriter writer)
        {
            if (map == null || map.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<string, Value> entry in map)
            {
                int entryMark = writer.BeginLengthDelimited(fieldNumber);
                writer.WriteString(1, entry.Key);

                int valueMark = writer.BeginLengthDelimited(2);
                WriteValue(entry.Value, writer);
                writer.EndLengthDelimited(valueMark);

                writer.EndLengthDelimited(entryMark);
            }
        }

        private static void WriteGameEvent(GameEvent evt, ProtobufWireWriter writer)
        {
            writer.WriteSInt64(1, evt.TimeUs);
            writer.WriteString(2, evt.Name);
            WriteValueMap(3, evt.Fields, writer);
            if (evt.HasOutcome)
            {
                writer.WriteStringAlways(4, evt.Outcome);
            }
            writer.WriteString(5, evt.SpanName);
            writer.WriteEnum(6, (int)evt.SpanMarker);
            writer.WriteUInt32(7, evt.SpanNumber);
            writer.WriteUInt32(8, evt.Order);
        }

        private static void WriteStateRecord(StateRecord rec, ProtobufWireWriter writer)
        {
            writer.WriteSInt64(1, rec.TimeUs);
            writer.WriteEnum(2, (int)rec.Kind);
            WriteValueMap(3, rec.Values, writer);

            List<string> removed = rec.RemovedKeys;
            for (int i = 0; i < removed.Count; i++)
            {
                writer.WriteStringAlways(4, removed[i]);
            }

            writer.WriteUInt32(5, rec.Order);
            writer.WriteUInt32(6, rec.Version);
        }

        private static void WriteCarried(Carried carried, ProtobufWireWriter writer)
        {
            if (carried.State != null)
            {
                int mark = writer.BeginLengthDelimited(1);
                WriteState(carried.State, writer);
                writer.EndLengthDelimited(mark);
            }

            if (carried.OpenSpans != null)
            {
                int mark = writer.BeginLengthDelimited(2);
                WriteOpenSpans(carried.OpenSpans, writer);
                writer.EndLengthDelimited(mark);
            }

            if (carried.HasLastEventTimeUs)
            {
                writer.WriteSInt64Always(3, carried.LastEventTimeUs);
            }
        }

        private static void WriteState(State state, ProtobufWireWriter writer)
        {
            WriteValueMap(1, state.Values, writer);
            writer.WriteUInt32(2, state.Version);
        }

        private static void WriteOpenSpans(OpenSpans openSpans, ProtobufWireWriter writer)
        {
            List<OpenSpan> spans = openSpans.Spans;
            for (int i = 0; i < spans.Count; i++)
            {
                int mark = writer.BeginLengthDelimited(1);
                WriteOpenSpan(spans[i], writer);
                writer.EndLengthDelimited(mark);
            }
        }

        private static void WriteOpenSpan(OpenSpan span, ProtobufWireWriter writer)
        {
            writer.WriteString(1, span.Name);
            writer.WriteUInt32(2, span.Number);
            writer.WriteSInt64(3, span.OpenedTimeUs);
        }

        private static void WriteLifecycleRecord(LifecycleRecord rec, ProtobufWireWriter writer)
        {
            writer.WriteSInt64(1, rec.TimeUs);
            writer.WriteEnum(2, (int)rec.Kind);
            writer.WriteSInt64(3, rec.ClockStepS);
        }

        private static void WriteGapRecord(GapRecord rec, ProtobufWireWriter writer)
        {
            writer.WriteEnum(1, (int)rec.Signal);
            writer.WriteEnum(2, (int)rec.Clock);
            writer.WriteSInt64(3, rec.StartTimeUs);
            writer.WriteSInt64(4, rec.EndTimeUs);
            writer.WriteEnum(5, (int)rec.Cause);
            writer.WriteUInt32(6, rec.BatchesDropped);
            writer.WriteUInt64(7, rec.FirstDroppedSequence);
        }

        private static void WriteDiagnostic(Diagnostic diag, ProtobufWireWriter writer)
        {
            writer.WriteSInt64(1, diag.TimeUs);
            writer.WriteEnum(2, (int)diag.Severity);
            writer.WriteString(3, diag.Code);
            writer.WriteString(4, diag.Detail);
            writer.WriteUInt32(5, diag.Count);
        }

        // ====================================================================
        // Decoding
        // ====================================================================

        private static bool TryReadBatch(ref ProtobufWireReader reader, Batch batch)
        {
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType))
                {
                    return false;
                }

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader headerReader))
                        {
                            return false;
                        }
                        if (batch.Header == null)
                        {
                            batch.Header = new Header { SchemaVersion = 0 };
                        }
                        if (!TryReadHeader(ref headerReader, batch.Header))
                        {
                            return false;
                        }
                        break;

                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader blockReader))
                        {
                            return false;
                        }
                        SensorBlock block = new SensorBlock();
                        if (!TryReadSensorBlock(ref blockReader, block))
                        {
                            return false;
                        }
                        batch.SensorBlocks.Add(block);
                        break;

                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader touchReader))
                        {
                            return false;
                        }
                        if (batch.Touch == null)
                        {
                            batch.Touch = new Touch();
                        }
                        if (!TryReadTouch(ref touchReader, batch.Touch))
                        {
                            return false;
                        }
                        break;

                    case 4:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader eventReader))
                        {
                            return false;
                        }
                        GameEvent evt = new GameEvent();
                        if (!TryReadGameEvent(ref eventReader, evt))
                        {
                            return false;
                        }
                        batch.Events.Add(evt);
                        break;

                    case 5:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader stateRecReader))
                        {
                            return false;
                        }
                        StateRecord stateRec = new StateRecord();
                        if (!TryReadStateRecord(ref stateRecReader, stateRec))
                        {
                            return false;
                        }
                        batch.StateRecords.Add(stateRec);
                        break;

                    case 6:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader lifeReader))
                        {
                            return false;
                        }
                        if (!TryReadLifecycleRecord(ref lifeReader, out LifecycleRecord lifeRec))
                        {
                            return false;
                        }
                        batch.Lifecycle.Add(lifeRec);
                        break;

                    case 7:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader gapReader))
                        {
                            return false;
                        }
                        if (!TryReadGapRecord(ref gapReader, out GapRecord gapRec))
                        {
                            return false;
                        }
                        batch.Gaps.Add(gapRec);
                        break;

                    case 8:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader screenReader))
                        {
                            return false;
                        }
                        if (!TryReadScreenRecord(ref screenReader, out ScreenRecord screenRec))
                        {
                            return false;
                        }
                        batch.ScreenChanges.Add(screenRec);
                        break;

                    case 9:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader diagReader))
                        {
                            return false;
                        }
                        Diagnostic diag = new Diagnostic();
                        if (!TryReadDiagnostic(ref diagReader, diag))
                        {
                            return false;
                        }
                        batch.Diagnostics.Add(diag);
                        break;

                    case 10:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader carriedReader))
                        {
                            return false;
                        }
                        if (batch.Carried == null)
                        {
                            batch.Carried = new Carried();
                        }
                        if (!TryReadCarried(ref carriedReader, batch.Carried))
                        {
                            return false;
                        }
                        break;

                    default:
                        if (!reader.TrySkipField(wireType))
                        {
                            return false;
                        }
                        break;
                }
            }

            return true;
        }

        private static bool TryReadHeader(ref ProtobufWireReader reader, Header header)
        {
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType))
                {
                    return false;
                }

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadString(out header.IdOrg)) return false;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadString(out header.IdDeployment)) return false;
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadString(out header.IdSource)) return false;
                        break;
                    case 4:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadString(out header.IdSession)) return false;
                        break;
                    case 5:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint64(out header.Sequence)) return false;
                        break;
                    case 6:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out header.SchemaVersion)) return false;
                        break;
                    case 7:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader sdkReader)) return false;
                        header.HasSdkVersion = true;
                        if (!TryReadSdkVersion(ref sdkReader, ref header.SdkVersion)) return false;
                        break;
                    case 8:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadInt32(out int platformVal)) return false;
                        header.Platform = (Platform)platformVal;
                        break;
                    case 9:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadString(out header.OsVersion)) return false;
                        break;
                    case 10:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadString(out header.DeviceModel)) return false;
                        break;
                    case 11:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadString(out header.SettingsVersion)) return false;
                        break;
                    case 12:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out header.WindowStartUs)) return false;
                        break;
                    case 13:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out header.WindowLengthS)) return false;
                        break;
                    case 14:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out header.ClosedUs)) return false;
                        break;
                    case 15:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadInt32(out int closeReasonVal)) return false;
                        header.CloseReason = (CloseReason)closeReasonVal;
                        break;
                    case 16:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader clocksReader)) return false;
                        header.HasClocks = true;
                        if (!TryReadClockReading(ref clocksReader, ref header.Clocks)) return false;
                        break;
                    case 17:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader anchorReader)) return false;
                        if (header.ServerAnchor == null)
                        {
                            header.ServerAnchor = new ServerAnchor();
                        }
                        if (!TryReadServerAnchor(ref anchorReader, header.ServerAnchor)) return false;
                        break;
                    case 18:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader screenReader)) return false;
                        header.HasScreen = true;
                        if (!TryReadScreenRecordInto(ref screenReader, ref header.Screen)) return false;
                        break;
                    case 19:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadBool(out header.HasGyroscope)) return false;
                        break;
                    case 20:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out header.BatchesDropped)) return false;
                        break;
                    case 21:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt32(out int utcOffset)) return false;
                        header.HasUtcOffsetMinutes = true;
                        header.UtcOffsetMinutes = utcOffset;
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadSdkVersion(ref ProtobufWireReader reader, ref SdkVersion version)
        {
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out version.Major)) return false;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out version.Minor)) return false;
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out version.Patch)) return false;
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadClockReading(ref ProtobufWireReader reader, ref ClockReading reading)
        {
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out reading.SessionUs)) return false;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out reading.UnityUs)) return false;
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out long sensorUs)) return false;
                        reading.HasSensorUs = true;
                        reading.SensorUs = sensorUs;
                        break;
                    case 4:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out reading.WallUtcMs)) return false;
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadServerAnchor(ref ProtobufWireReader reader, ServerAnchor anchor)
        {
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out anchor.ServerUtcMs)) return false;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader clockReader)) return false;
                        anchor.HasAtReceipt = true;
                        if (!TryReadClockReading(ref clockReader, ref anchor.AtReceipt)) return false;
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out anchor.RoundTripMs)) return false;
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadSensorBlock(ref ProtobufWireReader reader, SensorBlock block)
        {
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadInt32(out int sensorVal)) return false;
                        block.Sensor = (Sensor)sensorVal;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadInt32(out int sourceVal)) return false;
                        block.Source = (CaptureSource)sourceVal;
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadInt32(out int fallbackVal)) return false;
                        block.FallbackReason = (FallbackReason)fallbackVal;
                        break;
                    case 4:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadInt32(out int clockVal)) return false;
                        block.Clock = (Clock)clockVal;
                        break;
                    case 5:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadInt32(out int unitVal)) return false;
                        block.Unit = (ValueUnit)unitVal;
                        break;
                    case 6:
                        if (wireType != ProtobufWireWriter.WireTypeFixed64 || !reader.TryReadDouble(out block.Scale)) return false;
                        break;
                    case 7:
                        if (wireType != ProtobufWireWriter.WireTypeFixed32 || !reader.TryReadFloat(out block.RequestedHz)) return false;
                        break;
                    case 8:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out block.SampleCount)) return false;
                        break;
                    case 9:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out block.LongestGapUs)) return false;
                        break;
                    case 10:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out block.FirstTimeUs)) return false;
                        break;
                    case 11:
                        if (!reader.TryReadPackedOrSingleSInt32(wireType, block.TimeStepsUs)) return false;
                        break;
                    case 12:
                        if (!reader.TryReadPackedOrSingleSInt32(wireType, block.X)) return false;
                        break;
                    case 13:
                        if (!reader.TryReadPackedOrSingleSInt32(wireType, block.Y)) return false;
                        break;
                    case 14:
                        if (!reader.TryReadPackedOrSingleSInt32(wireType, block.Z)) return false;
                        break;
                    case 15:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader holdReader)) return false;
                        Hold hold = new Hold();
                        if (!TryReadHold(ref holdReader, hold)) return false;
                        block.Holds.Add(hold);
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadHold(ref ProtobufWireReader reader, Hold hold)
        {
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out hold.StartTimeUs)) return false;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out hold.EndTimeUs)) return false;
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out hold.SampleCount)) return false;
                        break;
                    case 4:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt32(out hold.X)) return false;
                        break;
                    case 5:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt32(out hold.Y)) return false;
                        break;
                    case 6:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt32(out hold.Z)) return false;
                        break;
                    case 7:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader statsReader)) return false;
                        if (!TryReadHoldStats(ref statsReader, out HoldStats stats)) return false;
                        hold.Stats.Add(stats);
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadHoldStats(ref ProtobufWireReader reader, out HoldStats stats)
        {
            stats = default;
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out stats.Sum)) return false;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeFixed64 || !reader.TryReadDouble(out stats.SumOfSquares)) return false;
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt32(out stats.Minimum)) return false;
                        break;
                    case 4:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt32(out stats.Maximum)) return false;
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadTouch(ref ProtobufWireReader reader, Touch touch)
        {
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadInt32(out int modeVal)) return false;
                        touch.Mode = (TouchMode)modeVal;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeFixed32 || !reader.TryReadFloat(out touch.RequestedHz)) return false;
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeFixed64 || !reader.TryReadDouble(out touch.PositionScale)) return false;
                        break;
                    case 4:
                        if (wireType != ProtobufWireWriter.WireTypeFixed64 || !reader.TryReadDouble(out touch.SizeScale)) return false;
                        break;
                    case 5:
                        if (wireType != ProtobufWireWriter.WireTypeFixed64 || !reader.TryReadDouble(out touch.PressureScale)) return false;
                        break;
                    case 6:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader contactReader)) return false;
                        TouchContact contact = new TouchContact();
                        if (!TryReadTouchContact(ref contactReader, contact)) return false;
                        touch.Contacts.Add(contact);
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadTouchContact(ref ProtobufWireReader reader, TouchContact contact)
        {
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out contact.FingerId)) return false;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadBool(out contact.Continued)) return false;
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadInt32(out int endVal)) return false;
                        contact.End = (TouchEnd)endVal;
                        break;
                    case 4:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out contact.FirstTimeUs)) return false;
                        break;
                    case 5:
                        if (!reader.TryReadPackedOrSingleSInt32(wireType, contact.TimeStepsUs)) return false;
                        break;
                    case 6:
                        if (!reader.TryReadPackedOrSingleSInt32(wireType, contact.X)) return false;
                        break;
                    case 7:
                        if (!reader.TryReadPackedOrSingleSInt32(wireType, contact.Y)) return false;
                        break;
                    case 8:
                        if (!reader.TryReadPackedOrSingleSInt32(wireType, contact.SizeMajor)) return false;
                        break;
                    case 9:
                        if (!reader.TryReadPackedOrSingleSInt32(wireType, contact.SizeMinor)) return false;
                        break;
                    case 10:
                        if (!reader.TryReadPackedOrSingleSInt32(wireType, contact.Pressure)) return false;
                        break;
                    case 11:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader holdReader)) return false;
                        TouchHold hold = new TouchHold();
                        if (!TryReadTouchHold(ref holdReader, hold)) return false;
                        contact.Holds.Add(hold);
                        break;
                    case 12:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out contact.EventsSeen)) return false;
                        break;
                    case 13:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader beforeReader)) return false;
                        if (contact.Before == null)
                        {
                            contact.Before = new ContactBefore();
                        }
                        if (!TryReadContactBefore(ref beforeReader, contact.Before)) return false;
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadContactBefore(ref ProtobufWireReader reader, ContactBefore before)
        {
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out before.DownTimeUs)) return false;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt32(out before.MinX)) return false;
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt32(out before.MaxX)) return false;
                        break;
                    case 4:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt32(out before.MinY)) return false;
                        break;
                    case 5:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt32(out before.MaxY)) return false;
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadTouchHold(ref ProtobufWireReader reader, TouchHold hold)
        {
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out hold.StartTimeUs)) return false;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out hold.EndTimeUs)) return false;
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out hold.SampleCount)) return false;
                        break;
                    case 4:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt32(out hold.X)) return false;
                        break;
                    case 5:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt32(out hold.Y)) return false;
                        break;
                    case 6:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt32(out hold.SizeMajor)) return false;
                        break;
                    case 7:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt32(out hold.SizeMinor)) return false;
                        break;
                    case 8:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt32(out hold.Pressure)) return false;
                        break;
                    case 9:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader statsReader)) return false;
                        if (!TryReadHoldStats(ref statsReader, out HoldStats stats)) return false;
                        hold.Stats.Add(stats);
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadScreenRecord(ref ProtobufWireReader reader, out ScreenRecord record)
        {
            record = default;
            return TryReadScreenRecordInto(ref reader, ref record);
        }

        private static bool TryReadScreenRecordInto(ref ProtobufWireReader reader, ref ScreenRecord record)
        {
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out record.TimeUs)) return false;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out record.WidthPx)) return false;
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out record.HeightPx)) return false;
                        break;
                    case 4:
                        if (wireType != ProtobufWireWriter.WireTypeFixed32 || !reader.TryReadFloat(out record.DensityDpi)) return false;
                        break;
                    case 5:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadInt32(out int oriVal)) return false;
                        record.Orientation = (Orientation)oriVal;
                        break;
                    case 6:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadInt32(out int origVal)) return false;
                        record.Origin = (CoordinateOrigin)origVal;
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadValue(ref ProtobufWireReader reader, out Value value)
        {
            value = Value.Empty;
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadString(out string text)) return false;
                        value = Value.FromText(text);
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeFixed64 || !reader.TryReadDouble(out double num)) return false;
                        value = Value.FromNumber(num);
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadBool(out bool flag)) return false;
                        value = Value.FromFlag(flag);
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadValueMapEntry(ref ProtobufWireReader reader, Dictionary<string, Value> map)
        {
            string key = string.Empty;
            Value value = Value.Empty;

            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadString(out key)) return false;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader valReader)) return false;
                        if (!TryReadValue(ref valReader, out value)) return false;
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            map[key] = value;
            return true;
        }

        private static bool TryReadGameEvent(ref ProtobufWireReader reader, GameEvent evt)
        {
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out evt.TimeUs)) return false;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadString(out evt.Name)) return false;
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader entryReader)) return false;
                        if (!TryReadValueMapEntry(ref entryReader, evt.Fields)) return false;
                        break;
                    case 4:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadString(out string outcome)) return false;
                        evt.HasOutcome = true;
                        evt.Outcome = outcome;
                        break;
                    case 5:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadString(out evt.SpanName)) return false;
                        break;
                    case 6:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadInt32(out int markerVal)) return false;
                        evt.SpanMarker = (SpanMarker)markerVal;
                        break;
                    case 7:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out evt.SpanNumber)) return false;
                        break;
                    case 8:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out evt.Order)) return false;
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadStateRecord(ref ProtobufWireReader reader, StateRecord rec)
        {
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out rec.TimeUs)) return false;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadInt32(out int kindVal)) return false;
                        rec.Kind = (StateKind)kindVal;
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader entryReader)) return false;
                        if (!TryReadValueMapEntry(ref entryReader, rec.Values)) return false;
                        break;
                    case 4:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadString(out string removedKey)) return false;
                        rec.RemovedKeys.Add(removedKey);
                        break;
                    case 5:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out rec.Order)) return false;
                        break;
                    case 6:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out rec.Version)) return false;
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadCarried(ref ProtobufWireReader reader, Carried carried)
        {
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader stateReader)) return false;
                        if (carried.State == null)
                        {
                            carried.State = new State();
                        }
                        if (!TryReadState(ref stateReader, carried.State)) return false;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader spansReader)) return false;
                        if (carried.OpenSpans == null)
                        {
                            carried.OpenSpans = new OpenSpans();
                        }
                        if (!TryReadOpenSpans(ref spansReader, carried.OpenSpans)) return false;
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out long lastEvtTime)) return false;
                        carried.HasLastEventTimeUs = true;
                        carried.LastEventTimeUs = lastEvtTime;
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadState(ref ProtobufWireReader reader, State state)
        {
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader entryReader)) return false;
                        if (!TryReadValueMapEntry(ref entryReader, state.Values)) return false;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out state.Version)) return false;
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadOpenSpans(ref ProtobufWireReader reader, OpenSpans openSpans)
        {
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadSubReader(out ProtobufWireReader spanReader)) return false;
                        if (!TryReadOpenSpan(ref spanReader, out OpenSpan span)) return false;
                        openSpans.Spans.Add(span);
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadOpenSpan(ref ProtobufWireReader reader, out OpenSpan span)
        {
            span = new OpenSpan(string.Empty, 0, 0);
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadString(out span.Name)) return false;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out span.Number)) return false;
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out span.OpenedTimeUs)) return false;
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadLifecycleRecord(ref ProtobufWireReader reader, out LifecycleRecord rec)
        {
            rec = default;
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out rec.TimeUs)) return false;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadInt32(out int kindVal)) return false;
                        rec.Kind = (LifecycleKind)kindVal;
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out rec.ClockStepS)) return false;
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadGapRecord(ref ProtobufWireReader reader, out GapRecord rec)
        {
            rec = default;
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadInt32(out int sigVal)) return false;
                        rec.Signal = (GapSignal)sigVal;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadInt32(out int clkVal)) return false;
                        rec.Clock = (Clock)clkVal;
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out rec.StartTimeUs)) return false;
                        break;
                    case 4:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out rec.EndTimeUs)) return false;
                        break;
                    case 5:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadInt32(out int causeVal)) return false;
                        rec.Cause = (GapCause)causeVal;
                        break;
                    case 6:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out rec.BatchesDropped)) return false;
                        break;
                    case 7:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint64(out rec.FirstDroppedSequence)) return false;
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadDiagnostic(ref ProtobufWireReader reader, Diagnostic diag)
        {
            while (!reader.IsAtEnd)
            {
                if (!reader.TryReadTag(out int fieldNumber, out int wireType)) return false;

                switch (fieldNumber)
                {
                    case 1:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadSInt64(out diag.TimeUs)) return false;
                        break;
                    case 2:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadInt32(out int sevVal)) return false;
                        diag.Severity = (Severity)sevVal;
                        break;
                    case 3:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadString(out diag.Code)) return false;
                        break;
                    case 4:
                        if (wireType != ProtobufWireWriter.WireTypeLengthDelimited || !reader.TryReadString(out diag.Detail)) return false;
                        break;
                    case 5:
                        if (wireType != ProtobufWireWriter.WireTypeVarint || !reader.TryReadVarint32(out diag.Count)) return false;
                        break;
                    default:
                        if (!reader.TrySkipField(wireType)) return false;
                        break;
                }
            }

            return true;
        }
    }
}
