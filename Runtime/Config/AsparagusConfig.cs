#if UNITY_5_3_OR_NEWER
using UnityEngine;

namespace Asparagus.Sdk
{
    /// <summary>
    /// Central Unity <see cref="ScriptableObject"/> configuration loaded from
    /// <c>Assets/Resources/AsparagusConfig.asset</c> by <see cref="AsparagusSDK.StartSession()"/>.
    /// Create or open it via <b>Tools -&gt; Asparagus SDK -&gt; Edit Configuration</b>.
    /// </summary>
    [CreateAssetMenu(fileName = "AsparagusConfig", menuName = "Asparagus SDK/Configuration")]
    public sealed class AsparagusConfig : ScriptableObject
    {
        public const string ResourceName = "AsparagusConfig";

        [Header("Environment Selection")]
        [Tooltip("Automatic uses Staging in the Unity Editor & Development Builds, and Production in Release builds.")]
        [SerializeField] private AsparagusEnvironmentMode _environmentMode = AsparagusEnvironmentMode.Automatic;

        [Header("Deployment API Keys")]
        [Tooltip("API key used when the active environment resolves to Staging (https://control-plane.affectstack-stage.com).")]
        [SerializeField] private string _stagingApiKey = string.Empty;

        [Tooltip("API key used when the active environment resolves to Production (https://control-plane.affectstack.com).")]
        [SerializeField] private string _productionApiKey = string.Empty;

        [Header("Session Options")]
        [Tooltip("When enabled, StartSession() sends demo=true unless overridden in code.")]
        [SerializeField] private bool _demoMode = false;

        public AsparagusEnvironmentMode EnvironmentMode
        {
            get { return _environmentMode; }
        }

        public string StagingApiKey
        {
            get { return _stagingApiKey ?? string.Empty; }
        }

        public string ProductionApiKey
        {
            get { return _productionApiKey ?? string.Empty; }
        }

        public bool DemoMode
        {
            get { return _demoMode; }
        }

        public AsparagusConfiguration ToConfiguration()
        {
            return new AsparagusConfiguration
            {
                EnvironmentMode = _environmentMode,
                StagingApiKey = _stagingApiKey ?? string.Empty,
                ProductionApiKey = _productionApiKey ?? string.Empty,
                DemoMode = _demoMode
            };
        }

        public static AsparagusConfig LoadFromResources()
        {
            try
            {
                return Resources.Load<AsparagusConfig>(ResourceName);
            }
            catch
            {
                return null;
            }
        }
    }
}
#endif
