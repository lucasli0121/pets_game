using System;
using System.Collections.Generic;
using PetV1.Core;
using UnityEngine;

namespace PetV1.Interaction
{
    using PetV1.Pet;

    /// <summary>
    /// 手势识别器（纯逻辑，不依赖 MonoBehaviour）。输入是逐帧的指针流，输出是结构化 <see cref="GestureEvent"/>。
    /// 阈值全部走 <see cref="Config"/>，可在 DebugPanel 上调；识别规则对应需求文档 2.3 与第六节。
    ///
    /// 单指：
    ///   duration &lt;= tapMaxDuration 且 distance &lt;= tapMaxDistance   -> Tap（窗口内重复 -> DoubleTap）
    ///   duration >= longPressDuration 且 distance <= longPressMaxDistance -> LongPress
    ///   avgSpeed >= fastSpeed                                          -> FastSwipe
    ///   distance >= strokeMinDistance                                  -> Stroke（抚摸）
    /// 多指（fingerCount >= multiTouchMinFingers）：
    ///   反转次数 >= scratchMinReversals 且总位移不过大   -> MultiScratch
    ///   时长短且位移小                              -> MultiTap
    ///   按住不动                                    -> MultiHold
    ///   其余                                        -> MultiScratch
    ///
    /// 未命中宠物时按需求文档第九节改写为 SCREEN_* 事件。
    /// </summary>
    public class GestureRecognizer
    {
        /// <summary>可调阈值。</summary>
        [Serializable]
        public class Config
        {
            [Header("Tap")]
            [Tooltip("点击最长时长（秒）。")]
            public float tapMaxDuration = 0.30f;

            [Tooltip("点击最大位移（像素）。")]
            public float tapMaxDistance = 24f;

            [Tooltip("双击的两次间隔上限（秒）。")]
            public float doubleTapWindow = 0.28f;

            [Header("Long Press")]
            [Tooltip("长按判定时长（秒）。")]
            public float longPressDuration = 0.70f;

            [Tooltip("长按时允许的最大位移（像素）。")]
            public float longPressMaxDistance = 20f;

            [Header("Stroke / Swipe")]
            [Tooltip("判定为抚摸所需的最小位移（像素）。")]
            public float strokeMinDistance = 26f;

            [Tooltip("高于此平均速度算快速划动（像素/秒）。")]
            public float fastSpeed = 900f;

            [Header("Multi Touch")]
            [Tooltip("判定为多指的最小手指数。")]
            public int multiTouchMinFingers = 2;

            [Tooltip("多指挠屏的最小方向反转次数。")]
            public int scratchMinReversals = 3;

            [Tooltip("多指挠屏的总位移上限（像素），超过说明是整体划动而非反复挠。")]
            public float scratchMaxTotalDistance = 900f;

            [Tooltip("多指最短持续时间（秒），超过则不算多点轻点。")]
            public float multiTapMaxDuration = 0.28f;

            [Header("Screen")]
            [Tooltip("未命中宠物时，屏幕级手势是否也要上报。")]
            public bool emitScreenGestures = true;
        }

        /// <summary>单指跟踪状态。</summary>
        private class PointerTrack
        {
            public int id;
            public Vector2 start;
            public Vector2 current;
            public float startTime;
            public float lastSampleTime;
            public float totalDistance;
            public float peakSpeed;
            public bool hitPet;
            public PetRegion region = PetRegion.Unknown;
            public bool finished;
            public readonly List<Vector2> trajectory = new List<Vector2>(32);

            private Vector2 lastSample;
            private bool hasLastSample;

            public float Duration(float now) => Mathf.Max(0f, now - startTime);
            public float DistanceFromStart => Vector2.Distance(start, current);
            public float AverageSpeed(float now)
            {
                float d = Duration(now);
                return d > 0.0001f ? totalDistance / d : 0f;
            }

            /// <summary>方向反转次数：来回挠的特征。阈值 0.3 表示与上一段方向夹角超过约 107 度。</summary>
            public int CountReversals()
            {
                if (trajectory.Count < 3) return 0;
                int reversals = 0;
                Vector2 lastDir = Vector2.zero;
                for (int i = 1; i < trajectory.Count; i++)
                {
                    Vector2 dir = trajectory[i] - trajectory[i - 1];
                    if (dir.sqrMagnitude < 4f) continue;
                    dir.Normalize();
                    if (lastDir != Vector2.zero && Vector2.Dot(dir, lastDir) < -0.3f) reversals++;
                    lastDir = dir;
                }
                return reversals;
            }

            public void Sample(Vector2 position, float now, bool isFirst)
            {
                current = position;
                if (isFirst)
                {
                    start = position;
                    lastSample = position;
                    hasLastSample = true;
                    startTime = now;
                    lastSampleTime = now;
                    trajectory.Clear();
                    trajectory.Add(position);
                    return;
                }

                if (!hasLastSample)
                {
                    lastSample = position;
                    hasLastSample = true;
                }

                float step = Vector2.Distance(lastSample, position);
                if (step > 0f)
                {
                    totalDistance += step;
                    float dt = Mathf.Max(0.0001f, now - lastSampleTime);
                    float instant = step / dt;
                    if (instant > peakSpeed) peakSpeed = instant;
                }
                lastSample = position;
                lastSampleTime = now;

                if (trajectory.Count == 0 || (trajectory[trajectory.Count - 1] - position).sqrMagnitude > 4f)
                    trajectory.Add(position);
            }

            /// <summary>生成不可变事件快照。</summary>
            public GestureEvent ToEvent(float now, int fingerCount)
            {
                float duration = Duration(now);
                var ev = new GestureEvent
                {
                    fingerCount = fingerCount,
                    durationMs = Mathf.RoundToInt(duration * 1000f),
                    distance = DistanceFromStart,
                    averageSpeed = AverageSpeed(now),
                    peakSpeed = peakSpeed,
                    hitPet = hitPet,
                    targetRegion = region,
                    startPosition = start,
                    currentPosition = current,
                    delta = current - start,
                    reversalCount = CountReversals(),
                };
                ev.trajectory.AddRange(trajectory);
                return ev;
            }
        }

        private readonly Dictionary<int, PointerTrack> _tracks = new Dictionary<int, PointerTrack>();
        private Config _config = new Config();

        private float _lastTapTime = -10f;
        private PetRegion _lastTapRegion = PetRegion.Unknown;

        /// <summary>手势上报事件。TouchInputManager 订阅并转给 BehaviorController。</summary>
        public event Action<GestureEvent> GestureRecognized;

        public Config Configuration
        {
            get => _config;
            set => _config = value ?? new Config();
        }

        public int ActivePointerCount => _tracks.Count;

        public void BeginPointer(int id, Vector2 position, float now, bool hitPet, PetRegion region)
        {
            var track = new PointerTrack
            {
                id = id,
                hitPet = hitPet,
                region = region,
            };
            track.Sample(position, now, true);
            _tracks[id] = track;
            PetLog.Touch("finger down",
                PetLog.Kv("fingerId", id, "fingerCount", _tracks.Count,
                          "hitPet", hitPet, "region", region, "pos", position.ToString()));
        }

        public void MovePointer(int id, Vector2 position, float now)
        {
            if (!_tracks.TryGetValue(id, out var track) || track.finished) return;
            track.Sample(position, now, false);
        }

        public void EndPointer(int id, Vector2 position, float now, bool canceled = false)
        {
            if (!_tracks.TryGetValue(id, out var track) || track.finished) return;
            track.Sample(position, now, false);
            track.finished = true;
            PetLog.Touch("finger up",
                PetLog.Kv("fingerId", id, "canceled", canceled,
                          "duration", Mathf.Round(track.Duration(now) * 1000),
                          "distance", Mathf.RoundToInt(track.DistanceFromStart),
                          "avgSpeed", Mathf.RoundToInt(track.AverageSpeed(now)),
                          "peakSpeed", Mathf.RoundToInt(track.peakSpeed),
                          "reversals", track.CountReversals()));
        }

        /// <summary>每帧末尾调用：把已结束的手指归类成手势并上报。仍在按下的手指继续跟踪。</summary>
        public void Tick(float now)
        {
            // 1) 先收集本帧结束的手指
            List<int> finishedIds = null;
            foreach (var kv in _tracks)
            {
                if (!kv.Value.finished) continue;
                finishedIds ??= new List<int>(4);
                finishedIds.Add(kv.Key);
            }

            if (finishedIds == null) return;

            int totalFingersAtLift = finishedIds.Count;

            if (totalFingersAtLift >= _config.multiTouchMinFingers)
            {
                Emit(BuildMultiTouch(finishedIds, now, totalFingersAtLift));
            }
            else
            {
                foreach (int id in finishedIds)
                {
                    var track = _tracks[id];
                    GestureEvent ev = track.ToEvent(now, 1);
                    _tracks.Remove(id);
                    Emit(RecognizeSingle(ev, now));
                }
            }
        }

        private GestureEvent BuildMultiTouch(List<int> ids, float now, int fingerCount)
        {
            float totalDistance = 0f;
            float maxDistance = 0f;
            int reversals = 0;
            float duration = 0f;
            bool anyHitPet = false;
            PetRegion region = PetRegion.Unknown;
            Vector2 start = Vector2.zero;
            Vector2 current = Vector2.zero;
            var merged = new List<Vector2>(32);

            foreach (int id in ids)
            {
                var track = _tracks[id];
                totalDistance += track.DistanceFromStart;
                if (track.DistanceFromStart > maxDistance) maxDistance = track.DistanceFromStart;
                reversals = Mathf.Max(reversals, track.CountReversals());
                duration = Mathf.Max(duration, track.Duration(now));
                anyHitPet |= track.hitPet;
                if (region == PetRegion.Unknown) region = track.region;
                start = track.start;
                current = track.current;
                merged.AddRange(track.trajectory);
            }

            GestureType type;
            if (reversals >= _config.scratchMinReversals && totalDistance <= _config.scratchMaxTotalDistance)
                type = GestureType.MultiScratch;
            else if (duration <= _config.multiTapMaxDuration && maxDistance <= _config.tapMaxDistance)
                type = GestureType.MultiTap;
            else if (duration >= _config.longPressDuration && maxDistance <= _config.longPressMaxDistance)
                type = GestureType.MultiHold;
            else
                type = GestureType.MultiScratch;

            var ev = new GestureEvent
            {
                gesture = type,
                fingerCount = fingerCount,
                durationMs = Mathf.RoundToInt(duration * 1000f),
                distance = totalDistance,
                averageSpeed = duration > 0.0001f ? totalDistance / duration : 0f,
                peakSpeed = maxDistance,
                reversalCount = reversals,
                hitPet = anyHitPet,
                targetRegion = region,
                startPosition = start,
                currentPosition = current,
            };
            ev.trajectory.AddRange(merged);
            return ev;
        }

        private GestureEvent RecognizeSingle(GestureEvent partial, float now)
        {
            float duration = partial.durationMs / 1000f;
            GestureType type;

            if (duration >= _config.longPressDuration && partial.distance <= _config.longPressMaxDistance)
            {
                type = GestureType.LongPress;
            }
            else if (duration <= _config.tapMaxDuration && partial.distance <= _config.tapMaxDistance)
            {
                if (now - _lastTapTime <= _config.doubleTapWindow && _lastTapRegion == partial.targetRegion)
                {
                    type = GestureType.DoubleTap;
                    _lastTapTime = -10f;
                }
                else
                {
                    type = GestureType.Tap;
                    _lastTapTime = now;
                    _lastTapRegion = partial.targetRegion;
                }
            }
            else if (partial.averageSpeed >= _config.fastSpeed)
            {
                type = GestureType.FastSwipe;
            }
            else if (partial.distance >= _config.strokeMinDistance)
            {
                type = GestureType.Stroke;
            }
            else if (partial.peakSpeed >= _config.fastSpeed)
            {
                type = GestureType.FastSwipe;
            }
            else
            {
                // 没到点击时长、也没达到移动距离：按下后中途停住。按长按处理更符合直觉。
                type = duration >= _config.longPressDuration ? GestureType.LongPress : GestureType.Tap;
            }

            partial.gesture = type;
            return partial;
        }

        private void Emit(GestureEvent ev)
        {
            if (ev == null || ev.gesture == GestureType.None) return;

            if (!ev.hitPet)
            {
                if (!_config.emitScreenGestures) return;
                switch (ev.gesture)
                {
                    case GestureType.Tap:
                    case GestureType.DoubleTap:
                        ev.gesture = GestureType.ScreenTap;
                        break;
                    case GestureType.FastSwipe:
                    case GestureType.Stroke:
                        ev.gesture = GestureType.ScreenSwipe;
                        break;
                    case GestureType.MultiScratch:
                        ev.gesture = GestureType.ScreenScratch;
                        break;
                    case GestureType.MultiTap:
                    case GestureType.MultiHold:
                        ev.gesture = GestureType.ScreenMultiTouch;
                        break;
                }
            }

            PetLog.Gesture("recognized",
                PetLog.Kv("gesture", ev.gesture,
                          "region", ev.targetRegion,
                          "finger_count", ev.fingerCount,
                          "duration_ms", ev.durationMs,
                          "distance", Mathf.RoundToInt(ev.distance),
                          "speed", Mathf.RoundToInt(ev.averageSpeed),
                          "hit_pet", ev.hitPet,
                          "json", ev.ToJson()));

            GestureRecognized?.Invoke(ev);
        }

        public void Reset()
        {
            _tracks.Clear();
            _lastTapTime = -10f;
            _lastTapRegion = PetRegion.Unknown;
        }
    }
}