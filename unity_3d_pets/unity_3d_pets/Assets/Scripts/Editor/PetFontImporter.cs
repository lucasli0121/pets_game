using System.IO;
using UnityEditor;
using UnityEngine;

namespace PetV1.EditorTools
{
    /// <summary>
    /// 中文字体导入配置。
    ///
    /// 为什么必须有这一步：WebGL 无法访问系统字体，
    /// <c>Font.CreateDynamicFontFromOSFont</c> 在 WebGL 下不可用（构建期直接报
    /// "Need to include font data on WebGL"）。所以中文字形必须预先烘成图集打进包。
    ///
    /// 关键设置是 <c>fontTextureCase</c>：
    /// - Dynamic（Unity 默认值）→ 运行时向 OS 要字形，WebGL 下必然失败
    /// - Unicode → 把字体文件里的所有字形烘成图集，体积由子集大小决定
    ///
    /// 字体文件已用 fonttools 做过子集化（只保留项目实际用字），
    /// 所以 Unicode 不会像完整思源黑体那样炸出几十 MB 图集。
    /// </summary>
    public static class PetFontImporter
    {
        private const string FontDir = "Assets/Resources/PetFont";

        /// <summary>烘图集用的字号。UI 各处的实际字号由文本组件缩放，不影响清晰度上限。</summary>
        private const int AtlasFontSize = 32;

        [MenuItem("Pet V1/9. Import Font")]
        public static void Import()
        {
            if (!Directory.Exists(FontDir))
            {
                Debug.LogError("[PetV1] 字体目录不存在：" + FontDir);
                return;
            }

            string[] files = Directory.GetFiles(FontDir, "*.*", SearchOption.TopDirectoryOnly);
            int configured = 0;

            for (int i = 0; i < files.Length; i++)
            {
                string ext = Path.GetExtension(files[i]).ToLowerInvariant();
                if (ext != ".ttf" && ext != ".otf") continue;

                string assetPath = files[i].Replace('\\', '/');

                var importer = AssetImporter.GetAtPath(assetPath) as TrueTypeFontImporter;
                if (importer == null)
                {
                    Debug.LogWarning("[PetV1] 不是字体资源，跳过：" + assetPath);
                    continue;
                }

                importer.fontTextureCase = FontTextureCase.Unicode;
                importer.includeFontData = false;
                importer.fontSize = AtlasFontSize;
                importer.fontRenderingMode = FontRenderingMode.Smooth;
                importer.characterPadding = 1;
                importer.SaveAndReimport();

                configured++;
                Debug.Log("[PetV1] 已配置字体：" + assetPath);
            }

            AssetDatabase.Refresh();

            if (configured == 0)
                Debug.LogWarning("[PetV1] 没找到可配置的字体文件，检查 " + FontDir);
            else
                Debug.Log("[PetV1] 字体导入完成，共 " + configured + " 个");
        }
    }
}
