using System.Collections.Generic;
using PetV1.Model;
using PetV1.Pet;
using UnityEngine;
using UnityEngine.UI;

namespace PetV1.UI
{
    /// <summary>底导四个页签。与展板第 2 页一致：宠物 / 食品 / 商店 / 场景。</summary>
    public enum HomeTab
    {
        Pet = 0,
        Food = 1,
        Shop = 2,
        Scene = 3,
    }

    /// <summary>
    /// UI 根。持有状态、页签切换、顶栏与底导，是所有页面的唯一父节点。
    ///
    /// 职责边界：
    /// - 持有 <see cref="PetRuntimeState"/> 并在数据变化后广播 <see cref="StateChanged"/>
    /// - 管理四个页面的显隐（同一时刻只有一个是active）
    /// - 顶栏（等级/经验/积分/设置）与底导由它统一建，避免每个页面各建一份
    ///
    /// 页面只做「渲染自己那部分」，不碰彼此 —— 切页签时只改 SetActive，
    /// 不重建，重建会丢掉滚动位置与动画状态。
    /// </summary>
    [DisallowMultipleComponent]
    public class PetHomeUI : MonoBehaviour
    {
        /// <summary>底导文字。</summary>
        private static readonly string[] TabLabels = { "宠物", "食品", "商店", "场景" };

        /// <summary>
        /// 底导图标名，对应 <see cref="PetIcons"/> 里的图形。
        /// 顺序必须与 <see cref="TabLabels"/> 一致（数组按下标取值）。
        /// </summary>
        private static readonly string[] TabIcons = { "pet", "food", "shop", "scene" };

        [Header("运行时数据")]
        [Tooltip("宠物状态快照。所有页面都从这一份读，不各自持有副本。")]
        [SerializeField] private PetRuntimeState state;

        [Tooltip("商品配置。对应后台 config_food。")]
        [SerializeField] private FoodCatalog catalog;

        [Header("场景")]
        [Tooltip("场景库。场景页与切场景都要用。")]
        [SerializeField] private PetSceneLibrary library;

        [Tooltip("场景控制。点「使用」时由它切背景与重摆取景。")]
        [SerializeField] private PetSceneStage stage;

        // 事件：任何数据变化都广播，页面自己决定要不要重绘。
        // 为什么不直接让页面互相找：页面之间会形成环（食品页要知道商店页余额变了没有）。
        public event System.Action StateChanged;
        public event System.Action<HomeTab> TabChanged;

        private readonly Dictionary<HomeTab, GameObject> _pages = new Dictionary<HomeTab, GameObject>();
        private HomeTab _current = HomeTab.Pet;

        // 顶栏控件引用
        private Text _levelText;
        private Text _pointsText;

        /// <summary>
        /// 顶栏常驻经验条。挂在等级胶囊下方，宽度跟着胶囊。
        /// 之前经验条收在 <see cref="PetLevelPopup"/> 浮层里，要点开才看得到——
        /// 但经验是玩家最常看的数值之一，常驻更合理（用户口径）。
        /// </summary>
        private Image _topExpFill;

        // 底导按钮
        private readonly List<Button> _tabButtons = new List<Button>();
        private readonly List<Image> _tabPills = new List<Image>();
        private readonly List<Image> _tabIcons = new List<Image>();
        private readonly List<Text> _tabTexts = new List<Text>();

        private Image _backdrop;

        // 页面实例
        private PetGrowthPage _petPage;
        private FoodPage _foodPage;
        private ShopPage _shopPage;
        private ScenePage _scenePage;
        private FoodDetailPopup _detailPopup;
        private PetStatusPopup _statusPopup;
        private CheckInPopup _checkInPopup;
        private AdoptFlow _adoptFlow;
        private PetV1.Ads.PetAdService _ads;
        private bool _interstitialTried;
        private ToastWidget _toast;

        /// <summary>会话（登录 / 拉配置 / 同步状态 / 买 / 喂）。可能为 null —— 还没 Begin 时。</summary>
        private PetV1.Data.PetSession _session;

        /// <summary>
        /// 服务端会话。页面用它发写请求与判断是否联网。
        /// 为 null 表示还没初始化完（Build 期间），调用方要能容忍。
        /// </summary>
        public PetV1.Data.PetSession Session => _session;

        public PetRuntimeState State
        {
            get
            {
                if (state == null) state = PetRuntimeState.CreateDefault();
                EnsureStateCollections();
                return state;
            }
        }

        /// <summary>
        /// 把反序列化丢掉的集合补回默认值。
        ///
        /// <c>state</c> 是 <c>[SerializeField]</c>，构建期由 <c>CreateDefault()</c> 填好并存进场景，
        /// 但 <c>inventory</c> 是 Dictionary（带 <c>[NonSerialized]</c>）——
        /// Unity 的序列化后备不支持字典，那个字段存不进场景文件。
        /// 于是运行期反序列化出来的 state 是个「其他字段都对、唯独背包全空」的对象，
        /// 表现是「Lv.3、饱腹 65 都对，但所有食品数量都是 0，点喂食一律提示已用完」。
        ///
        /// **接上服务端之后这个兜底必须让位**：后台新用户的背包本来就是空的
        /// （不白送食品，只发 500 初始积分）。继续兜底会把「服务端说没有」
        /// 覆盖成「本地说有三个」，玩家买之前根本分不清哪些是真实库存。
        /// 判据用 <see cref="PetV1.Data.PetSession.RemoteAuthoritative"/>，
        /// 它在 sync-state 成功落库之后置 true。
        /// </summary>
        private void EnsureStateCollections()
        {
            state.EnsureCollections();

            // 服务端已经下发过权威背包：一个字节都不要改，空就是空。
            if (_session != null && _session.RemoteAuthoritative) return;

            bool any = false;
            foreach (var food in Catalog.AllSorted())
            {
                if (state.CountOf(food.id) > 0) { any = true; break; }
            }
            if (any) return;

            var def = PetRuntimeState.CreateDefault();
            state.inventory = def.inventory;
            if (state.unlockedScenes == null || state.unlockedScenes.Count == 0)
                state.unlockedScenes = def.unlockedScenes;
        }

        public FoodCatalog Catalog
        {
            get
            {
                if (catalog == null) catalog = FoodCatalog.Fallback();
                return catalog;
            }
        }

        /// <summary>
        /// 场景库。可能为 null —— 编辑器里手搭场景而没跑构建器时。
        /// 调用方（ScenePage）要能容忍，不要直接解引用。
        /// </summary>
        public PetSceneLibrary Library => library;

        public HomeTab CurrentTab => _current;

        /// <summary>
        /// 构建整个 UI。由 <c>PetHomeSceneBuilder</c> 在建场时调用，
        /// 这样场景里的层级结构是构建产物、可重建，不依赖手工拖拽。
        /// </summary>
        public void Build(PetSceneLibrary sceneLibrary, PetSceneStage sceneStage,
                          PetRuntimeState initialState, FoodCatalog foodCatalog)
        {
            library = sceneLibrary;
            stage = sceneStage;
            state = initialState ?? PetRuntimeState.CreateDefault();
            catalog = foodCatalog;

            var canvas = GetComponent<Canvas>();
            var canvasRect = (RectTransform)transform;

            // ---- 竖屏内容框 ----
            // 为什么不直接用画布根：CanvasScaler 每帧覆写画布根的 rect，
            // 改它的 sizeDelta 会被冲掉。内容必须挂进一个自己控制尺寸的子节点，
            // 由 PetPortraitGuard 按屏幕比例摆成居中的 9:16。
            var safeFrame = NewSafeFrame(canvasRect);

            // ---- 底板：页面底色 + 场景透视窗口 ----
            // 场景页之外的页面要盖住 3D 场景（背景是 2D 图 + 3D 狗），
            // 所以用一个半透明白底板；宠物页则整块透明露出场景。
            var backdrop = PetUIFactory.NewPanel("Backdrop", safeFrame);
            PetUIFactory.Fill((RectTransform)backdrop.transform);
            var backdropImg = backdrop.AddComponent<Image>();
            _backdrop = backdropImg;
            backdropImg.sprite = PetUIFactory.WhiteSprite;
            backdropImg.color = PetUITheme.PageFill;
            // **不能开 raycastTarget**：它是铺满全屏的，开了就等于在 3D 场景上盖了一层
            // 看不见的拦截板，宠物页的触摸互动（摸头、挠屏）会全部失效 ——
            // 表现是「点狗没反应」，但看起来像手势功能坏了。
            // 页面内部的滚动区域各自有 ScrollRect 的 raycast，不依赖这个底板。
            backdropImg.raycastTarget = false;
            backdrop.transform.SetAsFirstSibling();

            // ---- 分层容器 ----
            var pageLayer = NewLayer("PageLayer", safeFrame, PetUIFactory.LayerPage);
            var popupLayer = NewLayer("PopupLayer", safeFrame, PetUIFactory.LayerPopup);
            var toastLayer = NewLayer("ToastLayer", safeFrame, PetUIFactory.LayerToast);

            // ---- 四个页面 ----
            BuildPages(pageLayer);

            // ---- 顶栏 ----
            BuildTopBar(safeFrame);

            // ---- 底导 ----
            BuildBottomNav(safeFrame);

            // ---- 弹窗与提示 ----
            _detailPopup = new FoodDetailPopup();
            _detailPopup.Build(popupLayer, this);

            // 状态浮层。挂在 PopupLayer 而不是页面里——
            // 入口在顶栏（跨页签），放页面里会随页面显隐被销毁。
            // 等级不再需要浮层：等级胶囊 + 常驻经验条已经直接显示级内进度。
            _statusPopup = new PetStatusPopup();
            _statusPopup.Build(popupLayer, this);

            // 签到弹窗。入口有两个：顶栏日历按钮，以及「当天第一次启动且未签到」的自动弹出。
            _checkInPopup = new CheckInPopup();
            _checkInPopup.Build(popupLayer, this);

            // 领养流程（启动页 + 选宠）。建在 PopupLayer 之上：
            // 没有宠物时它要盖住整个主界面，包括顶栏与底导。
            _adoptFlow = new AdoptFlow();
            _adoptFlow.Build(popupLayer, this);

            _toast = new ToastWidget();
            _toast.Build(toastLayer);

            SwitchTab(HomeTab.Pet);
            RefreshAll();
        }

        /// <summary>
        /// 运行期自检并补建。
        ///
        /// <see cref="Build"/> 只在构建器（编辑期）里跑一次，而它产出的引用
        /// ——<c>_pages</c> / <c>_tabButtons</c> / <c>_toast</c> / 四个页面实例——
        /// 全是**非序列化私有字段**：Dictionary 和普通 C# 类不是 Unity 的序列化目标，
        /// 运行期从场景反序列化出来必然是 null。
        ///
        /// 症状分两层，且**都不报错**，只靠看画面发现不了：
        /// ① <c>SwitchTab</c> 遍历的是空字典 → 底导按钮的 onClick 射线命中得到、
        ///    按钮也确实变色了，但页面一个都没切换（onClick 闭包是构建期绑的，
        ///    能存进场景；闭包里引用的字段却全空）；
        /// ② <c>RefreshAll</c> 空转，列表页保留构建期那次布局的残值 ——
        ///    隐藏状态下重建的行高度停在 -20，射线一个都命不中。
        ///
        /// 修法是运行期重建整棵树，而不是想办法把这些字段序列化 ——
        /// 它们本来就不该被序列化（每次进 Play 都应该拿到干净状态）。
        /// 已有子节点先清掉，避免和构建期产物叠成两套 UI。
        /// </summary>
        private void Start()
        {
            BindRunner();

            if (_pages.Count == 0)
            {
                // 必须 DestroyImmediate，不能用 Destroy ——
                // Destroy 帧末才生效，重建期间旧节点还在层级里，
                // 于是 transform.Find("PageLayer") 会先命中那棵即将销毁的死树，
                // SwitchTab 里对它的 ForceRebuildLayoutImmediate 全部作用在错误对象上。
                // 症状是「行高仍是 -20」，看起来像布局修复没生效。
                for (int i = transform.childCount - 1; i >= 0; i--)
                    DestroyImmediate(transform.GetChild(i).gameObject);

                Build(library, stage, state, catalog);
            }
            else
            {
                RefreshAll();
            }

            SwitchTab(HomeTab.Pet);

            // 服务端会话放在最后：它要拿到重建好的 Catalog / State，
            // 而且第一次 Changed 会触发 RefreshAll，那时四个页面必须已经建完。
            BindSession();
        }

        /// <summary>
        /// 接上会话层。
        ///
        /// 顺序要求：必须在 <see cref="Build"/> 之后 —— <see cref="Catalog"/> 的 getter
        /// 在 null 时会给一份 <c>FoodCatalog.Fallback()</c> 临时实例，
        /// 如果 session 先拿到那个，服务端下发的商品就写进了一份没人读的对象里，
        /// 表现是「接上后台了但商品价格没变」。
        /// </summary>
        private void BindSession()
        {
            _session = GetComponent<PetV1.Data.PetSession>();
            if (_session == null) _session = gameObject.AddComponent<PetV1.Data.PetSession>();

            // 先退订再订阅：Start 在重建路径上可能被跑到两次，重复订阅会让 RefreshAll 跑两遍
            _session.Changed -= OnSessionChanged;
            _session.Changed += OnSessionChanged;
            _session.Notice -= ShowToast;
            _session.Notice += ShowToast;
            _session.AdoptionRequired -= OnAdoptionRequired;
            _session.AdoptionRequired += OnAdoptionRequired;

            _session.Begin(this, Catalog, State);

            // 广告层放在会话之后：它自己不发请求，但 UI 要通过它拿
            // IAdService 实现（编辑器假 / 微信真）。
            _ads = GetComponent<PetV1.Ads.PetAdService>();
            if (_ads == null) _ads = gameObject.AddComponent<PetV1.Ads.PetAdService>();
        }

        /// <summary>广告播放层。可能为 null（BindSession 还没跑）。</summary>
        public PetV1.Ads.PetAdService Ads => _ads;

        /// <summary>
        /// 服务端说这只账号还没有宠物：走领养流程。
        ///
        /// 主界面此时照常建着（页面/顶栏/底导都在），但领流程盖在最上面，
        /// 而且它的根开了 raycastTarget，背后的按钮点不到。
        /// 不清掉主界面是为了领养完成后能立刻露出完整的一屏，
        /// 不用再重建一次层级。
        /// </summary>
        private void OnAdoptionRequired()
        {
            if (_adoptFlow == null || _adoptFlow.IsOpen) return;
            _adoptFlow.Show();
        }

        /// <summary>
        /// 领养完成。由 <see cref="AdoptFlow"/> 点「选择它」成功后调用。
        ///
        /// 这里只做「露回主界面 + 刷新」。初始积分与赠送食品在
        /// <see cref="PetV1.Data.PetSession.Adopt"/> 里已经补过一次 sync-state，
        /// 不需要再发请求。
        /// </summary>
        public void OnAdoptionCompleted()
        {
            if (_adoptFlow != null) _adoptFlow.Hide();
            RefreshAll();
            ShowToast("领养成功，欢迎回家");
            SwitchTab(HomeTab.Pet);
        }

        private void OnSessionChanged()
        {
            RefreshAll();

            // 服务端的 activeScene 可能和画面不一致：换设备登录、上次切换在服务端成功
            // 但客户端没等到响应、或者本地 state 被场景重建重置过。
            // 以服务端为准重贴一次，否则会出现「界面显示已选中公园、画面还是小屋」。
            if (stage != null && !string.IsNullOrEmpty(State.activeScene)
                && stage.CurrentSceneId != State.activeScene)
            {
                Debug.Log($"[PetV1] 服务端当前场景是 {State.activeScene}，画面停在 " +
                          $"{stage.CurrentSceneId}，按服务端重贴。");
                stage.ApplyScene(State.activeScene);
            }

            TryAutoShowCheckIn();
            TryAutoPlayInterstitial();
        }

        /// <summary>
        /// 冷启动自动播一次插屏广告（可跳过，无奖励）。
        ///
        /// 三个闸门，缺一个都不播：
        /// ① 只播一次（<see cref="_interstitialTried"/>）—— 会话层每完成一次
        ///    写操作都会触发 Changed，不设闸门等于每次买东西都弹一次广告；
        /// ② 领养流程开着不播 —— 新玩家正在选宠，这时候弹广告会把流程打断，
        ///    而且展板的启动页本身就是全屏，叠不上；
        /// ③ 后台把插屏广告位关了就不播（<see cref="PetRuntimeState.AdInterstitialReady"/>）
        ///    —— 默认配置里插屏是 disabled，要后台在管理端打开。
        ///
        /// 播完只上报 <c>ad/impression</c>（只记录不发奖，文档 13.8 ②）。
        /// </summary>
        private void TryAutoPlayInterstitial()
        {
            if (_interstitialTried) return;
            if (_ads == null) return;

            var s = _session;
            if (s == null || !s.Online) return;
            if (_adoptFlow != null && _adoptFlow.IsOpen) return;

            var st = State;
            if (!st.AdInterstitialReady) return;

            _interstitialTried = true;
            Debug.Log("[PetV1] 冷启动插屏广告。");
            _ads.PlayInterstitial(completed =>
                s.ReportAdImpression(st.adInterstitialUnitId, "app_open", completed));
        }

        /// <summary>自动弹签到窗的日期标记（PlayerPrefs 键）。</summary>
        private const string CheckInAutoPopupKey = "pet_checkin_autopopup_date";
        private bool _autoCheckInTried;

        /// <summary>
        /// 「当天第一次打开且还没签到」时自动弹一次签到窗。
        ///
        /// 判据是两段与：
        ///   ① 服务端说今天没签（<c>checkin.claimedToday == false</c>）
        ///   ② 本地今天还没自动弹过（PlayerPrefs 记日期）
        ///
        /// 为什么要本地记：服务端不知道客户端启动了几次。只按 ① 判断的话，
        /// 玩家一天里每次切后台回来都会弹一次，很快就变成骚扰。
        /// 记日期而不是记 bool：跨天自动失效，不用每天手动清标记。
        ///
        /// 只尝试一次（<c>_autoCheckInTried</c>）：<c>OnSessionChanged</c> 会被
        /// 买/喂/切场景频繁触发，不设这个闸门等于给每次操作都挂一次弹窗检查。
        /// </summary>
        private void TryAutoShowCheckIn()
        {
            if (_autoCheckInTried) return;

            // 领养流程开着时不弹：玩家还没有宠物，先让他把狗选完。
            // 两个弹窗叠在一起，签到那个会盖在选宠页上面，看着像程序乱了。
            if (_adoptFlow != null && _adoptFlow.IsOpen) return;

            var s = _session;
            // 等签到状态到位。false 时「没签」只是「还不知道」，不能当真的没签去弹窗。
            if (s == null || !s.Online || !s.HasCheckInStatus) return;

            _autoCheckInTried = true;

            if (s.CheckinClaimedToday) return;

            // 用服务端给的日期做标记，不用本地算的。
            // 设备时间被改到明天时，本地算会以为「新的一天」而再弹一次窗。
            string today = !string.IsNullOrEmpty(State.checkinServerDate)
                ? State.checkinServerDate
                : CheckInPopup.DateKey(CheckInPopup.TodayCst());
            if (PlayerPrefs.GetString(CheckInAutoPopupKey, "") == today) return;

            PlayerPrefs.SetString(CheckInAutoPopupKey, today);
            PlayerPrefs.Save();
            Debug.Log($"[PetV1] 今天（{today}）第一次启动且未签到，自动弹出签到窗。");
            _checkInPopup?.Show();
        }

        /// <summary>
        /// 把每帧驱动补回来。
        ///
        /// <see cref="PetUIRunner"/> 与 <see cref="PetHomeUI"/> 挂在同一个 Canvas 上，
        /// 但它的 <c>Home</c> 引用**不保证活得到运行期**：
        /// ① 自动属性的 backing field 不被序列化（已改成 SerializeField，见那边注释）；
        /// ② 场景文件可能是改这行代码之前构建的，字段还是空的。
        /// 两种情况的表现一样 —— Toast 弹出来不消失、切页面也没用、Console 零报错。
        ///
        /// 放 <see cref="Start"/> 里而不是 <see cref="Awake"/>：要晚于反序列化完成，
        /// 且早于第一帧 Update，窗口内不会漏掉一次 Tick。
        /// </summary>
        private void BindRunner()
        {
            var runner = GetComponent<PetUIRunner>();
            if (runner == null) runner = gameObject.AddComponent<PetUIRunner>();
            if (runner.Home != this) runner.Home = this;
        }

        /// <summary>
        /// 让隐藏页面的列表跑一次布局。
        ///
        /// uGUI 不为隐藏层级跑布局，所以列表在隐藏状态下重建后
        /// 行高一直是 0（自检读到的是 -20 这种反直觉值），射线一个都命不中。
        ///
        /// **只能对「即将显示的那一页」做，不要全页暖机。**
        /// 全页暖机（把所有页 SetActive(true) 排一遍再关回去）看着更彻底，
        /// 但它跑的时刻由 Start / 帧计数决定，而 CanvasScaler 把 Canvas 摆到
        /// 目标分辨率是在首帧之后：那一刻 Canvas 的 rect 还是 0×0，
        /// 算出来的行高必然是 0。之后页面又隐藏了，不会自动补正，
        /// 于是行高永远停在 -20 —— 症状是「列表显示正常但整页点不动」，
        /// 而且**在 Start 里怎么改都无效**，因为真正生效的时刻不在 Start 里。
        ///
        /// 挂在 SwitchTab 上就没有这个问题：
        /// 切页是用户操作-triggered，那时 Canvas 尺寸早已稳定，
        /// 且被激活的正是要显示的那一页。
        /// </summary>
        private void RelayoutPage(HomeTab tab)
        {
            if (!_pages.TryGetValue(tab, out var page)) return;
            PetPageBase.RelayoutTree(page.transform as RectTransform);
        }

        /// <summary>
        /// 建竖屏内容框并挂上 <see cref="PetPortraitGuard"/>。
        ///
        /// 框本身不带任何 Graphic（纯空节点），所以既不画东西也不吃射线 ——
        /// 宠物页要露出 3D 场景并接收触摸，多一层 Image 就等于多一块拦截板。
        /// 尺寸全部由 Guard 每帧按屏幕比例写，这里只建节点与接线。
        /// </summary>
        private RectTransform NewSafeFrame(RectTransform canvasRect)
        {
            var go = new GameObject("SafeFrame", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(canvasRect, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(PetPortraitGuard.DesignWidth, PetPortraitGuard.DesignHeight);
            rt.anchoredPosition = Vector2.zero;

            var scaler = canvasRect.GetComponent<CanvasScaler>();
            var guard = canvasRect.GetComponent<PetPortraitGuard>();
            if (guard == null) guard = canvasRect.gameObject.AddComponent<PetPortraitGuard>();

            // 转屏 / 改窗口尺寸后框会变，需要让当前页重排一次 ——
            // uGUI 不会为已排好的隐藏页补布局，且 SafeFrame 尺寸变了
            // 各页的 offset 关系也要跟着重算。
            guard.FrameResized -= OnFrameResized;
            guard.FrameResized += OnFrameResized;
            guard.Configure(scaler, rt);

            return rt;
        }

        private void OnFrameResized()
        {
            RelayoutPage(_current);
            var frame = transform.Find("SafeFrame") as RectTransform;
            if (frame != null) PetPageBase.RelayoutTree(frame);
        }

        /// <summary>
        /// 建一个铺满的分层容器。
        /// 返回 <see cref="RectTransform"/> 而不是 GameObject ——
        /// 下游（页面与弹窗的 Build）要的是 Transform 父节点，
        /// 返回 GameObject 会让每个调用点都要写 .transform。
        /// </summary>
        private static RectTransform NewLayer(string name, Transform parent, int order)
        {
            var go = PetUIFactory.NewPanel(name, parent);
            // uGUI 的绘制顺序 = 层级列表顺序，靠 AddComponent 的先后不直观，
            // 所以显式设 siblingIndex。乘 10 是给同层内部留出插入空间。
            go.transform.SetSiblingIndex(order * 10);
            return (RectTransform)go.transform;
        }

        private void BuildPages(Transform parent)
        {
            _petPage = new PetGrowthPage();
            _foodPage = new FoodPage();
            _shopPage = new ShopPage();
            _scenePage = new ScenePage();

            _petPage.Build(parent, this);
            _foodPage.Build(parent, this);
            _shopPage.Build(parent, this);
            _scenePage.Build(parent, this);

            _pages[HomeTab.Pet] = _petPage.Root;
            _pages[HomeTab.Food] = _foodPage.Root;
            _pages[HomeTab.Shop] = _shopPage.Root;
            _pages[HomeTab.Scene] = _scenePage.Root;
        }

        private void BuildTopBar(Transform parent)
        {
            var bar = PetUIFactory.NewPanel("TopBar", parent);
            var brt = (RectTransform)bar.transform;
            brt.anchorMin = new Vector2(0f, 1f);
            brt.anchorMax = new Vector2(1f, 1f);
            brt.pivot = new Vector2(0.5f, 1f);
            brt.offsetMin = new Vector2(0f, -PetUITheme.TopBarHeight);
            brt.offsetMax = new Vector2(0f, 0f);

            float top = PetUITheme.SafeTop + 8f;
            float d = PetUITheme.TopIconButton;
            float y = -(top + d * 0.5f);

            // ---- 左：宠物头像 + 等级胶囊 + 常驻经验条 ----
            // 展板第 2 页左上角是一个圆形狗头，紧挨着等级文字和一条黄色经验条。
            // 三者都在顶栏直接显示，不再收进浮层：经验是玩家最常看的数值，
            // 藏起来要点两次才看到，纯属多余一步。
            //
            // **垂直对齐的基准是「Lv.3 胶囊 + 经验条」这一组的中心**，头像中心
            // 要落在同一个 y 上（用户口径：爪爪图标与 Lv.3 和进度条同行居中）。
            //
            // 组的上下沿：组顶 = 胶囊顶 = pillY + PillH/2；
            // 组底 = 胶囊底 - 间距 - 条高 = pillY - PillH/2 - ExpGap - ExpH。
            // 跨度 = PillH + ExpGap + ExpH，组中心 = 组顶 - 跨度/2 = pillY - 11。
            // 胶囊和进度条自己不动（它们与右侧齿轮同高，动它们会引出新的不对齐），
            // 只把头像移到组中心。
            const float PillH = 56f;
            const float ExpH = 14f;
            const float ExpGap = 8f;
            float pillY = y;
            float expY = pillY - PillH * 0.5f - ExpGap - ExpH * 0.5f;
            float groupY = pillY + PillH * 0.5f - (PillH + ExpGap + ExpH) * 0.5f;

            var avatarBtn = PetUIFactory.NewIconButton(bar.transform, "pet", d,
                                                         PetUITheme.Glass, out var avatarIcon);
            avatarIcon.color = PetUITheme.Primary;
            var art = (RectTransform)avatarBtn.transform;
            art.anchorMin = art.anchorMax = new Vector2(0f, 1f);
            // pivot 用 (0, 0.5)：anchoredPosition.y 直接是**中心**。
            // 之前用 (0,1)（顶边）时，anchoredPosition.y 是顶边位置，
            // 按钮中心落在 y - 38，与等级组中心差 27px —— 看着就是「爪爪偏下」。
            art.pivot = new Vector2(0f, 0.5f);
            art.anchoredPosition = new Vector2(PetUITheme.PageMargin, groupY);
            // 头像只作身份标识，不接点击（等级信息已经直接显示了）。
            avatarBtn.onClick.RemoveAllListeners();

            // 等级胶囊贴在头像右侧，显示等级。
            // 圆角用胶囊半径（高的一半），不要用 d*0.5 —— 那是给正方形
            // 用的，胶囊比正方形矮，塞进去上下两端的圆角会被 9-slice 拉变形。
            var levelPill = PetUIFactory.NewSolid("LevelPill", bar.transform,
                                                    PetUITheme.Glass, 28f);
            var lprt = (RectTransform)levelPill.transform;
            lprt.anchorMin = lprt.anchorMax = new Vector2(0f, 1f);
            lprt.pivot = new Vector2(0f, 0.5f);
            lprt.sizeDelta = new Vector2(126f, PillH);
            lprt.anchoredPosition = new Vector2(PetUITheme.PageMargin + d + 10f, pillY);
            levelPill.raycastTarget = false;

            _levelText = PetUIFactory.NewText("Lv", lprt, "Lv.1", PetUITheme.FontBody,
                                              PetUITheme.TextPrimary, TextAnchor.MiddleCenter);
            PetUIFactory.Fill((RectTransform)_levelText.transform);

            // 经验条：胶囊正下方，200 宽（用户口径：要长，不要一小截）。
            // 左边缘与胶囊对齐，往右伸出 74 —— 和狗头一起看，
            // 顶上是一排信息（狗 + Lv + 条），底下一排导航，视觉上各占一条带。
            //
            // **NewProgressBar 返回 Fill、out 给 Track** —— 要摆尺寸的是 Track。
            _topExpFill = PetUIFactory.NewProgressBar(bar.transform, PetUITheme.ExpBar,
                                                      out var expTrack);
            expTrack.color = PetUITheme.Glass;
            var trt = (RectTransform)expTrack.transform;
            trt.anchorMin = trt.anchorMax = new Vector2(0f, 1f);
            trt.pivot = new Vector2(0f, 0.5f);
            trt.sizeDelta = new Vector2(200f, ExpH);
            trt.anchoredPosition = new Vector2(PetUITheme.PageMargin + d + 10f, expY);
            expTrack.raycastTarget = false;

            // ---- 右：积分胶囊 + 齿轮 ----
            // 积分用「心形图标 + 数字」，与展板右上角的红心一致。
            // 积分胶囊：圆角用胶囊半径 28（高的一半），不是 d*0.5=38。
            // 38 配 56 高会让上下两个圆角加起来超过高度（76>56），
            // 9-slice 中间被压成负数 → 上下边缘鼓出去变形。
            var pointsPill = PetUIFactory.NewSolid("PointsPill", bar.transform,
                                                    PetUITheme.Glass, 28f);
            var prt = (RectTransform)pointsPill.transform;
            prt.anchorMin = prt.anchorMax = new Vector2(1f, 1f);
            prt.pivot = new Vector2(1f, 0.5f);
            prt.sizeDelta = new Vector2(150f, 56f);
            // 积分胶囊的位置：**等级胶囊右边缘 → 齿轮左边缘**这条区间的 1/3 处
            // （从齿轮那头数）。用户口径：宠物等级 --- 积分 --- 设置，积分偏右。
            //
            // 写死成SafeFrame 局部坐标 732 而不是「相对齿轮的偏移」：
            // 两端都是布局定位出来的绝对量，写死一个数更直观；
            // 改尺寸时比例失真会在 Play 验证里立刻暴露（要看的是百分比，不是像素）。
            const float PointsCenterX = 732f;
            // pivot=(1,0.5) 时 anchoredPosition.x 指的是**右边缘**相对锚点的位置，
            // 中心 = 右边缘 - 胶囊宽的一半。
            float rightEdge = PointsCenterX + prt.sizeDelta.x * 0.5f;
            prt.anchoredPosition = new Vector2(rightEdge - 1080f, y);
            pointsPill.raycastTarget = false;     // 纯显示，不接点击

            var heart = PetIcons.NewIcon("heart", prt,
                                         new Vector2(38f, 38f), PetUITheme.Heart);
            var hrt = (RectTransform)heart.transform;
            hrt.anchorMin = hrt.anchorMax = new Vector2(0f, 0.5f);
            hrt.pivot = new Vector2(0f, 0.5f);
            hrt.anchoredPosition = new Vector2(16f, 0f);

            _pointsText = PetUIFactory.NewText("Value", prt, "500", PetUITheme.FontBody,
                                                PetUITheme.TextPrimary, TextAnchor.MiddleLeft);
            var vrt = (RectTransform)_pointsText.transform;
            vrt.anchorMin = new Vector2(0f, 0f);
            vrt.anchorMax = new Vector2(1f, 1f);
            vrt.pivot = new Vector2(0f, 0.5f);
            vrt.offsetMin = new Vector2(60f, 0f);
            vrt.offsetMax = new Vector2(-14f, 0f);

            // 齿轮：按用户口径，点它弹出宠物状态与属性（状态条与属性条都在浮层里）
            var gearBtn = PetUIFactory.NewIconButton(bar.transform, "gear", d,
                                                       PetUITheme.Glass, out var gearIcon);
            gearIcon.color = PetUITheme.TextPrimary;
            var grt = (RectTransform)gearBtn.transform;
            grt.anchorMin = grt.anchorMax = new Vector2(1f, 1f);
            grt.pivot = new Vector2(1f, 0.5f);
            grt.anchoredPosition = new Vector2(-PetUITheme.PageMargin, y);
            gearBtn.onClick.AddListener(() => _statusPopup?.Show());

            // 签到入口：积分胶囊左侧，与它同高。
            //
            // 图标暂用 star（签到 = 每日奖励），现有图标集里没有日历 ——
            // 新画一张要和另外 10 张同风格（纯白 RGB + alpha 质心居中），
            // 等美术资源到位再换，调用点只改这一处字符串。
            const float CheckInGap = 12f;
            float checkInRight = (PointsCenterX - prt.sizeDelta.x * 0.5f) - CheckInGap;
            var checkInBtn = PetUIFactory.NewIconButton(bar.transform, "star", d,
                                                         PetUITheme.Glass, out var checkInIcon);
            checkInIcon.color = PetUITheme.Coin;
            var cirt = (RectTransform)checkInBtn.transform;
            // anchor 必须和齿轮一样贴顶边（y=1）：y 变量是「相对顶边下移多少」，
            // 写成 (1,0.5) 会让它按顶栏中心偏移，按钮整体掉下去半个 y。
            cirt.anchorMin = cirt.anchorMax = new Vector2(1f, 1f);
            cirt.pivot = new Vector2(1f, 0.5f);
            cirt.anchoredPosition = new Vector2(checkInRight - 1080f, y);
            checkInBtn.onClick.AddListener(() => _checkInPopup?.Show());
        }

        /// <summary>
        /// 底部导航。四格，每格一个图标 + 文字。
        ///
        /// **底板透明**：宠物页底下是 3D 场景，白色导航条会把客厅地面齐腰切断，
        /// 像在一个网页上贴了条工具栏。改为「无底色 + 图标下方一小块半透明圆角衬底」，
        /// 既能读出选中态，又不遮挡场景。
        ///
        /// 图标由 <see cref="PetIcons"/> 程序化生成，不用美术资源。
        /// </summary>
        private void BuildBottomNav(Transform parent)
        {
            var bar = PetUIFactory.NewPanel("BottomNav", parent);
            var brt = (RectTransform)bar.transform;
            brt.anchorMin = new Vector2(0f, 0f);
            brt.anchorMax = new Vector2(1f, 0f);
            brt.pivot = new Vector2(0.5f, 0f);
            brt.offsetMin = new Vector2(0f, PetUITheme.SafeBottom);
            brt.offsetMax = new Vector2(0f, PetUITheme.SafeBottom + PetUITheme.NavBarHeight);

            // slot 按导航条**实际宽度**均分，不能用 PetUITheme.NavSlot 的硬编码值：
            // SafeFrame 恒 1080 宽但画布按屏幕缩放，导航条实测宽 1018，
            // 硬编码 270×4=1080 会让第4 格（商店/场景）溢出屏幕右边 62px，点不到。
            float slot = brt.rect.width / TabLabels.Length;
            if (slot <= 1f) slot = PetUITheme.NavSlot;   // 布局未跑时的兜底

            for (int i = 0; i < TabLabels.Length; i++)
            {
                // 闭包捕获的是本次循环的局部变量 tab（C# 5.0 之后 for
                // 的循环变量是每轮一份），所以不需要额外的 captured。
                var tab = HomeTab.Pet + i;
                float cx = slot * (i + 0.5f);

                // 选中态衬底：半透明圆角块，选中时显示，未选中时隐藏。
                // 用 SetActive 而不是改 alpha —— 隐藏的节点不参与布局与射线。
                //
                // **尺寸必须完整包住图标与文字**：之前是 104x104，
                // 而图标在顶部往下 36~84、标签在 92~120，标签底边越过了 104
                // —— 表现就是「字都超出背景了」。现在 150x120 全包住，
                // 顶栏往下留 4px，四周左右各留 20px（slot 270 的一半）。
                var pill = PetUIFactory.NewSolid("Pill", bar.transform,
                                                 PetUITheme.Glass, 26f);
                pill.raycastTarget = false;
                var prt = (RectTransform)pill.transform;
                prt.anchorMin = prt.anchorMax = new Vector2(0f, 1f);
                prt.pivot = new Vector2(0.5f, 1f);
                prt.sizeDelta = new Vector2(150f, 120f);
                prt.anchoredPosition = new Vector2(cx, -4f);

                var icon = PetIcons.NewIcon(TabIcons[i], bar.transform,
                                            new Vector2(PetUITheme.NavIcon, PetUITheme.NavIcon),
                                            PetUITheme.TextDisabled);
                var irt = (RectTransform)icon.transform;
                irt.anchorMin = irt.anchorMax = new Vector2(0f, 1f);
                irt.pivot = new Vector2(0.5f, 1f);
                irt.anchoredPosition = new Vector2(cx, -34f);

                var label = PetUIFactory.NewText("Label", bar.transform, TabLabels[i],
                                                 PetUITheme.FontSmall, PetUITheme.TextSecondary,
                                                 TextAnchor.UpperCenter);
                var lrt = (RectTransform)label.transform;
                lrt.anchorMin = lrt.anchorMax = new Vector2(0f, 1f);
                lrt.pivot = new Vector2(0.5f, 1f);
                // 宽度收成 Pill 同宽（150）而不是整个 slot（270），
                // 这样文字换行/对齐的参照系就是那块圆角底，不会看着跑偏。
                lrt.sizeDelta = new Vector2(150f, 28f);
                lrt.anchoredPosition = new Vector2(cx, -90f);

                // 整块可点（图标 + 文字各占一半，单独点文字点不到）。
                // Hit 必须铺满整格且是这一格最深的命中项：
                // Pill / Icon / Label 都不开射线，只有它接点击。
                var hit = PetUIFactory.NewHitArea("Hit", bar.transform);
                var hrt = (RectTransform)hit.transform;
                hrt.anchorMin = hrt.anchorMax = new Vector2(0f, 0f);
                // pivot 必须是下中点：用默认的居中 pivot 时，anchoredPosition.y=0
                // 表示「中心贴在导航条底边」，按钮会有一半落在条外——
                // 症状是「点导航条上半部分没反应，下半部分才切页」。
                hrt.pivot = new Vector2(0.5f, 0f);
                hrt.sizeDelta = new Vector2(slot, PetUITheme.NavBarHeight);
                // **x 必须是 slot*(i+0.5)**：anchor 在左下、pivot.x=0.5，
                // 所以 anchoredPosition.x 就是 Hit 的**中心**，得跟 Pill 的中心
                // （= cx = slot*(i+0.5)）对齐。写成 slot*i 会让整个 Hit 网格
                // 左移半个 slot —— 症状是「点某个标签总是选到隔壁那页」：
                // 实测 Pill 中心 127.3/381.8/636.3/890.8，而 Hit 覆盖
                // -127~127 / 127~382 / 382~636 / 636~891，恰好错开半格。
                hrt.anchoredPosition = new Vector2(cx, 0f);
                var btn = hit.gameObject.AddComponent<Button>();
                btn.targetGraphic = hit;
                btn.onClick.AddListener(() => SwitchTab(tab));

                _tabButtons.Add(btn);
                _tabPills.Add(pill);
                _tabIcons.Add(icon);
                _tabTexts.Add(label);
            }
        }

        /// <summary>切换页签。只改显隐，不重建。</summary>
        public void SwitchTab(HomeTab tab)
        {
            _current = tab;
            foreach (var kv in _pages)
            {
                if (kv.Key == tab) continue;
                kv.Value.SetActive(false);
            }
            if (_pages.TryGetValue(tab, out var page)) page.SetActive(true);

            // 页面刚显示时立刻重排这一页。理由见 RelayoutPage 的注释。
            RelayoutPage(tab);

            // 底导选中态。选中项显示半透明圆角衬底，未选中项不显示 ——
            // 用 SetActive 而不是改 alpha：隐藏的节点不参与射线，
            // 也不会因为颜色接近而误判为「点了没反应」。
            for (int i = 0; i < _tabButtons.Count; i++)
            {
                bool on = HomeTab.Pet + i == tab;
                if (_tabPills[i] != null) _tabPills[i].gameObject.SetActive(on);
                _tabIcons[i].color = on ? PetUITheme.Primary : PetUITheme.TextDisabled;
                _tabTexts[i].color = on ? PetUITheme.Primary : PetUITheme.TextSecondary;
            }

            // 宠物页要露出 3D 场景，其他三页盖住。
            if (_backdrop != null)
            {
                _backdrop.color = tab == HomeTab.Pet
                    ? new Color(0f, 0f, 0f, 0f)     // 透明，露出场景
                    : PetUITheme.PageFill;
            }

            TabChanged?.Invoke(tab);
        }

        /// <summary>全量刷新。数据变更后由页面自己调，或这里统一调。</summary>
        public void RefreshAll()
        {
            RefreshTopBar();
            _petPage?.Refresh();
            _foodPage?.Refresh();
            _shopPage?.Refresh();
            _scenePage?.Refresh();
            // 浮层开着时也要跟着刷新：喂食/购买改的是状态，
            // 玩家如果正开着面板，不刷新就会看到过期数值。
            if (_statusPopup != null && _statusPopup.IsOpen) _statusPopup.Refresh();
            if (_checkInPopup != null && _checkInPopup.IsOpen) _checkInPopup.Refresh();
            StateChanged?.Invoke();
        }

        private void RefreshTopBar()
        {
            var s = State;
            if (_levelText != null) _levelText.text = $"Lv.{s.level}";
            if (_pointsText != null) _pointsText.text = s.points.ToString();
            if (_topExpFill != null) _topExpFill.fillAmount = s.ExpProgress01;
        }

        /// <summary>数据变了：刷新全部 + 广播。页面内部改状态后走这里，不要直接 RefreshAll。</summary>
        public void NotifyStateChanged()
        {
            RefreshAll();
        }

        // ------------------------------------------------------------------
        // 供页面调用
        // ------------------------------------------------------------------

        public void ShowFoodDetail(FoodItemConfig food) => _detailPopup?.Show(food);

        /// <summary>打开状态/属性浮层。顶栏右上角的齿轮调它。</summary>
        public void ShowPetStatus() => _statusPopup?.Show();

        public void ShowToast(string msg) => _toast?.Show(msg);

        public ToastWidget Toast => _toast;

        /// <summary>
        /// 切换场景。走 <see cref="PetSceneStage.ApplyScene"/>，
        /// 由它按新 layout 重摆相机、灯光、活动区 —— 不要在 UI 侧直接换贴图。
        /// </summary>
        /// <summary>
        /// 切换场景。
        ///
        /// 走 <c>/api/v1/scene/use</c>：服务端确认 <c>activeScene</c> 之后客户端才切画面。
        /// **不能先切画面再发请求** —— 请求失败时画面已经换了，
        /// 玩家会以为自己切换成功了，下次进游戏又变回去。
        ///
        /// <paramref name="onDone"/> 成功与失败都会调用，调用方据此决定要不要提示。
        /// </summary>
        public void SwitchScene(string sceneId, System.Action onDone = null)
        {
            if (string.IsNullOrEmpty(sceneId))
            {
                onDone?.Invoke();
                return;
            }

            var session = _session;
            if (session == null || !session.Online)
            {
                ShowToast("没连上服务端，暂时不能切换场景");
                onDone?.Invoke();
                return;
            }

            session.UseScene(sceneId, (ok, msg) =>
            {
                if (!ok)
                {
                    if (!string.IsNullOrEmpty(msg)) ShowToast(msg);
                    onDone?.Invoke();
                    return;
                }
                // activeScene 已由 session 按服务端返回值写好，这里只管画面与重绘
                ApplySceneVisual(sceneId);
                onDone?.Invoke();
            });
        }

        /// <summary>只负责 3D 画面切换 + 本地快照刷新，不发网络请求。</summary>
        private void ApplySceneVisual(string sceneId)
        {
            if (stage != null) stage.ApplyScene(sceneId);
            State.activeScene = sceneId;
            RefreshAll();
        }
    }
}
