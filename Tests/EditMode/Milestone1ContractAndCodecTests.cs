using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Encoding;
using NUnit.Framework;

namespace Asparagus.Sdk.Tests
{
    [TestFixture]
    public sealed class Milestone1ContractAndCodecTests
    {
        // ====================================================================
        // 1. Full Schema v3 Fixture Round-Trip (batch_schema_2.json)
        // ====================================================================

        [Test]
        public void FullSchemaV3Fixture_RoundTrips_UncompressedAndGzip()
        {
            Batch original = BuildSchemaV3FixtureBatch();
            ProtobufWireWriter writer = new ProtobufWireWriter();

            // Uncompressed round-trip
            Assert.IsTrue(BatchProtobufCodec.TryEncodeBatch(original, writer, out string encodeErr), encodeErr);
            byte[] rawBytes = writer.ToArray();
            Assert.Greater(rawBytes.Length, 0);
            Assert.IsFalse(rawBytes.Length >= 2 && rawBytes[0] == 0x1f && rawBytes[1] == 0x8b);

            Assert.IsTrue(
                BatchProtobufCodec.TryDecodeBatch(rawBytes, 0, rawBytes.Length, out Batch decodedRaw, out string decodeRawErr),
                decodeRawErr);
            AssertBatchesDeepEqual(original, decodedRaw);

            // GZip-compressed round-trip via TryEncodeUploadPayload (reusing scratchWriter)
            using (MemoryStream gzipStream = new MemoryStream())
            {
                Assert.IsTrue(
                    BatchProtobufCodec.TryEncodeUploadPayload(original, true, writer, gzipStream, out string gzErr),
                    gzErr);

                byte[] gzipBytes = gzipStream.ToArray();
                Assert.Greater(gzipBytes.Length, 2);
                Assert.AreEqual(0x1f, gzipBytes[0], "GZip magic byte 0");
                Assert.AreEqual(0x8b, gzipBytes[1], "GZip magic byte 1");

                Assert.IsTrue(
                    BatchProtobufCodec.TryDecodeBatch(gzipBytes, 0, gzipBytes.Length, out Batch decodedGz, out string decodeGzErr),
                    decodeGzErr);
                AssertBatchesDeepEqual(original, decodedGz);
            }

            // Uncompressed path via TryEncodeUploadPayload
            using (MemoryStream plainStream = new MemoryStream())
            {
                Assert.IsTrue(
                    BatchProtobufCodec.TryEncodeUploadPayload(original, false, writer, plainStream, out string plainErr),
                    plainErr);

                byte[] plainBytes = plainStream.ToArray();
                Assert.AreEqual(rawBytes, plainBytes);
            }
        }

        // ====================================================================
        // 2. Explicit Presence vs Empty / Zero Semantics
        // ====================================================================

        [Test]
        public void OptionalAndPresenceFields_DistinguishAbsentFromZeroOrEmpty()
        {
            // Case A: UtcOffsetMinutes = 0 (present), SensorUs = 0 (present), Outcome = "" (present),
            // Carried with empty State, empty OpenSpans, and LastEventTimeUs = 0 (present).
            Batch presentZeroBatch = new Batch
            {
                Header = new Header
                {
                    IdOrg = "org",
                    IdDeployment = "dep",
                    IdSource = "src",
                    IdSession = "ses",
                    Sequence = 1,
                    SchemaVersion = BatchConstants.SchemaVersion
                },
                Carried = new Carried
                {
                    State = new State { Version = 0 },
                    OpenSpans = new OpenSpans()
                }
            };
            presentZeroBatch.Header.SetUtcOffsetMinutes(0);
            ClockReading zeroSensorClock = new ClockReading { SessionUs = 1000, UnityUs = 500, WallUtcMs = 2000 };
            zeroSensorClock.SetSensorUs(0);
            presentZeroBatch.Header.SetClocks(zeroSensorClock);
            presentZeroBatch.Carried.SetLastEventTimeUs(0);

            GameEvent emptyOutcomeEvent = new GameEvent { TimeUs = 100, Name = "ev", Order = 1 };
            emptyOutcomeEvent.SetOutcome(string.Empty);
            emptyOutcomeEvent.Fields["empty_val"] = Value.Empty;
            emptyOutcomeEvent.Fields["empty_text"] = Value.FromText(string.Empty);
            emptyOutcomeEvent.Fields["zero_num"] = Value.FromNumber(0.0);
            emptyOutcomeEvent.Fields["false_flag"] = Value.FromFlag(false);
            presentZeroBatch.Events.Add(emptyOutcomeEvent);

            ProtobufWireWriter writer = new ProtobufWireWriter();
            Assert.IsTrue(BatchProtobufCodec.TryEncodeBatch(presentZeroBatch, writer, out string err1), err1);
            byte[] bytes1 = writer.ToArray();
            Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(bytes1, 0, bytes1.Length, out Batch dec1, out string derr1), derr1);

            Assert.IsNotNull(dec1.Header);
            Assert.IsTrue(dec1.Header.HasUtcOffsetMinutes);
            Assert.AreEqual(0, dec1.Header.UtcOffsetMinutes);
            Assert.IsTrue(dec1.Header.HasClocks);
            Assert.IsTrue(dec1.Header.Clocks.HasSensorUs);
            Assert.AreEqual(0L, dec1.Header.Clocks.SensorUs);

            Assert.IsNotNull(dec1.Carried);
            Assert.IsNotNull(dec1.Carried.State, "Present-and-empty State must survive round-trip as non-null.");
            Assert.AreEqual(0, dec1.Carried.State.Values.Count);
            Assert.AreEqual(0u, dec1.Carried.State.Version);
            Assert.IsNotNull(dec1.Carried.OpenSpans, "Present-and-empty OpenSpans must survive round-trip as non-null.");
            Assert.AreEqual(0, dec1.Carried.OpenSpans.Spans.Count);
            Assert.IsTrue(dec1.Carried.HasLastEventTimeUs);
            Assert.AreEqual(0L, dec1.Carried.LastEventTimeUs);

            Assert.AreEqual(1, dec1.Events.Count);
            Assert.IsTrue(dec1.Events[0].HasOutcome);
            Assert.AreEqual(string.Empty, dec1.Events[0].Outcome);
            Assert.AreEqual(ValueKind.None, dec1.Events[0].Fields["empty_val"].Kind);
            Assert.AreEqual(ValueKind.Text, dec1.Events[0].Fields["empty_text"].Kind);
            Assert.AreEqual(string.Empty, dec1.Events[0].Fields["empty_text"].TextValue);
            Assert.AreEqual(ValueKind.Number, dec1.Events[0].Fields["zero_num"].Kind);
            Assert.AreEqual(0.0, dec1.Events[0].Fields["zero_num"].NumberValue);
            Assert.AreEqual(ValueKind.Flag, dec1.Events[0].Fields["false_flag"].Kind);
            Assert.IsFalse(dec1.Events[0].Fields["false_flag"].FlagValue);

            // Case B: Absent optional fields, absent Carried
            Batch absentBatch = new Batch
            {
                Header = new Header
                {
                    IdOrg = "org",
                    IdDeployment = "dep",
                    IdSource = "src",
                    IdSession = "ses",
                    Sequence = 2,
                    SchemaVersion = BatchConstants.SchemaVersion
                },
                Carried = null
            };
            absentBatch.Header.SetClocks(new ClockReading { SessionUs = 1000, UnityUs = 500, WallUtcMs = 2000 });
            absentBatch.Events.Add(new GameEvent { TimeUs = 100, Name = "ev2", Order = 1 });

            Assert.IsTrue(BatchProtobufCodec.TryEncodeBatch(absentBatch, writer, out string err2), err2);
            byte[] bytes2 = writer.ToArray();
            Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(bytes2, 0, bytes2.Length, out Batch dec2, out string derr2), derr2);

            Assert.IsFalse(dec2.Header.HasUtcOffsetMinutes);
            Assert.IsFalse(dec2.Header.Clocks.HasSensorUs);
            Assert.IsNull(dec2.Carried);
            Assert.IsFalse(dec2.Events[0].HasOutcome);

            // Case C: Present Carried with absent State and absent OpenSpans
            Batch partialCarriedBatch = new Batch
            {
                Header = new Header { IdOrg = "o", IdDeployment = "d", IdSource = "s", IdSession = "x", Sequence = 3 },
                Carried = new Carried { State = null, OpenSpans = null }
            };
            partialCarriedBatch.Carried.SetLastEventTimeUs(555L);

            Assert.IsTrue(BatchProtobufCodec.TryEncodeBatch(partialCarriedBatch, writer, out string err3), err3);
            byte[] bytes3 = writer.ToArray();
            Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(bytes3, 0, bytes3.Length, out Batch dec3, out string derr3), derr3);

            Assert.IsNotNull(dec3.Carried);
            Assert.IsNull(dec3.Carried.State);
            Assert.IsNull(dec3.Carried.OpenSpans);
            Assert.IsTrue(dec3.Carried.HasLastEventTimeUs);
            Assert.AreEqual(555L, dec3.Carried.LastEventTimeUs);
        }

        [Test]
        public void HeaderStrings_AreTruncatedTo1024Utf8BytesOnEncode()
        {
            string oversized = new string('é', 600); // 1200 UTF-8 bytes
            Batch batch = new Batch
            {
                Header = new Header
                {
                    IdOrg = oversized,
                    IdDeployment = oversized,
                    IdSource = oversized,
                    IdSession = oversized,
                    OsVersion = oversized,
                    DeviceModel = oversized,
                    SettingsVersion = oversized,
                    Sequence = 1
                }
            };

            ProtobufWireWriter writer = new ProtobufWireWriter();
            Assert.IsTrue(BatchProtobufCodec.TryEncodeBatch(batch, writer, out string err), err);
            byte[] bytes = writer.ToArray();
            Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(bytes, 0, bytes.Length, out Batch decoded, out string derr), derr);

            Assert.LessOrEqual(Utf8StringBounds.GetUtf8ByteCount(decoded.Header.IdOrg), BatchConstants.MaxHeaderStringBytes);
            Assert.LessOrEqual(Utf8StringBounds.GetUtf8ByteCount(decoded.Header.IdDeployment), BatchConstants.MaxHeaderStringBytes);
            Assert.LessOrEqual(Utf8StringBounds.GetUtf8ByteCount(decoded.Header.IdSource), BatchConstants.MaxHeaderStringBytes);
            Assert.LessOrEqual(Utf8StringBounds.GetUtf8ByteCount(decoded.Header.IdSession), BatchConstants.MaxHeaderStringBytes);
            Assert.LessOrEqual(Utf8StringBounds.GetUtf8ByteCount(decoded.Header.OsVersion), BatchConstants.MaxHeaderStringBytes);
            Assert.LessOrEqual(Utf8StringBounds.GetUtf8ByteCount(decoded.Header.DeviceModel), BatchConstants.MaxHeaderStringBytes);
            Assert.LessOrEqual(Utf8StringBounds.GetUtf8ByteCount(decoded.Header.SettingsVersion), BatchConstants.MaxHeaderStringBytes);
            Assert.AreEqual(512, decoded.Header.IdOrg.Length);
        }

        // ====================================================================
        // 3. UploadResponse & Forward-Compatibility Tests
        // ====================================================================

        [Test]
        public void UploadResponse_DecodesStoredAndSkipsUnknownFields()
        {
            byte[] canonicalStored = { 0x08, 0x01 };
            Assert.IsTrue(BatchProtobufCodec.TryDecodeUploadResponse(canonicalStored, 0, canonicalStored.Length, out UploadResponse resp));
            Assert.AreEqual(UploadStatus.Stored, resp.Status);

            // Unknown enum value preserved safely
            byte[] futureEnum = { 0x08, 0x2a };
            Assert.IsTrue(BatchProtobufCodec.TryDecodeUploadResponse(futureEnum, 0, futureEnum.Length, out UploadResponse respFuture));
            Assert.AreEqual((UploadStatus)42, respFuture.Status);

            // Append unknown fields of all 4 proto3 wire types (varint, fixed64, length-delimited, fixed32)
            ProtobufWireWriter writer = new ProtobufWireWriter();
            writer.WriteEnum(1, (int)UploadStatus.Stored);
            writer.WriteUInt64(99, 123456789012345UL);
            writer.WriteDoubleAlways(100, 3.1415926535);
            writer.WriteStringAlways(101, "future-metadata");
            writer.WriteFloatAlways(102, 42.5f);

            byte[] withUnknowns = writer.ToArray();
            Assert.IsTrue(BatchProtobufCodec.TryDecodeUploadResponse(withUnknowns, 0, withUnknowns.Length, out UploadResponse respWithUnknowns));
            Assert.AreEqual(UploadStatus.Stored, respWithUnknowns.Status);
        }

        [Test]
        public void BatchDecoder_SkipsUnknownFieldsAndSupportsUnpackedRepeatedSInt32()
        {
            ProtobufWireWriter writer = new ProtobufWireWriter();

            // Write a SensorBlock with UNPACKED sint32 entries for field 12 (x) and an unknown field
            int blockMark = writer.BeginLengthDelimited(2);
            writer.WriteEnum(1, (int)Sensor.Accelerometer);
            writer.WriteSInt32Always(12, 100);
            writer.WriteSInt32Always(12, -50);
            writer.WriteSInt32Always(12, int.MinValue);
            writer.WriteStringAlways(99, "future_block_field");
            writer.EndLengthDelimited(blockMark);

            // Append unknown top-level Batch fields of every proto3 wire type
            writer.WriteUInt64(50, 999UL);
            writer.WriteDoubleAlways(51, 9.81);
            writer.WriteStringAlways(52, "unknown-batch-extension");
            writer.WriteFloatAlways(53, 1.5f);

            byte[] bytes = writer.ToArray();
            Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(bytes, 0, bytes.Length, out Batch decoded, out string err), err);
            Assert.AreEqual(1, decoded.SensorBlocks.Count);
            Assert.AreEqual(Sensor.Accelerometer, decoded.SensorBlocks[0].Sensor);
            CollectionAssert.AreEqual(new[] { 100, -50, int.MinValue }, decoded.SensorBlocks[0].X);

            // Corrupted / truncated bytes fail cleanly without throwing
            byte[] truncated = { 0x0a, 0x20, 0x01, 0x02 };
            Assert.IsFalse(BatchProtobufCodec.TryDecodeBatch(truncated, 0, truncated.Length, out Batch failedBatch, out string failErr));
            Assert.IsNull(failedBatch);
            Assert.IsNotEmpty(failErr);
        }

        // ====================================================================
        // 4. UTF-8 Truncation Safety Tests
        // ====================================================================

        [Test]
        public void Utf8StringBounds_TruncateUtf8_NeverSplitsMultiByteOrSurrogatePairs()
        {
            // "Café 小店 🛒":
            // 'C'(1) 'a'(1) 'f'(1) 'é'(2) ' '(1) '小'(3) '店'(3) ' '(1) '🛒'(4) => total 17 bytes
            const string sample = "Café 小店 🛒";
            Assert.AreEqual(17, Utf8StringBounds.GetUtf8ByteCount(sample));

            // Fast path returns exact same string reference when within limit
            string unchanged = Utf8StringBounds.TruncateUtf8(sample, 17);
            Assert.AreSame(sample, unchanged);
            Assert.AreSame(sample, Utf8StringBounds.TruncateUtf8(sample, 1024));

            UTF8Encoding strictUtf8 = new UTF8Encoding(false, true);

            for (int limit = 0; limit <= 20; limit++)
            {
                string truncated = Utf8StringBounds.TruncateUtf8(sample, limit);
                byte[] utf8Bytes = strictUtf8.GetBytes(truncated);
                Assert.LessOrEqual(utf8Bytes.Length, limit, "Limit=" + limit);

                string roundTripped = strictUtf8.GetString(utf8Bytes);
                Assert.AreEqual(truncated, roundTripped, "Invalid UTF-8 at limit=" + limit);
                Assert.IsFalse(roundTripped.Contains("\uFFFD"), "Replacement char at limit=" + limit);
                Assert.IsTrue(sample.StartsWith(truncated, StringComparison.Ordinal));
            }

            // Verify exact expected boundary cuts:
            Assert.AreEqual("", Utf8StringBounds.TruncateUtf8(sample, 0));
            Assert.AreEqual("Caf", Utf8StringBounds.TruncateUtf8(sample, 4));          // 'é' needs 2 bytes (3+2=5 > 4)
            Assert.AreEqual("Café", Utf8StringBounds.TruncateUtf8(sample, 5));         // 3+2=5
            Assert.AreEqual("Café ", Utf8StringBounds.TruncateUtf8(sample, 8));        // '小' needs 3 bytes (6+3=9 > 8)
            Assert.AreEqual("Café 小", Utf8StringBounds.TruncateUtf8(sample, 9));      // 6+3=9
            Assert.AreEqual("Café 小店 ", Utf8StringBounds.TruncateUtf8(sample, 16));  // '🛒' needs 4 bytes (13+4=17 > 16)
            Assert.AreEqual("Café 小店 🛒", Utf8StringBounds.TruncateUtf8(sample, 17));
        }

        // ====================================================================
        // 5. SessionJsonCodec & Settings Presence Rules
        // ====================================================================

        [Test]
        public void SessionJsonCodec_SerializesSessionStartRequest_InProtobufSnakeCaseJson()
        {
            SessionStartRequest req = new SessionStartRequest
            {
                ApiKey = "test-api-key",
                DeviceIdentifier = "dev-id-123",
                Platform = Platform.Android,
                AppVersion = "2.1.0",
                BuildNumber = "42",
                Demo = true,
                Device = new DeviceProperties
                {
                    Maker = "Acme",
                    Model = "Phone \"Pro\" \\ 1",
                    OsVersion = "15",
                    DeviceType = "Handheld",
                    ScreenWidthPx = 1080,
                    ScreenHeightPx = 2400,
                    ScreenDensityDpi = 420f,
                    HasGyroscope = true,
                    Timezone = "Europe/London",
                    Locale = "en-GB"
                }
            };
            req.SetSdkVersion(new SdkVersion(1, 2, 3));
            req.SessionProperties["cohort"] = "beta";
            req.SessionProperties["rank"] = 7.5;
            req.SessionProperties["vip"] = false;
            req.SessionProperties["empty"] = Value.Empty;

            Assert.IsTrue(SessionJsonCodec.TrySerializeSessionStartRequest(req, out string json, out string err), err);

            StringAssert.Contains("\"api_key\":\"test-api-key\"", json);
            StringAssert.Contains("\"device_identifier\":\"dev-id-123\"", json);
            StringAssert.Contains("\"sdk_version\":{\"major\":1,\"minor\":2,\"patch\":3}", json);
            StringAssert.Contains("\"platform\":\"PLATFORM_ANDROID\"", json);
            StringAssert.Contains("\"model\":\"Phone \\\"Pro\\\" \\\\ 1\"", json);
            StringAssert.Contains("\"screen_width_px\":1080", json);
            StringAssert.Contains("\"has_gyroscope\":true", json);
            StringAssert.Contains("\"app_version\":\"2.1.0\"", json);
            StringAssert.Contains("\"build_number\":\"42\"", json);
            StringAssert.Contains("\"cohort\":{\"text\":\"beta\"}", json);
            StringAssert.Contains("\"rank\":{\"number\":7.5}", json);
            StringAssert.Contains("\"vip\":{\"flag\":false}", json);
            StringAssert.Contains("\"empty\":{}", json);
            StringAssert.Contains("\"demo\":true", json);
        }

        [Test]
        public void SessionJsonCodec_ParsesSuccessEnvelope_WithExplicitZerosDefaultsClampingAndUnknowns()
        {
            // Tests:
            // - snake_case & camelCase mixed
            // - 64-bit token_expires_utc_ms as string, server_utc_ms as number
            // - explicit 0 & false override defaults (accelerometer_hz: 0, compress: false, queue_max_bytes: 0)
            // - omitted fields retain defaults.json
            // - "At least 1" fields with 0 (batch_length_s: 0, token_life_s: 0, new_session_after_s: 0) keep defaults
            // - retry_first_wait_ms (50000) > retry_longest_wait_ms (10000) clamps EffectiveRetryFirstWaitMs to 10000
            // - unknown motion_source enum string keeps default MotionSource.Native
            // - unknown nested JSON fields are ignored silently
            const string json = @"{
                ""status"": ""success"",
                ""extra_top_level"": { ""nested"": [1, true, null, ""ignored""] },
                ""data"": {
                    ""id_org"": ""org-1"",
                    ""idDeployment"": ""dep-1"",
                    ""id_source"": ""src-1"",
                    ""idSession"": ""ses-1"",
                    ""token"": ""jwt-opaque"",
                    ""token_expires_utc_ms"": ""1794823260000"",
                    ""serverUtcMs"": 1794736860000,
                    ""batch_endpoint"": ""https://stage.example/asparagus/v1/batches"",
                    ""access_request_endpoint"": ""https://stage.example/access"",
                    ""forgetRequestEndpoint"": ""https://stage.example/forget"",
                    ""unknown_data_field"": 999,
                    ""settings"": {
                        ""version"": ""v42"",
                        ""accelerometer_hz"": 0,
                        ""gyroscopeHz"": 50,
                        ""magnetometer_hz"": 0,
                        ""motion_source"": ""MOTION_SOURCE_FUTURE_UNKNOWN"",
                        ""hold_run_length"": 0,
                        ""batch_length_s"": 0,
                        ""compress"": false,
                        ""queue_max_bytes"": 0,
                        ""retry_first_wait_ms"": 50000,
                        ""retry_longest_wait_ms"": 10000,
                        ""pause_touch_on_keyboard"": true,
                        ""token_life_s"": 0,
                        ""new_session_after_s"": 0,
                        ""unknown_setting"": { ""a"": 1 }
                    },
                    ""models"": [
                        {
                            ""purpose"": ""ad_propensity"",
                            ""name"": ""prop_v1"",
                            ""version"": ""1.0"",
                            ""download_url"": ""https://cdn.example/m.onnx"",
                            ""arguments"": [""level"", ""score""],
                            ""unknown_model_prop"": true
                        }
                    ]
                }
            }";

            Assert.IsTrue(
                SessionJsonCodec.TryParseSessionStartResponse(json, out SessionStartResponse resp, out string[] errors, out string parseErr),
                parseErr);
            Assert.IsNotNull(resp);
            Assert.AreEqual(0, errors.Length);

            Assert.AreEqual("org-1", resp.IdOrg);
            Assert.AreEqual("dep-1", resp.IdDeployment);
            Assert.AreEqual("src-1", resp.IdSource);
            Assert.AreEqual("ses-1", resp.IdSession);
            Assert.AreEqual("jwt-opaque", resp.Token);
            Assert.AreEqual(1794823260000L, resp.TokenExpiresUtcMs);
            Assert.AreEqual(1794736860000L, resp.ServerUtcMs);
            Assert.AreEqual("https://stage.example/asparagus/v1/batches", resp.BatchEndpoint);
            Assert.AreEqual("https://stage.example/access", resp.AccessRequestEndpoint);
            Assert.AreEqual("https://stage.example/forget", resp.ForgetRequestEndpoint);

            SessionSettings s = resp.Settings;
            Assert.AreEqual("v42", s.Version);
            Assert.AreEqual(0u, s.AccelerometerHz, "Explicit 0 must override default 100");
            Assert.AreEqual(50u, s.GyroscopeHz);
            Assert.AreEqual(SessionSettings.DefaultGravityHz, s.GravityHz, "Omitted retains default");
            Assert.AreEqual(SessionSettings.DefaultTouchHz, s.TouchHz, "Omitted retains default");
            Assert.AreEqual(0u, s.MagnetometerHz, "Explicit 0 must override default 10");
            Assert.AreEqual(MotionSource.Native, s.MotionSource, "Unknown enum retains default Native");
            Assert.AreEqual(0u, s.HoldRunLength, "Explicit 0 switches holds off");
            Assert.AreEqual(SessionSettings.DefaultBatchLengthS, s.BatchLengthS, "Invalid 0 for 'At least 1' falls back to default 10");
            Assert.IsFalse(s.Compress, "Explicit false must override default true");
            Assert.AreEqual(0u, s.QueueMaxBytes, "Explicit 0 disables disk queue");
            Assert.AreEqual(SessionSettings.DefaultQueueMaxAgeS, s.QueueMaxAgeS, "Omitted retains default 900");
            Assert.AreEqual(50000u, s.RetryFirstWaitMs);
            Assert.AreEqual(10000u, s.RetryLongestWaitMs);
            Assert.AreEqual(10000u, s.EffectiveRetryFirstWaitMs, "First wait above longest clamps to longest");
            Assert.IsTrue(s.PauseTouchOnKeyboard);
            Assert.AreEqual(SessionSettings.DefaultTokenLifeS, s.TokenLifeS, "Invalid 0 falls back to default 86400");
            Assert.AreEqual(SessionSettings.DefaultNewSessionAfterS, s.NewSessionAfterS, "Invalid 0 falls back to default 1800");

            Assert.AreEqual(1, resp.Models.Count);
            Assert.AreEqual("ad_propensity", resp.Models[0].Purpose);
            Assert.AreEqual("prop_v1", resp.Models[0].Name);
            Assert.AreEqual("1.0", resp.Models[0].Version);
            Assert.AreEqual("https://cdn.example/m.onnx", resp.Models[0].DownloadUrl);
            CollectionAssert.AreEqual(new[] { "level", "score" }, resp.Models[0].Arguments);
        }

        [Test]
        public void SessionJsonCodec_ParsesNewSessionAfterBounds_MotionSource_AndErrorEnvelope()
        {
            // new_session_after_s = 86401 (out of range -> keeps default 1800) & motion_source = UNSPECIFIED (keeps Native)
            const string outOfRangeJson = @"{
                ""status"": ""success"",
                ""data"": {
                    ""settings"": {
                        ""motion_source"": ""MOTION_SOURCE_UNSPECIFIED"",
                        ""new_session_after_s"": 86401,
                        ""retry_first_wait_ms"": 0,
                        ""retry_longest_wait_ms"": 0
                    }
                }
            }";
            Assert.IsTrue(SessionJsonCodec.TryParseSessionStartResponse(outOfRangeJson, out SessionStartResponse r1, out _, out string e1), e1);
            Assert.AreEqual(MotionSource.Native, r1.Settings.MotionSource);
            Assert.AreEqual(1800u, r1.Settings.NewSessionAfterS);
            Assert.AreEqual(2000u, r1.Settings.RetryFirstWaitMs);
            Assert.AreEqual(300000u, r1.Settings.RetryLongestWaitMs);

            // Valid upper bound 86400 & MOTION_SOURCE_UNITY
            const string validBoundsJson = @"{
                ""status"": ""success"",
                ""data"": {
                    ""settings"": {
                        ""motion_source"": ""MOTION_SOURCE_UNITY"",
                        ""new_session_after_s"": 86400
                    }
                }
            }";
            Assert.IsTrue(SessionJsonCodec.TryParseSessionStartResponse(validBoundsJson, out SessionStartResponse r2, out _, out string e2), e2);
            Assert.AreEqual(MotionSource.Unity, r2.Settings.MotionSource);
            Assert.AreEqual(86400u, r2.Settings.NewSessionAfterS);

            // Error envelope
            const string errorJson = @"{
                ""status"": ""error"",
                ""errors"": [""invalid api key"", { ""message"": ""deployment disabled"" }]
            }";
            Assert.IsFalse(SessionJsonCodec.TryParseSessionStartResponse(errorJson, out SessionStartResponse rErr, out string[] errs, out string parseErr));
            Assert.IsNull(rErr);
            CollectionAssert.AreEqual(new[] { "invalid api key", "deployment disabled" }, errs);
            Assert.AreEqual("invalid api key", parseErr);

            // Malformed JSON never throws
            Assert.IsFalse(SessionJsonCodec.TryParseSessionStartResponse("{broken", out _, out _, out string badErr));
            Assert.IsNotEmpty(badErr);
        }

        [Test]
        public void SessionSettingsDefaults_MatchContractDefaultsJson()
        {
            SessionSettings d = SessionSettings.CreateDefaults();
            Assert.AreEqual(100u, d.AccelerometerHz);
            Assert.AreEqual(100u, d.GyroscopeHz);
            Assert.AreEqual(10u, d.GravityHz);
            Assert.AreEqual(60u, d.TouchHz);
            Assert.AreEqual(MotionSource.Native, d.MotionSource);
            Assert.AreEqual(120u, d.NativeBufferS);
            Assert.AreEqual(1000u, d.NativeStartLimitMs);
            Assert.AreEqual(5000u, d.NativeStallLimitMs);
            Assert.AreEqual(0u, d.AccelerometerHoldTolerance);
            Assert.AreEqual(0u, d.GyroscopeHoldTolerance);
            Assert.AreEqual(0u, d.GravityHoldTolerance);
            Assert.AreEqual(0u, d.TouchHoldTolerance);
            Assert.AreEqual(10u, d.HoldRunLength);
            Assert.AreEqual(250u, d.GapThresholdMs);
            Assert.AreEqual(5u, d.GapThresholdPeriods);
            Assert.AreEqual(10u, d.BatchLengthS);
            Assert.IsTrue(d.Compress);
            Assert.AreEqual(50000000u, d.QueueMaxBytes);
            Assert.AreEqual(900u, d.QueueMaxAgeS);
            Assert.AreEqual(2000u, d.RetryFirstWaitMs);
            Assert.AreEqual(300000u, d.RetryLongestWaitMs);
            Assert.IsFalse(d.PauseTouchOnKeyboard);
            Assert.AreEqual(86400u, d.TokenLifeS);
            Assert.AreEqual(1800u, d.NewSessionAfterS);
            Assert.AreEqual(10u, d.MagnetometerHz);
            Assert.AreEqual(0u, d.MagnetometerHoldTolerance);
            Assert.AreEqual(2000u, d.EffectiveRetryFirstWaitMs);
        }

        [Test]
        public void ContractCatalogAndProtoEnumNames_AreCompleteAndConsistent()
        {
            Assert.AreEqual(10, CoreSpans.All.Length);
            Assert.AreEqual("banner", CoreSpans.Banner);
            Assert.GreaterOrEqual(Array.IndexOf(CoreSpans.All, CoreSpans.Banner), 0);
            Assert.AreEqual("ad", CoreSpans.AdForType(null));
            Assert.AreEqual("ad", CoreSpans.AdForType(string.Empty));
            Assert.AreEqual("ad_rewarded", CoreSpans.AdForType("rewarded"));
            Assert.AreEqual("ad_banner", CoreSpans.AdForType("banner"));

            Assert.AreEqual(11, CoreEventNames.All.Length);
            Assert.IsTrue(CoreEventNames.TryGetMetadata(CoreEventNames.GameLevelStarted, out CoreEventMetadata lvlStart));
            Assert.AreEqual(CoreEventRole.Opens, lvlStart.Role);
            Assert.AreEqual(CoreSpans.Level, lvlStart.DefaultSpan);
            Assert.IsFalse(lvlStart.CarriesOutcome);

            Assert.IsTrue(CoreEventNames.TryGetMetadata(CoreEventNames.AdRevenue, out CoreEventMetadata adRev));
            Assert.AreEqual(CoreEventRole.None, adRev.Role);
            Assert.IsNull(adRev.DefaultSpan);
            Assert.IsTrue(adRev.CarriesOutcome);

            Assert.IsTrue(SdkStateKeys.IsReservedKey(SdkStateKeys.ScreenBrightness));
            Assert.IsTrue(SdkStateKeys.IsReservedKey(SdkStateKeys.ScreenLocked));
            Assert.IsFalse(SdkStateKeys.IsReservedKey(StandardStateKeys.GameLevel));

            Assert.IsTrue(ProtoEnumNames.TryParseSensor(Sensor.Magnetometer.ToProtoName(), out Sensor mag));
            Assert.AreEqual(Sensor.Magnetometer, mag);
            Assert.IsTrue(ProtoEnumNames.TryParseValueUnit(ValueUnit.Microtesla.ToProtoName(), out ValueUnit ut));
            Assert.AreEqual(ValueUnit.Microtesla, ut);
            Assert.IsTrue(ProtoEnumNames.TryParseGapSignal(GapSignal.Magnetometer.ToProtoName(), out GapSignal gmag));
            Assert.AreEqual(GapSignal.Magnetometer, gmag);
            Assert.IsTrue(ProtoEnumNames.TryParseMotionSource(MotionSource.Native.ToProtoName(), out MotionSource msrc));
            Assert.AreEqual(MotionSource.Native, msrc);
        }

        [Test]
        public void SessionJsonCodec_SerializesCollectionHistory_OmitsWhenZero_AndQuotesUint64Seconds()
        {
            // Zero history -> "history" MUST be omitted completely
            SessionStartRequest reqZero = new SessionStartRequest
            {
                ApiKey = "k",
                DeviceIdentifier = "d",
                History = default
            };
            Assert.IsTrue(SessionJsonCodec.TrySerializeSessionStartRequest(reqZero, out string jsonZero, out string errZero), errZero);
            StringAssert.DoesNotContain("\"history\"", jsonZero);

            // Non-zero history including uint64 > uint.MaxValue -> collected_seconds_before MUST be a JSON string
            SessionStartRequest reqHist = new SessionStartRequest
            {
                ApiKey = "k",
                DeviceIdentifier = "d",
                History = new CollectionHistory(12u, 5000000000UL, 3600u)
            };
            Assert.IsTrue(SessionJsonCodec.TrySerializeSessionStartRequest(reqHist, out string jsonHist, out string errHist), errHist);
            StringAssert.Contains("\"history\":{\"sessions_before\":12,\"collected_seconds_before\":\"5000000000\",\"longest_session_seconds_before\":3600}", jsonHist);
        }

        [Test]
        public void SessionJsonCodec_ParsesSettingsLabel_AndHandlesOmittedOrNullSettings()
        {
            // Non-collecting response with settings_label and omitted "settings"
            const string omittedSettingsJson = @"{
                ""status"": ""success"",
                ""data"": {
                    ""id_org"": ""org-1"",
                    ""id_deployment"": ""dep-1"",
                    ""id_source"": ""src-1"",
                    ""id_session"": ""ses-holdout"",
                    ""token"": ""tok-holdout"",
                    ""server_utc_ms"": ""1794736860000"",
                    ""batch_endpoint"": """",
                    ""settings_label"": ""holdout-10pct""
                }
            }";
            Assert.IsTrue(SessionJsonCodec.TryParseSessionStartResponse(omittedSettingsJson, out SessionStartResponse r1, out _, out string e1), e1);
            Assert.AreEqual("holdout-10pct", r1.SettingsLabel);
            Assert.AreEqual(string.Empty, r1.BatchEndpoint);
            Assert.IsNotNull(r1.Settings);
            Assert.AreEqual(SessionSettings.DefaultAccelerometerHz, r1.Settings.AccelerometerHz);

            // Explicit null "settings" with camelCase settingsLabel
            const string nullSettingsJson = @"{
                ""status"": ""success"",
                ""data"": {
                    ""id_session"": ""ses-null"",
                    ""batch_endpoint"": """",
                    ""settings"": null,
                    ""settingsLabel"": ""share-beta""
                }
            }";
            Assert.IsTrue(SessionJsonCodec.TryParseSessionStartResponse(nullSettingsJson, out SessionStartResponse r2, out _, out string e2), e2);
            Assert.AreEqual("share-beta", r2.SettingsLabel);
            Assert.IsNotNull(r2.Settings);
            Assert.AreEqual(SessionSettings.DefaultBatchLengthS, r2.Settings.BatchLengthS);
        }

        // ====================================================================
        // Fixture Builder (exact values from specs/batch_schema_2.json)
        // ====================================================================

        private static Batch BuildSchemaV3FixtureBatch()
        {
            Batch batch = new Batch();

            Header header = new Header
            {
                IdOrg = "made-up-org-1",
                IdDeployment = "made-up-deployment-1",
                IdSource = "made-up-device-1",
                IdSession = "made-up-session-1",
                Sequence = 214UL,
                SchemaVersion = 3u,
                Platform = Platform.Android,
                OsVersion = "15",
                DeviceModel = "Made-up Phone 3",
                SettingsVersion = "made-up-settings-9",
                WindowStartUs = 1794736860000000L,
                WindowLengthS = 10u,
                ClosedUs = 1794736870000000L,
                CloseReason = CloseReason.WindowFull,
                HasGyroscope = true,
                BatchesDropped = 2u
            };
            header.SetSdkVersion(new SdkVersion(1, 2, 3));
            header.SetUtcOffsetMinutes(-300);

            ClockReading clocks = new ClockReading
            {
                SessionUs = 1794736860000000L,
                UnityUs = 2961250000L,
                WallUtcMs = 1794737040412L
            };
            clocks.SetSensorUs(9007199254740993L);
            header.SetClocks(clocks);

            ServerAnchor anchor = new ServerAnchor
            {
                ServerUtcMs = 1794733920250L,
                RoundTripMs = 84u
            };
            anchor.SetAtReceipt(new ClockReading
            {
                SessionUs = 1794733920250000L,
                UnityUs = 23500000L,
                WallUtcMs = 1794734100662L
            });
            header.ServerAnchor = anchor;

            header.SetScreen(new ScreenRecord
            {
                WidthPx = 1080u,
                HeightPx = 2400u,
                DensityDpi = 420f,
                Orientation = Orientation.Portrait,
                Origin = CoordinateOrigin.BottomLeft
            });
            batch.Header = header;

            // SensorBlock 1: Accelerometer
            SensorBlock acc = new SensorBlock
            {
                Sensor = Sensor.Accelerometer,
                Source = CaptureSource.NativeAndroid,
                Clock = Clock.Sensor,
                Unit = ValueUnit.MetresPerSecondSquared,
                Scale = 1e-8,
                RequestedHz = 100f,
                SampleCount = 1000u,
                LongestGapUs = 10012u,
                FirstTimeUs = 9007199254744993L
            };
            acc.TimeStepsUs.AddRange(new[] { 10012, 9988, 10004, 9996, 10000 });
            acc.X.AddRange(new[] { 1200000, 1072541824, -2147483648, 2147483647, -1033741823, -38500000 });
            acc.Y.AddRange(new[] { -800000, 250800000, -560000000, 500000000, -220000000, 29100000 });
            acc.Z.AddRange(new[] { 980665000, 519335000, -1080000000, 890000000, -300000000, -29300000 });
            Hold accHold = new Hold
            {
                StartTimeUs = 9007199254804993L,
                EndTimeUs = 9007199264734993L,
                SampleCount = 994u,
                X = 1300000,
                Y = -700000,
                Z = 980660000
            };
            accHold.Stats.Add(new HoldStats(1292200000L, 1689800000000000d, 1100000, 1500000));
            accHold.Stats.Add(new HoldStats(-695800000L, 497000000000000d, -900000, -500000));
            accHold.Stats.Add(new HoldStats(974776040000L, 9.559238813264e20d, 980460000, 980860000));
            acc.Holds.Add(accHold);
            batch.SensorBlocks.Add(acc);

            // SensorBlock 2: Gyroscope
            SensorBlock gyro = new SensorBlock
            {
                Sensor = Sensor.Gyroscope,
                Source = CaptureSource.UnityEvents,
                FallbackReason = FallbackReason.SensorRefused,
                Clock = Clock.Unity,
                Unit = ValueUnit.RadiansPerSecond,
                Scale = 0.000001,
                RequestedHz = 100f,
                SampleCount = 4u,
                LongestGapUs = 10300u,
                FirstTimeUs = 2961255000L
            };
            gyro.TimeStepsUs.AddRange(new[] { 9800, 10300, 9900 });
            gyro.X.AddRange(new[] { 1200, -46200, 75000, -30500 });
            gyro.Y.AddRange(new[] { -800, 22800, -37000, 15400 });
            gyro.Z.AddRange(new[] { 300, -9300, 15000, -6100 });
            batch.SensorBlocks.Add(gyro);

            // SensorBlock 3: Gravity (hold-only)
            SensorBlock grav = new SensorBlock
            {
                Sensor = Sensor.Gravity,
                Source = CaptureSource.NativeAndroid,
                Clock = Clock.Sensor,
                Unit = ValueUnit.MetresPerSecondSquared,
                Scale = 1e-8,
                RequestedHz = 5f,
                SampleCount = 50u,
                LongestGapUs = 200000u
            };
            grav.Holds.Add(new Hold
            {
                StartTimeUs = 9007199254890993L,
                EndTimeUs = 9007199264690993L,
                SampleCount = 50u,
                X = -1250000,
                Y = 830000,
                Z = 980664000
            });
            batch.SensorBlocks.Add(grav);

            // SensorBlock 4: Magnetometer
            SensorBlock mag = new SensorBlock
            {
                Sensor = Sensor.Magnetometer,
                Source = CaptureSource.NativeAndroid,
                Clock = Clock.Sensor,
                Unit = ValueUnit.Microtesla,
                Scale = 0.01,
                RequestedHz = 10f,
                SampleCount = 3u,
                LongestGapUs = 100000u,
                FirstTimeUs = 9007199255740993L
            };
            mag.TimeStepsUs.AddRange(new[] { 100000, 100000 });
            mag.X.AddRange(new[] { 2000, 3, -2 });
            mag.Y.AddRange(new[] { -500, -1, 1 });
            mag.Z.AddRange(new[] { -4000, 2, 0 });
            batch.SensorBlocks.Add(mag);

            // Touch
            Touch touch = new Touch
            {
                Mode = TouchMode.Events,
                RequestedHz = 60f,
                PositionScale = 0.1,
                SizeScale = 0.01,
                PressureScale = 0.001
            };

            TouchContact c1 = new TouchContact
            {
                FingerId = 1u,
                Continued = true,
                End = TouchEnd.Lifted,
                FirstTimeUs = 2961252000L,
                EventsSeen = 68u,
                Before = new ContactBefore
                {
                    DownTimeUs = 2961000000L,
                    MinX = 5300,
                    MaxX = 5400,
                    MinY = 12000,
                    MaxY = 12100
                }
            };
            c1.TimeStepsUs.AddRange(new[] { 16600, 16700, 16700, 510000 });
            c1.X.AddRange(new[] { 5400, 25, 45, 20, 0 });
            c1.Y.AddRange(new[] { 12000, -50, -110, -40, 0 });
            c1.SizeMajor.AddRange(new[] { 1250, 10, -5, -5, -350 });
            c1.SizeMinor.AddRange(new[] { 1100, -5, 5, 0, -300 });
            c1.Pressure.AddRange(new[] { 620, 20, 15, -5, -650 });

            TouchHold th1 = new TouchHold
            {
                StartTimeUs = 2961318700L,
                EndTimeUs = 2961803000L,
                SampleCount = 30u,
                X = 5490,
                Y = 11800,
                SizeMajor = 1250,
                SizeMinor = 1100,
                Pressure = 650
            };
            th1.Stats.Add(new HoldStats(164790L, 905191650d, 5490, 5496));
            th1.Stats.Add(new HoldStats(353880L, 4174368800d, 11792, 11800));
            th1.Stats.Add(new HoldStats(37350L, 46501250d, 1240, 1250));
            th1.Stats.Add(new HoldStats(33020L, 36344200d, 1098, 1104));
            th1.Stats.Add(new HoldStats(19550L, 12741250d, 645, 660));
            c1.Holds.Add(th1);
            touch.Contacts.Add(c1);

            TouchContact c2 = new TouchContact
            {
                FingerId = 2u,
                End = TouchEnd.StillDown,
                FirstTimeUs = 2971240000L,
                EventsSeen = 1u
            };
            c2.X.Add(8000);
            c2.Y.Add(3000);
            c2.SizeMajor.Add(1300);
            c2.SizeMinor.Add(1150);
            c2.Pressure.Add(600);
            touch.Contacts.Add(c2);
            batch.Touch = touch;

            // GameEvents
            GameEvent e1 = new GameEvent
            {
                TimeUs = 2962250000L,
                Name = "game_level_succeeded",
                SpanName = "level",
                SpanMarker = SpanMarker.Close,
                SpanNumber = 7u,
                Order = 1u
            };
            e1.SetOutcome("succeeded");
            e1.Fields["level"] = Value.FromText("3");
            e1.Fields["score"] = Value.FromNumber(1250.5);
            e1.Fields["perfect"] = Value.FromFlag(true);
            e1.Fields["bonus"] = Value.Empty;
            batch.Events.Add(e1);

            GameEvent e2 = new GameEvent
            {
                TimeUs = 2962251000L,
                Name = "game_level_started",
                SpanName = "level",
                SpanMarker = SpanMarker.Open,
                SpanNumber = 8u,
                Order = 3u
            };
            e2.Fields["level"] = Value.FromText("4");
            e2.Fields["attempt"] = Value.FromNumber(1);
            batch.Events.Add(e2);

            GameEvent e3 = new GameEvent
            {
                TimeUs = 2967250000L,
                Name = "button_clicked",
                Order = 5u
            };
            e3.Fields["button"] = Value.FromText("Café 小店 🛒");
            batch.Events.Add(e3);

            // StateRecords
            StateRecord sr1 = new StateRecord
            {
                TimeUs = 2962250400L,
                Kind = StateKind.Patch,
                Order = 2u,
                Version = 13u
            };
            sr1.Values["level"] = Value.FromText("4");
            sr1.RemovedKeys.Add("boss");
            batch.StateRecords.Add(sr1);

            StateRecord sr2 = new StateRecord
            {
                TimeUs = 2967250000L,
                Kind = StateKind.Put,
                Order = 4u,
                Version = 14u
            };
            sr2.Values["level"] = Value.FromText("4");
            sr2.Values["screen"] = Value.FromText("shop");
            sr2.Values["lives"] = Value.FromNumber(3);
            batch.StateRecords.Add(sr2);

            // Lifecycle
            batch.Lifecycle.Add(new LifecycleRecord(2961250000L, LifecycleKind.ClockStepped, 2L));
            batch.Lifecycle.Add(new LifecycleRecord(2968250000L, LifecycleKind.FocusLost));
            batch.Lifecycle.Add(new LifecycleRecord(2968650000L, LifecycleKind.FocusGained));

            // Gaps
            batch.Gaps.Add(new GapRecord
            {
                Signal = GapSignal.Everything,
                Clock = Clock.Session,
                StartTimeUs = 1794734320000000L,
                EndTimeUs = 1794734340000000L,
                Cause = GapCause.BatchesDropped,
                BatchesDropped = 2u,
                FirstDroppedSequence = 41UL
            });
            batch.Gaps.Add(new GapRecord
            {
                Signal = GapSignal.Gyroscope,
                Clock = Clock.Unity,
                StartTimeUs = 2961285000L,
                EndTimeUs = 2971250000L,
                Cause = GapCause.SensorSilent
            });
            batch.Gaps.Add(new GapRecord
            {
                Signal = GapSignal.Magnetometer,
                Clock = Clock.Sensor,
                StartTimeUs = 9007199255940993L,
                EndTimeUs = 9007199258940993L,
                Cause = GapCause.SensorSilent
            });

            // ScreenChanges
            batch.ScreenChanges.Add(new ScreenRecord
            {
                TimeUs = 2967250000L,
                WidthPx = 2400u,
                HeightPx = 1080u,
                DensityDpi = 420f,
                Orientation = Orientation.LandscapeLeft,
                Origin = CoordinateOrigin.BottomLeft
            });

            // Diagnostics
            batch.Diagnostics.Add(new Diagnostic
            {
                TimeUs = 2961535000L,
                Severity = Severity.Warning,
                Code = "sensor_silent",
                Detail = "gyroscope: no sample for 250 ms",
                Count = 1u
            });

            // Carried
            Carried carried = new Carried
            {
                State = new State { Version = 12u },
                OpenSpans = new OpenSpans()
            };
            carried.State.Values["level"] = Value.FromText("3");
            carried.State.Values["screen"] = Value.FromText("game");
            carried.State.Values["boss"] = Value.FromFlag(true);
            carried.State.Values["lives"] = Value.FromNumber(2);
            carried.OpenSpans.Spans.Add(new OpenSpan("level", 7u, 2920250000L));
            carried.SetLastEventTimeUs(2955125000L);
            batch.Carried = carried;

            return batch;
        }

        // ====================================================================
        // Deep Assertion Helpers
        // ====================================================================

        private static void AssertBatchesDeepEqual(Batch expected, Batch actual)
        {
            Assert.IsNotNull(actual);
            AssertHeaderEqual(expected.Header, actual.Header);

            Assert.AreEqual(expected.SensorBlocks.Count, actual.SensorBlocks.Count);
            for (int i = 0; i < expected.SensorBlocks.Count; i++)
            {
                AssertSensorBlockEqual(expected.SensorBlocks[i], actual.SensorBlocks[i]);
            }

            AssertTouchEqual(expected.Touch, actual.Touch);

            Assert.AreEqual(expected.Events.Count, actual.Events.Count);
            for (int i = 0; i < expected.Events.Count; i++)
            {
                AssertGameEventEqual(expected.Events[i], actual.Events[i]);
            }

            Assert.AreEqual(expected.StateRecords.Count, actual.StateRecords.Count);
            for (int i = 0; i < expected.StateRecords.Count; i++)
            {
                AssertStateRecordEqual(expected.StateRecords[i], actual.StateRecords[i]);
            }

            CollectionAssert.AreEqual(expected.Lifecycle, actual.Lifecycle);
            CollectionAssert.AreEqual(expected.Gaps, actual.Gaps);
            CollectionAssert.AreEqual(expected.ScreenChanges, actual.ScreenChanges);

            Assert.AreEqual(expected.Diagnostics.Count, actual.Diagnostics.Count);
            for (int i = 0; i < expected.Diagnostics.Count; i++)
            {
                Diagnostic ed = expected.Diagnostics[i];
                Diagnostic ad = actual.Diagnostics[i];
                Assert.AreEqual(ed.TimeUs, ad.TimeUs);
                Assert.AreEqual(ed.Severity, ad.Severity);
                Assert.AreEqual(ed.Code, ad.Code);
                Assert.AreEqual(ed.Detail, ad.Detail);
                Assert.AreEqual(ed.Count, ad.Count);
            }

            AssertCarriedEqual(expected.Carried, actual.Carried);
        }

        private static void AssertHeaderEqual(Header e, Header a)
        {
            Assert.IsNotNull(a);
            Assert.AreEqual(e.IdOrg, a.IdOrg);
            Assert.AreEqual(e.IdDeployment, a.IdDeployment);
            Assert.AreEqual(e.IdSource, a.IdSource);
            Assert.AreEqual(e.IdSession, a.IdSession);
            Assert.AreEqual(e.Sequence, a.Sequence);
            Assert.AreEqual(e.SchemaVersion, a.SchemaVersion);
            Assert.AreEqual(e.SdkVersion, a.SdkVersion);
            Assert.AreEqual(e.Platform, a.Platform);
            Assert.AreEqual(e.OsVersion, a.OsVersion);
            Assert.AreEqual(e.DeviceModel, a.DeviceModel);
            Assert.AreEqual(e.SettingsVersion, a.SettingsVersion);
            Assert.AreEqual(e.WindowStartUs, a.WindowStartUs);
            Assert.AreEqual(e.WindowLengthS, a.WindowLengthS);
            Assert.AreEqual(e.ClosedUs, a.ClosedUs);
            Assert.AreEqual(e.CloseReason, a.CloseReason);
            Assert.AreEqual(e.Clocks, a.Clocks);
            Assert.AreEqual(e.Screen, a.Screen);
            Assert.AreEqual(e.HasGyroscope, a.HasGyroscope);
            Assert.AreEqual(e.BatchesDropped, a.BatchesDropped);
            Assert.AreEqual(e.HasUtcOffsetMinutes, a.HasUtcOffsetMinutes);
            Assert.AreEqual(e.UtcOffsetMinutes, a.UtcOffsetMinutes);

            Assert.IsNotNull(a.ServerAnchor);
            Assert.AreEqual(e.ServerAnchor.ServerUtcMs, a.ServerAnchor.ServerUtcMs);
            Assert.AreEqual(e.ServerAnchor.AtReceipt, a.ServerAnchor.AtReceipt);
            Assert.AreEqual(e.ServerAnchor.RoundTripMs, a.ServerAnchor.RoundTripMs);
        }

        private static void AssertSensorBlockEqual(SensorBlock e, SensorBlock a)
        {
            Assert.AreEqual(e.Sensor, a.Sensor);
            Assert.AreEqual(e.Source, a.Source);
            Assert.AreEqual(e.FallbackReason, a.FallbackReason);
            Assert.AreEqual(e.Clock, a.Clock);
            Assert.AreEqual(e.Unit, a.Unit);
            Assert.AreEqual(e.Scale, a.Scale);
            Assert.AreEqual(e.RequestedHz, a.RequestedHz);
            Assert.AreEqual(e.SampleCount, a.SampleCount);
            Assert.AreEqual(e.LongestGapUs, a.LongestGapUs);
            Assert.AreEqual(e.FirstTimeUs, a.FirstTimeUs);
            CollectionAssert.AreEqual(e.TimeStepsUs, a.TimeStepsUs);
            CollectionAssert.AreEqual(e.X, a.X);
            CollectionAssert.AreEqual(e.Y, a.Y);
            CollectionAssert.AreEqual(e.Z, a.Z);

            Assert.AreEqual(e.Holds.Count, a.Holds.Count);
            for (int i = 0; i < e.Holds.Count; i++)
            {
                Hold eh = e.Holds[i];
                Hold ah = a.Holds[i];
                Assert.AreEqual(eh.StartTimeUs, ah.StartTimeUs);
                Assert.AreEqual(eh.EndTimeUs, ah.EndTimeUs);
                Assert.AreEqual(eh.SampleCount, ah.SampleCount);
                Assert.AreEqual(eh.X, ah.X);
                Assert.AreEqual(eh.Y, ah.Y);
                Assert.AreEqual(eh.Z, ah.Z);
                CollectionAssert.AreEqual(eh.Stats, ah.Stats);
            }
        }

        private static void AssertTouchEqual(Touch e, Touch a)
        {
            Assert.IsNotNull(a);
            Assert.AreEqual(e.Mode, a.Mode);
            Assert.AreEqual(e.RequestedHz, a.RequestedHz);
            Assert.AreEqual(e.PositionScale, a.PositionScale);
            Assert.AreEqual(e.SizeScale, a.SizeScale);
            Assert.AreEqual(e.PressureScale, a.PressureScale);
            Assert.AreEqual(e.Contacts.Count, a.Contacts.Count);

            for (int i = 0; i < e.Contacts.Count; i++)
            {
                TouchContact ec = e.Contacts[i];
                TouchContact ac = a.Contacts[i];
                Assert.AreEqual(ec.FingerId, ac.FingerId);
                Assert.AreEqual(ec.Continued, ac.Continued);
                Assert.AreEqual(ec.End, ac.End);
                Assert.AreEqual(ec.FirstTimeUs, ac.FirstTimeUs);
                CollectionAssert.AreEqual(ec.TimeStepsUs, ac.TimeStepsUs);
                CollectionAssert.AreEqual(ec.X, ac.X);
                CollectionAssert.AreEqual(ec.Y, ac.Y);
                CollectionAssert.AreEqual(ec.SizeMajor, ac.SizeMajor);
                CollectionAssert.AreEqual(ec.SizeMinor, ac.SizeMinor);
                CollectionAssert.AreEqual(ec.Pressure, ac.Pressure);
                Assert.AreEqual(ec.EventsSeen, ac.EventsSeen);

                Assert.AreEqual(ec.Holds.Count, ac.Holds.Count);
                for (int h = 0; h < ec.Holds.Count; h++)
                {
                    TouchHold eth = ec.Holds[h];
                    TouchHold ath = ac.Holds[h];
                    Assert.AreEqual(eth.StartTimeUs, ath.StartTimeUs);
                    Assert.AreEqual(eth.EndTimeUs, ath.EndTimeUs);
                    Assert.AreEqual(eth.SampleCount, ath.SampleCount);
                    Assert.AreEqual(eth.X, ath.X);
                    Assert.AreEqual(eth.Y, ath.Y);
                    Assert.AreEqual(eth.SizeMajor, ath.SizeMajor);
                    Assert.AreEqual(eth.SizeMinor, ath.SizeMinor);
                    Assert.AreEqual(eth.Pressure, ath.Pressure);
                    CollectionAssert.AreEqual(eth.Stats, ath.Stats);
                }

                if (ec.Before == null)
                {
                    Assert.IsNull(ac.Before);
                }
                else
                {
                    Assert.IsNotNull(ac.Before);
                    Assert.AreEqual(ec.Before.DownTimeUs, ac.Before.DownTimeUs);
                    Assert.AreEqual(ec.Before.MinX, ac.Before.MinX);
                    Assert.AreEqual(ec.Before.MaxX, ac.Before.MaxX);
                    Assert.AreEqual(ec.Before.MinY, ac.Before.MinY);
                    Assert.AreEqual(ec.Before.MaxY, ac.Before.MaxY);
                }
            }
        }

        private static void AssertGameEventEqual(GameEvent e, GameEvent a)
        {
            Assert.AreEqual(e.TimeUs, a.TimeUs);
            Assert.AreEqual(e.Name, a.Name);
            AssertValueMapEqual(e.Fields, a.Fields);
            Assert.AreEqual(e.HasOutcome, a.HasOutcome);
            Assert.AreEqual(e.Outcome, a.Outcome);
            Assert.AreEqual(e.SpanName, a.SpanName);
            Assert.AreEqual(e.SpanMarker, a.SpanMarker);
            Assert.AreEqual(e.SpanNumber, a.SpanNumber);
            Assert.AreEqual(e.Order, a.Order);
        }

        private static void AssertStateRecordEqual(StateRecord e, StateRecord a)
        {
            Assert.AreEqual(e.TimeUs, a.TimeUs);
            Assert.AreEqual(e.Kind, a.Kind);
            AssertValueMapEqual(e.Values, a.Values);
            CollectionAssert.AreEqual(e.RemovedKeys, a.RemovedKeys);
            Assert.AreEqual(e.Order, a.Order);
            Assert.AreEqual(e.Version, a.Version);
        }

        private static void AssertCarriedEqual(Carried e, Carried a)
        {
            Assert.IsNotNull(a);
            Assert.IsNotNull(a.State);
            AssertValueMapEqual(e.State.Values, a.State.Values);
            Assert.AreEqual(e.State.Version, a.State.Version);
            Assert.IsNotNull(a.OpenSpans);
            CollectionAssert.AreEqual(e.OpenSpans.Spans, a.OpenSpans.Spans);
            Assert.AreEqual(e.HasLastEventTimeUs, a.HasLastEventTimeUs);
            Assert.AreEqual(e.LastEventTimeUs, a.LastEventTimeUs);
        }

        private static void AssertValueMapEqual(Dictionary<string, Value> e, Dictionary<string, Value> a)
        {
            Assert.AreEqual(e.Count, a.Count);
            foreach (KeyValuePair<string, Value> kv in e)
            {
                Assert.IsTrue(a.TryGetValue(kv.Key, out Value actualVal), "Missing key: " + kv.Key);
                Assert.AreEqual(kv.Value, actualVal, "Mismatch for key: " + kv.Key);
            }
        }
    }
}
