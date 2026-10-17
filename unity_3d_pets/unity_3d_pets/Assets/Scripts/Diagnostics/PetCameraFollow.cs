using UnityEngine;

namespace PetV1.Diagnostics
{
    /// <summary>
    /// 简易跟随相机。让相机跟着宠物水平移动与轻微俯视，避免宠物走出画面
    /// （需求文档 8.2 要求宠物不能「穿过摄像机」或走出场景）。
    ///
    /// 原型阶段刻意做成 LookAt 式跟随而不是平滑轨道：
    /// 电子宠物的视觉参照系应该稳定跟随宠物，否则玩家会失去方位感。
    /// 平滑系数偏大（跟随慢），这样既有跟随感又能看出宠物相对世界的位移。
    /// </summary>
    [DisallowMultipleComponent]
    public class PetCameraFollow : MonoBehaviour
    {
        [SerializeField] private Transform target;

        [Tooltip("水平面上的跟随偏移（米）。相机相对宠物的固定位置。")]
        [SerializeField] private Vector3 offset = new Vector3(0f, 1.4f, -4.2f);

        [Tooltip("跟随平滑度。越大越跟得紧，越小越滞后。0 表示硬跟随（无平滑）。")]
        [SerializeField] private float smoothing = 2.5f;

        [Tooltip("是否只跟随水平位置，保持高度不变（推荐，避免画面上下晃动）。")]
        [SerializeField] private bool horizontalOnly = true;

        private Vector3 _smoothedPosition;

        public Transform Target
        {
            get => target;
            set => target = value;
        }

        private void Start()
        {
            if (target == null) return;
            _smoothedPosition = DesiredPosition();
            transform.position = _smoothedPosition;
        }

        private void LateUpdate()
        {
            if (target == null) return;

            Vector3 desired = DesiredPosition();

            if (smoothing <= 0f)
            {
                transform.position = desired;
            }
            else
            {
                _smoothedPosition = Vector3.Lerp(_smoothedPosition, desired, smoothing * Time.deltaTime);
                transform.position = _smoothedPosition;
            }

            // 始终看向宠物。LookAt 比固定朝向好：宠物转向时画面中心始终是它。
            Vector3 lookTarget = target.position + Vector3.up * lookHeightOffset;
            if (lookTarget.y > transform.position.y)
            {
                // 相机在宠物下方时稍微抬头，避免看穿地面
                lookTarget.y = transform.position.y;
            }
            transform.rotation = Quaternion.LookRotation(lookTarget - transform.position, Vector3.up);
        }

        [Tooltip("看向的目标点相对宠物的高度（米）。约在狗身中部。")]
        [SerializeField] private float lookHeightOffset = 0.35f;

        private Vector3 DesiredPosition()
        {
            Vector3 basePos = target.position + offset;
            if (!horizontalOnly) return basePos;

            // 只跟随 XZ，Y 保持初始值（相机高度不随宠物上下变化）
            return new Vector3(basePos.x, transform.position.y, basePos.z);
        }

        /// <summary>场景构建时由 Editor 调用。</summary>
        public void Configure(Transform petTransform, Vector3 cameraOffset)
        {
            target = petTransform;
            offset = cameraOffset;
        }
    }
}