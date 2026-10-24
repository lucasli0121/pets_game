using UnityEngine;
using UnityEngine.Rendering;

namespace PetV1.Pet
{
    /// <summary>
    /// 宠物脚下的椭圆半透明假阴影。
    ///
    /// PRD 4.5 硬约束：「宠物脚下必须有跟随移动的椭圆半透明假阴影，
    /// 否则 3D 宠物会显得漂浮在背景上」。
    ///
    /// 为什么需要它：场景是 2D 背景图 + 3D 宠物，背景上的阴影是画出来的，
    /// 而 3D 宠物没有接收阴影的地面（背景图不接受实时光照）。
    /// 没有这个假阴影，玩家的空间感知会认为狗浮在半空。
    ///
    /// 实现要点：
    /// - 用 Quad + 径向渐变贴图（不是纯色方块，边缘要柔）
    /// - 必须用 Unlit 材质，否则会被场景光照影响，白天黑夜场景里亮度不对
    /// - **放在背景 quad 与宠物之间的深度**，靠 renderQueue 控制
    /// - 随宠物移动，且朝向跟随宠物 Y 轴旋转（狗转身时影子椭圆方向跟着转）
    /// - 按跳跃高度淡出：狗跳起来时影子应变小变淡
    /// </summary>
    [DisallowMultipleComponent]
    public class PetFakeShadow : MonoBehaviour
    {
        [Header("Shape")]
        [Tooltip("影子宽度（世界单位）。默认按宠物体型给。")]
        [SerializeField] private float width = 0.6f;

        [Tooltip("影子长度（世界单位）。通常略短于宽度，让它呈椭圆。")]
        [SerializeField] private float length = 0.4f;

        [Header("Look")]
        [Tooltip("基础不透明度（0~1）。系数这么低是因为 shader 里还会乘一层径向衰减，"
                 + "边缘实际只有 1/3 左右。夜间场景可以调低。")]
        [SerializeField] private float maxOpacity = 0.45f;

        [Tooltip("影子颜色。深棕灰，不要纯黑 —— 纯黑在暖色背景上很脏。")]
        [SerializeField] private Color shadowColor = new Color(0.25f, 0.20f, 0.16f, 1f);

        [Tooltip("边缘柔化程度。0=硬边，1=完全弥散。"
                 + "别超过 0.6：shader 里 inner = 1 - softness，"
                 + "softness 越大实心核心越小，而 12° 俯角下能露出来的"
                 + "只有最外圈那一道 —— 核心一小，露出来的那圈就全是接近透明的值。")]
        [Range(0f, 0.6f)] [SerializeField] private float softness = 0.45f;

        [Header("Height Response")]
        [Tooltip("离开地面多少米后影子完全消失。0.6m 约等于狗跳到最高点。")]
        [SerializeField] private float fadeHeight = 0.6f;

        [Tooltip("高度变化时影子缩小到多少倍。跳得越高影子越小。")]
        [SerializeField] private float minScaleAtHeight = 0.4f;

        private MeshRenderer _renderer;
        private Material _material;
        private Transform _target;
        private float _groundY;
        private float _baseWidth;
        private float _baseLength;

        /// <summary>当前实际不透明度（供调试面板读）。</summary>
        public float CurrentOpacity { get; private set; }

        /// <summary>影子所在的世界坐标，供场景自检确认它确实在宠物脚下。</summary>
        public Vector3 ShadowWorldPosition => transform.position;

        /// <summary>
        /// 绑定到宠物。宠物移动时影子自动跟随。
        /// </summary>
        /// <param name="pet">宠物本体</param>
        /// <param name="groundY">
        /// 地面高度（世界 Y）。影子钉在这个高度，宠物跳起来时按
        /// <c>height - groundY</c> 计算离地高度，使影子变小变淡。
        /// </param>
        public void Attach(Transform pet, float groundY)
        {
            _target = pet;
            _groundY = groundY;
            _baseWidth = width;
            _baseLength = length;
            EnsureRenderer();
            ApplyTransform(0f);
        }

        private void LateUpdate()
        {
            if (_target == null) return;
            float height = Mathf.Max(0f, _target.position.y - _groundY);
            ApplyTransform(height);
        }

        private void ApplyTransform(float height)
        {
            if (_target == null) return;

            // 位置：跟随宠物 XZ，Y 钉在地面
            Vector3 pos = _target.position;
            pos.y = _groundY;
            transform.position = pos;

            // 朝向：跟随宠物 Y 轴旋转，狗转身时影子方向跟着转
            transform.rotation = Quaternion.Euler(90f, _target.eulerAngles.y, 0f);

            // 高度响应：越高越小越淡
            float t = fadeHeight <= 0f ? 0f : Mathf.Clamp01(height / fadeHeight);
            float scale = Mathf.Lerp(1f, minScaleAtHeight, t);
            transform.localScale = new Vector3(_baseWidth * scale, _baseLength * scale, 1f);

            float opacity = maxOpacity * (1f - t);
            CurrentOpacity = opacity;

            if (_material != null)
            {
                var c = shadowColor;
                c.a = opacity;
                _material.SetColor(ShadowColorId, c);
            }
        }

        private void EnsureRenderer()
        {
            if (_renderer == null) _renderer = GetComponent<MeshRenderer>();
            if (_renderer == null) _renderer = gameObject.AddComponent<MeshRenderer>();

            if (GetComponent<MeshFilter>() == null)
            {
                var mf = gameObject.AddComponent<MeshFilter>();
                mf.sharedMesh = BuildQuad();
            }

            if (_material == null)
            {
                _material = new Material(UnlitShader());
                _material.name = "PetFakeShadowMat(实例)";
                _material.SetFloat(SoftnessId, softness);
                // alpha 先给 maxOpacity 而不是0：材质创建到 LateUpdate 首次写值之间有若干帧，
                // 用 0 会让这些帧里影子完全透明（编辑器暂停/低帧率时能直接看到「阴影闪一下没了」）。
                _material.SetColor(ShadowColorId, new Color(shadowColor.r, shadowColor.g, shadowColor.b, maxOpacity));
                _material.renderQueue = (int)RenderQueue.Transparent;
                // 不写深度，避免与背景 quad 抢 ZTest 导致忽隐忽现
                _material.SetInt(ZWriteId, 0);
                _material.SetInt(ZTestId, (int)CompareFunction.LessEqual);
            }

            _renderer.sharedMaterial = _material;
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            // 排序在背景之后、宠物之前：背景 renderQueue=2000，影子 3000
            _renderer.sortingOrder = -1;
        }

        private static readonly int ShadowColorId = Shader.PropertyToID("_ShadowColor");
        private static readonly int SoftnessId = Shader.PropertyToID("_Softness");
        private static readonly int ZWriteId = Shader.PropertyToID("_ZWrite");
        private static readonly int ZTestId = Shader.PropertyToID("_ZTest");

        private Shader UnlitShader()
        {
            // URP 的 Unlit/Lit 都没有 _ShadowColor，需要用自定义 Shader。
            // 找不到就退回 Sprites/Default（它有 _Color，径向渐变靠贴图）。
            return Shader.Find("PetV1/FakeShadow")
                   ?? Shader.Find("Sprites/Default")
                   ?? Shader.Find("Universal Render Pipeline/Unlit");
        }

        private static Mesh BuildQuad()
        {
            var m = new Mesh { name = "FakeShadowQuad" };
            // 以原点为中心，XY 平面，法线朝 +Z（配合旋转 90 度变成朝上）
            m.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f),
            });
            m.SetUVs(0, new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(1f, 1f), new Vector2(0f, 1f),
            });
            m.SetNormals(new[]
            {
                Vector3.back, Vector3.back, Vector3.back, Vector3.back,
            });
            m.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            m.RecalculateBounds();
            return m;
        }

/// <summary>从场景配置应用外观参数。</summary>
        public void Configure(float shadowOpacity)
        {
        maxOpacity = Mathf.Clamp01(shadowOpacity);
       // 必须在写材质前确保材质存在。
   // 构建期的调用顺序是 FitToBody → Configure → Attach，
   // 而 _material 是在 Attach → EnsureRenderer 里才创建的，
    // 所以 Configure 先跑时 _material 是 null，写入全部静默丢失。
  EnsureRenderer();
      if (_material != null) _material.SetFloat(SoftnessId, softness);
            if (_renderer != null) _renderer.enabled = maxOpacity > 0.001f;
   }

        /// <summary>
        /// 按宠物体型设置影子尺寸（避免手填数值）。
        ///
        /// 系数不能拍脑袋，必须满足一个硬条件：
        /// <b>影子要在狗的投影轮廓之外露出来，否则等于没有。</b>
        ///
        /// 12° 俯角下，地面上的影子在画面纵向被压缩到 sin(12°) ≈ 0.21 倍，
        /// 所以「长度大于狗身长」几乎不会带来可见像素 ——
        /// 唯一能读出来的方向是<em>横向</em>（X 不被压缩）。
        /// 因此宽度系数必须明显大于 1，让影子从躯干两侧探出来。
        ///
        /// 之前 width 用 1.35 恰好等于狗的实测 X 跨度（0.42 × 1.35 = 0.567m
        /// 与 bounds 的 X 跨度完全相同），Z 用 0.55 又小于狗的 1.27m，
        /// 结果整个椭圆严丝合缝地藏在躯干投影里，屏幕上零像素，
        /// 渲染参数却全是正常的（isVisible=True、alpha=0.32、Cull Off 已生效）。
        /// </summary>
        public void FitToBody(float bodyWidth, float bodyLength)
        {
            width = Mathf.Max(0.1f, bodyWidth * WidthFactor);
            length = Mathf.Max(0.08f, bodyLength * LengthFactor);
            _baseWidth = width;
            _baseLength = length;
            transform.localScale = new Vector3(width, length, 1f);
        }

        /// <summary>宽度系数。要让影子从躯干两侧探出，必须明显大于 1。</summary>
        public const float WidthFactor = 1.85f;

        /// <summary>
        /// 长度系数。大于 1 保证前端（朝向 -Z、靠相机那侧）探出前爪，
        /// 但因为俯角压缩，视觉收益很小，主要靠宽度出效果。
        /// </summary>
        public const float LengthFactor = 1.5f;

        private void OnDestroy()
        {
            // 材质是运行时 new 的，必须销毁，否则每次重建场景都泄漏一个
            if (_material != null) Destroy(_material);
            var mf = GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && mf.sharedMesh.name == "FakeShadowQuad")
            {
                Destroy(mf.sharedMesh);
            }
        }
    }
}