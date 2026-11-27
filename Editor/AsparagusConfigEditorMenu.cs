#if UNITY_EDITOR
using Asparagus.Sdk;
using UnityEditor;
using UnityEngine;

namespace Asparagus.Sdk.Editor
{
    /// <summary>
    /// Unity Editor menu helper that creates (if missing) and focuses
    /// <c>Assets/Resources/AsparagusConfig.asset</c>.
    /// </summary>
    public static class AsparagusConfigEditorMenu
    {
        private const string ResourcesFolderPath = "Assets/Resources";
        private const string ConfigAssetPath = "Assets/Resources/AsparagusConfig.asset";

        [MenuItem("Tools/Asparagus SDK/Edit Configuration")]
        public static void EditConfiguration()
        {
            try
            {
                AsparagusConfig config = AssetDatabase.LoadAssetAtPath<AsparagusConfig>(ConfigAssetPath);
                if (config == null)
                {
                    if (!AssetDatabase.IsValidFolder(ResourcesFolderPath))
                    {
                        AssetDatabase.CreateFolder("Assets", "Resources");
                    }

                    config = ScriptableObject.CreateInstance<AsparagusConfig>();
                    AssetDatabase.CreateAsset(config, ConfigAssetPath);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                }

                EditorUtility.FocusProjectWindow();
                Selection.activeObject = config;
                EditorGUIUtility.PingObject(config);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[Asparagus SDK] Failed to open AsparagusConfig asset: " + ex.Message);
            }
        }
    }
}
#endif
