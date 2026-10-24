using System;
using PetV1.Model;
using UnityEngine;

namespace PetV1.Ads
{
    /// <summary>
    /// 广告入口。挂在 UI 同一个 GameObject 上（和 PetSession 一样由
    /// <see cref="PetHomeUI"/> 在 BindSession 时确保存在）。
    ///
    /// 职责只有两件：
    /// ① 按平台选 <see cref="IAdService"/> 实现（编辑器假 / 微信真）；
    /// ② 提供「播完再发奖」的业务方法，把播放结果转成一次 ad/reward 请求。
    ///
    /// **不自己算积分**：发奖一律走服务端 ad/reward（文档 6.2），
    /// 客户端只负责「放广告」和「把结果告诉后台」。
    /// </summary>
    [DisallowMultipleComponent]
    public class PetAdService : MonoBehaviour
    {
        private IAdService _impl;

        /// <summary>当前实现。编辑器下是假广告，微信小游戏下是真广告。</summary>
        public IAdService Impl
        {
            get
            {
                if (_impl == null) _impl = CreateImpl();
                return _impl;
            }
        }

        /// <summary>能不能放广告（供 UI 决定要不要放入口）。</summary>
        public bool Available => Impl.Available;

        /// <summary>播插屏。透传给当前实现，UI 不直接碰 IAdService。</summary>
        public void PlayInterstitial(Action<bool> onClosed) => Impl.PlayInterstitial(onClosed);

        /// <summary>播激励视频。透传给当前实现。</summary>
        public void PlayRewarded(string placement, Action<bool> onClosed)
            => Impl.PlayRewarded(placement, onClosed);

        private static IAdService CreateImpl()
        {
            // WebGL 且不在编辑器里 = 小游戏环境。其余一律假实现。
            if (Application.platform == RuntimePlatform.WebGLPlayer)
                return new WeChatAdService();
            return new EditorAdService();
        }

        /// <summary>
        /// 播激励视频，看完再由 <paramref name="claim"/> 去发奖。
        ///
        /// 为什么把发奖做成回调而不是自己发：发奖要带 requestId、要写状态，
        /// 那是 <see cref="PetV1.Data.PetSession"/> 的职责（整个工程唯一数据源）。
        /// 这里只回答一个问题：「玩家看完了没有」。
        ///
        /// </summary>
        /// <param name="placement">广告位，后台对账用。</param>
        /// <param name="claim">
        /// 播完回调。参数 = 是否看完；实现里发完奖再调 <paramref name="done"/>。
        /// </param>
        /// <param name="done">最终结果：ok + 给玩家看的文案。</param>
        public void PlayRewardedThenClaim(string placement,
                                          Action<bool, Action<bool, string>> claim,
                                          Action<bool, string> done)
        {
            Impl.PlayRewarded(placement, completed =>
            {
                if (!completed)
                {
                    // 提前关 / 拉取失败。不发奖，也不报错 ——
                    // 玩家主动关广告不是故障，弹一句就够。
                    done?.Invoke(false, "广告没看完，这次没有奖励");
                    return;
                }
                claim?.Invoke(true, done);
            });
        }
    }
}
