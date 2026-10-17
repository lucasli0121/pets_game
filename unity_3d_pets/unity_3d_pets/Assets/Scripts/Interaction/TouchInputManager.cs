using PetV1.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using EnhancedTouchSupport = UnityEngine.InputSystem.EnhancedTouch.EnhancedTouchSupport;
using EnhancedTouch = UnityEngine.InputSystem.EnhancedTouch;
using TouchPhaseNew = UnityEngine.InputSystem.TouchPhase;

namespace PetV1.Interaction
{
    using PetV1.Pet;

    /// <summary>
    /// 触摸输入管理器。需求文档 2.3 / 第六节的输入层。
    /// 职责：采集屏幕指针 -> 射线判定是否命中宠物及命中哪个区域 -> 喂给 <see cref="GestureRecognizer"/>。
    /// 它不产出任何行为，只产出 <see cref="GestureEvent"/>。
    ///
    /// 本工程 ProjectSettings 的 activeInputHandler = 1（仅新Input System），
    /// 因此使用 UnityEngine.InputSystem.EnhancedTouch，不使用会抛异常的旧版 Input.touches。
    /// 编辑器下用鼠标模拟单指，便于单机调试；真实多指必须在手机上测。
    /// </summary>
    [DisallowMultipleComponent]
    public class TouchInputManager : MonoBehaviour
    {
        [Header("Raycast")]
        [Tooltip("用于判定触摸是否命中宠物的 Layer。留空表示使用全部可射线检测的层。")]
        [SerializeField] private LayerMask petLayers = 0;

        [Tooltip("摄像机。为空时使用 Camera.main。")]
        [SerializeField] private Camera targetCamera;

        [Header("Gesture Config")]
        [SerializeField] private GestureRecognizer.Config gestureConfig = new GestureRecognizer.Config();

        [Header("Debug")]
        [Tooltip("打印每帧指针数量。排查多指识别问题时打开。")]
        [SerializeField] private bool logPointerCountPerFrame = false;

        private GestureRecognizer _recognizer;
        private const int MousePointerId = -1000;

        /// <summary>手势事件出口。BehaviorController 订阅它。</summary>
        public event System.Action<GestureEvent> GestureDetected;

        public GestureRecognizer Recognizer => _recognizer;
        public GestureRecognizer.Config Config => gestureConfig;

        private void Awake()
        {
            _recognizer = new GestureRecognizer { Configuration = gestureConfig };
            _recognizer.GestureRecognized += OnGestureRecognized;
        }

        private void OnDestroy()
        {
            if (_recognizer != null) _recognizer.GestureRecognized -= OnGestureRecognized;
        }

        private void OnEnable()
        {
            // EnhancedTouch 必须显式启用，否则 activeTouches 永远为空。
            EnhancedTouchSupport.Enable();
        }

        private void OnDisable()
        {
            EnhancedTouchSupport.Disable();
        }

        private void Update()
        {
            if (_recognizer == null) return;
            float now = Time.unscaledTime;

            ProcessTouches(now);
            ProcessMouse(now);

            _recognizer.Tick(now);

            if (logPointerCountPerFrame)
                PetLog.Touch("frame", PetLog.Kv("activePointers", _recognizer.ActivePointerCount));
        }

        private void ProcessTouches(float now)
        {
            if (Touchscreen.current == null) return;

            var touches = EnhancedTouch.Touch.activeTouches;
            int count = touches.Count;
            for (int i = 0; i < count; i++)
            {
                var touch = touches[i];
                int id = touch.touchId;
                Vector2 pos = touch.screenPosition;

                switch (touch.phase)
                {
                    case TouchPhaseNew.Began:
                    {
                        TryResolvePet(pos, out var region, out bool hitPet);
                        _recognizer.BeginPointer(id, pos, now, hitPet, region);
                        break;
                    }
                    case TouchPhaseNew.Moved:
                    case TouchPhaseNew.Stationary:
                        _recognizer.MovePointer(id, pos, now);
                        break;
                    case TouchPhaseNew.Ended:
                        _recognizer.MovePointer(id, pos, now);
                        _recognizer.EndPointer(id, pos, now);
                        break;
                    case TouchPhaseNew.Canceled:
                        _recognizer.EndPointer(id, pos, now, true);
                        break;
                }
            }
        }

        private void ProcessMouse(float now)
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            // EnhancedTouchSupport.Enable() 会把鼠标也模拟成一次触摸，
            // 所以触屏路径已经处理过鼠标了。这里必须整段跳过，
            // 否则同一个物理按下会被上报两次（Touchscreen 一次 + Mouse 一次），
            // 产生一个 TouchPhase.Canceled，手势被中途取消 -> 点击/拖动都识别不出来。
            if (Touchscreen.current != null) return;

            Vector2 pos = mouse.position.ReadValue();
            if (mouse.leftButton.wasPressedThisFrame)
            {
                TryResolvePet(pos, out var region, out bool hitPet);
                _recognizer.BeginPointer(MousePointerId, pos, now, hitPet, region);
            }
            else if (mouse.leftButton.isPressed)
            {
                _recognizer.MovePointer(MousePointerId, pos, now);
            }
            else if (mouse.leftButton.wasReleasedThisFrame)
            {
                _recognizer.MovePointer(MousePointerId, pos, now);
                _recognizer.EndPointer(MousePointerId, pos, now);
            }
        }

        /// <summary>
        /// 射线检测是否命中宠物，并解析身体区域。需求文档 6.1。
        /// </summary>
        /// <param name="rayHitSomething">射线是否打到了任何 Collider（含墙/地板）。
        /// 打到了非宠物物体时仍返回 true，但 <paramref name="hitPet"/> 为 false。</param>
        /// <param name="hitPet">是否真的打到了宠物身上的 Collider
        ///（通过 GetComponentInParent&lt;PetRegionCollider&gt; 确认）。</param>
        private bool TryResolvePet(Vector2 screenPos, out PetRegion region, out bool hitPet)
        {
            region = PetRegion.Unknown;
            hitPet = false;
            var cam = targetCamera != null ? targetCamera : Camera.main;
            if (cam == null) return false;

            Ray ray = cam.ScreenPointToRay(screenPos);

            // petLayers 留空时用 ~0（全部层）。注意 Physics.defaultRaycastLayers 在 Unity 6 已移除。
            int mask = petLayers.value != 0 ? petLayers.value : ~0;

            if (!Physics.Raycast(ray, out var hit, 500f, mask, QueryTriggerInteraction.Ignore))
                return false;

            // 必须确认打到的确实是宠物身上的 Collider。
            // 原来只要射线有命中就当命中，而墙 / 地面也都在默认层里，
            // 于是点击墙面会被判成「点到了狗」——日志里能直接看到
            // `raycast hit collider=Wall_South, region=Body`。
            var tag = hit.collider.GetComponentInParent<PetRegionCollider>();
            if (tag == null)
            {
                PetLog.Touch("raycast missed pet",
                    PetLog.Kv("collider", hit.collider.name,
                              "layer", LayerMask.LayerToName(hit.collider.gameObject.layer)));
                region = PetRegion.Unknown;
                return true;
            }

            hitPet = true;
            region = tag.Region;

            PetLog.Touch("raycast hit",
                PetLog.Kv("collider", hit.collider.name, "region", region,
                          "distance", Mathf.Round(hit.distance * 100f) / 100f));
            return true;
        }

        private void OnGestureRecognized(GestureEvent ev)
        {
            GestureDetected?.Invoke(ev);
        }

        /// <summary>DebugPanel 调阈值时用。</summary>
        public void ApplyConfig(GestureRecognizer.Config config)
        {
            gestureConfig = config;
            if (_recognizer != null) _recognizer.Configuration = config;
        }

        /// <summary>供 DebugPanel 强制触发一个手势（不经过真实触摸）。</summary>
        public void InjectSyntheticGesture(GestureType type, int fingerCount = 1,
            PetRegion region = PetRegion.Body, bool hitPet = true)
        {
            var ev = new GestureEvent
            {
                gesture = type,
                fingerCount = fingerCount,
                targetRegion = region,
                hitPet = hitPet,
                durationMs = 350,
                distance = fingerCount > 1 ? 60f : 0f,
                averageSpeed = 120f,
                startPosition = new Vector2(400f, 400f),
                currentPosition = new Vector2(460f, 400f),
            };
            PetLog.Touch("synthetic gesture injected",
                PetLog.Kv("gesture", ev.gesture, "fingerCount", fingerCount, "hitPet", hitPet));
            OnGestureRecognized(ev);
        }
    }
}