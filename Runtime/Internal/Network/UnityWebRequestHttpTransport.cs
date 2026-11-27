using System;
using System.Collections.Generic;
using System.Diagnostics;
#if UNITY_5_3_OR_NEWER
using UnityEngine.Networking;
#endif

namespace Asparagus.Sdk.Internal.Network
{
    /// <summary>
    /// Non-blocking <c>UnityWebRequest</c> HTTP POST transport with strict native disposal,
    /// round-trip timing, and contract header formatting (omitting <c>content-encoding</c> when uncompressed).
    /// </summary>
    public sealed class UnityWebRequestHttpTransport : IHttpTransport
    {
#if UNITY_5_3_OR_NEWER
        private struct InFlightRequest
        {
            public UnityWebRequest Request;
            public UnityWebRequestAsyncOperation Operation;
            public Action<HttpResponseResult> Callback;
            public long StartTicks;
        }

        private readonly List<InFlightRequest> _inFlight = new List<InFlightRequest>(4);
        private bool _disposed;
#endif

        public void StartPost(in HttpRequestSpec spec, Action<HttpResponseResult> onCompleted)
        {
#if UNITY_5_3_OR_NEWER
            if (_disposed || string.IsNullOrEmpty(spec.Url))
            {
                SafeInvoke(onCompleted, new HttpResponseResult(0L, null, 0, 0u, "Transport disposed or empty URL."));
                return;
            }

            UnityWebRequest request = null;
            try
            {
                byte[] payload = PrepareBody(spec.Body, spec.BodyLength);
                request = new UnityWebRequest(spec.Url, UnityWebRequest.kHttpVerbPOST);
                if (payload.Length > 0)
                {
                    UploadHandlerRaw uploadHandler = new UploadHandlerRaw(payload);
                    if (!string.IsNullOrEmpty(spec.ContentType))
                    {
                        uploadHandler.contentType = spec.ContentType;
                    }
                    request.uploadHandler = uploadHandler;
                }
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = spec.TimeoutSeconds;

                if (!string.IsNullOrEmpty(spec.BearerToken))
                {
                    request.SetRequestHeader("authorization", "Bearer " + spec.BearerToken);
                }

                if (!string.IsNullOrEmpty(spec.ContentType))
                {
                    request.SetRequestHeader("content-type", spec.ContentType);
                }

                // Per session.proto line 114: send content-encoding: gzip ONLY when compressed; omit when uncompressed.
                if (string.Equals(spec.ContentEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
                {
                    request.SetRequestHeader("content-encoding", "gzip");
                }

                long startTicks = Stopwatch.GetTimestamp();
                UnityWebRequestAsyncOperation op = request.SendWebRequest();
                _inFlight.Add(new InFlightRequest
                {
                    Request = request,
                    Operation = op,
                    Callback = onCompleted,
                    StartTicks = startTicks
                });
            }
            catch (Exception ex)
            {
                if (request != null)
                {
                    try { request.Dispose(); } catch { }
                }

                SafeInvoke(onCompleted, new HttpResponseResult(0L, null, 0, 0u, ex.Message));
            }
#else
            SafeInvoke(onCompleted, new HttpResponseResult(0L, null, 0, 0u, "UnityWebRequest unavailable outside Unity runtime."));
#endif
        }

        public void Tick()
        {
#if UNITY_5_3_OR_NEWER
            if (_disposed || _inFlight.Count == 0)
            {
                return;
            }

            for (int i = _inFlight.Count - 1; i >= 0; i--)
            {
                InFlightRequest item = _inFlight[i];
                bool done = false;
                try
                {
                    done = item.Operation == null || item.Operation.isDone;
                }
                catch
                {
                    done = true;
                }

                if (!done)
                {
                    continue;
                }

                _inFlight.RemoveAt(i);
                HttpResponseResult result;
                try
                {
                    uint rttMs = ComputeElapsedMs(item.StartTicks);
                    long statusCode = item.Request != null ? item.Request.responseCode : 0L;
                    byte[] body = null;
                    int bodyLen = 0;
                    string err = null;

                    if (item.Request != null)
                    {
                        if (item.Request.result == UnityWebRequest.Result.ConnectionError
                            || item.Request.result == UnityWebRequest.Result.DataProcessingError)
                        {
                            statusCode = 0L;
                            err = item.Request.error;
                        }
                        else
                        {
                            if (item.Request.downloadHandler != null)
                            {
                                body = item.Request.downloadHandler.data;
                                bodyLen = body != null ? body.Length : 0;
                            }

                            if (statusCode <= 0L)
                            {
                                err = item.Request.error;
                            }
                        }
                    }

                    result = new HttpResponseResult(statusCode, body, bodyLen, rttMs, err);
                }
                catch (Exception ex)
                {
                    result = new HttpResponseResult(0L, null, 0, ComputeElapsedMs(item.StartTicks), ex.Message);
                }
                finally
                {
                    if (item.Request != null)
                    {
                        try { item.Request.Dispose(); } catch { }
                    }
                }

                SafeInvoke(item.Callback, result);
            }
#endif
        }

        public void Dispose()
        {
#if UNITY_5_3_OR_NEWER
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (int i = 0; i < _inFlight.Count; i++)
            {
                UnityWebRequest req = _inFlight[i].Request;
                if (req != null)
                {
                    try { req.Abort(); } catch { }
                    try { req.Dispose(); } catch { }
                }
            }

            _inFlight.Clear();
#endif
        }

#if UNITY_5_3_OR_NEWER
        private static byte[] PrepareBody(byte[] body, int length)
        {
            if (body == null || length <= 0)
            {
                return Array.Empty<byte>();
            }

            if (length >= body.Length)
            {
                return body;
            }

            byte[] exact = new byte[length];
            Buffer.BlockCopy(body, 0, exact, 0, length);
            return exact;
        }

        private static uint ComputeElapsedMs(long startTicks)
        {
            long delta = Stopwatch.GetTimestamp() - startTicks;
            if (delta <= 0L || Stopwatch.Frequency <= 0L)
            {
                return 0u;
            }

            long ms = (delta * 1000L) / Stopwatch.Frequency;
            if (ms < 0L) return 0u;
            if (ms > uint.MaxValue) return uint.MaxValue;
            return (uint)ms;
        }
#endif

        private static void SafeInvoke(Action<HttpResponseResult> callback, HttpResponseResult result)
        {
            if (callback == null)
            {
                return;
            }

            try
            {
                callback(result);
            }
            catch
            {
                // Fault containment: callback exceptions never escape transport.
            }
        }
    }
}
