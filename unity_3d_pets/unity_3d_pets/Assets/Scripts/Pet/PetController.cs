using PetV1.Core;
using UnityEngine;

namespace PetV1.Pet
{
    /// <summary>
    /// 宠物身体控制器。负责移动、朝向、区域约束，不做行为决策（那是 BehaviorController 的事）。
    /// 需求文档第八节要求宠物不能走出场景 / 穿墙 / 穿过摄像机 / 卡在角落，这里用三类约束实现：
    ///   1. <see cref="wanderBounds"/>：矩形活动区硬边界
    ///   2. <see cref="obstacleMask"/> + 前向探测射线：撞墙前提前转向
    ///   3. 卡住检测：连续多帧位移低于阈值则强制脱困
    /// </summary>
    [DisallowMultipleComponent]
    public class PetController : MonoBehaviour
    {
        [Header("Wander Area")]
        [Tooltip("活动区域半尺寸（米）。宠物中心不会超出这个范围。")]
        [SerializeField] private Vector2 wanderBounds = new Vector2(3.5f, 3.5f);

        [Tooltip("活动区域中心偏移（相对初始位置）。")]
        [SerializeField] private Vector2 wanderCenterOffset = Vector2.zero;

        [Header("Obstacle Avoidance")]
        [Tooltip("参与避障检测的 Layer（墙、家具等）。")]
        [SerializeField] private LayerMask obstacleMask = 0;

        [Tooltip("前向探测距离（米）。宠物前方有障碍时提前改变方向。")]
        [SerializeField] private float lookAheadDistance = 1.1f;

        [Tooltip("侧向探测角度（度）。用 ±该角度的两条射线做扇形探测。")]
        [SerializeField] private float lookAheadAngle = 35f;

        [Tooltip("探测射线起点相对宠物中心的高度。避免打到地面。")]
        [SerializeField] private float probeHeight = 0.25f;

        [Header("Stuck Recovery")]
        [Tooltip("连续多少秒位移小于阈值判定为卡住。")]
        [SerializeField] private float stuckTimeThreshold = 0.8f;

        [Tooltip("判定卡住的位移阈值（米）。")]
        [SerializeField] private float stuckDistanceThreshold = 0.02f;

        [Tooltip("卡住时的脱困转向角度（度）。")]
        [SerializeField] private float unstuckTurnAngle = 140f;

        [Header("Turning")]
        [Tooltip("每秒最大转向速度（度/秒）。")]
        // 原来 420（1.4 秒转一圈），实测用户反馈「转向过快、突然转向不自然」。
        // 中型犬自然转身约 90-180 度/秒，取 150：转 90 度约 0.6 秒，有明确的转向过程。
        [SerializeField] private float turnSpeed = 150f;

        private Vector3 _homePosition;
        private Vector3 _moveTarget;
        private bool _hasMoveTarget;
        private float _currentSpeed;
        private float _stuckTimer;
        private Vector3 _lastProgressPosition;

        /// <summary>本帧正在转向（还没转到目标朝向，因此刻意不位移）。
        /// 卡住检测要跳过这种「有意不动」，否则每次变向都会误判成卡住。</summary>
        private bool _turning;
        private float _turnSign = 1f;

        /// <summary>当前实际移动速度（m/s）。AnimationOrchestrator 会用它驱动 Animator 的 Speed 参数。</summary>
        public float CurrentSpeed => _currentSpeed;
        public bool HasMoveTarget => _hasMoveTarget;
        public Vector3 HomePosition => _homePosition;
        public bool IsStuck => _stuckTimer >= stuckTimeThreshold;

        /// <summary>碰撞体高度，用于把射线起点抬到身体中部。</summary>
        public float BodyHeight { get; set; } = 0.5f;

        /// <summary>
        /// 从 <c>PetModelProfile</c> 注入体型相关参数。
        ///
        /// 换模型后体型与步幅都不同，Collider 半径与转向速度必须同步，
        /// 否则会出现「点击区域错位」或「转向像漂移」。
        /// 由场景构建工具在生成场景时调用一次，不在运行期反复调。
        ///
        /// 注意：**不写 wanderBounds**。活动区是场景布局决定的（房间多大），
        /// 场景构建器用 <see cref="SetWanderArea"/> 单独设，
        /// 这里若也写会把调用方的设置覆盖掉。
        /// </summary>
        public void ApplyModelProfile(PetV1.Model.PetModelProfile profile)
        {
            if (profile == null) return;
            BodyHeight = profile.bodyHeight;
            turnSpeed = profile.turnSpeed;
            Debug.Log($"[PetV1] PetController 应用模型参数：" +
                      $"身高 {profile.bodyHeight:F2}m，转向 {profile.turnSpeed:F0}°/s");
        }

        private void Awake()
        {
            _homePosition = transform.position;
            _moveTarget = _homePosition;
            _lastProgressPosition = _homePosition;
        }

        /// <summary>
        /// 向某点移动。行为控制器给出的目标点会先经过边界钳制。
        /// </summary>
        public bool MoveTo(Vector3 worldTarget, float speed)
        {
            Vector3 clamped = ClampToWanderArea(worldTarget);
            _moveTarget = clamped;
            _hasMoveTarget = true;
            _currentSpeed = Mathf.Max(0f, speed);
            return (clamped - transform.position).sqrMagnitude > 0.0004f;
        }

        /// <summary>原地转向，不位移。</summary>
        public void FaceTowards(Vector3 worldPoint)
        {
            Vector3 dir = worldPoint - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return;
            Quaternion target = Quaternion.LookRotation(dir.normalized, Vector3.up);
            RotateTowards(target);
            _currentSpeed = 0f;
            _hasMoveTarget = false;
            _turning = false;
        }

        /// <summary>按当前朝向转固定角度。自主行为的 Turn 行为用。</summary>
        public void TurnByAngle(float degrees)
        {
            transform.Rotate(0f, degrees, 0f, Space.World);
            _currentSpeed = 0f;
            _hasMoveTarget = false;
            _turning = false;
            _stuckTimer = 0f;
            _lastProgressPosition = transform.position;
        }

        /// <summary>停止移动，回到 Idle。</summary>
        public void Stop()
        {
            _hasMoveTarget = false;
            _currentSpeed = 0f;
            _turning = false;
            _stuckTimer = 0f;
            _lastProgressPosition = transform.position;
        }

        /// <summary>回到初始位置附近。需求文档 2.5 的「回到初始区域」。</summary>
        public void ReturnHome(float speed)
        {
            _homePosition = transform.position;
            MoveTo(_homePosition, speed);
        }

        /// <summary>设置新的活动区域（场景配置用）。</summary>
        public void SetWanderArea(Vector2 halfSize, Vector2 centerOffset)
        {
            wanderBounds = new Vector2(Mathf.Max(0.5f, halfSize.x), Mathf.Max(0.5f, halfSize.y));
            wanderCenterOffset = centerOffset;
            Vector3 clamped = ClampToWanderArea(transform.position);
            transform.position = clamped;
        }

        private void Update()
        {
            if (_hasMoveTarget && _currentSpeed > 0f)
            {
                StepMovement();
            }
            else
            {
                _currentSpeed = 0f;
            }

            UpdateStuckDetection(Time.deltaTime);
        }

        private void StepMovement()
        {
            Vector3 position = transform.position;
            Vector3 toTarget = _moveTarget - position;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;

            if (distance < _currentSpeed * Time.deltaTime || distance < 0.02f)
            {
                transform.position = new Vector3(_moveTarget.x, position.y, _moveTarget.z);
                _hasMoveTarget = false;
                _currentSpeed = 0f;
                return;
            }

            Vector3 direction = toTarget / distance;

            // 避障：前方扇形三条射线，任一命中则选择未被阻挡的方向
            if (IsBlocked(direction))
            {
                Vector3 left = Quaternion.Euler(0f, -lookAheadAngle, 0f) * direction;
                Vector3 right = Quaternion.Euler(0f, lookAheadAngle, 0f) * direction;
                Vector3 behind = Quaternion.Euler(0f, 180f, 0f) * direction;

                if (!IsBlocked(left)) direction = left;
                else if (!IsBlocked(right)) direction = right;
                else if (!IsBlocked(behind)) direction = behind;
                else direction = Quaternion.Euler(0f, lookAheadAngle * _turnSign, 0f) * direction;
            }

            direction.Normalize();

            Quaternion lookRotation = Quaternion.LookRotation(direction, Vector3.up);
            bool aligned = RotateTowards(lookRotation);
            _turning = !aligned;

            // 转向未完成时不位移：否则狗会一边高速旋转一边横着滑出去，
            // 看起来就是「突然转向」的不自然感。转到位后才开始走。
            if (!aligned) return;

            Vector3 next = position + direction * (_currentSpeed * Time.deltaTime);
            next = ClampToWanderArea(next);
            transform.position = new Vector3(next.x, position.y, next.z);
        }

        /// <summary>
        /// 以固定角速度转向目标朝向。
        /// 只处理 Y 轴：宠物始终直立（移动全程保持 transform.position.y 不变），
        /// 不需要做三轴欧拉角插值。欧拉角跨 ±180 时会翻转，
        /// Quaternion.RotateTowards 走四元数最短路径，没有这个问题。
        /// </summary>
        /// <returns>true 表示已转到目标朝向（容差内），可以开始位移。</returns>
        private bool RotateTowards(Quaternion target)
        {
            float maxStep = turnSpeed * Time.deltaTime;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, target, maxStep);

            // 用 Quaternion.Angle 判断剩余角度，不用欧拉角差（跨 ±180 会翻转）。
            return Quaternion.Angle(transform.rotation, target) <= TurnAlignedTolerance;
        }

        /// <summary>剩余角度小于这个值就算「已转到位」。</summary>
        private const float TurnAlignedTolerance = 8f;

        private bool IsBlocked(Vector3 direction)
        {
            if (obstacleMask.value == 0) return false;
            Vector3 origin = transform.position + Vector3.up * (BodyHeight * probeHeight + 0.1f);
            Vector3 dir = direction;
            dir.y = 0f;
            dir.Normalize();
            return Physics.SphereCast(origin, 0.08f, dir, out _, lookAheadDistance, obstacleMask, QueryTriggerInteraction.Ignore);
        }

        private Vector3 ClampToWanderArea(Vector3 position)
        {
            Vector3 center = _homePosition + new Vector3(wanderCenterOffset.x, 0f, wanderCenterOffset.y);
            float x = Mathf.Clamp(position.x, center.x - wanderBounds.x, center.x + wanderBounds.x);
            float z = Mathf.Clamp(position.z, center.z - wanderBounds.y, center.z + wanderBounds.y);
            return new Vector3(x, position.y, z);
        }

        /// <summary>随机取一个活动区内的合法点。</summary>
        public Vector3 GetRandomWanderPoint(float margin = 0.5f)
        {
            Vector3 center = _homePosition + new Vector3(wanderCenterOffset.x, 0f, wanderCenterOffset.y);
            float usableX = Mathf.Max(0.2f, wanderBounds.x - margin);
            float usableZ = Mathf.Max(0.2f, wanderBounds.y - margin);
            return new Vector3(
                center.x + Random.Range(-usableX, usableX),
                center.y,
                center.z + Random.Range(-usableZ, usableZ));
        }

        private void UpdateStuckDetection(float deltaTime)
        {
            // 转向未完成时 _hasMoveTarget 和 _currentSpeed 都还是上一次的，
            // 但本帧刻意不位移（先转身再走）。这种「没动」是有意为之，
            // 不能算卡住，否则每次变向都会误触发 Recover。
            if (_turning)
            {
                _stuckTimer = 0f;
                _lastProgressPosition = transform.position;
                return;
            }

            bool tryingToMove = _hasMoveTarget && _currentSpeed > 0.01f;
            if (!tryingToMove)
            {
                _stuckTimer = 0f;
                _lastProgressPosition = transform.position;
                return;
            }

            float moved = Vector3.Distance(transform.position, _lastProgressPosition);
            if (moved < stuckDistanceThreshold)
            {
                _stuckTimer += deltaTime;
                if (_stuckTimer >= stuckTimeThreshold)
                {
                    RecoverFromStuck();
                }
            }
            else
            {
                _stuckTimer = 0f;
                _lastProgressPosition = transform.position;
            }
        }

        /// <summary>脱困：随机左右转一个大角度，并把目标点换成新的随机点。</summary>
        public void RecoverFromStuck()
        {
            _turnSign = Random.value < 0.5f ? -1f : 1f;
            TurnByAngle(unstuckTurnAngle * _turnSign);
            _moveTarget = GetRandomWanderPoint();
            _hasMoveTarget = false;
            _stuckTimer = 0f;
            _turning = false;
            _lastProgressPosition = transform.position;
            PetLog.Behavior("stuck detected -> unstuck",
                PetLog.Kv("turn", Mathf.RoundToInt(unstuckTurnAngle * _turnSign)));
        }

        /// <summary>DebugPanel 用：直接瞬移到某点（不改变活动区定义）。</summary>
        public void Teleport(Vector3 position)
        {
            transform.position = ClampToWanderArea(position);
            _lastProgressPosition = transform.position;
            _stuckTimer = 0f;
            _turning = false;
        }

        public Vector2 WanderBounds => wanderBounds;
    }
}