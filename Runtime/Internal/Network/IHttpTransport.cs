using System;

namespace Asparagus.Sdk.Internal.Network
{
    public readonly struct HttpRequestSpec
    {
        public readonly string Url;
        public readonly string BearerToken;
        public readonly string ContentType;
        public readonly string ContentEncoding;
        public readonly byte[] Body;
        public readonly int BodyLength;
        public readonly int TimeoutSeconds;

        public HttpRequestSpec(
            string url,
            string bearerToken,
            string contentType,
            string contentEncoding,
            byte[] body,
            int bodyLength,
            int timeoutSeconds = 15)
        {
            Url = url ?? string.Empty;
            BearerToken = bearerToken;
            ContentType = contentType;
            ContentEncoding = contentEncoding;
            Body = body;
            BodyLength = bodyLength;
            TimeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 15;
        }
    }

    public readonly struct HttpResponseResult
    {
        /// <summary>HTTP status code, or <c>0</c> on network error / timeout / no answer.</summary>
        public readonly long StatusCode;
        public readonly byte[] Body;
        public readonly int BodyLength;
        public readonly uint RoundTripMs;
        public readonly string ErrorMessage;

        public HttpResponseResult(
            long statusCode,
            byte[] body,
            int bodyLength,
            uint roundTripMs,
            string errorMessage = null)
        {
            StatusCode = statusCode;
            Body = body;
            BodyLength = bodyLength;
            RoundTripMs = roundTripMs;
            ErrorMessage = errorMessage;
        }
    }

    /// <summary>
    /// Non-blocking HTTP POST transport interface decouplable from <c>UnityWebRequest</c> for EditMode tests.
    /// </summary>
    public interface IHttpTransport : IDisposable
    {
        void StartPost(in HttpRequestSpec spec, Action<HttpResponseResult> onCompleted);
        void Tick();
    }
}
