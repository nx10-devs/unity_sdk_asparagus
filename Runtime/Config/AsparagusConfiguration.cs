using System;

namespace Asparagus.Sdk
{
    /// <summary>
    /// Pure-C# configuration holding both Staging and Production deployment API keys,
    /// environment selection mode, and default demo flag.
    /// </summary>
    public sealed class AsparagusConfiguration
    {
        public const string StagingApiKeyEnvVar = "ASPARAGUS_STAGING_API_KEY";
        public const string ProductionApiKeyEnvVar = "ASPARAGUS_PRODUCTION_API_KEY";
        public const string FallbackApiKeyEnvVar = "ASPARAGUS_API_KEY";

        public AsparagusEnvironmentMode EnvironmentMode = AsparagusEnvironmentMode.Automatic;
        public string StagingApiKey = string.Empty;
        public string ProductionApiKey = string.Empty;
        public bool DemoMode = false;

        public AsparagusConfiguration Clone()
        {
            return new AsparagusConfiguration
            {
                EnvironmentMode = EnvironmentMode,
                StagingApiKey = StagingApiKey ?? string.Empty,
                ProductionApiKey = ProductionApiKey ?? string.Empty,
                DemoMode = DemoMode
            };
        }

        public AsparagusEnvironment ResolveEnvironment(bool isEditorOrDevelopmentBuild)
        {
            switch (EnvironmentMode)
            {
                case AsparagusEnvironmentMode.Staging:
                    return AsparagusEnvironment.Staging;
                case AsparagusEnvironmentMode.Production:
                    return AsparagusEnvironment.Production;
                default:
                    return isEditorOrDevelopmentBuild
                        ? AsparagusEnvironment.Staging
                        : AsparagusEnvironment.Production;
            }
        }

        public string ResolveControlPlaneBaseUrl(bool isEditorOrDevelopmentBuild)
        {
            return AsparagusEndpoints.GetControlPlaneBaseUrl(ResolveEnvironment(isEditorOrDevelopmentBuild));
        }

        public string ResolveApiKey(bool isEditorOrDevelopmentBuild, Func<string, string> envReader = null)
        {
            return ResolveApiKeyForEnvironment(ResolveEnvironment(isEditorOrDevelopmentBuild), envReader);
        }

        public string ResolveApiKeyForEnvironment(AsparagusEnvironment environment, Func<string, string> envReader = null)
        {
            string configured = environment == AsparagusEnvironment.Staging
                ? TrimOrEmpty(StagingApiKey)
                : TrimOrEmpty(ProductionApiKey);

            if (!string.IsNullOrEmpty(configured))
            {
                return configured;
            }

            string envSpecific = ReadEnvTrimmed(
                environment == AsparagusEnvironment.Staging ? StagingApiKeyEnvVar : ProductionApiKeyEnvVar,
                envReader);
            if (!string.IsNullOrEmpty(envSpecific))
            {
                return envSpecific;
            }

            return ReadEnvTrimmed(FallbackApiKeyEnvVar, envReader);
        }

        private static string ReadEnvTrimmed(string variableName, Func<string, string> envReader)
        {
            if (string.IsNullOrEmpty(variableName))
            {
                return string.Empty;
            }

            try
            {
                string raw = envReader != null
                    ? envReader(variableName)
                    : Environment.GetEnvironmentVariable(variableName);
                return TrimOrEmpty(raw);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string TrimOrEmpty(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
    }
}
