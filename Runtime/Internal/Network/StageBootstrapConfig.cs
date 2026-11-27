using Asparagus.Sdk.Contracts;

namespace Asparagus.Sdk.Internal.Network
{
    /// <summary>
    /// Explicit session bootstrap override for stage testing while <c>/routes/asparagus/sessions/start</c>
    /// is not yet deployed on stage (<c>BUILDING_AN_SDK.md</c> lines 104-133).
    /// Never inspects JWT contents and calls zero Turnip endpoints.
    /// </summary>
    public sealed class StageBootstrapConfig
    {
        public string Token = string.Empty;
        public string IdOrg = string.Empty;
        public string IdDeployment = string.Empty;
        public string IdSource = string.Empty;
        public string IdSession = string.Empty;
        public string BatchEndpoint = string.Empty;
        public SessionSettings Settings;
        public long? ServerUtcMs;
        public long TokenExpiresUtcMs;
        public string AccessRequestEndpoint = string.Empty;
        public string ForgetRequestEndpoint = string.Empty;
    }
}
