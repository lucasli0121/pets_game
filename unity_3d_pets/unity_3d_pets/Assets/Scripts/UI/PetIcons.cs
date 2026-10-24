using System.Collections.Generic;
using UnityEngine;

namespace PetV1.UI
{
    /// <summary>
    /// UI 图标。按名字从 Resources 加载美术资源，不在运行期生成图形。
    ///
    /// 资源位置：<c>Assets/Resources/PetIcons/&lt;名字&gt;.png</c>，
    /// 导入设置由 <c>PetIconImporter</c> 统一刷成 Sprite / 无压缩 / 关闭 mipmap。
    ///
    /// **图标必须是纯白 RGB + alpha 覆盖度**：uGUI 的 <c>Image.color</c> 是
    /// 逐通道相乘，白图标才能被染成主蓝 / 红 / 灰；彩图染色会变脏。
    /// 所以图标的颜色不写在资源里，交给 <c>Image.color</c>。
    ///
    /// 未知名字返回一个显眼的洋红方块 —— 拼错名字时要一眼看得出来，
    /// 返回 null 只会得到「图标不显示」，排查成本高得多。
    /// </summary>
    public static class PetIcons
    {
        /// <summary>Resources 下的目录名。</summary>
        public const string ResourceDir = "PetIcons/";

        private static readonly Dictionary<string, Sprite> s_cache =
            new Dictionary<string, Sprite>();

        private static readonly List<string> s_missingLogged = new List<string>();

        /// <summary>
        /// 取图标 Sprite。按名字缓存，重复调用不重复加载。
        /// 资源缺失时打一条 Error 日志（只打一次），并返回洋红方块。
        /// </summary>
        public static Sprite Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return Missing();
            if (s_cache.TryGetValue(name, out var cached) && cached != null) return cached;

            var sprite = Resources.Load<Sprite>(ResourceDir + name);
            if (sprite == null)
            {
                if (!s_missingLogged.Contains(name))
                {
                    s_missingLogged.Add(name);
                    Debug.LogError("[PetIcons] 找不到图标资源 Resources/" + ResourceDir
                                   + name + ".png（贴图的 import type 必须是 Sprite）");
                }
                sprite = Missing();
            }
            else if (!s_missingLogged.Contains(name))
            {
                // 加载成功也记一笔，Missing() 里清掉列表省内存的逻辑就没必要了，
                // 保留判断只是为了避免同一张图反复打日志。
                s_missingLogged.Add(name);
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
        /// 建一个只显示图标的 Image。不吃射线（纯装饰），
        /// 要点击就把它套进按钮里，或者用 <see cref="PetUIFactory.NewHitArea"/>。
        /// </summary>
        public static UnityEngine.UI.Image NewIcon(string name, Transform parent,
                                                   Vector2 size, Color color)
        {
            var go = new GameObject("Icon_" + name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            var img = go.AddComponent<UnityEngine.UI.Image>();
            img.sprite = Get(name);
            img.color = color;
            img.raycastTarget = false;
            img.preserveAspect = true;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = size;
            return img;
        }

        // ---------------------------------------------------------------
        // 缺图占位
        // ---------------------------------------------------------------

        private static Sprite s_missing;

        private static Sprite Missing()
        {
            if (s_missing != null) return s_missing;
            var tex = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            var p = new Color32[256];
            for (int i = 0; i < p.Length; i++) p[i] = new Color32(255, 0, 255, 255);
            tex.SetPixels32(p);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            s_missing = Sprite.Create(tex, new Rect(0, 0, 16, 16), new Vector2(0.5f, 0.5f), 100f);
            s_missing.hideFlags = HideFlags.HideAndDontSave;
            return s_missing;
        }
    }
}