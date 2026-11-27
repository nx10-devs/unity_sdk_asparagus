using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Asparagus.Sdk.Contracts;

namespace Asparagus.Sdk.Internal.Encoding
{
    /// <summary>
    /// Pure-C#, zero-dependency Protobuf-JSON serializer for <see cref="SessionStartRequest"/> and
    /// fault-contained parser for Control Plane <see cref="SessionStartResponse"/> envelopes.
    /// </summary>
    public static class SessionJsonCodec
    {
        private const int MaxJsonDepth = 64;

        // ====================================================================
        // Serialization (SessionStartRequest -> Protobuf JSON, snake_case)
        // ====================================================================

        public static bool TrySerializeSessionStartRequest(
            SessionStartRequest request,
            out string json,
            out string error)
        {
            StringBuilder sb = new StringBuilder(512);
            if (!TrySerializeSessionStartRequest(request, sb, out error))
            {
                json = string.Empty;
                return false;
            }

            json = sb.ToString();
            return true;
        }

        public static bool TrySerializeSessionStartRequest(
            SessionStartRequest request,
            StringBuilder output,
            out string error)
        {
            error = null;
            if (request == null)
            {
                error = "SessionStartRequest is null.";
                return false;
            }

            if (output == null)
            {
                error = "Output StringBuilder is null.";
                return false;
            }

            try
            {
                output.Clear();
                output.Append('{');
                bool first = true;

                if (!string.IsNullOrEmpty(request.ApiKey))
                {
                    AppendFieldName(output, "api_key", ref first);
                    AppendJsonString(output, request.ApiKey);
                }

                if (!string.IsNullOrEmpty(request.DeviceIdentifier))
                {
                    AppendFieldName(output, "device_identifier", ref first);
                    AppendJsonString(output, request.DeviceIdentifier);
                }

                if (request.HasSdkVersion || !request.SdkVersion.IsZero)
                {
                    AppendFieldName(output, "sdk_version", ref first);
                    AppendSdkVersion(output, request.SdkVersion);
                }

                if (request.Platform != Platform.Unspecified)
                {
                    AppendFieldName(output, "platform", ref first);
                    AppendJsonString(output, request.Platform.ToProtoName());
                }

                if (request.Device != null)
                {
                    AppendFieldName(output, "device", ref first);
                    AppendDeviceProperties(output, request.Device);
                }

                if (!string.IsNullOrEmpty(request.AppVersion))
                {
                    AppendFieldName(output, "app_version", ref first);
                    AppendJsonString(output, request.AppVersion);
                }

                if (!string.IsNullOrEmpty(request.BuildNumber))
                {
                    AppendFieldName(output, "build_number", ref first);
                    AppendJsonString(output, request.BuildNumber);
                }

                if (request.SessionProperties != null && request.SessionProperties.Count > 0)
                {
                    AppendFieldName(output, "session_properties", ref first);
                    AppendValueMap(output, request.SessionProperties);
                }

                if (request.Demo)
                {
                    AppendFieldName(output, "demo", ref first);
                    output.Append("true");
                }

                if (!request.History.IsZero)
                {
                    AppendFieldName(output, "history", ref first);
                    AppendCollectionHistory(output, request.History);
                }

                output.Append('}');
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void AppendCollectionHistory(StringBuilder sb, CollectionHistory history)
        {
            sb.Append('{');
            bool first = true;
            if (history.SessionsBefore != 0u)
            {
                AppendFieldName(sb, "sessions_before", ref first);
                sb.Append(history.SessionsBefore.ToString(CultureInfo.InvariantCulture));
            }
            if (history.CollectedSecondsBefore != 0UL)
            {
                AppendFieldName(sb, "collected_seconds_before", ref first);
                AppendJsonString(sb, history.CollectedSecondsBefore.ToString(CultureInfo.InvariantCulture));
            }
            if (history.LongestSessionSecondsBefore != 0u)
            {
                AppendFieldName(sb, "longest_session_seconds_before", ref first);
                sb.Append(history.LongestSessionSecondsBefore.ToString(CultureInfo.InvariantCulture));
            }
            sb.Append('}');
        }

        private static void AppendSdkVersion(StringBuilder sb, SdkVersion version)
        {
            sb.Append('{');
            bool first = true;
            if (version.Major != 0)
            {
                AppendFieldName(sb, "major", ref first);
                sb.Append(version.Major.ToString(CultureInfo.InvariantCulture));
            }
            if (version.Minor != 0)
            {
                AppendFieldName(sb, "minor", ref first);
                sb.Append(version.Minor.ToString(CultureInfo.InvariantCulture));
            }
            if (version.Patch != 0)
            {
                AppendFieldName(sb, "patch", ref first);
                sb.Append(version.Patch.ToString(CultureInfo.InvariantCulture));
            }
            sb.Append('}');
        }

        private static void AppendDeviceProperties(StringBuilder sb, DeviceProperties device)
        {
            sb.Append('{');
            bool first = true;

            if (!string.IsNullOrEmpty(device.Maker))
            {
                AppendFieldName(sb, "maker", ref first);
                AppendJsonString(sb, device.Maker);
            }
            if (!string.IsNullOrEmpty(device.Model))
            {
                AppendFieldName(sb, "model", ref first);
                AppendJsonString(sb, device.Model);
            }
            if (!string.IsNullOrEmpty(device.OsVersion))
            {
                AppendFieldName(sb, "os_version", ref first);
                AppendJsonString(sb, device.OsVersion);
            }
            if (!string.IsNullOrEmpty(device.DeviceType))
            {
                AppendFieldName(sb, "device_type", ref first);
                AppendJsonString(sb, device.DeviceType);
            }
            if (device.ScreenWidthPx != 0)
            {
                AppendFieldName(sb, "screen_width_px", ref first);
                sb.Append(device.ScreenWidthPx.ToString(CultureInfo.InvariantCulture));
            }
            if (device.ScreenHeightPx != 0)
            {
                AppendFieldName(sb, "screen_height_px", ref first);
                sb.Append(device.ScreenHeightPx.ToString(CultureInfo.InvariantCulture));
            }
            if (device.ScreenDensityDpi != 0f && !float.IsNaN(device.ScreenDensityDpi) && !float.IsInfinity(device.ScreenDensityDpi))
            {
                AppendFieldName(sb, "screen_density_dpi", ref first);
                sb.Append(device.ScreenDensityDpi.ToString("R", CultureInfo.InvariantCulture));
            }
            if (device.HasGyroscope)
            {
                AppendFieldName(sb, "has_gyroscope", ref first);
                sb.Append("true");
            }
            if (!string.IsNullOrEmpty(device.Timezone))
            {
                AppendFieldName(sb, "timezone", ref first);
                AppendJsonString(sb, device.Timezone);
            }
            if (!string.IsNullOrEmpty(device.Locale))
            {
                AppendFieldName(sb, "locale", ref first);
                AppendJsonString(sb, device.Locale);
            }

            sb.Append('}');
        }

        private static void AppendValueMap(StringBuilder sb, Dictionary<string, Value> map)
        {
            sb.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, Value> entry in map)
            {
                AppendFieldName(sb, entry.Key ?? string.Empty, ref first);
                AppendValue(sb, entry.Value);
            }
            sb.Append('}');
        }

        private static void AppendValue(StringBuilder sb, Value value)
        {
            switch (value.Kind)
            {
                case ValueKind.Text:
                    sb.Append("{\"text\":");
                    AppendJsonString(sb, value.TextValue);
                    sb.Append('}');
                    break;

                case ValueKind.Number:
                    double number = value.NumberValue;
                    if (double.IsNaN(number) || double.IsInfinity(number))
                    {
                        sb.Append("{}");
                    }
                    else
                    {
                        sb.Append("{\"number\":");
                        sb.Append(number.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append('}');
                    }
                    break;

                case ValueKind.Flag:
                    sb.Append(value.FlagValue ? "{\"flag\":true}" : "{\"flag\":false}");
                    break;

                case ValueKind.None:
                default:
                    sb.Append("{}");
                    break;
            }
        }

        private static void AppendFieldName(StringBuilder sb, string name, ref bool first)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            AppendJsonString(sb, name);
            sb.Append(':');
        }

        private static void AppendJsonString(StringBuilder sb, string text)
        {
            sb.Append('"');
            if (!string.IsNullOrEmpty(text))
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20)
                            {
                                sb.Append("\\u");
                                sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                sb.Append(c);
                            }
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        // ====================================================================
        // Parsing (Control Plane Envelope -> SessionStartResponse)
        // ====================================================================

        public static bool TryParseSessionStartResponse(
            string json,
            out SessionStartResponse response,
            out string[] errors,
            out string parseError)
        {
            response = null;
            errors = Array.Empty<string>();
            parseError = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                parseError = "Response JSON is empty.";
                return false;
            }

            try
            {
                JsonReader reader = new JsonReader(json);
                reader.SkipWhitespace();
                if (!reader.TryConsumeChar('{'))
                {
                    parseError = "Expected top-level JSON object.";
                    return false;
                }

                string status = null;
                SessionStartResponse parsedData = null;
                List<string> parsedErrors = null;

                reader.SkipWhitespace();
                if (!reader.TryConsumeChar('}'))
                {
                    while (true)
                    {
                        reader.SkipWhitespace();
                        if (!reader.TryReadString(out string key))
                        {
                            parseError = "Expected property name in envelope.";
                            return false;
                        }

                        reader.SkipWhitespace();
                        if (!reader.TryConsumeChar(':'))
                        {
                            parseError = "Expected ':' after property name.";
                            return false;
                        }

                        reader.SkipWhitespace();
                        if (key == "status")
                        {
                            if (reader.TryConsumeNull())
                            {
                                status = null;
                            }
                            else if (!reader.TryReadString(out status))
                            {
                                parseError = "Expected string for envelope status.";
                                return false;
                            }
                        }
                        else if (key == "data")
                        {
                            if (reader.TryConsumeNull())
                            {
                                parsedData = null;
                            }
                            else
                            {
                                parsedData = new SessionStartResponse();
                                if (!TryReadSessionStartData(ref reader, parsedData))
                                {
                                    parseError = "Malformed SessionStartResponse data object.";
                                    return false;
                                }
                            }
                        }
                        else if (key == "errors")
                        {
                            if (reader.TryConsumeNull())
                            {
                                parsedErrors = null;
                            }
                            else
                            {
                                parsedErrors = new List<string>();
                                if (!TryReadErrorsArray(ref reader, parsedErrors))
                                {
                                    parseError = "Malformed errors array in envelope.";
                                    return false;
                                }
                            }
                        }
                        else
                        {
                            if (!reader.TrySkipValue(0))
                            {
                                parseError = "Failed to skip unknown envelope property.";
                                return false;
                            }
                        }

                        reader.SkipWhitespace();
                        if (reader.TryConsumeChar(','))
                        {
                            continue;
                        }

                        if (reader.TryConsumeChar('}'))
                        {
                            break;
                        }

                        parseError = "Expected ',' or '}' in envelope object.";
                        return false;
                    }
                }

                reader.SkipWhitespace();
                if (!reader.IsAtEnd)
                {
                    parseError = "Unexpected trailing characters after JSON object.";
                    return false;
                }

                if (parsedErrors != null && parsedErrors.Count > 0)
                {
                    errors = parsedErrors.ToArray();
                }

                if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                {
                    if (parsedData == null)
                    {
                        parseError = "Envelope status is 'success' but 'data' is missing or null.";
                        return false;
                    }

                    response = parsedData;
                    return true;
                }

                if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
                {
                    parseError = errors.Length > 0 ? errors[0] : "Session start returned error status.";
                    return false;
                }

                parseError = "Unknown or missing envelope status.";
                return false;
            }
            catch (Exception ex)
            {
                response = null;
                parseError = ex.Message;
                return false;
            }
        }

        private static bool TryReadErrorsArray(ref JsonReader reader, List<string> errors)
        {
            if (!reader.TryConsumeChar('['))
            {
                return false;
            }

            reader.SkipWhitespace();
            if (reader.TryConsumeChar(']'))
            {
                return true;
            }

            while (true)
            {
                reader.SkipWhitespace();
                char peek = reader.PeekChar();
                if (peek == '"')
                {
                    if (!reader.TryReadString(out string errStr))
                    {
                        return false;
                    }
                    errors.Add(errStr);
                }
                else if (peek == '{')
                {
                    int start = reader.Position;
                    string extractedMessage;
                    if (!TryReadErrorObject(ref reader, out extractedMessage))
                    {
                        return false;
                    }
                    if (!string.IsNullOrEmpty(extractedMessage))
                    {
                        errors.Add(extractedMessage);
                    }
                    else
                    {
                        errors.Add(reader.Slice(start, reader.Position - start));
                    }
                }
                else
                {
                    int start = reader.Position;
                    if (!reader.TrySkipValue(0))
                    {
                        return false;
                    }
                    errors.Add(reader.Slice(start, reader.Position - start));
                }

                reader.SkipWhitespace();
                if (reader.TryConsumeChar(','))
                {
                    continue;
                }

                return reader.TryConsumeChar(']');
            }
        }

        private static bool TryReadErrorObject(ref JsonReader reader, out string message)
        {
            message = null;
            if (!reader.TryConsumeChar('{'))
            {
                return false;
            }

            reader.SkipWhitespace();
            if (reader.TryConsumeChar('}'))
            {
                return true;
            }

            while (true)
            {
                reader.SkipWhitespace();
                if (!reader.TryReadString(out string key))
                {
                    return false;
                }

                reader.SkipWhitespace();
                if (!reader.TryConsumeChar(':'))
                {
                    return false;
                }

                reader.SkipWhitespace();
                if ((key == "message" || key == "error" || key == "detail") && reader.PeekChar() == '"')
                {
                    if (!reader.TryReadString(out string val))
                    {
                        return false;
                    }
                    if (string.IsNullOrEmpty(message))
                    {
                        message = val;
                    }
                }
                else
                {
                    if (!reader.TrySkipValue(1))
                    {
                        return false;
                    }
                }

                reader.SkipWhitespace();
                if (reader.TryConsumeChar(','))
                {
                    continue;
                }

                return reader.TryConsumeChar('}');
            }
        }

        private static bool TryReadSessionStartData(ref JsonReader reader, SessionStartResponse target)
        {
            if (!reader.TryConsumeChar('{'))
            {
                return false;
            }

            reader.SkipWhitespace();
            if (reader.TryConsumeChar('}'))
            {
                return true;
            }

            while (true)
            {
                reader.SkipWhitespace();
                if (!reader.TryReadString(out string key))
                {
                    return false;
                }

                reader.SkipWhitespace();
                if (!reader.TryConsumeChar(':'))
                {
                    return false;
                }

                reader.SkipWhitespace();
                switch (key)
                {
                    case "id_org":
                    case "idOrg":
                        if (!TryReadNullableString(ref reader, out string idOrg)) return false;
                        if (idOrg != null) target.IdOrg = idOrg;
                        break;

                    case "id_deployment":
                    case "idDeployment":
                        if (!TryReadNullableString(ref reader, out string idDep)) return false;
                        if (idDep != null) target.IdDeployment = idDep;
                        break;

                    case "id_source":
                    case "idSource":
                        if (!TryReadNullableString(ref reader, out string idSrc)) return false;
                        if (idSrc != null) target.IdSource = idSrc;
                        break;

                    case "id_session":
                    case "idSession":
                        if (!TryReadNullableString(ref reader, out string idSes)) return false;
                        if (idSes != null) target.IdSession = idSes;
                        break;

                    case "token":
                        if (!TryReadNullableString(ref reader, out string token)) return false;
                        if (token != null) target.Token = token;
                        break;

                    case "token_expires_utc_ms":
                    case "tokenExpiresUtcMs":
                        if (!TryReadNullableInt64(ref reader, out bool hasTokExp, out long tokExp)) return false;
                        if (hasTokExp) target.TokenExpiresUtcMs = tokExp;
                        break;

                    case "server_utc_ms":
                    case "serverUtcMs":
                        if (!TryReadNullableInt64(ref reader, out bool hasSrvUtc, out long srvUtc)) return false;
                        if (hasSrvUtc) target.ServerUtcMs = srvUtc;
                        break;

                    case "batch_endpoint":
                    case "batchEndpoint":
                        if (!TryReadNullableString(ref reader, out string batchEp)) return false;
                        if (batchEp != null) target.BatchEndpoint = batchEp;
                        break;

                    case "access_request_endpoint":
                    case "accessRequestEndpoint":
                        if (!TryReadNullableString(ref reader, out string accessEp)) return false;
                        if (accessEp != null) target.AccessRequestEndpoint = accessEp;
                        break;

                    case "forget_request_endpoint":
                    case "forgetRequestEndpoint":
                        if (!TryReadNullableString(ref reader, out string forgetEp)) return false;
                        if (forgetEp != null) target.ForgetRequestEndpoint = forgetEp;
                        break;

                    case "settings":
                        if (!reader.TryConsumeNull())
                        {
                            if (!TryReadSettings(ref reader, target.Settings)) return false;
                        }
                        break;

                    case "models":
                        if (!reader.TryConsumeNull())
                        {
                            if (!TryReadModels(ref reader, target.Models)) return false;
                        }
                        break;

                    case "settings_label":
                    case "settingsLabel":
                        if (!TryReadNullableString(ref reader, out string settingsLabel)) return false;
                        if (settingsLabel != null) target.SettingsLabel = settingsLabel;
                        break;

                    default:
                        if (!reader.TrySkipValue(1)) return false;
                        break;
                }

                reader.SkipWhitespace();
                if (reader.TryConsumeChar(','))
                {
                    continue;
                }

                return reader.TryConsumeChar('}');
            }
        }

        private static bool TryReadSettings(ref JsonReader reader, SessionSettings settings)
        {
            if (!reader.TryConsumeChar('{'))
            {
                return false;
            }

            reader.SkipWhitespace();
            if (reader.TryConsumeChar('}'))
            {
                return true;
            }

            while (true)
            {
                reader.SkipWhitespace();
                if (!reader.TryReadString(out string key))
                {
                    return false;
                }

                reader.SkipWhitespace();
                if (!reader.TryConsumeChar(':'))
                {
                    return false;
                }

                reader.SkipWhitespace();
                switch (key)
                {
                    case "version":
                        if (!TryReadNullableString(ref reader, out string ver)) return false;
                        if (ver != null) settings.Version = ver;
                        break;

                    case "accelerometer_hz":
                    case "accelerometerHz":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasAccHz, out uint accHz)) return false;
                        if (hasAccHz) settings.AccelerometerHz = accHz;
                        break;

                    case "gyroscope_hz":
                    case "gyroscopeHz":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasGyrHz, out uint gyrHz)) return false;
                        if (hasGyrHz) settings.GyroscopeHz = gyrHz;
                        break;

                    case "gravity_hz":
                    case "gravityHz":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasGravHz, out uint gravHz)) return false;
                        if (hasGravHz) settings.GravityHz = gravHz;
                        break;

                    case "touch_hz":
                    case "touchHz":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasTouchHz, out uint touchHz)) return false;
                        if (hasTouchHz) settings.TouchHz = touchHz;
                        break;

                    case "motion_source":
                    case "motionSource":
                        if (!TryReadMotionSource(ref reader, out bool hasMs, out MotionSource ms)) return false;
                        if (hasMs) settings.ApplyMotionSource(ms);
                        break;

                    case "native_buffer_s":
                    case "nativeBufferS":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasNb, out uint nb)) return false;
                        if (hasNb) settings.NativeBufferS = nb;
                        break;

                    case "native_start_limit_ms":
                    case "nativeStartLimitMs":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasNstart, out uint nstart)) return false;
                        if (hasNstart) settings.NativeStartLimitMs = nstart;
                        break;

                    case "native_stall_limit_ms":
                    case "nativeStallLimitMs":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasNstall, out uint nstall)) return false;
                        if (hasNstall) settings.NativeStallLimitMs = nstall;
                        break;

                    case "accelerometer_hold_tolerance":
                    case "accelerometerHoldTolerance":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasAht, out uint aht)) return false;
                        if (hasAht) settings.AccelerometerHoldTolerance = aht;
                        break;

                    case "gyroscope_hold_tolerance":
                    case "gyroscopeHoldTolerance":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasGht, out uint ght)) return false;
                        if (hasGht) settings.GyroscopeHoldTolerance = ght;
                        break;

                    case "gravity_hold_tolerance":
                    case "gravityHoldTolerance":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasGrht, out uint grht)) return false;
                        if (hasGrht) settings.GravityHoldTolerance = grht;
                        break;

                    case "touch_hold_tolerance":
                    case "touchHoldTolerance":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasTht, out uint tht)) return false;
                        if (hasTht) settings.TouchHoldTolerance = tht;
                        break;

                    case "hold_run_length":
                    case "holdRunLength":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasHrl, out uint hrl)) return false;
                        if (hasHrl) settings.HoldRunLength = hrl;
                        break;

                    case "gap_threshold_ms":
                    case "gapThresholdMs":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasGtm, out uint gtm)) return false;
                        if (hasGtm) settings.GapThresholdMs = gtm;
                        break;

                    case "gap_threshold_periods":
                    case "gapThresholdPeriods":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasGtp, out uint gtp)) return false;
                        if (hasGtp) settings.GapThresholdPeriods = gtp;
                        break;

                    case "batch_length_s":
                    case "batchLengthS":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasBls, out uint bls)) return false;
                        if (hasBls) settings.ApplyBatchLengthS(bls);
                        break;

                    case "compress":
                        if (!TryReadOptionalBool(ref reader, out bool hasComp, out bool comp)) return false;
                        if (hasComp) settings.Compress = comp;
                        break;

                    case "queue_max_bytes":
                    case "queueMaxBytes":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasQmb, out uint qmb)) return false;
                        if (hasQmb) settings.QueueMaxBytes = qmb;
                        break;

                    case "queue_max_age_s":
                    case "queueMaxAgeS":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasQma, out uint qma)) return false;
                        if (hasQma) settings.QueueMaxAgeS = qma;
                        break;

                    case "retry_first_wait_ms":
                    case "retryFirstWaitMs":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasRfw, out uint rfw)) return false;
                        if (hasRfw) settings.ApplyRetryFirstWaitMs(rfw);
                        break;

                    case "retry_longest_wait_ms":
                    case "retryLongestWaitMs":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasRlw, out uint rlw)) return false;
                        if (hasRlw) settings.ApplyRetryLongestWaitMs(rlw);
                        break;

                    case "pause_touch_on_keyboard":
                    case "pauseTouchOnKeyboard":
                        if (!TryReadOptionalBool(ref reader, out bool hasPtk, out bool ptk)) return false;
                        if (hasPtk) settings.PauseTouchOnKeyboard = ptk;
                        break;

                    case "token_life_s":
                    case "tokenLifeS":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasTls, out uint tls)) return false;
                        if (hasTls) settings.ApplyTokenLifeS(tls);
                        break;

                    case "new_session_after_s":
                    case "newSessionAfterS":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasNsa, out uint nsa)) return false;
                        if (hasNsa) settings.ApplyNewSessionAfterS(nsa);
                        break;

                    case "magnetometer_hz":
                    case "magnetometerHz":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasMagHz, out uint magHz)) return false;
                        if (hasMagHz) settings.MagnetometerHz = magHz;
                        break;

                    case "magnetometer_hold_tolerance":
                    case "magnetometerHoldTolerance":
                        if (!TryReadOptionalUInt32(ref reader, out bool hasMagHt, out uint magHt)) return false;
                        if (hasMagHt) settings.MagnetometerHoldTolerance = magHt;
                        break;

                    default:
                        if (!reader.TrySkipValue(2)) return false;
                        break;
                }

                reader.SkipWhitespace();
                if (reader.TryConsumeChar(','))
                {
                    continue;
                }

                return reader.TryConsumeChar('}');
            }
        }

        private static bool TryReadModels(ref JsonReader reader, List<ModelReference> models)
        {
            if (!reader.TryConsumeChar('['))
            {
                return false;
            }

            reader.SkipWhitespace();
            if (reader.TryConsumeChar(']'))
            {
                return true;
            }

            while (true)
            {
                reader.SkipWhitespace();
                if (!reader.TryConsumeNull())
                {
                    ModelReference model = new ModelReference();
                    if (!TryReadModelReference(ref reader, model))
                    {
                        return false;
                    }
                    models.Add(model);
                }

                reader.SkipWhitespace();
                if (reader.TryConsumeChar(','))
                {
                    continue;
                }

                return reader.TryConsumeChar(']');
            }
        }

        private static bool TryReadModelReference(ref JsonReader reader, ModelReference model)
        {
            if (!reader.TryConsumeChar('{'))
            {
                return false;
            }

            reader.SkipWhitespace();
            if (reader.TryConsumeChar('}'))
            {
                return true;
            }

            while (true)
            {
                reader.SkipWhitespace();
                if (!reader.TryReadString(out string key))
                {
                    return false;
                }

                reader.SkipWhitespace();
                if (!reader.TryConsumeChar(':'))
                {
                    return false;
                }

                reader.SkipWhitespace();
                switch (key)
                {
                    case "purpose":
                        if (!TryReadNullableString(ref reader, out string purpose)) return false;
                        if (purpose != null) model.Purpose = purpose;
                        break;

                    case "name":
                        if (!TryReadNullableString(ref reader, out string name)) return false;
                        if (name != null) model.Name = name;
                        break;

                    case "version":
                        if (!TryReadNullableString(ref reader, out string version)) return false;
                        if (version != null) model.Version = version;
                        break;

                    case "download_url":
                    case "downloadUrl":
                        if (!TryReadNullableString(ref reader, out string url)) return false;
                        if (url != null) model.DownloadUrl = url;
                        break;

                    case "arguments":
                        if (!reader.TryConsumeNull())
                        {
                            if (!TryReadStringArray(ref reader, model.Arguments)) return false;
                        }
                        break;

                    default:
                        if (!reader.TrySkipValue(2)) return false;
                        break;
                }

                reader.SkipWhitespace();
                if (reader.TryConsumeChar(','))
                {
                    continue;
                }

                return reader.TryConsumeChar('}');
            }
        }

        private static bool TryReadStringArray(ref JsonReader reader, List<string> target)
        {
            if (!reader.TryConsumeChar('['))
            {
                return false;
            }

            reader.SkipWhitespace();
            if (reader.TryConsumeChar(']'))
            {
                return true;
            }

            while (true)
            {
                reader.SkipWhitespace();
                if (!TryReadNullableString(ref reader, out string item))
                {
                    return false;
                }
                if (item != null)
                {
                    target.Add(item);
                }

                reader.SkipWhitespace();
                if (reader.TryConsumeChar(','))
                {
                    continue;
                }

                return reader.TryConsumeChar(']');
            }
        }

        private static bool TryReadNullableString(ref JsonReader reader, out string value)
        {
            if (reader.TryConsumeNull())
            {
                value = null;
                return true;
            }

            return reader.TryReadString(out value);
        }

        private static bool TryReadNullableInt64(ref JsonReader reader, out bool hasValue, out long value)
        {
            hasValue = false;
            value = 0;

            if (reader.TryConsumeNull())
            {
                return true;
            }

            char peek = reader.PeekChar();
            if (peek == '"')
            {
                if (!reader.TryReadString(out string str))
                {
                    return false;
                }

                if (long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                {
                    hasValue = true;
                    return true;
                }

                return false;
            }

            if (!reader.TryReadNumberToken(out string token))
            {
                return false;
            }

            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                hasValue = true;
                return true;
            }

            return false;
        }

        private static bool TryReadOptionalUInt32(ref JsonReader reader, out bool hasValue, out uint value)
        {
            hasValue = false;
            value = 0;

            if (reader.TryConsumeNull())
            {
                return true;
            }

            string token;
            if (reader.PeekChar() == '"')
            {
                if (!reader.TryReadString(out token))
                {
                    return false;
                }
            }
            else
            {
                if (!reader.TryReadNumberToken(out token))
                {
                    return false;
                }
            }

            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long signed))
            {
                if (signed >= 0 && signed <= uint.MaxValue)
                {
                    value = (uint)signed;
                    hasValue = true;
                }
                // Out-of-range (e.g., negative) is treated as left out rather than failing the whole response.
                return true;
            }

            return false;
        }

        private static bool TryReadOptionalBool(ref JsonReader reader, out bool hasValue, out bool value)
        {
            hasValue = false;
            value = false;

            if (reader.TryConsumeNull())
            {
                return true;
            }

            if (!reader.TryReadBool(out value))
            {
                return false;
            }

            hasValue = true;
            return true;
        }

        private static bool TryReadMotionSource(ref JsonReader reader, out bool hasValue, out MotionSource value)
        {
            hasValue = false;
            value = MotionSource.Unspecified;

            if (reader.TryConsumeNull())
            {
                return true;
            }

            if (reader.PeekChar() == '"')
            {
                if (!reader.TryReadString(out string name))
                {
                    return false;
                }

                // Unknown enum names are silently ignored per session.proto lines 30-31.
                if (ProtoEnumNames.TryParseMotionSource(name, out MotionSource parsed) && parsed != MotionSource.Unspecified)
                {
                    value = parsed;
                    hasValue = true;
                }
                return true;
            }

            if (!reader.TryReadNumberToken(out string token))
            {
                return false;
            }

            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int num))
            {
                if (num == (int)MotionSource.Native || num == (int)MotionSource.Unity)
                {
                    value = (MotionSource)num;
                    hasValue = true;
                }
                return true;
            }

            return false;
        }

        // ====================================================================
        // Minimal allocation-conscious recursive-descent JSON reader
        // ====================================================================

        private struct JsonReader
        {
            private readonly string _json;
            private int _pos;
            private readonly int _len;

            public JsonReader(string json)
            {
                _json = json;
                _pos = 0;
                _len = json.Length;
            }

            public int Position
            {
                get { return _pos; }
            }

            public bool IsAtEnd
            {
                get { return _pos >= _len; }
            }

            public char PeekChar()
            {
                return _pos < _len ? _json[_pos] : '\0';
            }

            public string Slice(int start, int length)
            {
                if (start < 0 || length <= 0 || start + length > _len)
                {
                    return string.Empty;
                }

                return _json.Substring(start, length);
            }

            public void SkipWhitespace()
            {
                while (_pos < _len)
                {
                    char c = _json[_pos];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                    {
                        _pos++;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            public bool TryConsumeChar(char expected)
            {
                if (_pos < _len && _json[_pos] == expected)
                {
                    _pos++;
                    return true;
                }

                return false;
            }

            public bool TryConsumeNull()
            {
                if (MatchesLiteral("null"))
                {
                    _pos += 4;
                    return true;
                }

                return false;
            }

            public bool TryReadBool(out bool value)
            {
                if (MatchesLiteral("true"))
                {
                    _pos += 4;
                    value = true;
                    return true;
                }

                if (MatchesLiteral("false"))
                {
                    _pos += 5;
                    value = false;
                    return true;
                }

                value = false;
                return false;
            }

            public bool TryReadString(out string value)
            {
                value = string.Empty;
                if (!TryConsumeChar('"'))
                {
                    return false;
                }

                int start = _pos;
                bool hasEscape = false;
                while (_pos < _len)
                {
                    char c = _json[_pos];
                    if (c == '"')
                    {
                        if (!hasEscape)
                        {
                            value = _pos == start ? string.Empty : _json.Substring(start, _pos - start);
                            _pos++;
                            return true;
                        }
                        break;
                    }

                    if (c == '\\')
                    {
                        hasEscape = true;
                        break;
                    }

                    if (c < 0x20)
                    {
                        return false;
                    }

                    _pos++;
                }

                if (!hasEscape)
                {
                    return false;
                }

                StringBuilder sb = new StringBuilder(_pos - start + 16);
                if (_pos > start)
                {
                    sb.Append(_json, start, _pos - start);
                }

                while (_pos < _len)
                {
                    char c = _json[_pos++];
                    if (c == '"')
                    {
                        value = sb.ToString();
                        return true;
                    }

                    if (c < 0x20)
                    {
                        return false;
                    }

                    if (c == '\\')
                    {
                        if (_pos >= _len)
                        {
                            return false;
                        }

                        char esc = _json[_pos++];
                        switch (esc)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (!TryReadHex4(out int codeUnit))
                                {
                                    return false;
                                }
                                sb.Append((char)codeUnit);
                                break;
                            default:
                                return false;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }

                return false;
            }

            public bool TryReadNumberToken(out string token)
            {
                token = string.Empty;
                int start = _pos;

                if (_pos < _len && _json[_pos] == '-')
                {
                    _pos++;
                }

                int digitsStart = _pos;
                if (_pos < _len && _json[_pos] == '0')
                {
                    _pos++;
                }
                else
                {
                    while (_pos < _len && _json[_pos] >= '0' && _json[_pos] <= '9')
                    {
                        _pos++;
                    }
                }

                if (_pos == digitsStart)
                {
                    _pos = start;
                    return false;
                }

                if (_pos < _len && _json[_pos] == '.')
                {
                    _pos++;
                    int fracStart = _pos;
                    while (_pos < _len && _json[_pos] >= '0' && _json[_pos] <= '9')
                    {
                        _pos++;
                    }
                    if (_pos == fracStart)
                    {
                        _pos = start;
                        return false;
                    }
                }

                if (_pos < _len && (_json[_pos] == 'e' || _json[_pos] == 'E'))
                {
                    _pos++;
                    if (_pos < _len && (_json[_pos] == '+' || _json[_pos] == '-'))
                    {
                        _pos++;
                    }
                    int expStart = _pos;
                    while (_pos < _len && _json[_pos] >= '0' && _json[_pos] <= '9')
                    {
                        _pos++;
                    }
                    if (_pos == expStart)
                    {
                        _pos = start;
                        return false;
                    }
                }

                token = _json.Substring(start, _pos - start);
                return true;
            }

            public bool TrySkipValue(int depth)
            {
                if (depth > MaxJsonDepth)
                {
                    return false;
                }

                SkipWhitespace();
                if (_pos >= _len)
                {
                    return false;
                }

                char c = _json[_pos];
                if (c == '"')
                {
                    return TryReadString(out _);
                }

                if (c == '{')
                {
                    _pos++;
                    SkipWhitespace();
                    if (TryConsumeChar('}'))
                    {
                        return true;
                    }

                    while (true)
                    {
                        SkipWhitespace();
                        if (!TryReadString(out _)) return false;
                        SkipWhitespace();
                        if (!TryConsumeChar(':')) return false;
                        if (!TrySkipValue(depth + 1)) return false;
                        SkipWhitespace();
                        if (TryConsumeChar(',')) continue;
                        return TryConsumeChar('}');
                    }
                }

                if (c == '[')
                {
                    _pos++;
                    SkipWhitespace();
                    if (TryConsumeChar(']'))
                    {
                        return true;
                    }

                    while (true)
                    {
                        if (!TrySkipValue(depth + 1)) return false;
                        SkipWhitespace();
                        if (TryConsumeChar(',')) continue;
                        return TryConsumeChar(']');
                    }
                }

                if (c == 't' || c == 'f')
                {
                    return TryReadBool(out _);
                }

                if (c == 'n')
                {
                    return TryConsumeNull();
                }

                if (c == '-' || (c >= '0' && c <= '9'))
                {
                    return TryReadNumberToken(out _);
                }

                return false;
            }

            private bool MatchesLiteral(string literal)
            {
                int litLen = literal.Length;
                if (_pos + litLen > _len)
                {
                    return false;
                }

                for (int i = 0; i < litLen; i++)
                {
                    if (_json[_pos + i] != literal[i])
                    {
                        return false;
                    }
                }

                if (_pos + litLen < _len)
                {
                    char next = _json[_pos + litLen];
                    if ((next >= 'a' && next <= 'z') || (next >= 'A' && next <= 'Z') || (next >= '0' && next <= '9') || next == '_')
                    {
                        return false;
                    }
                }

                return true;
            }

            private bool TryReadHex4(out int value)
            {
                value = 0;
                if (_pos + 4 > _len)
                {
                    return false;
                }

                for (int i = 0; i < 4; i++)
                {
                    char c = _json[_pos++];
                    int digit;
                    if (c >= '0' && c <= '9') digit = c - '0';
                    else if (c >= 'a' && c <= 'f') digit = 10 + (c - 'a');
                    else if (c >= 'A' && c <= 'F') digit = 10 + (c - 'A');
                    else return false;

                    value = (value << 4) | digit;
                }

                return true;
            }
        }
    }
}
