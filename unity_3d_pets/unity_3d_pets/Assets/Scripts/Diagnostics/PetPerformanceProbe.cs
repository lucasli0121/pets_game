using PetV1.Animation;
using PetV1.Core;
using PetV1.Pet;
using UnityEngine;

namespace PetV1.Diagnostics
{
    /// <summary>
    /// PerformanceTest 场景的指标采集（需求文档第十七节）。
    /// 每秒向 Console 输出一次 FPS / 内存 / 对象数，便于在真机日志里直接读。
    /// </summary>
    public class PetPerformanceProbe : MonoBehaviour
    {
        [SerializeField] private float sampleWindow = 1f;

        [SerializeField] private bool logEveryWindow = true;

        private float _fpsAccum;
        private int _fpsFrames;
        private float _elapsed;

        public float LastFps { get; private set; }

        public void Configure(bool log = true)
        {
            logEveryWindow = log;
            _fpsAccum = 0f;
            _fpsFrames = 0;
            _elapsed = 0f;
        }

        private void Update()
        {
            _fpsAccum += Time.unscaledDeltaTime;
            _fpsFrames++;
            _elapsed += Time.unscaledDeltaTime;
            if (_elapsed < sampleWindow) return;

            LastFps = _fpsFrames / Mathf.Max(0.0001f, _fpsAccum);

            if (logEveryWindow)
            {
                long mono = System.GC.GetTotalMemory(false);
                long total = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
                int transforms = FindObjectsByType<Transform>(FindObjectsInactive.Include).Length;
                Debug.Log($"[Perf] fps={LastFps:F1} monoUsed={mono / (1024 * 1024)}MB " +
                          $"totalAlloc={total / (1024 * 1024)}MB transforms={transforms}");
            }

            _fpsAccum = 0f;
            _fpsFrames = 0;
            _elapsed = 0f;
        }
    }
}
