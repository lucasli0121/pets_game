using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace PetV1.EditorTools
{
    /// <summary>
    /// 微信小游戏打包预检。
    ///
    /// 为什么要有这个脚本：Build Settings 里挂的是 SampleScene 而非 PetHome，
    /// 直接构建会打出空场景黑屏，第一反应容易误判成「转换失败」，白花排查时间。
    /// 这里把能确定的项一次改好，不确定的（Graphics API）只报告不动手。
    /// </summary>
    public static class PetWeChatBuildPreflight
    {
        private const string HomeScenePath = "Assets/Scenes/PetPrototype/PetHome.unity";

        [MenuItem("Pet V1/8. WeChat Build Preflight")]
        public static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Pet V1] 微信小游戏打包预检");
            sb.AppendLine();

            FixBuildScenes(sb);
            ConfigurePlayerSettings(sb);
            ReportGraphicsApi(sb);
            ReportSummary(sb);

            Debug.Log(sb.ToString());
        }

        private static void Try(string label, System.Action action, StringBuilder sb)
        {
            try
            {
                action();
                sb.AppendLine("  [OK]   " + label);
            }
            catch (System.Exception e)
            {
                sb.AppendLine("  [FAIL] " + label + " -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void FixBuildScenes(StringBuilder sb)
        {
            sb.AppendLine("1. 构建场景");

            var scene = AssetDatabase.LoadAssetAtPath<SceneAsset>(HomeScenePath);
            if (scene == null)
            {
                sb.AppendLine("  [FAIL] 找不到 " + HomeScenePath);
                return;
            }

            var before = EditorBuildSettings.scenes;
            var beforeNames = new StringBuilder();
            for (int i = 0; i < before.Length; i++)
            {
                if (i > 0) beforeNames.Append(", ");
                beforeNames.Append(before[i].path);
            }
            sb.AppendLine("  原列表：" + (before.Length == 0 ? "(空)" : beforeNames.ToString()));

            bool correct = before.Length == 1 && before[0].path == HomeScenePath;
            if (correct)
            {
                sb.AppendLine("  [OK]   构建列表已是 PetHome 单场景");
            }
            else
            {
                EditorBuildSettings.scenes = new[]
                {
                    new EditorBuildSettingsScene(HomeScenePath, true),
                };
                sb.AppendLine("  [OK]   已重置为单场景 " + HomeScenePath);
            }
            sb.AppendLine();
        }

        private static void ConfigurePlayerSettings(StringBuilder sb)
        {
            sb.AppendLine("2. Player Settings");

            Try("Scripting Backend = IL2CPP", delegate
            {
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.WebGL, ScriptingImplementation.IL2CPP);
            }, sb);

            Try("Compression Format = Brotli", delegate
            {
                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
            }, sb);

            // 工程里有 14 处 catch 承担容错降级（缓存读取失败、网络失败回退）。
            // None 模式下 try/catch 不生效，这些路径会静默失效，必须用 ExplicitlyThrown。
            Try("Enable Exceptions = Explicitly Thrown", delegate
            {
                PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            }, sb);

            Try("Managed Stripping = Medium", delegate
            {
                PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, ManagedStrippingLevel.Medium);
            }, sb);

            Try("Strip Engine Code = on", delegate
            {
                PlayerSettings.stripEngineCode = true;
            }, sb);

            Try("Data Caching = on", delegate
            {
                PlayerSettings.WebGL.dataCaching = true;
            }, sb);

            Try("Development Build = off", delegate
            {
                EditorUserBuildSettings.development = false;
            }, sb);

            sb.AppendLine();
        }

        private static void ReportGraphicsApi(StringBuilder sb)
        {
            sb.AppendLine("3. Graphics API（只报告，不改）");

            try
            {
                bool auto = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.WebGL);
                var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.WebGL);

                var joined = new StringBuilder();
                for (int i = 0; i < apis.Length; i++)
                {
                    if (i > 0) joined.Append(", ");
                    joined.Append(apis[i].ToString());
                }

                sb.AppendLine("  Auto Graphics API = " + (auto ? "on" : "off"));
                sb.AppendLine("  当前列表：" + (apis.Length == 0 ? "(空)" : joined.ToString()));
                sb.AppendLine("  OpenGLES3 = WebGL2，OpenGLES2 = WebGL1。");
                sb.AppendLine("  微信 Android 端自 8.0.24 起正式支持 WebGL2，iOS 高性能模式需 15.0 以上。");
                sb.AppendLine("  URP 的 SRP Batcher / GPU Instancing / 后处理依赖 WebGL2，保留 OpenGLES3 即可。");
            }
            catch (System.Exception e)
            {
                sb.AppendLine("  [FAIL] 读取失败 -> " + e.GetType().Name + ": " + e.Message);
            }

            sb.AppendLine();

            sb.AppendLine("4. 渲染与兼容性（只报告，不改）");

            try
            {
                sb.AppendLine("  Color Space = " + PlayerSettings.colorSpace);
                sb.AppendLine("    URP 需要 Linear，Linear + WebGL2 是微信官方支持组合。");
            }
            catch (System.Exception e)
            {
                sb.AppendLine("  [FAIL] Color Space -> " + e.Message);
            }

            try
            {
                sb.AppendLine("  Strip Engine Code = " + PlayerSettings.stripEngineCode);
            }
            catch (System.Exception e)
            {
                sb.AppendLine("  [FAIL] Strip Engine Code -> " + e.Message);
            }

            try
            {
                sb.AppendLine("  Api Compatibility = " + PlayerSettings.GetApiCompatibilityLevel(NamedBuildTarget.WebGL));
            }
            catch (System.Exception e)
            {
                sb.AppendLine("  [FAIL] Api Compatibility -> " + e.Message);
            }
        }

        private static void ReportSummary(StringBuilder sb)
        {
            sb.AppendLine("5. 上机前的两项人工确认");
            sb.AppendLine("  [ ] 微信公众平台已开通「能力地图 -> 生产提效包 -> 快适配」");
            sb.AppendLine("      不开则转换插件报「插件未授权」直接失败");
            sb.AppendLine("  [ ] Build Settings 的目标平台切到 WebGL");
            sb.AppendLine();
            sb.AppendLine("AppID：wx9efb6a004410d487（转换面板里填，测试号不行）");
            sb.AppendLine("详细步骤见 docs/微信小游戏试水打包清单.md");
        }
    }
}
