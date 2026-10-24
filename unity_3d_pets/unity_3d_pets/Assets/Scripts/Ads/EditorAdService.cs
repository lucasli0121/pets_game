using System;
using System.Collections;
using UnityEngine;

namespace PetV1.Ads
{
    /// <summary>
    /// 编辑器 / 非微信环境下的假广告。
    ///
    /// 它不播任何东西，只是等一小会儿然后回调，让**业务链路**能跑通：
    /// 点入口 → 等播放 → 回调 isEnded → 调 ad/reward → 余额刷新。
    /// 这些逻辑在真机上要另外调三个只有真机才暴露的问题（提前关闭、拉取失败、
    /// 次数上限），先用假实现把能验的都验掉。
    ///
    /// <see cref="ForceOutcome"/> 是给测试用的：设成 Skip / Fail 可以模拟
    /// 「玩家提前关广告」和「广告拉取失败」两条分支，不用真的去点。
    /// </summary>
    public class EditorAdService : IAdService
    {
        /// <summary>模拟播放时长（秒）。</summary>
        public float FakeDuration = 1.2f;

        /// <summary>强制回调结果。None = 正常看完。</summary>
        public enum Outcome { None, Skip, Fail }

        public Outcome ForceOutcome = Outcome.None;

        public bool Available => true;

        public void PlayInterstitial(Action<bool> onClosed)
        {
            Run(onClosed);
        }

        public void PlayRewarded(string placement, Action<bool> onClosed)
        {
            Run(onClosed);
        }

        private void Run(Action<bool> onClosed)
        {
            // 用协程等时长，模拟「广告正在播」这段时间里 UI 应该是什么样。
            // 挂在临时对象上，跑完即销毁，不占场景。
            var go = new GameObject("[FakeAd]");
            var driver = go.AddComponent<FakeAdDriver>();
            driver.Run(FakeDuration, ForceOutcome, ok =>
            {
                UnityEngine.Object.Destroy(go);
                onClosed?.Invoke(ok);
            });
        }

        private class FakeAdDriver : MonoBehaviour
        {
            private float _t;
            private float _dur;
            private EditorAdService.Outcome _outcome;
            private Action<bool> _done;

            public void Run(float duration, EditorAdService.Outcome outcome, Action<bool> done)
            {
                _t = 0f;
                _dur = duration;
                _outcome = outcome;
                _done = done;
            }

            private void Update()
            {
                _t += Time.deltaTime;
                if (_t < _dur) return;

                bool ok = _outcome switch
                {
                    EditorAdService.Outcome.Skip => false,
                    EditorAdService.Outcome.Fail => false,
                    _ => true,
                };
                var d = _done;
                _done = null;
                d?.Invoke(ok);
                UnityEngine.Object.Destroy(this);
            }
        }
    }
}
