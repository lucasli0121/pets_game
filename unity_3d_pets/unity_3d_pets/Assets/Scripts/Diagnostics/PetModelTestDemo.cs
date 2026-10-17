using PetV1.Animation;
using PetV1.Core;
using PetV1.Pet;
using UnityEngine;

namespace PetV1.Diagnostics
{
    /// <summary>
    /// ModelTest 场景的自动演示：循环 Idle -> Walk -> Run，空格手动切换。
    /// 只用于验证 Clip 可播与循环是否正常，不参与正式行为链路。
    /// 放在 runtime 程序集里，因为它会被挂进场景（Editor 程序集的组件无法进 Build）。
    /// </summary>
    public class PetModelTestDemo : MonoBehaviour
    {
        [SerializeField] private AnimationOrchestrator controller;
        [SerializeField] private PetController petController;
        [SerializeField] private float clipDisplaySeconds = 6f;

        [SerializeField] private PetCapability index = PetCapability.Idle;
        private float _timer;

        public void Configure(AnimationOrchestrator orchestrator, PetController pet)
        {
            controller = orchestrator;
            petController = pet != null ? pet : GetComponent<PetController>();
            index = PetCapability.Idle;
            _timer = 0f;
            PlayCurrent();
        }

        private void Update()
        {
            if (controller == null || petController == null) return;

            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && keyboard.spaceKey.wasPressedThisFrame) Next();

            _timer += Time.deltaTime;
            if (_timer >= clipDisplaySeconds)
            {
                _timer = 0f;
                Next();
            }
        }

        private void Next()
        {
            _timer = 0f;
            index = index switch
            {
                PetCapability.Idle => PetCapability.Walk,
                PetCapability.Walk => PetCapability.Run,
                _ => PetCapability.Idle,
            };
            PlayCurrent();
        }

        private void PlayCurrent()
        {
            float speed = index switch
            {
                PetCapability.Walk => 1.2f,
                PetCapability.Run => 3.0f,
                _ => 0f,
            };

            if (index == PetCapability.Idle)
            {
                petController.Stop();
            }
            else
            {
                // 沿 X 轴来回移动，便于观察 Walk / Run 的步态与速度差异
                float dir = Mathf.Sin(Time.time * 0.7f);
                Vector3 target = petController.transform.position + new Vector3(dir * 2f, 0f, 0f);
                petController.MoveTo(target, speed);
            }

            controller.PlayCapability(index, speed);
            PetLog.Animation("model test demo", PetLog.Kv("capability", index, "speed", speed));
        }
    }
}
