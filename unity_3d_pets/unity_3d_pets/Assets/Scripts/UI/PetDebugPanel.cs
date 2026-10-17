using System.Text;
using PetV1.Animation;
using PetV1.Behavior;
using PetV1.Core;
using PetV1.Interaction;
using UnityEngine;

namespace PetV1.UI
{
    using PetV1.Pet;

    /// <summary>
    /// 调试面板。需求文档第十五节：必须显示
    /// CurrentState / CurrentBehavior / CurrentAnimation / Emotion / Personality / LastGesture / LastEvent，
    /// 并提供 Idle/Walk/Run、Touch/Scratch、Reset 测试按钮。
    ///
    /// 用 IMGUI 而不是 UGUI Canvas：原型阶段要的是「拖进场景就能用」，不引入 UI 资源依赖，
    /// 也不会因为 Canvas 引用丢失导致面板消失。正式 UI 阶段再换 UGUI/UI Toolkit。
    /// </summary>
    [DisallowMultipleComponent]
    public class PetDebugPanel : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private PetBehaviorController behaviorController;
        [SerializeField] private AnimationOrchestrator animationOrchestrator;
        [SerializeField] private TouchInputManager touchInputManager;

        [Header("Visibility")]
        [SerializeField] private bool visible = true;

        [Tooltip("面板开关按键（` 反引号）。")]
        [SerializeField] private UnityEngine.InputSystem.Key toggleKey = UnityEngine.InputSystem.Key.Backquote;

        [Header("Layout")]
        [SerializeField] private Vector2 panelOffset = new Vector2(12f, 12f);
        [SerializeField] private float width = 380f;

        [Header("Gesture Tuning")]
        [Tooltip("显示手势阈值调节区，便于真机调参。")]
        [SerializeField] private bool showGestureTuning = true;

        private GUIStyle _boxStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _labelStyle;
        private readonly StringBuilder _sb = new StringBuilder(512);

        // FPS 采样
        private float _fps;
        private float _fpsAccum;
        private int _fpsFrames;
        private float _minFps = 999f;
        private float _maxFps;
        private float _sampleTimer;

        private static string[] s_behaviorNames = { "Idle", "Walk", "Run", "Stop", "Turn", "Look", "Recover", "Return", "Excited" };

        private void Awake()
        {
            if (behaviorController == null) behaviorController = FindAnyObjectByType<PetBehaviorController>();
            if (animationOrchestrator == null) animationOrchestrator = FindAnyObjectByType<AnimationOrchestrator>();
            if (touchInputManager == null) touchInputManager = FindAnyObjectByType<TouchInputManager>();
        }

        private void Update()
        {
            SampleFps();

            // 本工程 activeInputHandler=1，仅新 Input System 可用；用旧版 Input 会直接抛异常。
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && keyboard[toggleKey].wasPressedThisFrame) visible = !visible;
        }

        private void SampleFps()
        {
            _fpsAccum += Time.unscaledDeltaTime;
            _fpsFrames++;
            if (_fpsAccum < 0.25f) return;

            _fps = _fpsFrames / _fpsAccum;
            _minFps = Mathf.Min(_minFps, _fps);
            _maxFps = Mathf.Max(_maxFps, _fps);
            _fpsAccum = 0f;
            _fpsFrames = 0;

            _sampleTimer += 0.25f;
            if (_sampleTimer >= 1f)
            {
                _sampleTimer = 0f;
                _minFps = 999f;
                _maxFps = 0f;
            }
        }

        private void OnGUI()
        {
            if (!visible) return;
            EnsureStyles();

            float height = showGestureTuning ? 620f : 420f;
            var rect = new Rect(panelOffset.x, panelOffset.y, width, height);
            GUILayout.BeginArea(rect, _boxStyle);

            GUILayout.Label("PET DEBUG  (toggle: `)", _headerStyle);

            if (behaviorController == null || animationOrchestrator == null)
            {
                GUILayout.Label("Pet controllers not found in scene.", _labelStyle);
                GUILayout.EndArea();
                return;
            }

            DrawStatus();
            GUILayout.Space(6f);
            DrawEmotion();
            DrawPersonality();
            GUILayout.Space(6f);
            DrawLastEvent();
            DrawContext();
            GUILayout.Space(6f);
            DrawButtons();
            GUILayout.Space(6f);
            DrawPerformance();

            if (showGestureTuning) DrawGestureTuning();

            GUILayout.EndArea();
        }

        private void DrawStatus()
        {
            var sm = behaviorController.StateMachine;
            GUILayout.Label($"State      : {behaviorController.CurrentState}   ({sm.TimeInState:F1}s)", _labelStyle);
            GUILayout.Label($"Behavior   : {behaviorController.CurrentBehavior}", _labelStyle);
            GUILayout.Label($"Animation  : {animationOrchestrator.CurrentClipName}  [{animationOrchestrator.CurrentCapability}]", _labelStyle);
            // 注意：不要把带引号的三元表达式直接塞进字符串插值 ——
            // 内层引号会提前结束外层插值，报 CS8076/CS8361。先算好再插。
            var pet = behaviorController.Context.Controller;
            float speed = pet != null ? pet.CurrentSpeed : 0f;
            GUILayout.Label($"Speed      : {speed:F2} m/s", _labelStyle);
        }

        private void DrawEmotion()
        {
            var e = behaviorController.Emotion;
            GUILayout.Label($"Emotion    : {e.Compact()}", _labelStyle);
            GUILayout.Label($"  since interaction: {behaviorController.Context.TimeSinceInteraction:F1}s", _labelStyle);
        }

        private void DrawPersonality()
        {
            var p = behaviorController.Personality;
            GUILayout.Label($"Drive      : Autonomous {p.AutonomousDrive:F2} | Restless {p.Restlessness:F2} | Interact {p.InteractionDrive:F2}", _labelStyle);
            GUILayout.Label($"Personality: {p.Compact()}", _labelStyle);
        }

        private void DrawLastEvent()
        {
            var g = behaviorController.LastGesture;
            GUILayout.Label($"LastGesture: {(g != null ? g.ToString() : "-")}", _labelStyle);
            GUILayout.Label($"LastEvent  : {behaviorController.LastEventLabel}", _labelStyle);
        }

        private void DrawContext()
        {
            GUILayout.Label($"Context    : {behaviorController.Context.Compact()}", _labelStyle);
            GUILayout.Label($"Score      : {behaviorController.ScoreDetail()}", _labelStyle);
            if (animationOrchestrator.Map != null)
                GUILayout.Label($"Capability : {animationOrchestrator.Map.CapabilitySummary()}", _labelStyle);
        }

        private void DrawButtons()
        {
            GUILayout.Label("Behavior", _headerStyle);
            GUILayout.BeginHorizontal();
            for (int i = 0; i < 6; i++)
            {
                var b = (PetBehavior)i;
                if (GUILayout.Button(s_behaviorNames[i], GUILayout.Height(22f)))
                    behaviorController.DebugForceBehavior(b);
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            for (int i = 6; i < s_behaviorNames.Length; i++)
            {
                var b = (PetBehavior)i;
                if (GUILayout.Button(s_behaviorNames[i], GUILayout.Height(22f)))
                    behaviorController.DebugForceBehavior(b);
            }
            if (GUILayout.Button("Autonomy", GUILayout.Height(22f)))
                behaviorController.SetAutonomy(!behaviorController.AutonomyEnabled);
            GUILayout.EndHorizontal();

            GUILayout.Label("Interaction", _headerStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Tap", GUILayout.Height(22f)) && touchInputManager != null)
                touchInputManager.InjectSyntheticGesture(GestureType.Tap, 1, PetRegion.Head, true);
            if (GUILayout.Button("Stroke", GUILayout.Height(22f)) && touchInputManager != null)
                touchInputManager.InjectSyntheticGesture(GestureType.Stroke, 1, PetRegion.Body, true);
            if (GUILayout.Button("FastSwipe", GUILayout.Height(22f)) && touchInputManager != null)
                touchInputManager.InjectSyntheticGesture(GestureType.FastSwipe, 1, PetRegion.Body, true);
            if (GUILayout.Button("Scratch", GUILayout.Height(22f)) && touchInputManager != null)
                touchInputManager.InjectSyntheticGesture(GestureType.MultiScratch, 3, PetRegion.Body, true);
            if (GUILayout.Button("ScreenScratch", GUILayout.Height(22f)) && touchInputManager != null)
                touchInputManager.InjectSyntheticGesture(GestureType.ScreenScratch, 3, PetRegion.Unknown, false);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset", GUILayout.Height(22f))) behaviorController.DebugReset();
            if (GUILayout.Button("Clear Gesture", GUILayout.Height(22f))) behaviorController.DebugResetGestures();
            if (GUILayout.Button("Log: " + (PetLog.Enabled(PetLogChannel.None) ? "-" : "on"), GUILayout.Height(22f)))
                ToggleLogChannels();
            if (GUILayout.Button("Clear Log", GUILayout.Height(22f))) PetLog.ClearHistory();
            GUILayout.EndHorizontal();
        }

        private static void ToggleLogChannels()
        {
            PetLog.Channels = PetLog.Enabled(PetLogChannel.Touch) && PetLog.Enabled(PetLogChannel.Gesture)
                ? PetLogChannel.None
                : PetLogChannel.All;
        }

        private void DrawPerformance()
        {
            GUILayout.Label("Performance", _headerStyle);
            GUILayout.Label($"  FPS {_fps:F0}   min {_minFps:F0} / max {_maxFps:F0}", _labelStyle);
            GUILayout.Label($"  DrawCalls {ReadCounter("Draw Calls Count")}   " +
                            $"Batches {ReadCounter("Batches Count")}   " +
                            $"Triangles {ReadCounter("Triangles Count")}", _labelStyle);
            // 先算好数值再插值：不要把带引号的方法调用塞进 ${} 里
            long managedMb = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);
            long monoMb = System.GC.GetTotalMemory(false) / (1024 * 1024);
            GUILayout.Label($"  Managed {managedMb} MB   Mono {monoMb} MB", _labelStyle);
        }

        /// <summary>读一次 Profiler 计数器并立即释放。放在 OnGUI 里开销可接受（原型期每秒十几次）。</summary>
        private static long ReadCounter(string counterName)
        {
            try
            {
                using var recorder = Unity.Profiling.ProfilerRecorder.StartNew(
                    Unity.Profiling.ProfilerCategory.Render, counterName);
                return recorder.Valid ? recorder.LastValue : -1;
            }
            catch (System.Exception)
            {
                return -1;
            }
        }

        private void DrawGestureTuning()
        {
            if (touchInputManager == null || touchInputManager.Recognizer == null) return;
            var cfg = touchInputManager.Config;

            GUILayout.Label("Gesture Thresholds", _headerStyle);
            cfg.tapMaxDuration = Slider(cfg.tapMaxDuration, 0.05f, 0.8f, "Tap Max Dur", 0.01f);
            cfg.tapMaxDistance = Slider(cfg.tapMaxDistance, 4f, 80f, "Tap Max Dist", 1f);
            cfg.longPressDuration = Slider(cfg.longPressDuration, 0.2f, 2f, "LongPress Dur", 0.05f);
            cfg.fastSpeed = Slider(cfg.fastSpeed, 200f, 3000f, "Fast Speed", 10f);
            cfg.strokeMinDistance = Slider(cfg.strokeMinDistance, 5f, 120f, "Stroke Min Dist", 1f);
            cfg.scratchMinReversals = (int)Slider(cfg.scratchMinReversals, 1f, 12f, "Scratch Reversals", 1f);

            GUILayout.Label("Log History", _headerStyle);
            var history = PetLog.History;
            int start = Mathf.Max(0, history.Count - 6);
            for (int i = start; i < history.Count; i++)
            {
                GUILayout.Label("  " + history[i], _labelStyle);
            }
        }

        private static float Slider(float value, float min, float max, string label, float step)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(120f));
            value = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(150f));
            GUILayout.Label(value.ToString("F2"), GUILayout.Width(40f));
            GUILayout.EndHorizontal();
            return Mathf.Round(value / step) * step;
        }

        private void EnsureStyles()
        {
            if (_boxStyle != null) return;
            var bg = new Texture2D(1, 1);
            bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.72f));
            bg.Apply();
            _boxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(10, 10, 8, 8) };
            _boxStyle.normal.background = bg;
            _headerStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 12 };
            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = false };
        }
    }
}