using System;

namespace Asparagus.Sdk
{
    /// <summary>
    /// Target Asparagus Control Plane environment.
    /// </summary>
    public enum AsparagusEnvironment : byte
    {
        Staging = 0,
        Production = 1
    }

    /// <summary>
    /// Controls how <see cref="AsparagusConfiguration"/> chooses between
    /// <see cref="AsparagusEnvironment.Staging"/> and <see cref="AsparagusEnvironment.Production"/>.
    /// </summary>
    public enum AsparagusEnvironmentMode : byte
    {
        /// <summary>
        /// Uses Staging in the Unity Editor and Development Builds, and Production in non-development Release builds.
        /// </summary>
        Automatic = 0,

        /// <summary>
        /// Always targets Staging (<c>https://control-plane.affectstack-stage.com</c>).
        /// </summary>
        Staging = 1,

        /// <summary>
        /// Always targets Production (<c>https://control-plane.affectstack.com</c>).
        /// </summary>
        Production = 2
    }

    /// <summary>
    /// Canonical Control Plane URLs and environment classification helpers.
    /// </summary>
    public static class AsparagusEndpoints
    {
        public const string StagingControlPlaneBaseUrl = "https://control-plane.affectstack-stage.com";
        public const string ProductionControlPlaneBaseUrl = "https://control-plane.affectstack.com";
        public const string SessionStartPath = "/routes/asparagus/sessions/start";

        public static string GetControlPlaneBaseUrl(AsparagusEnvironment environment)
        {
            return environment == AsparagusEnvironment.Production
                ? ProductionControlPlaneBaseUrl
                : StagingControlPlaneBaseUrl;
        }

        public static string GetSessionStartUrl(AsparagusEnvironment environment)
        {
            return GetControlPlaneBaseUrl(environment) + SessionStartPath;
        }

        public static AsparagusEnvironment ClassifyEnvironment(string controlPlaneBaseUrl)
        {
            if (!string.IsNullOrEmpty(controlPlaneBaseUrl)
                && controlPlaneBaseUrl.IndexOf("affectstack-stage.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return AsparagusEnvironment.Staging;
            }

            return AsparagusEnvironment.Production;
        }
    }
}
