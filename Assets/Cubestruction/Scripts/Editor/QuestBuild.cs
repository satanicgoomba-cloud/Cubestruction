#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace Cubestruction.EditorTools
{
    /// <summary>
    /// Scripts the README's manual VR setup (XR Plug-in Management + OpenXR features) and builds
    /// the Android APKs. Menu: Cubestruction ▸ Configure VR / Build Quest APK / Build Android Mobile APK,
    /// or from the command line (both APKs):
    ///   Unity -batchmode -buildTarget Android -projectPath . -executeMethod Cubestruction.EditorTools.QuestBuild.BuildAll
    /// Output: Builds/Quest/Cubestruction.apk and Builds/Mobile/Cubestruction-mobile.apk
    /// </summary>
    public static class QuestBuild
    {
        const string OpenXRLoader = "UnityEngine.XR.OpenXR.OpenXRLoader";
        const string QuestApk = "Builds/Quest/Cubestruction.apk";
        const string MobileApk = "Builds/Mobile/Cubestruction-mobile.apk";

        // Feature type names, matched by name so this file needs no reference to each feature's assembly.
        static readonly string[] AndroidFeatures = { "MetaQuestFeature", "OculusTouchControllerProfile", "MetaQuestTouchPlusControllerProfile" };
        static readonly string[] DesktopFeatures = { "OculusTouchControllerProfile", "ValveIndexControllerProfile" };

        [MenuItem("Cubestruction/Configure VR (OpenXR, Quest + PC VR)", priority = 20)]
        public static void ConfigureVR()
        {
            EnableOpenXR(BuildTargetGroup.Android, AndroidFeatures);
            EnableOpenXR(BuildTargetGroup.Standalone, DesktopFeatures);
            OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android).renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
            OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone).renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
            AssetDatabase.SaveAssets();
            Debug.Log("[QuestBuild] VR configured: OpenXR on Android (Meta Quest) and Standalone (PC VR), single-pass instanced, ASTC.");
        }

        static void EnableOpenXR(BuildTargetGroup group, string[] featureNames)
        {
            var general = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
            if (general == null)
            {
                // First time for this platform: create the per-target settings the XR Management UI would.
                EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey, out XRGeneralSettingsPerBuildTarget perTarget);
                if (perTarget == null) throw new System.Exception("XR Plug-in Management settings asset missing.");
                general = ScriptableObject.CreateInstance<XRGeneralSettings>();
                general.name = group + " Settings";
                var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
                manager.name = group + " Providers";
                general.Manager = manager;
                AssetDatabase.AddObjectToAsset(general, perTarget);
                AssetDatabase.AddObjectToAsset(manager, perTarget);
                perTarget.SetSettingsForBuildTarget(group, general);
                EditorUtility.SetDirty(perTarget);
            }
            general.InitManagerOnStart = true;
            if (!general.Manager.activeLoaders.Any(l => l != null && l.GetType().FullName == OpenXRLoader))
                XRPackageMetadataStore.AssignLoader(general.Manager, OpenXRLoader, group);

            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            foreach (var f in settings.GetFeatures())
                if (featureNames.Contains(f.GetType().Name)) { f.enabled = true; EditorUtility.SetDirty(f); }
            var missing = featureNames.Where(n => !settings.GetFeatures().Any(f => f.GetType().Name == n && f.enabled)).ToArray();
            if (missing.Length > 0) Debug.LogWarning($"[QuestBuild] {group}: OpenXR features not found: {string.Join(", ", missing)}");
            EditorUtility.SetDirty(settings);
        }

        [MenuItem("Cubestruction/Build Quest APK", priority = 21)]
        public static void Build() => BuildApk(vr: true, QuestApk);

        /// <summary>Android phones/tablets: the same game without the OpenXR loader, so it starts in
        /// flat mode with touch controls. Restores the Quest VR settings afterwards.</summary>
        [MenuItem("Cubestruction/Build Android Mobile APK", priority = 22)]
        public static void BuildMobile() => BuildApk(vr: false, MobileApk);

        /// <summary>Both APKs in one run (command line: -executeMethod ...QuestBuild.BuildAll).</summary>
        public static void BuildAll()
        {
            bool ok = BuildApk(vr: true, QuestApk, exit: false) & BuildApk(vr: false, MobileApk, exit: false);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        static bool BuildApk(bool vr, string path, bool exit = true)
        {
            ConfigureVR();
            var android = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
            if (!vr)
            {
                XRPackageMetadataStore.RemoveLoader(android.Manager, OpenXRLoader, BuildTargetGroup.Android);
                android.InitManagerOnStart = false;
                AssetDatabase.SaveAssets();
            }
            BuildReport report;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var scenes = EditorBuildSettings.scenes.Where(sc => sc.enabled).Select(sc => sc.path).ToArray();
                report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = path,
                    target = BuildTarget.Android,
                    targetGroup = BuildTargetGroup.Android,
                    options = BuildOptions.None,
                });
            }
            finally
            {
                if (!vr) ConfigureVR();   // put the Quest setup back
            }
            var s = report.summary;
            Debug.Log($"[QuestBuild] {(vr ? "Quest" : "Mobile")} {s.result}: {path}, {s.totalSize / (1024 * 1024)} MB, {s.totalErrors} errors, {s.totalTime.TotalSeconds:0}s");
            bool ok = s.result == BuildResult.Succeeded;
            if (exit && Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
            return ok;
        }
    }
}
#endif
