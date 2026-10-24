using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace PetV1.UI
{
    /// <summary>
    /// 食品 / 金币插画。按 key 从 Resources 加载美术资源，不在运行期生成图形。
    ///
    /// 资源位置：<c>Assets/Resources/PetFoodArt/&lt;名字&gt;.png</c>，
    /// 导入设置由 <see cref="PetV1.EditorTools.PetIconImporter"/> 统一刷成
    /// Sprite / 无压缩 / 关闭 mipmap。
    ///
    /// **与 <see cref="PetIcons"/> 是两类资产，别混用**：
    /// <list type="bullet">
    /// <item>PetIcons 是**纯白 RGB + alpha 覆盖度**的图标，颜色由 <c>Image.color</c> 染出来；
    /// 彩图染色会变脏，所以图标的颜色不写在资源里。</item>
    /// <item>PetFoodArt 是**彩色插画**（牛肉罐头、蛋糕、金币），颜色必须**保留原色** ——
    /// <see cref="NewArt"/> 建出来的 Image 恒为白色（乘 1），调用方不要再去改它的 color，
    /// 改了就是「给插画蒙一层色」，看着发灰发脏。</item>
    /// </list>
    ///
    /// 资源缺失时打一条 Error 日志（只打一次）并回落成 <paramref name="fallbackColor"/>
    /// 纯色块 —— 布局不塌、一眼能看出是哪个位置缺图。
    /// 不返回洋红方块：那是 PetIcons 的约定（纯装饰图标缺了要刺眼），
    /// 食品插画缺图更像「内容没配」，色块 + Error 日志更贴场景。
    /// </summary>
    public static class PetFoodArt
    {
        /// <summary>Resources 下的目录名。</summary>
        public const string ResourceDir = "PetFoodArt/";

        private static readonly Dictionary<string, Sprite> s_cache =
            new Dictionary<string, Sprite>();

        private static readonly List<string> s_missingLogged = new List<string>();

        /// <summary>
        /// 后台 icon key → 本地资源名。后台 seed 下发的 key 形如 <c>icon_food_beef</c>，
        /// 本地兜底资源叫 <c>food_beef</c>，去掉 <c>icon_</c> 前缀即可对上。
        /// 不带前缀的 key（如 <c>coin</c>）原样返回。
        /// </summary>
        public static string ResolveName(string iconKey)
        {
            if (string.IsNullOrEmpty(iconKey)) return "";
            const string prefix = "icon_";
            return iconKey.StartsWith(prefix) ? iconKey.Substring(prefix.Length) : iconKey;
        }

        /// <summary>
        /// 取插画 Sprite。按名字缓存，重复调用不重复加载。
        /// 资源缺失时打一条 Error 日志（只打一次）并返回 null。
        /// </summary>
        public static Sprite Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (s_cache.TryGetValue(name, out var cached)) return cached;

            var sprite = Resources.Load<Sprite>(ResourceDir + name);
            if (sprite == null)
            {
                if (!s_missingLogged.Contains(name))
                {
                    s_missingLogged.Add(name);
                    Debug.LogError("[PetFoodArt] 找不到插画资源 Resources/" + ResourceDir
                                   + name + ".png（贴图的 import type 必须是 Sprite，"
                                   + "跑菜单 Pet V1/6. Import Icons 刷一遍）");
                }
            }

            s_cache[name] = sprite;
            return sprite;
        }

        /// <summary>清缓存。切换场景或热重载资源后调用，避免拿到旧引用。</summary>
        public static void ClearCache()
        {
            s_cache.Clear();
            s_missingLogged.Clear();
        }

        // ---------------------------------------------------------------
        // 控件工厂
        // ---------------------------------------------------------------

        /// <summary>
        /// 把插画贴到一个已有的 Image 上。**保留原色**：取到图时 sprite 用原图、
        /// color 恒为白（乘 1，不染色）；缺图时用 1×1 白图 + <paramref name="fallbackColor"/>，
        /// 视觉上退化成占位色块。
        ///
        /// 弹窗里的图标只建一次、Show 时按不同食物换内容，走这个方法
        /// （重建节点会让弹窗每次打开都多几个子物体）。
        /// </summary>
        public static void Apply(Image img, string iconKey, Color fallbackColor)
        {
            if (img == null) return;
            var sprite = Get(ResolveName(iconKey));
            img.sprite = sprite != null ? sprite : PetUIFactory.WhiteSprite;
            img.color = sprite != null ? Color.white : fallbackColor;
        }

        /// <summary>
        /// 建一个显示插画的 Image。**保留原色**：取到图时 color 恒为白，
        /// 缺图时用 <paramref name="fallbackColor"/> 的纯色块顶上。
        ///
        /// 尺寸由调用方给，preserveAspect = true（插画是 256×256 正方形，
        /// 给非正方形尺寸时按短边等比缩，不会拉扁）。
        /// 不吃射线（纯装饰），要点击就套进按钮里，或者用
        /// <see cref="PetUIFactory.NewHitArea"/>。
        /// </summary>
        public static Image NewArt(string iconKey, Transform parent, Vector2 size,
                                   Color fallbackColor)
        {
            var go = new GameObject("Art_" + ResolveName(iconKey), typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            var img = go.AddComponent<Image>();
            Apply(img, iconKey, fallbackColor);
            img.raycastTarget = false;
            img.preserveAspect = true;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = size;
            return img;
        }
    }
}
