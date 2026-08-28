using UnityEditor;

namespace UniVRM10.EditorTools
{
    [InitializeOnLoad]
    internal static class FastSpringBoneEditorReloadCleanup
    {
        static FastSpringBoneEditorReloadCleanup()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= DisposeSpringBoneResources;
            AssemblyReloadEvents.beforeAssemblyReload += DisposeSpringBoneResources;
            EditorApplication.quitting -= DisposeSpringBoneResources;
            EditorApplication.quitting += DisposeSpringBoneResources;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
                DisposeSpringBoneResources();
        }

        private static void DisposeSpringBoneResources()
        {
            Vrm10Instance.DisposeFastSpringBoneResourcesForEditorReload();
        }
    }
}
