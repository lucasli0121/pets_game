using System.IO;
using UnityEditor;
using UnityEngine;
using WeChatWASM;

namespace PetV1.WeChat
{
    /// <summary>
    /// 微信小游戏导出配置。
    ///
    /// 配置类定义在预编译的 wx-editor.dll 里（源码不可见），字段名由
    /// <see cref="PetWeChatDump"/> 反射实际对象得到，不是猜的。
    ///
    /// 关键决策：
    /// - assetLoadType = 1（小游戏包内）。0 是 CDN，我们没有 CDN 服务；
    ///   包内加载的上限是 20MB，当前首包 12.81MB + wasm 6.13MB ≈ 19MB，贴着上限。
    /// - Webgl2 = true。微信 Android 端自 8.0.24 起支持 WebGL2，iOS 高性能模式需 15.0+；
    ///   URP 的 SRP Batcher / GPU Instancing / 后处理都依赖它，关掉等于自废渲染特性。
    /// </summary>
    public static class PetWeChatExport
    {
        private const string AppId = "wx9efb6a004410d487";
        private const string OutDirName = "Builds/WeChat";
        private const string ProjectName = "宠屋";

        [MenuItem("Pet V1/11. Configure WeChat Export")]
        public static void Configure()
        {
            var conf = WXConvertCore.config;

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string dst = Path.Combine(projectRoot, OutDirName).Replace('\\', '/');

            conf.ProjectConf.Appid = AppId;
            conf.ProjectConf.assetLoadType = 1;

            // 首包资源 Brotli 压缩。实测未压缩 21.72MB，超过包内加载 20MB 上限；
            // 压缩后约 12.8MB。官方说明的代价是首次启动多约 200ms，划算。
            conf.ProjectConf.compressDataPackage = true;
            conf.ProjectConf.DST = dst;
            conf.ProjectConf.relativeDST = OutDirName;
            conf.ProjectConf.Orientation = WXScreenOritation.Portrait;
            conf.ProjectConf.MemorySize = 256;
            conf.ProjectConf.projectName = ProjectName;

            conf.CompileOptions.Webgl2 = true;
            conf.CompileOptions.DevelopBuild = false;
            conf.CompileOptions.brotliMT = true;

            // 必须全量重建。Unity 的 Bee 增量构建不把 emscriptenArgs 变化算进缓存键，
            // 改 EXPORTED_RUNTIME_METHODS 后只会重新生成 data，framework.js / wasm 直接复用旧产物，
            // 表现为「参数改了但报错不变」。踩过两次。
            conf.CompileOptions.CleanBuild = true;

            EditorUtility.SetDirty(conf);
            AssetDatabase.SaveAssets();

            Debug.Log("[PetV1] 微信导出配置已写入。\n" +
                      "  AppId        = " + conf.ProjectConf.Appid + "\n" +
                      "  首包加载      = " + (conf.ProjectConf.assetLoadType == 1 ? "小游戏包内" : "CDN") + "\n" +
                      "  导出目录      = " + conf.ProjectConf.DST + "\n" +
                      "  方向          = " + conf.ProjectConf.Orientation + "\n" +
                      "  WebGL2        = " + conf.CompileOptions.Webgl2);
        }

        /// <summary>
        /// 执行导出。会先 Build WebGL 再转换，整体十分钟以上，期间编辑器是卡住的。
        /// </summary>
        [MenuItem("Pet V1/12. Export WeChat MiniGame")]
        public static void Export()
        {
            Configure();
            Debug.Log("[PetV1] 开始导出微信小游戏，预计十分钟以上...");
            var err = WXConvertCore.DoExport(true);
            Debug.Log("[PetV1] 导出结束，结果：" + err);
        }
    }
}
