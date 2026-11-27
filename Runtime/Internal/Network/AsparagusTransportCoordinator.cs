using System;
using System.Text;
using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Encoding;
using Asparagus.Sdk.Internal.Pipeline;
using Asparagus.Sdk.Internal.Time;
using Asparagus.Sdk.Internal.Tracking;

namespace Asparagus.Sdk.Internal.Network
{
    public enum TransportState : byte
    {
        Idle = 0,
        StartingSession = 1,
        Active = 2,
        WaitingSessionRetry = 3,
        WaitingReauth401 = 4,
        RefusedForAppRun = 5
    }

    /// <summary>
    /// Full protocol state machine implementing <c>session.proto</c> and <c>BUILDING_AN_SDK.md</c>:
    /// Control Plane session start (plus Stage Bootstrap Override), proactive token-expiry refresh,
    /// FIFO batch uploads, 2xx/401/408/429/5xx/4xx status matrix handling with jittered exponential
    /// backoff, and compliance access/forget POST requests.
    /// </summary>
    public sealed class AsparagusTransportCoordinator
    {
        public const string DefaultControlPlaneBaseUrl = "https://control-plane.affectstack-stage.com";
        private const string SessionStartPath = "/routes/asparagus/sessions/start";
        private const string ContentTypeJson = "application/json";
        private const string ContentTypeProtobuf = "application/x-protobuf";

        private readonly IHttpTransport _httpTransport;
        private readonly AsparagusWorker _worker;
        private readonly SessionClock _sessionClock;
        private readonly IClockSource _clockSource;
        private readonly RetryBackoffPolicy _retryPolicy;
        private readonly DiagnosticRecorder _diagnostics;

        private TransportState _state = TransportState.Idle;
        private bool _isCollectingEnabled;
        private bool _isRefusedForAppRun;
        private bool _isStageOverrideMode;
        private bool _stageTokenRejectedPaused;

        private string _controlPlaneBaseUrl = string.Empty;
        private SessionStartRequest _lastStartRequest;
        private SessionStartResponse _activeResponse;

        private bool _sessionRequestInFlight;
        private bool _pendingDeferredSessionStart;
        private uint _sessionGeneration;
        private bool _uploadInFlight;
        private int _sessionRetryAttempt;
        private int _uploadRetryAttempt;
        private int _consecutive401Count;

        private long _lastTickSteadyMs;
        private long _nextSessionRetrySteadyMs;
        private long _nextUploadRetrySteadyMs;

        public Func<CollectionHistory> HistoryProvider;
        public Action OnBeforeSessionActivated;
        public Action<SessionStartResponse, uint> OnSessionStarted;
        public Action OnStageTokenRejected;

        public AsparagusTransportCoordinator(
            IHttpTransport httpTransport,
            AsparagusWorker worker,
            SessionClock sessionClock,
            IClockSource clockSource,
            RetryBackoffPolicy retryPolicy = null,
            DiagnosticRecorder diagnostics = null)
        {
            _httpTransport = httpTransport ?? throw new ArgumentNullException(nameof(httpTransport));
            _worker = worker ?? throw new ArgumentNullException(nameof(worker));
            _sessionClock = sessionClock ?? throw new ArgumentNullException(nameof(sessionClock));
            _clockSource = clockSource ?? new SystemClockSource();
            _retryPolicy = retryPolicy ?? new RetryBackoffPolicy();
            _diagnostics = diagnostics;
            _lastTickSteadyMs = _clockSource.SteadyTimeUs / 1000L;
        }

        public TransportState State
        {
            get { return _state; }
        }

        public bool IsCollectingEnabled
        {
            get { return _isCollectingEnabled; }
        }

        public bool IsRefusedForAppRun
        {
            get { return _isRefusedForAppRun; }
        }

        public bool IsStageOverrideMode
        {
            get { return _isStageOverrideMode; }
        }

        public SessionStartResponse ActiveSessionResponse
        {
            get { return _activeResponse; }
        }

        public SessionSettings ActiveSettings
        {
            get
            {
                return _activeResponse != null && _activeResponse.Settings != null
                    ? _activeResponse.Settings
                    : SessionSettings.CreateDefaults();
            }
        }

        public string BatchEndpoint
        {
            get { return _activeResponse != null ? _activeResponse.BatchEndpoint : string.Empty; }
        }

        public string Token
        {
            get { return _activeResponse != null ? _activeResponse.Token : string.Empty; }
        }

        public int Consecutive401Count
        {
            get { return _consecutive401Count; }
        }

        public int UploadRetryAttempt
        {
            get { return _uploadRetryAttempt; }
        }

        public int SessionRetryAttempt
        {
            get { return _sessionRetryAttempt; }
        }

        public bool StartSessionViaControlPlane(string controlPlaneBaseUrl, SessionStartRequest request)
        {
            try
            {
                if (_isRefusedForAppRun)
                {
                    return false;
                }

                if (string.IsNullOrEmpty(controlPlaneBaseUrl) || request == null)
                {
                    return false;
                }

                _controlPlaneBaseUrl = controlPlaneBaseUrl.TrimEnd('/');
                _lastStartRequest = request;
                _isStageOverrideMode = false;
                _stageTokenRejectedPaused = false;
                _isCollectingEnabled = false;
                _sessionRetryAttempt = 0;
                _sessionGeneration++;
                _sessionRequestInFlight = false;
                _lastTickSteadyMs = _clockSource.SteadyTimeUs / 1000L;
                _worker.SetCanAcceptImmediateUpload(false);

                if (!_worker.IsStartupLoadComplete)
                {
                    // Defer SessionStart POST until background worker completes loading asparagus_history.dat.
                    _pendingDeferredSessionStart = true;
                    _state = TransportState.StartingSession;
                    return true;
                }

                _pendingDeferredSessionStart = false;
                DispatchSessionStartRequest();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool StartSessionWithStageOverride(StageBootstrapConfig config)
        {
            try
            {
                if (config == null)
                {
                    return false;
                }

                SessionSettings settings = config.Settings != null
                    ? config.Settings.Clone()
                    : SessionSettings.CreateDefaults();

                long serverUtcMs = config.ServerUtcMs.HasValue
                    ? config.ServerUtcMs.Value
                    : _clockSource.WallUtcMs;

                SessionStartResponse synthetic = new SessionStartResponse
                {
                    IdOrg = config.IdOrg ?? string.Empty,
                    IdDeployment = config.IdDeployment ?? string.Empty,
                    IdSource = config.IdSource ?? string.Empty,
                    IdSession = config.IdSession ?? string.Empty,
                    Token = config.Token ?? string.Empty,
                    TokenExpiresUtcMs = config.TokenExpiresUtcMs,
                    ServerUtcMs = serverUtcMs,
                    BatchEndpoint = config.BatchEndpoint ?? string.Empty,
                    AccessRequestEndpoint = config.AccessRequestEndpoint ?? string.Empty,
                    ForgetRequestEndpoint = config.ForgetRequestEndpoint ?? string.Empty,
                    Settings = settings
                };

                _isStageOverrideMode = true;
                _stageTokenRejectedPaused = false;
                _controlPlaneBaseUrl = string.Empty;
                _lastStartRequest = null;
                _sessionGeneration++;
                _sessionRequestInFlight = false;
                _pendingDeferredSessionStart = false;
                _sessionRetryAttempt = 0;
                _uploadRetryAttempt = 0;
                _consecutive401Count = 0;
                _nextUploadRetrySteadyMs = 0L;
                _lastTickSteadyMs = _clockSource.SteadyTimeUs / 1000L;

                ActivateSession(synthetic, 0u, false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void UpdateStageToken(string newToken)
        {
            if (_activeResponse == null || string.IsNullOrEmpty(newToken))
            {
                return;
            }

            _activeResponse.Token = newToken;
            _stageTokenRejectedPaused = false;
            _consecutive401Count = 0;
            _nextUploadRetrySteadyMs = 0L;
            UpdateWorkerImmediateUploadFlag(_lastTickSteadyMs);
        }

        public void StopSession()
        {
            _sessionGeneration++;
            _sessionRequestInFlight = false;
            _pendingDeferredSessionStart = false;
            _isCollectingEnabled = false;
            if (!_isRefusedForAppRun)
            {
                _state = TransportState.Idle;
            }
            UpdateWorkerImmediateUploadFlag(_lastTickSteadyMs);
        }

        public void Tick(long nowSteadyMs)
        {
            try
            {
                _lastTickSteadyMs = nowSteadyMs;
                _httpTransport.Tick();

                if (_isRefusedForAppRun)
                {
                    _worker.SetCanAcceptImmediateUpload(false);
                    return;
                }

                if (_pendingDeferredSessionStart && _worker.IsStartupLoadComplete && !_sessionRequestInFlight)
                {
                    _pendingDeferredSessionStart = false;
                    _worker.SetCanAcceptImmediateUpload(false);
                    DispatchSessionStartRequest();
                    return;
                }

                if ((_state == TransportState.WaitingSessionRetry || _state == TransportState.WaitingReauth401)
                    && !_sessionRequestInFlight
                    && nowSteadyMs >= _nextSessionRetrySteadyMs)
                {
                    _worker.SetCanAcceptImmediateUpload(false);
                    DispatchSessionStartRequest();
                    return;
                }

                // Allow pumping queued batches (including final SessionEnded batches) while Active or Idle (after StopSession).
                bool canPumpState = _state == TransportState.Active || _state == TransportState.Idle;
                if (!canPumpState
                    || _uploadInFlight
                    || _stageTokenRejectedPaused
                    || _activeResponse == null
                    || string.IsNullOrEmpty(_activeResponse.BatchEndpoint))
                {
                    _worker.SetCanAcceptImmediateUpload(false);
                    return;
                }

                // Proactive token-expiry check (session.proto line 101): refresh session before uploading while Active.
                if (_sessionClock.IsTokenExpired(_activeResponse.TokenExpiresUtcMs))
                {
                    _worker.SetCanAcceptImmediateUpload(false);
                    if (_state == TransportState.Active
                        && !_isStageOverrideMode
                        && !string.IsNullOrEmpty(_controlPlaneBaseUrl)
                        && _lastStartRequest != null)
                    {
                        DispatchSessionStartRequest();
                    }
                    return;
                }

                if (nowSteadyMs < _nextUploadRetrySteadyMs)
                {
                    _worker.SetCanAcceptImmediateUpload(false);
                    return;
                }

                _worker.SetCanAcceptImmediateUpload(true);

                if (_worker.TryTakePreparedUpload(out PreparedUpload prepared))
                {
                    DispatchBatchUpload(in prepared);
                    return;
                }

                if (_worker.DiskQueue.QueuedCount > 0)
                {
                    _worker.RequestNextUploadPayload(_clockSource.WallUtcMs);
                    if (_worker.IsRunningSynchronously && _worker.TryTakePreparedUpload(out PreparedUpload syncPrepared))
                    {
                        DispatchBatchUpload(in syncPrepared);
                    }
                }
            }
            catch
            {
                // Total fault containment: transport tick never throws into host game loop.
            }
        }

        public void SendPersonalDataAccessRequest(string jsonBody, Action<bool, long> callback)
        {
            string url = _activeResponse != null ? _activeResponse.AccessRequestEndpoint : string.Empty;
            SendCompliancePost(url, jsonBody, callback);
        }

        public void SendPersonalDataForgetRequest(string jsonBody, Action<bool, long> callback)
        {
            string url = _activeResponse != null ? _activeResponse.ForgetRequestEndpoint : string.Empty;
            SendCompliancePost(url, jsonBody, callback);
        }

        private void SendCompliancePost(string url, string jsonBody, Action<bool, long> callback)
        {
            try
            {
                if (string.IsNullOrEmpty(url) || _activeResponse == null || string.IsNullOrEmpty(_activeResponse.Token))
                {
                    SafeInvokeCompliance(callback, false, 0L);
                    return;
                }

                byte[] body = string.IsNullOrEmpty(jsonBody)
                    ? System.Text.Encoding.UTF8.GetBytes("{}")
                    : System.Text.Encoding.UTF8.GetBytes(jsonBody);

                HttpRequestSpec spec = new HttpRequestSpec(
                    url,
                    _activeResponse.Token,
                    ContentTypeJson,
                    null,
                    body,
                    body.Length);

                _httpTransport.StartPost(in spec, result =>
                {
                    bool ok = result.StatusCode >= 200L && result.StatusCode < 300L;
                    SafeInvokeCompliance(callback, ok, result.StatusCode);
                });
            }
            catch
            {
                SafeInvokeCompliance(callback, false, 0L);
            }
        }

        private void DispatchSessionStartRequest()
        {
            if (_sessionRequestInFlight || _isRefusedForAppRun || string.IsNullOrEmpty(_controlPlaneBaseUrl) || _lastStartRequest == null)
            {
                return;
            }

            if (HistoryProvider != null)
            {
                try
                {
                    _lastStartRequest.History = HistoryProvider();
                }
                catch
                {
                }
            }

            if (!SessionJsonCodec.TrySerializeSessionStartRequest(_lastStartRequest, out string json, out string err))
            {
                RecordWarning("session_start_serialize_failed", err ?? "Failed to serialize SessionStartRequest.");
                MarkRefusedForAppRun();
                return;
            }

            byte[] body = System.Text.Encoding.UTF8.GetBytes(json);
            string url = _controlPlaneBaseUrl + SessionStartPath;
            HttpRequestSpec spec = new HttpRequestSpec(url, null, ContentTypeJson, null, body, body.Length);

            uint requestGeneration = _sessionGeneration;
            _sessionRequestInFlight = true;
            _state = TransportState.StartingSession;
            _worker.SetCanAcceptImmediateUpload(false);
            _httpTransport.StartPost(in spec, result => HandleSessionStartResponse(requestGeneration, result));
        }

        private void HandleSessionStartResponse(uint requestGeneration, HttpResponseResult result)
        {
            // Ignore stale in-flight responses if StopSession() or a newer StartSession*() ran meanwhile.
            if (requestGeneration != _sessionGeneration)
            {
                return;
            }

            _sessionRequestInFlight = false;

            try
            {
                if (result.StatusCode == 200L)
                {
                    string json = (result.Body != null && result.BodyLength > 0)
                        ? System.Text.Encoding.UTF8.GetString(result.Body, 0, result.BodyLength)
                        : string.Empty;

                    if (SessionJsonCodec.TryParseSessionStartResponse(json, out SessionStartResponse parsed, out _, out string parseErr))
                    {
                        _sessionRetryAttempt = 0;
                        _nextUploadRetrySteadyMs = 0L;
                        bool isDemo = _lastStartRequest != null && _lastStartRequest.Demo;
                        ActivateSession(parsed, result.RoundTripMs, isDemo);
                        return;
                    }

                    RecordWarning("session_start_invalid_200", parseErr ?? "Session start 200 returned error or malformed JSON.");
                    MarkRefusedForAppRun();
                    return;
                }

                if (IsRetryableStatus(result.StatusCode))
                {
                    _isCollectingEnabled = false;
                    _state = TransportState.WaitingSessionRetry;
                    _worker.SetCanAcceptImmediateUpload(false);
                    // No settings yet: use built-in retry waits (session.proto lines 20-24).
                    long waitMs = _retryPolicy.ComputeDelayMs(
                        _sessionRetryAttempt++,
                        SessionSettings.DefaultRetryFirstWaitMs,
                        SessionSettings.DefaultRetryLongestWaitMs);
                    _nextSessionRetrySteadyMs = _lastTickSteadyMs + waitMs;
                    return;
                }

                // Any other status (400, 401, 403, 404, etc.): refused for this app run.
                RecordWarning("session_start_refused_" + result.StatusCode, "Session start refused with HTTP " + result.StatusCode);
                MarkRefusedForAppRun();
            }
            catch (Exception ex)
            {
                RecordWarning("session_start_handler_error", ex.Message);
                _state = TransportState.WaitingSessionRetry;
                _worker.SetCanAcceptImmediateUpload(false);
                long waitMs = _retryPolicy.ComputeDelayMs(
                    _sessionRetryAttempt++,
                    SessionSettings.DefaultRetryFirstWaitMs,
                    SessionSettings.DefaultRetryLongestWaitMs);
                _nextSessionRetrySteadyMs = _lastTickSteadyMs + waitMs;
            }
        }

        private void ActivateSession(SessionStartResponse response, uint roundTripMs, bool isDemo)
        {
            // Notify listeners BEFORE overwriting active session/clock so an outgoing session window
            // can close cleanly with its original SessionClock and ServerAnchor intact.
            if (OnBeforeSessionActivated != null)
            {
                try
                {
                    OnBeforeSessionActivated();
                }
                catch
                {
                }
            }

            _activeResponse = response;
            SessionSettings settings = ActiveSettings;

            _sessionClock.StartSession(response.ServerUtcMs, roundTripMs);
            _worker.DiskQueue.ConfigureLimits(settings.QueueMaxBytes, settings.QueueMaxAgeS);
            _worker.EnqueueDiscardMismatchedScope(response.IdOrg, response.IdDeployment, response.IdSource);

            _isCollectingEnabled = !isDemo && !string.IsNullOrEmpty(response.BatchEndpoint);
            _state = TransportState.Active;
            UpdateWorkerImmediateUploadFlag(_lastTickSteadyMs);

            if (OnSessionStarted != null)
            {
                try
                {
                    OnSessionStarted(response, roundTripMs);
                }
                catch
                {
                }
            }
        }

        private void DispatchBatchUpload(in PreparedUpload prepared)
        {
            if (_activeResponse == null || string.IsNullOrEmpty(_activeResponse.BatchEndpoint))
            {
                _worker.SetCanAcceptImmediateUpload(false);
                _worker.AcknowledgeRetryableFailure(in prepared);
                return;
            }

            _uploadInFlight = true;
            PreparedUpload captured = prepared;

            HttpRequestSpec spec = new HttpRequestSpec(
                _activeResponse.BatchEndpoint,
                _activeResponse.Token,
                ContentTypeProtobuf,
                prepared.Metadata.IsCompressed ? "gzip" : null,
                prepared.Payload,
                prepared.Payload != null ? prepared.Payload.Length : 0);

            _httpTransport.StartPost(in spec, result => HandleUploadResponse(captured, result));
        }

        private void HandleUploadResponse(PreparedUpload prepared, HttpResponseResult result)
        {
            _uploadInFlight = false;

            try
            {
                // 2xx: intake's queue holds the batch -> delete copy & reset both retry and 401 backoff counters.
                if (result.StatusCode >= 200L && result.StatusCode < 300L)
                {
                    if (result.Body != null && result.BodyLength > 0)
                    {
                        BatchProtobufCodec.TryDecodeUploadResponse(result.Body, 0, result.BodyLength, out _);
                    }

                    _worker.AcknowledgeSuccess(in prepared);
                    _uploadRetryAttempt = 0;
                    _consecutive401Count = 0;
                    _nextUploadRetrySteadyMs = 0L;
                    UpdateWorkerImmediateUploadFlag(_lastTickSteadyMs);
                    return;
                }

                // 401: token refused -> keep batch in queue, wait 401 backoff, start new session, retry under new token.
                if (result.StatusCode == 401L)
                {
                    _worker.SetCanAcceptImmediateUpload(false);
                    _worker.AcknowledgeRetryableFailure(in prepared);
                    SessionSettings s = ActiveSettings;
                    long waitMs = _retryPolicy.ComputeDelayMs(_consecutive401Count++, s.RetryFirstWaitMs, s.RetryLongestWaitMs);

                    if (_state == TransportState.Active
                        && !_isStageOverrideMode
                        && !string.IsNullOrEmpty(_controlPlaneBaseUrl)
                        && _lastStartRequest != null)
                    {
                        _state = TransportState.WaitingReauth401;
                        _nextSessionRetrySteadyMs = _lastTickSteadyMs + waitMs;
                    }
                    else
                    {
                        _stageTokenRejectedPaused = true;
                        RecordWarning("stage_token_rejected_401", "Upload received HTTP 401; waiting for refreshed token.");
                        if (OnStageTokenRejected != null)
                        {
                            try { OnStageTokenRejected(); } catch { }
                        }
                    }
                    return;
                }

                // 408, 429, 5xx, or 0 (offline/no answer): keep batch and retry with jittered exponential backoff.
                if (IsRetryableStatus(result.StatusCode))
                {
                    _worker.SetCanAcceptImmediateUpload(false);
                    _worker.AcknowledgeRetryableFailure(in prepared);
                    SessionSettings s = ActiveSettings;
                    long waitMs = _retryPolicy.ComputeDelayMs(_uploadRetryAttempt++, s.RetryFirstWaitMs, s.RetryLongestWaitMs);
                    _nextUploadRetrySteadyMs = _lastTickSteadyMs + waitMs;
                    return;
                }

                // Any other status (400, 403, 413, etc.): refused for good -> drop and record diagnostic.
                _worker.AcknowledgePermanentRefusal(in prepared);
                _uploadRetryAttempt = 0;
                _nextUploadRetrySteadyMs = 0L;
                UpdateWorkerImmediateUploadFlag(_lastTickSteadyMs);
                RecordWarning("upload_refused_" + result.StatusCode, "Batch permanently refused with HTTP " + result.StatusCode);
            }
            catch (Exception ex)
            {
                _worker.SetCanAcceptImmediateUpload(false);
                _worker.AcknowledgeRetryableFailure(in prepared);
                RecordWarning("upload_handler_error", ex.Message);
            }
        }

        private void MarkRefusedForAppRun()
        {
            _isRefusedForAppRun = true;
            _isCollectingEnabled = false;
            _state = TransportState.RefusedForAppRun;
            _worker.SetCanAcceptImmediateUpload(false);
        }

        private void UpdateWorkerImmediateUploadFlag(long nowSteadyMs)
        {
            bool canSendNow = !_isRefusedForAppRun
                && (_state == TransportState.Active || _state == TransportState.Idle)
                && !_stageTokenRejectedPaused
                && _activeResponse != null
                && !string.IsNullOrEmpty(_activeResponse.BatchEndpoint)
                && !_sessionClock.IsTokenExpired(_activeResponse.TokenExpiresUtcMs)
                && nowSteadyMs >= _nextUploadRetrySteadyMs;
            _worker.SetCanAcceptImmediateUpload(canSendNow);
        }

        private void RecordWarning(string code, string detail)
        {
            if (_diagnostics != null)
            {
                _diagnostics.RecordWarning(code, detail, _clockSource.UnityTimeUs);
            }
        }

        private static bool IsRetryableStatus(long statusCode)
        {
            return statusCode == 0L || statusCode == 408L || statusCode == 429L || statusCode >= 500L;
        }

        private static void SafeInvokeCompliance(Action<bool, long> callback, bool ok, long statusCode)
        {
            if (callback == null) return;
            try { callback(ok, statusCode); } catch { }
        }
    }
}
