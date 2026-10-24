using System.IO;
using UnityEditor;
using UnityEngine;

namespace PetV1.EditorTools
{
    /// <summary>
    /// 统一刷图标 PNG 的导入设置。扫两个目录：
    /// <list type="bullet">
    /// <item><c>Assets/Resources/PetIcons/</c> —— UI 图标（纯白 RGB + alpha，由 Image.color 染色）</item>
    /// <item><c>Assets/Resources/PetFoodArt/</c> —— 食品 / 金币插画（彩色，保留原色不染色）</item>
    /// </list>
    ///
    /// 为什么要专门一个刷子：PNG 丢进工程时 Unity 默认按 <c>Texture</c> 导入，
    /// <c>Resources.Load&lt;Sprite&gt;</c> 会返回 null —— 而且不报错，
    /// 症状是「图标位置有一块洋红方块」。手工一张张改容易漏，
    /// 换图 / 加图之后又得重来一遍，所以固化成菜单。
    ///
    /// 两类资产的**导入设置完全相同**（都是 UI 贴图），差别只在运行期怎么用：
    /// 见 <see cref="PetIcons"/> 与 <see cref="PetFoodArt"/> 的文档。
    ///
    /// 设置项与理由：
    /// - Texture Type = Sprite：Resources 加载和 uGUI 显示的前提
    /// - Sprite Import Mode = Single：一张图一个 Sprite，按文件名加载
    /// - Pixels Per Unit = 100：与 UI 层的 1 单位 = 1 像素 对齐，
    ///   <c>Image</c> 用 sizeDelta 直接给尺寸，不需要再按 PPU 换算
    /// - Alpha Is Transparency = true：边缘按 alpha 扩散，
    ///   避免双线性采样在透明区吸到黑边（白图标染主蓝时最明显）
    /// - Mip Maps = 关闭 + Compression = None：UI 图标尺寸固定，
    ///   mipmap 只会占内存并让小尺寸发糊
    /// - Wrap Mode = Clamp：图标不需要平铺
    /// </summary>
    public static class PetIconImporter
    {
        /// <summary>UI 图标目录（纯白，供染色）。</summary>
        public const string IconDir = "Assets/Resources/PetIcons";

        /// <summary>食品 / 金币插画目录（彩色，保留原色）。</summary>
        public const string FoodArtDir = "Assets/Resources/PetFoodArt";

        /// <summary>两个目录都要刷。新加插画目录时往这里加一行。</summary>
        public static readonly string[] Dirs = { IconDir, FoodArtDir };

        [MenuItem("Pet V1/6. Import Icons")]
        public static void ImportAll()
        {
            int touched = 0;
            int total = 0;

            foreach (var dir in Dirs)
            {
                if (!Directory.Exists(dir))
                {
                    Debug.LogWarning("[PetIconImporter] 目录不存在：" + dir);
                    continue;
                }

                var files = Directory.GetFiles(dir, "*.png", SearchOption.TopDirectoryOnly);
                total += files.Length;
                foreach (var f in files)
                {
                    var path = f.Replace('\\', '/');
                    var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (imp == null) continue;

                    // 已经是对的就不重写，重写会改 hash 让版本控制一片diff
                    bool need = imp.textureType != TextureImporterType.Sprite
                                || imp.spriteImportMode != SpriteImportMode.Single
                                || imp.mipmapEnabled
                                || imp.alphaIsTransparency == false
                                || imp.wrapMode != TextureWrapMode.Clamp
                                || imp.textureCompression != TextureImporterCompression.Uncompressed
                                || imp.spritePixelsPerUnit != 100f;
                    if (!need) continue;

                    imp.textureType = TextureImporterType.Sprite;
                    imp.spriteImportMode = SpriteImportMode.Single;
                    imp.spritePixelsPerUnit = 100f;
                    imp.alphaIsTransparency = true;
                    imp.mipmapEnabled = false;
                    imp.wrapMode = TextureWrapMode.Clamp;
                    imp.filterMode = FilterMode.Bilinear;
                    imp.textureCompression = TextureImporterCompression.Uncompressed;
                    imp.SaveAndReimport();
                    touched++;
                }
            }

            AssetDatabase.Refresh();
            Debug.Log("[PetIconImporter] 扫描 " + total + " 张（PetIcons + PetFoodArt），更新 " + touched + " 张");
        }
    }
}
