#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace VAS.Editor
{
    public static class VASWindowsBuild
    {
        private const string ProductName = "Virtual Avatar Studio";
        private const string ApplicationIdentifier = "com.portfolio.virtualavatarstudio";
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";
        private const string OutputDirectoryRelativePath = "Builds/Windows-x64";
        private const string OutputRelativePath = "Builds/Windows-x64/Virtual_Avatar_Studio.exe";
        private const string GraphicsSettingsPath = "ProjectSettings/GraphicsSettings.asset";
        private const string RuntimeMToonShaderName = "VRM10/Universal Render Pipeline/MToon10";
        private const string RuntimeMToonShaderGuid = "0781c64b7f2a1604ea6aa7e252080372";
        private const string RuntimeUniUnlitShaderName = "UniGLTF/UniUnlit";
        private const string RuntimeUniUnlitShaderGuid = "8c17b56f4bf084c47872edcb95237e4a";
        private const string RuntimeUrpLitShaderName = "Universal Render Pipeline/Lit";
        private const string RuntimeUrpLitShaderGuid = "933532a4fcc9baf4fa0491de14d08ed7";
        private const int OutputWidth = 1280;
        private const int OutputHeight = 720;

        private static readonly string[] RequiredFiles =
        {
            ScenePath,
            "Assets/Models/blaze_face_short_range.onnx",
            "Assets/Models/hand_detector.onnx",
            "Assets/Models/hand_landmarks_detector.onnx",
            "Assets/Models/pose_detection.onnx",
            "Assets/Models/pose_landmarks_detector_full.onnx",
            "Assets/Resources/Data/face_anchors.csv",
            "Assets/Resources/Data/hand_anchors.csv",
            "Assets/Resources/Data/pose_anchors.csv",
            "Assets/VAS/Resources/Fonts/NotoSansKR-Regular.otf"
        };

        private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static string OutputDirectory => Path.Combine(
            ProjectRoot,
            OutputDirectoryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        private static string OutputPath => Path.Combine(ProjectRoot, OutputRelativePath.Replace('/', Path.DirectorySeparatorChar));

        [MenuItem("VAS/빌드/Windows x64 방송 빌드")]
        public static void BuildWindows64()
        {
            EnsureWindowsTarget();
            ApplyBroadcastSettings();
            ValidatePreflight();

            CleanOutputDirectory();
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath) ?? ProjectRoot);

            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = OutputPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.StrictMode | BuildOptions.DetailedBuildReport
            });

            BuildSummary summary = report.summary;
            string message =
                $"[VAS Build] Result={summary.result}, " +
                $"Size={summary.totalSize:N0} bytes, " +
                $"Time={summary.totalTime.TotalSeconds:F1}s, " +
                $"Warnings={summary.totalWarnings}, Errors={summary.totalErrors}, " +
                $"Output={OutputPath}";

            if (summary.result != BuildResult.Succeeded)
            {
                throw new BuildFailedException(message);
            }

            string[] bundledVrmFiles = FindVrmFiles(OutputDirectory);
            if (bundledVrmFiles.Length > 0)
            {
                CleanOutputDirectory();
                throw new BuildFailedException(
                    "[VAS Build] 배포 결과에 VRM 파일이 포함되어 빌드를 제거했습니다:\n- " +
                    string.Join("\n- ", bundledVrmFiles));
            }

            Debug.Log(message + ", BundledVrmFiles=0");
        }

        [MenuItem("VAS/빌드/방송 설정 적용")]
        public static void ApplyBroadcastSettings()
        {
            PlayerSettings.companyName = "Portfolio";
            PlayerSettings.productName = ProductName;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, ApplicationIdentifier);
            PlayerSettings.defaultScreenWidth = OutputWidth;
            PlayerSettings.defaultScreenHeight = OutputHeight;
            PlayerSettings.defaultIsNativeResolution = false;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = false;
            PlayerSettings.allowFullscreenSwitch = false;
            PlayerSettings.runInBackground = true;
            PlayerSettings.forceSingleInstance = true;
            PlayerSettings.usePlayerLog = true;

            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(
                BuildTarget.StandaloneWindows64,
                new[] { GraphicsDeviceType.Direct3D11 });

            AssetDatabase.SaveAssets();
            Debug.Log("[VAS Build] Broadcast settings applied: Windows x64, 1280x720 windowed, Direct3D 11, single instance.");
        }

        [MenuItem("VAS/빌드/사전검사")]
        public static void ValidatePreflight()
        {
            var errors = new List<string>();

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                errors.Add("Unity is compiling or importing assets.");
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                errors.Add("Exit Play Mode before building.");
            }

            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64))
            {
                errors.Add("Windows x64 Build Support is not installed.");
            }

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            {
                errors.Add("The active build target is not Windows x64.");
            }

            string[] enabledScenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();

            if (enabledScenes.Length != 1 || enabledScenes[0] != ScenePath)
            {
                errors.Add($"Build Settings must contain only the enabled scene '{ScenePath}'.");
            }

            foreach (string relativePath in RequiredFiles)
            {
                string absolutePath = Path.Combine(ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var file = new FileInfo(absolutePath);
                if (!file.Exists || file.Length == 0)
                {
                    errors.Add($"Required runtime file is missing or empty: {relativePath}");
                }
            }

            string[] projectVrmFiles = FindVrmFiles(Application.dataPath);
            if (projectVrmFiles.Length > 0)
            {
                errors.Add(
                    "Assets 폴더에 배포 금지 VRM 파일이 있습니다. LocalOnly/Avatars로 이동하세요: " +
                    string.Join(", ", projectVrmFiles));
            }

            if (GraphicsSettings.defaultRenderPipeline == null)
            {
                errors.Add("A Universal Render Pipeline asset is not assigned in Graphics Settings.");
            }

            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("Assets/Settings/VASUniversalRenderer.asset") == null)
            {
                errors.Add("VASUniversalRenderer is missing.");
            }

            ValidateRuntimeShader(errors, RuntimeMToonShaderName, RuntimeMToonShaderGuid);
            ValidateRuntimeShader(errors, RuntimeUniUnlitShaderName, RuntimeUniUnlitShaderGuid);
            ValidateRuntimeShader(errors, RuntimeUrpLitShaderName, RuntimeUrpLitShaderGuid);

            PluginImporter[] spoutPlugins = PluginImporter.GetAllImporters()
                .Where(importer => string.Equals(
                    Path.GetFileName(importer.assetPath),
                    "KlakSpout.dll",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (spoutPlugins.Length == 0 ||
                !spoutPlugins.Any(importer => importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64)))
            {
                errors.Add("KlakSpout Win64 native plugin is unavailable.");
            }

            if (PlayerSettings.productName != ProductName)
            {
                errors.Add($"Product Name must be '{ProductName}'.");
            }

            if (PlayerSettings.defaultScreenWidth != OutputWidth ||
                PlayerSettings.defaultScreenHeight != OutputHeight ||
                PlayerSettings.defaultIsNativeResolution ||
                PlayerSettings.fullScreenMode != FullScreenMode.Windowed ||
                PlayerSettings.resizableWindow ||
                PlayerSettings.allowFullscreenSwitch)
            {
                errors.Add("Player window settings are not fixed to 1280x720 non-resizable Windowed mode.");
            }

            if (!PlayerSettings.runInBackground || !PlayerSettings.forceSingleInstance)
            {
                errors.Add("Run In Background and Force Single Instance must be enabled.");
            }

            GraphicsDeviceType[] graphicsApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
            if (PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64) ||
                graphicsApis.Length != 1 ||
                graphicsApis[0] != GraphicsDeviceType.Direct3D11)
            {
                errors.Add("Windows Graphics API must be fixed to Direct3D 11 for the verified Spout path.");
            }

            if (errors.Count > 0)
            {
                throw new BuildFailedException("[VAS Build] Preflight failed:\n- " + string.Join("\n- ", errors));
            }

            Debug.Log($"[VAS Build] Preflight passed. Scene={ScenePath}, Output={OutputRelativePath}");
        }

        private static string[] FindVrmFiles(string rootDirectory)
        {
            if (!Directory.Exists(rootDirectory)) return Array.Empty<string>();

            return Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories)
                .Where(path => string.Equals(Path.GetExtension(path), ".vrm", StringComparison.OrdinalIgnoreCase))
                .Select(path => Path.GetRelativePath(ProjectRoot, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void CleanOutputDirectory()
        {
            string projectRoot = Path.GetFullPath(ProjectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string outputDirectory = Path.GetFullPath(OutputDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string expectedParent = Path.GetFullPath(Path.Combine(projectRoot, "Builds"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(outputDirectory, projectRoot, StringComparison.OrdinalIgnoreCase) ||
                !outputDirectory.StartsWith(
                    expectedParent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new BuildFailedException(
                    $"[VAS Build] 안전하지 않은 빌드 정리 경로를 거부했습니다: {outputDirectory}");
            }

            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, true);
        }

        private static void ValidateRuntimeShader(
            ICollection<string> errors,
            string shaderName,
            string shaderGuid)
        {
            string shaderPath = AssetDatabase.GUIDToAssetPath(shaderGuid);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            if (shader == null || !string.Equals(shader.name, shaderName, StringComparison.Ordinal))
            {
                errors.Add($"Runtime VRM shader is unavailable: {shaderName}");
                return;
            }

            string graphicsSettingsAbsolutePath = Path.Combine(
                ProjectRoot,
                GraphicsSettingsPath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(graphicsSettingsAbsolutePath) ||
                File.ReadAllText(graphicsSettingsAbsolutePath)
                    .IndexOf($"guid: {shaderGuid}", StringComparison.OrdinalIgnoreCase) < 0)
            {
                errors.Add($"Always Included Shaders must contain '{shaderName}'.");
            }
        }

        private static void EnsureWindowsTarget()
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneWindows64)
            {
                return;
            }

            if (!EditorUserBuildSettings.SwitchActiveBuildTarget(
                    BuildTargetGroup.Standalone,
                    BuildTarget.StandaloneWindows64))
            {
                throw new BuildFailedException("[VAS Build] Failed to switch the active target to Windows x64.");
            }
        }
    }
}
#endif
