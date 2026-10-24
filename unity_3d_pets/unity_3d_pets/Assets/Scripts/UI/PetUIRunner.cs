using UnityEngine;

namespace PetV1.UI
{
    /// <summary>
    /// UI 的每帧驱动。只有 Toast 淡出需要逐帧，所以做成一个极小的 Runner，
    /// 挂在 Canvas 上。
    ///
    /// 为什么不让 Toast 自己是个 MonoBehaviour：页面与控件都是纯类
    /// （见 <see cref="PetPageBase"/> 的理由），突然混进一个 MonoBehaviour 会让
    /// 「哪些节点需要 Unity 生命周期」这条边界变模糊。这个 Runner 是唯一的例外，
    /// 它是明确的、单一的。
    ///
    /// 注意用的是 unscaledTime：弹窗打开时 timeScale 可能被置 0。
    /// </summary>
    [DisallowMultipleComponent]
    public class PetUIRunner : MonoBehaviour
    {
        [Tooltip("UI 根。由 PetHomeSceneBuilder 在构建期写入。")]
        [SerializeField] private PetHomeUI home;

        /// <summary>
        /// UI 根。
        ///
        /// **必须走 SerializeField，不能用自动属性**：自动属性的 backing field 是
        /// 编译器生成的私有字段，Unity 不序列化它。构建期写的 `runner.Home = home`
        /// 进 Play 反序列化后是 null，于是 <c>Update</c> 里 <c>Home?.Toast?.Tick()</c>
        /// 整条 null 短路，<see cref="ToastWidget.Tick"/> 永远不被调用 ——
        /// 提示框弹得出来，但到死都不消失，切页面、点哪儿都没用，且 Console 零报错。
        /// </summary>
        public PetHomeUI Home
        {
            get => home;
            set => home = value;
        }

        private void Update()
        {
            Home?.Toast?.Tick();
        }
    }
}
