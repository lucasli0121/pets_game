using System.Collections.Generic;
using PetV1.Model;
using UnityEngine;
using UnityEngine.UI;

namespace PetV1.UI
{
    /// <summary>
    /// 领养流程：启动页 → 选宠页 → 进主场景。对应设计展板 s1「启动与选宠」。
    ///
    /// 两屏的分工：
    /// - <see cref="ShowSplash"/> 启动页：全屏（3D 场景透出来）+ 标题 + 「开始」。
    ///   右上角 X 是「跳过开场白」，直接进选宠页 —— 关不掉整个流程，
    ///   没有宠物就进不了主场景（喂食/商城全要 petId）。
    /// - <see cref="ShowSelect"/> 选宠页：浅色整页 + 宠物卡 + 「选择它」。
    ///
    /// **V1 只有一只狗**：后台 <c>pet/adopt</c> 只放行 dog（cat 要
    /// <c>enable_double_pet</c> 开关，见 pets_server internal/service/pet.go），
    /// 所以第二张卡只做展示，点了只提示「更多宠物即将开放」。
    ///
    /// 宠物肖像是 <c>Assets/Resources/PetPortraits/border_collie.png</c>，
    /// 由 <c>PetV1.EditorTools.PetPortraitRenderer</c> 从场景里的 3D 狗渲出来。
    /// 用 Texture2D + Sprite.Create 而不是 Resources.Load&lt;Sprite&gt;：
    /// PNG 默认按 Texture 导入，Load&lt;Sprite&gt; 会返回 null 且不报错，
    /// 症状是头像位置一块洋红方块（这个坑在图标上已经踩过一次）。
    /// </summary>
    public class AdoptFlow
    {
        private PetHomeUI _ui;

        /// <summary>整个流程的根。两屏共用，靠显隐切换。</summary>
        private Image _root;

        // ---- 启动页 ----
        private RectTransform _splash;
        private Button _startBtn;

        // ---- 选宠页 ----
        private RectTransform _select;
        private Image _portrait;
        private Button _chooseBtn;
        private Text _chooseLabel;
        private Button _moreBtn;

        /// <summary>流程是否开着。开着的时候主界面不该响应输入。</summary>
        public bool IsOpen => _root != null && _root.gameObject.activeSelf;

        /// <summary>当前停在哪一屏。</summary>
        public enum Stage { Splash, Select }

        public Stage Current { get; private set; } = Stage.Splash;

        // ------------------------------------------------------------------
        // 构建
        // ------------------------------------------------------------------

        public void Build(Transform parent, PetHomeUI ui)
        {
            _ui = ui;

            // 根：铺满 SafeFrame。不放遮罩色 —— 启动页要让 3D 场景透出来，
            // 选宠页才需要浅色整页底（见下面 _select 自己那块）。
            // 但 raycastTarget 必须开：不开的话点得到背后的主界面
            // （此时还没有宠物，那些页面点下去全是空数据）。
            _root = PetUIFactory.NewSolid("AdoptMask", parent, new Color(0f, 0f, 0f, 0f), 0f);
            _root.raycastTarget = true;
            PetUIFactory.Fill((RectTransform)_root.transform);
            _root.gameObject.SetActive(false);

            BuildSplash(_root.transform);
            BuildSelect(_root.transform);
        }

        private void BuildSplash(Transform parent)
        {
            // 启动页底板：半透明黑。展板原稿是草原照片，本工程没有这张美术资源，
            // 用 3D 场景当背景；白色标题直接压在客厅墙上会读不清，
            // 所以压一层 45% 黑（与弹窗遮罩同值），狗仍然看得见。
            var bg = PetUIFactory.NewSolid("Splash", parent, PetUITheme.Scrim, 0f);
            bg.raycastTarget = true;
            _splash = (RectTransform)bg.transform;
            PetUIFactory.Fill(_splash);

            // 标题两行。展板是「你的虚拟宠物 / 从这里开始」，字号比页面标题大。
            var title = PetUIFactory.NewText("Title", _splash, "你的虚拟宠物\n从这里开始",
                                             PetUITheme.FontTitle, Color.white,
                                             TextAnchor.MiddleCenter);
            var trt = (RectTransform)title.transform;
            trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 0.5f);
            trt.pivot = new Vector2(0.5f, 0.5f);
            trt.sizeDelta = new Vector2(760f, 200f);
            // 放在画面上三分之一：狗在画面中下部，标题要避开它。
            trt.anchoredPosition = new Vector2(0f, 300f);

            // 右上角 X：跳过开场白。
            var close = PetUIFactory.NewCircleButton(_splash, "×", 76f,
                                                     new Color(1f, 1f, 1f, 0.25f),
                                                     out var closeTx);
            var crt = (RectTransform)close.transform;
            crt.anchorMin = crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(1f, 1f);
            crt.anchoredPosition = new Vector2(-32f, -32f);
            closeTx.color = Color.white;
            close.onClick.AddListener(ShowSelect);

            // 「开始」。展板是白底深字胶囊，放在底部安全区之上。
            _startBtn = PetUIFactory.NewPillButton(_splash, "开始", Color.white,
                                                    PetUITheme.FontHeading, out var startTx);
            startTx.color = PetUITheme.TextPrimary;
            var srt = (RectTransform)_startBtn.transform;
            srt.anchorMin = new Vector2(0.5f, 0f);
            srt.anchorMax = new Vector2(0.5f, 0f);
            srt.pivot = new Vector2(0.5f, 0f);
            srt.sizeDelta = new Vector2(480f, PetUITheme.PrimaryButtonHeight);
            srt.anchoredPosition = new Vector2(0f, PetUITheme.SafeBottom + 120f);
            _startBtn.onClick.AddListener(ShowSelect);
        }

        private void BuildSelect(Transform parent)
        {
            // 选宠页底板：整页浅色。展板这一屏就是浅色底 + 白卡。
            var bg = PetUIFactory.NewSolid("Select", parent, PetUITheme.PageFill, 0f);
            bg.raycastTarget = true;
            _select = (RectTransform)bg.transform;
            PetUIFactory.Fill(_select);
            _select.gameObject.SetActive(false);

            // 标题
            var title = PetUIFactory.NewText("Title", _select, "选择你的宠物",
                                             PetUITheme.FontHeading, PetUITheme.TextPrimary,
                                             TextAnchor.MiddleCenter);
            var trt = (RectTransform)title.transform;
            trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 1f);
            trt.pivot = new Vector2(0.5f, 1f);
            trt.sizeDelta = new Vector2(600f, 48f);
            trt.anchoredPosition = new Vector2(0f, -PetUITheme.TopBarHeight);

            // ---- 主卡 ----
            // 这里用实色白而不是主题的 Card（80% 白）：底下是浅色整页底，
            // 半透白会糊成一片，分不出卡片边界。展板这一屏的卡也是实色。
            var card = PetUIFactory.NewSolid("Card", _select, Color.white,
                                             PetUITheme.CardRadius);
            var crt = (RectTransform)card.transform;
            crt.anchorMin = crt.anchorMax = new Vector2(0.5f, 1f);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.sizeDelta = new Vector2(660f, 620f);
            crt.anchoredPosition = new Vector2(0f, -220f);

            // 头像区：浅蓝圆角块，狗肖像放里面。
            var art = PetUIFactory.NewSolid("Art", crt, new Color(0xE8 / 255f, 0xF1 / 255f, 0xFD / 255f),
                                            PetUITheme.CardRadius);
            var artRt = (RectTransform)art.transform;
            artRt.anchorMin = artRt.anchorMax = new Vector2(0.5f, 1f);
            artRt.pivot = new Vector2(0.5f, 1f);
            artRt.sizeDelta = new Vector2(360f, 300f);
            artRt.anchoredPosition = new Vector2(0f, -28f);

            _portrait = NewPortrait(artRt);
            var prt = (RectTransform)_portrait.transform;
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.pivot = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(260f, 260f);
            prt.anchoredPosition = Vector2.zero;

            // 品种名
            var name = PetUIFactory.NewText("Breed", crt, "Border Collie",
                                            PetUITheme.FontHeading, PetUITheme.TextPrimary,
                                            TextAnchor.MiddleCenter);
            var nrt = (RectTransform)name.transform;
            nrt.anchorMin = nrt.anchorMax = new Vector2(0.5f, 1f);
            nrt.pivot = new Vector2(0.5f, 1f);
            nrt.sizeDelta = new Vector2(560f, 44f);
            nrt.anchoredPosition = new Vector2(0f, -360f);

            // 性格标签。展板是「温顺 · 聪明 · 好奇」。
            var tags = PetUIFactory.NewText("Tags", crt, "温顺 · 聪明 · 好奇",
                                            PetUITheme.FontBody, PetUITheme.TextSecondary,
                                            TextAnchor.MiddleCenter);
            var grt = (RectTransform)tags.transform;
            grt.anchorMin = grt.anchorMax = new Vector2(0.5f, 1f);
            grt.pivot = new Vector2(0.5f, 1f);
            grt.sizeDelta = new Vector2(560f, 36f);
            grt.anchoredPosition = new Vector2(0f, -412f);

            // 「选择它」
            _chooseBtn = PetUIFactory.NewPillButton(crt, "选择它", PetUITheme.Primary,
                                                     PetUITheme.FontHeading, out _chooseLabel);
            var brt = (RectTransform)_chooseBtn.transform;
            brt.anchorMin = new Vector2(0.5f, 0f);
            brt.anchorMax = new Vector2(0.5f, 0f);
            brt.pivot = new Vector2(0.5f, 0f);
            brt.sizeDelta = new Vector2(420f, PetUITheme.PrimaryButtonHeight);
            brt.anchoredPosition = new Vector2(0f, 36f);
            _chooseBtn.onClick.AddListener(OnChoose);

            // ---- 第二张卡：更多宠物 ----
            // 只展示不交互（点了只提示）。后台 V1 只放行 dog，
            // 见 pets_server internal/service/pet.go 的 enable_double_pet 判断。
            var more = PetUIFactory.NewSolid("More", _select, new Color(1f, 1f, 1f, 0.6f),
                                             PetUITheme.CardRadius);
            var mrt = (RectTransform)more.transform;
            mrt.anchorMin = mrt.anchorMax = new Vector2(0.5f, 1f);
            mrt.pivot = new Vector2(0.5f, 1f);
            mrt.sizeDelta = new Vector2(660f, 180f);
            mrt.anchoredPosition = new Vector2(0f, -868f);

            // 灰色剪影：同一张肖像压成灰色 + 降透明度，不另做一张图。
            var ghost = NewPortrait(mrt);
            ghost.color = new Color(0.62f, 0.68f, 0.75f, 0.35f);
            var ghostRt = (RectTransform)ghost.transform;
            ghostRt.anchorMin = ghostRt.anchorMax = new Vector2(0.5f, 0.5f);
            ghostRt.pivot = new Vector2(0.5f, 0.5f);
            ghostRt.sizeDelta = new Vector2(110f, 110f);
            ghostRt.anchoredPosition = new Vector2(0f, 34f);

            var moreText = PetUIFactory.NewText("MoreText", mrt, "更多宠物即将开放",
                                                PetUITheme.FontBody, PetUITheme.TextSecondary,
                                                TextAnchor.MiddleCenter);
            var txRt = (RectTransform)moreText.transform;
            txRt.anchorMin = txRt.anchorMax = new Vector2(0.5f, 0.5f);
            txRt.pivot = new Vector2(0.5f, 0.5f);
            txRt.sizeDelta = new Vector2(560f, 36f);
            txRt.anchoredPosition = new Vector2(0f, -46f);

            _moreBtn = more.gameObject.AddComponent<Button>();
            _moreBtn.targetGraphic = more;
            _moreBtn.onClick.AddListener(() =>
                _ui.ShowToast("更多宠物即将开放"));
        }

        /// <summary>
        /// 建一个肖像 Image。纹理用 <c>Resources.Load&lt;Texture2D&gt;</c> 拿，
        /// 再 <c>Sprite.Create</c> —— PNG 默认按 Texture 导入，
        /// 直接 Load&lt;Sprite&gt; 返回 null 且不报错（洋红方块）。
        /// </summary>
        private static Image NewPortrait(Transform parent)
        {
            var go = new GameObject("Portrait", typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            var img = go.AddComponent<Image>();
            img.preserveAspect = true;
            img.raycastTarget = false;

            var tex = Resources.Load<Texture2D>(PortraitResource);
            if (tex == null)
            {
                Debug.LogWarning("[PetV1] 找不到宠物肖像资源 " + PortraitResource +
                                 "，请执行 Pet V1 / 7. Render Pet Portrait。");
            }
            else
            {
                img.sprite = Sprite.Create(tex,
                                           new Rect(0f, 0f, tex.width, tex.height),
                                           new Vector2(0.5f, 0.5f), 100f);
            }
            go.transform.SetParent(parent, false);
            return img;
        }

        /// <summary>Resources 下的肖像路径（不含扩展名）。</summary>
        public const string PortraitResource = "PetPortraits/border_collie";

        // ------------------------------------------------------------------
        // 显示
        // ------------------------------------------------------------------

        /// <summary>从启动页开始走流程。</summary>
        public void Show()
        {
            if (_root == null) return;
            _root.gameObject.SetActive(true);
            ShowSplash();
            ResetButtons();
        }

        /// <summary>
        /// 把按钮复位。「领养中…」是请求飞行期间的字样，成功后流程被关掉、
        /// 字样却留着；下次再开（换账号/重试）会看到一个点不动的「领养中…」。
        /// </summary>
        private void ResetButtons()
        {
            if (_chooseBtn != null)
            {
                _chooseBtn.interactable = true;
                _chooseLabel.text = "选择它";
            }
        }

        public void ShowSplash()
        {
            Current = Stage.Splash;
            if (_splash != null) _splash.gameObject.SetActive(true);
            if (_select != null) _select.gameObject.SetActive(false);
        }

        public void ShowSelect()
        {
            Current = Stage.Select;
            if (_splash != null) _splash.gameObject.SetActive(false);
            if (_select != null) _select.gameObject.SetActive(true);
        }

        public void Hide()
        {
            if (_root != null) _root.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------
        // 领养
        // ------------------------------------------------------------------

        private void OnChoose()
        {
            var s = _ui != null ? _ui.Session : null;
            if (s == null) return;

            if (!s.Online)
            {
                _ui.ShowToast("没连上服务端，暂时不能领养");
                return;
            }

            // 防连点：领养是有唯一索引的写操作，重复点会拿到「已经领养过该宠物」。
            _chooseBtn.interactable = false;
            _chooseLabel.text = "领养中…";

            // 性格四维取 <see cref="PetRuntimeState"/> 的默认值（边牧偏置 6/5/7/3）。
            // 不用 PetPersonality：那边是 0~1 的十维调参值（energy/sociability/...），
            // 字段名和后台 domain.Personality 的 lively/curiosity/affection/independence
            // 对不上，硬映射等于自己造一套口径。取 state 的四维，与后台 0~10 整数一致。
            // 展板的选宠页没有性格调整 UI，V1 只展示不培养
            // （修改走 pet/personality，要扣积分且有 24 小时冷却）。
            var st = _ui.State;
            s.Adopt("", st.lively, st.curiosity, st.affection, st.independence,
                    (ok, msg) =>
            {
                if (_root == null) return;
                if (!ok)
                {
                    _ui.ShowToast(string.IsNullOrEmpty(msg) ? "领养失败" : msg);
                    _chooseBtn.interactable = true;
                    _chooseLabel.text = "选择它";
                    return;
                }                Hide();
                _ui.OnAdoptionCompleted();
            });
        }
    }
}
