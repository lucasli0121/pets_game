using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace PetV1.UI
{
    /// <summary>
    /// 纯代码构建 uGUI 控件的工厂。
    ///
    /// 为什么不用 UI Toolkit（UXML/USS）：
    /// UXML/USS 要么在编辑器里拖（无法用代码审查 diff），
    /// 要么写成 USS 文件（引入 CSS 方言与 C# 两套样式来源，同一个颜色要改两处）。
    /// 这个原型后面要反复改（换模型、后台改数值、运营改文案），
    /// 纯代码的好处是「跑起来是什么样」在源码里一目了然，改完刷新即见。
    ///
    /// 为什么不用 TextMeshPro：
    /// 项目里没有导入 TMP Essential Resources，用 TMP 会因为缺默认字体资产
    /// 在运行时直接抛异常。内置的 LegacyRuntime.ttf 不含中文字形，
    /// 所以走 <see cref="ResolveFont"/> 的系统字体回退。
    ///
    /// 层级约定（1080×1920 基准，锚点用比例）：
    /// 顶层 Canvas 下按 0/1/2/3 分层：场景 UI / 页面 / 弹窗 / 最上提示。
    /// 数字小的先渲染，数字大的盖在上面。
    /// </summary>
    public static class PetUIFactory
    {
        // 分层序号。集中定义，避免各处写魔数。
        public const int LayerSceneUi = 0;
        public const int LayerPage = 1;
        public const int LayerPopup = 2;
        public const int LayerToast = 3;

        private static Font s_font;
        private static Sprite s_whiteSprite;

        /// <summary>打进包的中文字体（Resources 相对路径，不带扩展名）。</summary>
        public const string BundledFontPath = "PetFont/NotoSansSC-Pet";

        /// <summary>
        /// 中文字体。**第一优先是打进包的资源字体，不是系统字体。**
        ///
        /// 原因是 WebGL 的限制：它拿不到操作系统字体，
        /// <c>Font.CreateDynamicFontFromOSFont</c> 在 WebGL 下不可用，
        /// 而内置 <c>LegacyRuntime.ttf</c> 不含 CJK 字形（中文全是豆腐块）。
        /// 所以 WebGL 下只有一条路：用资源目录里那份已经子集化的 Noto Sans CJK SC。
        /// 子集由 fonttools 生成，只保留工程实际用字，15.7MB 压到 0.5MB；
        /// 导入设置必须是 Unicode 图集模式，见 <see cref="PetV1.EditorTools.PetFontImporter"/>。
        ///
        /// 系统字体降级为编辑器/桌面端的兜底：改文案时若出现子集外的字，
        /// 编辑器里能立刻看见实际效果，不至于因为缺字卡住排查。
        /// </summary>
        public static Font ResolveFont()
        {
            if (s_font != null) return s_font;

            // ① 资源字体。WebGL 下必须命中这一条。
            s_font = Resources.Load<Font>(BundledFontPath);
            if (s_font != null) return s_font;

            // ② 桌面/编辑器兜底：系统字体。
            // 优先级从上到下，第一个存在的就用它。
            string[] candidates =
            {
                "Microsoft YaHei",     // Windows
                "PingFang SC",         // iOS / macOS
                "Noto Sans CJK SC",    // Android
                "Source Han Sans SC",
                "Heiti SC",
                "SimHei",
            };

            try
            {
                var installed = Font.GetOSInstalledFontNames();
                var set = new HashSet<string>(installed ?? new string[0]);
                foreach (var name in candidates)
                {
                    if (!set.Contains(name)) continue;
                    s_font = Font.CreateDynamicFontFromOSFont(name, PetUITheme.FontBody);
                    if (s_font != null)
                    {
                        Debug.LogWarning("[PetV1] UI：没加载到资源字体，退回系统字体「" + name +
                                         "」。WebGL 下这条路径不可用，中文会显示成方块 —— " +
                                         "检查 Resources/" + BundledFontPath + " 是否存在。");
                        return s_font;
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PetV1] UI：枚举系统字体失败（{e.Message}），退回内置字体。");
            }

            s_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (s_font == null)
            {
                Debug.LogError("[PetV1] UI：拿不到任何字体，界面文字不会显示。" +
                               "请检查是否误删了 Unity 内置字体资源。");
            }
            return s_font;
        }

        /// <summary>1×1 白图。用作所有纯色块的贴图，避免每个控件都存一份。</summary>
        public static Sprite WhiteSprite
        {
            get
            {
                if (s_whiteSprite != null) return s_whiteSprite;
                var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                var px = new Color32[16];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
                tex.SetPixels32(px);
                tex.Apply();
                tex.hideFlags = HideFlags.HideAndDontSave;
                s_whiteSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 100f);
                s_whiteSprite.hideFlags = HideFlags.HideAndDontSave;
                return s_whiteSprite;
            }
        }

        /// <summary>
        /// 按圆角半径缓存的圆角矩形图。9-slice 用于任意尺寸。
        ///
        /// **必须按radius 分开生成，不能所有控件共用一张**：
        /// 9-slice 的 border 以 sprite 自身像素为单位，四角绘制时不缩放，
        /// 所以「图上的圆角像素数 = 屏幕上的圆角像素数」（PPU 一致时）。
        /// 共用一张 border=32 的图 ⇒ 不管控件是 76×76 还是 126×56，
        /// 圆角都是 32 像素：正方形的「圆」看着像方角块，胶囊的上下角被拉扁。
        /// —— 这就是之前截图里「狗头像是个方块、等级胶囊是方矩形」的原因。
        ///
        /// sprite 边长取 radius*2+2：多出的2px 是给羽化留的，
        /// 不然圆角边缘会有 1px 硬切。
        /// </summary>
        private static readonly Dictionary<int, Sprite> s_roundedCache =
            new Dictionary<int, Sprite>();

        /// <summary>取圆角矩形图。<paramref name="radius"/> 是屏幕像素半径。</summary>
        public static Sprite RoundedSpriteFor(float radius)
        {
            int r = Mathf.Max(1, Mathf.RoundToInt(radius));
            if (s_roundedCache.TryGetValue(r, out var cached) && cached != null) return cached;

            int size = r * 2 + 2;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            var center = new Vector2(r + 1f, r + 1f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // 到四个圆心取最近的那个（圆角矩形 = 四个 1/4 圆的并集），
                    // 比只用一个中心更接近真正的圆角矩形。
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float d = float.MaxValue;
                    d = Mathf.Min(d, Vector2.Distance(p, new Vector2(r + 1f, r + 1f)));
                    d = Mathf.Min(d, Vector2.Distance(p, new Vector2(size - r - 1f, r + 1f)));
                    d = Mathf.Min(d, Vector2.Distance(p, new Vector2(r + 1f, size - r - 1f)));
                    d = Mathf.Min(d, Vector2.Distance(p, new Vector2(size - r - 1f, size - r - 1f)));
                    float a = Mathf.Clamp01(r - d);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;

            var sp = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(r + 1f, r + 1f, r + 1f, r + 1f));
            sp.hideFlags = HideFlags.HideAndDontSave;
            s_roundedCache[r] = sp;
            return sp;
        }

        /// <summary>
        /// 旧的无参入口。现在也走按半径缓存，
        /// 只有调用方没给 radius 时才用它（固定 32 圆角）。
        /// </summary>
        public static Sprite RoundedSprite => RoundedSpriteFor(32f);

        // ---------------------------------------------------------------
        // 基础构件
        // ---------------------------------------------------------------

        public static GameObject NewPanel(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            // 缩放模式固定为按 1080×1920 设计尺寸等比缩放，
            // 这样 UI 在任何分辨率下比例一致，不会出现「刘海屏上按钮被压扁」。
            rt.localScale = Vector3.one;
            return go;
        }

        /// <summary>
        /// 纯色圆角块。卡片、按钮底、Tab 底都用它。
        ///
        /// **raycastTarget 只在 radius &gt; 0 时开，这是有意的**：
        /// radius==0 的方块多数是纯装饰（卡片里的 Body、整屏底衬），
        /// 开着会变成看不见的拦截板，把下面的按钮与 3D 场景触摸全吃掉。
        ///
        /// 但反过来也踩过坑：底导的 Hit 按钮、弹窗遮罩都是 alpha≈0 的方块，
        /// 走这里会被关掉射线，表现是「画面正常但什么都点不动」且零报错。
        /// 那类**确实需要接点击**的方块一律用 <see cref="NewHitArea"/>。
        /// </summary>
        public static Image NewSolid(string name, Transform parent, Color color, float radius)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            var img = go.AddComponent<Image>();
            // radius 真正参与 sprite 生成：9-slice 的 border 就是圆角像素半径，
            // 所以同一个 radius 在任意控件尺寸下都是同样的圆角大小。
            img.sprite = radius > 0f ? RoundedSpriteFor(radius) : WhiteSprite;
            img.type = radius > 0f ? Image.Type.Sliced : Image.Type.Simple;
            img.color = color;
            img.raycastTarget = radius > 0f;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return img;
        }

        /// <summary>
        /// 透明点击拦截板。alpha 取 0.001 而非 0：全透明的 Image 在部分
        /// 平台仍会被剔除，而这个值不影响观感。
        ///
        /// 与 <see cref="NewSolid"/> 的区别只有一处：**raycastTarget 一定是 true**。
        /// 别用 NewSolid(radius:0) 来做这件事，它默认关射线。
        /// </summary>
        public static Image NewHitArea(string name, Transform parent)
        {
            var img = NewSolid(name, parent, new Color(0f, 0f, 0f, 0.001f), 0f);
            img.raycastTarget = true;
            return img;
        }

        public static Text NewText(string name, Transform parent, string content,
                                   int fontSize, Color color, TextAnchor anchor = TextAnchor.MiddleLeft)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            var text = go.AddComponent<Text>();
            text.font = ResolveFont();
            text.fontSize = fontSize;
            text.color = color;
            text.text = content;
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;       // 文字不该拦点击，否则点卡片点不动
            text.supportRichText = true;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return text;
        }

        // ---------------------------------------------------------------
        // 布局助手
        // ---------------------------------------------------------------

        /// <summary>
        /// 铺满父物体。<paramref name="padding"/> 是四边内缩。
        /// 大部分容器直接用它，避免每个都手写 anchorMin/Max/offset 四个值。
        /// </summary>
        public static RectTransform Fill(RectTransform rt, float padding = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(padding, padding);
            rt.offsetMax = new Vector2(-padding, -padding);
            return rt;
        }

        /// <summary>固定尺寸并锚到某个点。position 的含义随 anchor 变，这里只做原样设置。</summary>
        public static RectTransform Box(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax,
                                        Vector2 offsetMin, Vector2 offsetMax)
        {
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            return rt;
        }

        /// <summary>左上角定位。size 是控件尺寸，(x, y) 是距左上角的偏移（y 向下为正）。</summary>
        public static RectTransform TopLeft(RectTransform rt, Vector2 size, Vector2 pos)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = size;
            rt.anchoredPosition = new Vector2(pos.x, -pos.y);
            return rt;
        }

        /// <summary>底部居中定位。(x, y) 是距底边的偏移（y 向上为正）。</summary>
        public static RectTransform BottomCenter(RectTransform rt, Vector2 size, Vector2 pos)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = size;
            rt.anchoredPosition = new Vector2(pos.x, pos.y);
            return rt;
        }

        /// <summary>
        /// 垂直排列列表。用 VerticalLayoutGroup + ContentSizeFitter，
        /// 加子项时不用手动算 y 坐标。
        ///
        /// 为什么不用 GridLayoutGroup：食品商店是两列网格，
        /// 但两列的卡片高度不一致（名称长会换行）。
        /// GridLayoutGroup 强制所有格子等高，会留出难看的空白或者截断文字。
        /// 这里用 HorizontalLayoutGroup 手动分配两列。
        /// </summary>
        public static VerticalLayoutGroup NewVerticalList(Transform parent, float spacing)
        {
            var go = new GameObject("List", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(0f, 0f);
            rt.offsetMax = new Vector2(0f, 0f);

            var v = go.AddComponent<VerticalLayoutGroup>();
            v.spacing = spacing;
            v.padding = new RectOffset(0, 0, 0, 0);
            v.childAlignment = TextAnchor.UpperLeft;
            v.childControlWidth = true;
            v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;

            var fitter = go.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return v;
        }

        /// <summary>横向排列。用于两列网格的行与卡片内的图文并排。</summary>
        public static HorizontalLayoutGroup NewHorizontalRow(Transform parent, float spacing)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            var h = go.AddComponent<HorizontalLayoutGroup>();
            h.spacing = spacing;
            h.childAlignment = TextAnchor.MiddleLeft;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = true;
            h.childForceExpandHeight = true;
            return h;
        }

        /// <summary>
        /// 状态条底槽（浅蓝）+ 填充（主题色）。
        ///
        /// **返回值是填充条的 Image（改 <c>fillAmount</c>），out 参数给的是底槽 Track（摆尺寸）。**
        /// 两个别弄反：把 <c>out</c> 的结果当填充条用，<c>fillAmount</c> 会设到底槽上，
        /// 屏幕上那条 Fill 永远停在工厂写死的 1f，表现为「进度条满格但百分比很小」。
        /// </summary>
        public static Image NewProgressBar(Transform parent, Color fillColor, out Image track)
        {
            track = NewSolid("Track", parent, PetUITheme.PrimaryLight, PetUITheme.BarRadius);
            Fill((RectTransform)track.transform);

            var fillGo = new GameObject("Fill", typeof(RectTransform));
            fillGo.layer = LayerMask.NameToLayer("UI");
            var fill = fillGo.AddComponent<Image>();
            fill.sprite = RoundedSpriteFor(PetUITheme.BarRadius);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillAmount = 1f;
            fill.color = fillColor;
            fill.raycastTarget = false;
            var frt = (RectTransform)fillGo.transform;
            frt.SetParent(track.transform, false);
            Fill(frt, 2f);   // 内缩 2px，让填充边缘不贴住底槽边框
            return fill;
        }

        /// <summary>胶囊按钮。返回 Image 以便挂 Button 与改色。</summary>
        public static Button NewPillButton(Transform parent, string label, Color fill,
                                           int fontSize, out Text text)
        {
            var img = NewSolid("Btn", parent, fill, PetUITheme.ButtonRadius);
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;

            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 1f, 1f, 0.9f);
            // 按下态不用深色乘算，直接换底色会更准（乘算会让颜色偏暗发灰）
            colors.pressedColor = new Color(0.88f, 0.88f, 0.88f, 1f);
            colors.disabledColor = new Color(1f, 1f, 1f, 0.6f);
            btn.colors = colors;

            text = NewText("Label", img.transform, label, fontSize, Color.white, TextAnchor.MiddleCenter);
            Fill((RectTransform)text.transform);
            return btn;
        }

        /// <summary>普通矩形按钮（次要操作用）。</summary>
        public static Button NewRectButton(Transform parent, string label, Color fill,
                                           int fontSize, float height, out Text text)
        {
            var img = NewSolid("Btn", parent, fill, PetUITheme.CardRadius);
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            var rt = (RectTransform)img.transform;
            rt.sizeDelta = new Vector2(0f, height);

            text = NewText("Label", img.transform, label, fontSize, Color.white, TextAnchor.MiddleCenter);
            Fill((RectTransform)text.transform);
            return btn;
        }

        /// <summary>圆形图标按钮。展板右上角的设置齿轮、详情页右上角的关闭 X。</summary>
        public static Button NewCircleButton(Transform parent, string label, float diameter,
                                             Color fill, out Text text)
        {
            var img = NewSolid("Btn", parent, fill, diameter * 0.5f);
            img.raycastTarget = true;
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            var rt = (RectTransform)img.transform;
            rt.sizeDelta = new Vector2(diameter, diameter);

            text = NewText("Label", img.transform, label, PetUITheme.FontHeading,
                           Color.white, TextAnchor.MiddleCenter);
            Fill((RectTransform)text.transform);
            return btn;
        }

        /// <summary>
        /// 圆形图标按钮（<see cref="PetIcons"/> 图形，不带文字）。
        ///
        /// 与 <see cref="NewCircleButton"/> 的区别只在内容：那个是文字占位，
        /// 这个是真的图标 —— 需求明确要求「所有地方都显示图标，不能只用文字」。
        ///
        /// 返回的 Image 是按钮底，图标是它的子节点且**不吃射线**，
        /// 否则图标会先于按钮命中，点下去没反应。
        /// </summary>
        public static Button NewIconButton(Transform parent, string iconName, float diameter,
                                           Color fill, out Image icon)
        {
            var img = NewSolid("Btn", parent, fill, diameter * 0.5f);
            img.raycastTarget = true;
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            var rt = (RectTransform)img.transform;
            rt.sizeDelta = new Vector2(diameter, diameter);

            // 图标占直径的 52%，四周留 24% 白边。
            // **不能用 Fill()**：那是铺满父物体，会让图标顶到圆边、
            // 看起来像「图标贴在按钮上没有居中」。居中靠 anchor 中心 + 定尺寸。
            icon = PetIcons.NewIcon(iconName, img.transform,
                                    new Vector2(diameter * 0.52f, diameter * 0.52f),
                                    Color.white);
            var irt = (RectTransform)icon.transform;
            irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 0.5f);
            irt.pivot = new Vector2(0.5f, 0.5f);
            irt.anchoredPosition = Vector2.zero;
            irt.localScale = Vector3.one;
            return btn;
        }

        /// <summary>
        /// 图标 + 文字的胶囊按钮。展板底部的「食饱」「训练」快捷入口。
        ///
        /// 图标在上、文字在下，纵向排列。整体仍然是一个 Button（图标与文字都不吃射线），
        /// 所以点击区是整个胶囊而不是两个小块。
        /// </summary>
        public static Button NewIconPillButton(Transform parent, string iconName, string label,
                                                float width, float height, Color fill,
                                                Color glyphColor, Color textColor,
                                                out Text text)
        {
            var img = NewSolid("Btn", parent, fill, height * 0.5f);
            img.raycastTarget = true;
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            var rt = (RectTransform)img.transform;
            rt.sizeDelta = new Vector2(width, height);

            float iconSize = height * 0.42f;
            float capH = height * 0.28f;
            float gap = (height - iconSize - capH) / 3f;

            var icon = PetIcons.NewIcon(iconName, img.transform,
                                        new Vector2(iconSize, iconSize), glyphColor);
            var irt = (RectTransform)icon.transform;
            // 锚在上中：pivot 与 anchor 一致，anchoredPosition.y 是「顶边往下多少」，
            // 横向以中线为 0，保证图标水平居中（之前 anchor 只有 y、
            // x 默认 0 走的是父物体左边，横向会偏）。
            irt.anchorMin = new Vector2(0.5f, 1f);
            irt.anchorMax = new Vector2(0.5f, 1f);
            irt.pivot = new Vector2(0.5f, 1f);
            irt.anchoredPosition = new Vector2(0f, -gap);

            text = NewText("Label", img.transform, label, PetUITheme.FontSmall,
                           textColor, TextAnchor.UpperCenter);
            var trt = (RectTransform)text.transform;
            trt.anchorMin = trt.anchorMax = new Vector2(0f, 0f);
            trt.pivot = new Vector2(0.5f, 0f);
            trt.sizeDelta = new Vector2(width, capH);
            trt.anchoredPosition = new Vector2(0f, gap * 0.7f);

            return btn;
        }

        /// <summary>
        /// 文本按钮。展板里「购买前确认」用的那种无底色纯文字按钮。
        /// </summary>
        public static Button NewTextButton(Transform parent, string label, Color color,
                                            int fontSize, out Text text)
        {
            var go = new GameObject("TextBtn", typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            var btn = go.AddComponent<Button>();
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            // 按钮本体没有 Image 时 Unity 不会自动产生过变反馈，
            // 补一个透明 targetGraphic 让 Button 的状态切换仍然可用。
            var target = NewHitArea("Hit", go.transform);
            Fill((RectTransform)target.transform);
            btn.targetGraphic = target;

            text = NewText("Label", go.transform, label, fontSize, color, TextAnchor.MiddleCenter);
            Fill((RectTransform)text.transform);
            return btn;
        }
    }
}
