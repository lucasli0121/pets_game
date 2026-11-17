# 宠屋养成记 · 服务端与管理端

V1 服务端（Go）+ 管理后台（内嵌单页应用）。实现依据：

- `docs/电子宠物APP_V1_后台与管理端需求文档.docx`
- `docs/电子宠物APP_V1_UI与功能需求文档.docx`

一个二进制同时提供客户端 API 与管理端页面，管理端静态资源用 `go:embed` 打进二进制，部署只需要一个 exe。

---

## 1. 技术栈与选型理由

| 项 | 选择 | 说明 |
|---|---|---|
| 语言 | Go 1.27 | 单文件静态二进制，内存占用低，与「Go 体积更小」的目标一致 |
| HTTP | 标准库 `net/http`（1.22+ 路由） | 不引框架，`go.sum` 只有 MongoDB 驱动一个直接依赖 |
| 数据库 | MongoDB 5.0.4（`localhost:27017`，库名 `pets_game`） | 按当前环境给定 |
| 鉴权 | 自实现 HS256 JWT | 约 80 行，省掉一个第三方库 |
| 管理端 | 原生 HTML/CSS/JS 单页 | 零构建、零依赖，直接改文件即可生效 |

编译产物：`bin/pets-server.exe`，**16.6 MB**，无运行时依赖。

---

## 2. 快速开始

```bash
# 1. 配置（不改也能跑，配置里已预填当前 Mongo）
cp .env.example .env

# 2. 编译
go build -o bin/pets-server.exe ./cmd/server

# 3. 启动（首次启动会自动灌默认配置 v1 + 建管理账号）
./bin/pets-server.exe

# 4. 打开管理端
#    http://127.0.0.1:8080/admin/
#    默认账号 admin / admin（上线必须改，见 7.3 / 7.4）
```

首次启动做的事：

1. 建全部集合与索引（`users.openid` 唯一、`daily_checkin(user_id,date)` 唯一、`request_dedup(user_id,request_id)` 唯一 + 30 天 TTL 等）；
2. `config_meta` 为空 → 写入默认配置快照，版本号 = 1，同时留一份 `config_release` v1；
3. `admin_users` 为空 → 建超管账号。

这三步都是幂等的，重复启动不会覆盖已有数据。

### 静态资源（CDN）

场景背景图与缩略图默认走「远程优先、本地兜底」：

| 配置字段 | 含义 | 例子 |
|---|---|---|
| `thumb` | 缩略图相对路径 | `scenes/thumb/scene_home_day.jpg` |
| `scenePath` | 场景背景大图相对路径 | `scenes/scene_home_day.jpg` |
| `sceneKey` | 打进安装包的资源 key，远程失败时兜底 | `home_day` |

服务端下发 `getConfig` 时用环境变量 `ASSET_BASE_URL` 拼出完整地址：

```json
{ "id": "home_day", "name": "温馨小家（白天）",
  "thumb":     "scenes/thumb/scene_home_day.jpg",
  "thumbUrl":  "https://cdn.example.com/pets/scenes/thumb/scene_home_day.jpg",
  "scenePath": "scenes/scene_home_day.jpg",
  "sceneUrl":  "https://cdn.example.com/pets/scenes/scene_home_day.jpg",
  "sceneKey":  "home_day" }
```

**客户端降级逻辑**：先下载 `sceneUrl`，失败（未配 CDN、离线、超时）再按 `sceneKey` 加载包内贴图。
`ASSET_BASE_URL` 留空时 `thumbUrl` / `sceneUrl` 原样下发相对路径，客户端据此判断没有 CDN，直接走包内。

两列路径都必须是相对路径，保存草稿时校验：拒绝 `http:` 开头的完整 URL、拒绝 `..` 穿越、拒绝 `/` 开头的绝对路径；
`scenePath` 与 `sceneKey` 不能同时为空。

源文件在 `art/scenes/final/`（大图 1080×1920）与 `art/scenes/thumb/`（缩略图 288×512）。
上传 CDN 时保持 `scenes/` 与 `scenes/thumb/` 两级目录结构即可与配置对上。

### 每张背景图的实测构图数据点（`scene.layout`）

场景形态是「2D 背景图 + 3D 宠物 + 正交相机」（PRD 4.5）。相机要按狗脚底线反推高度，
而狗脚底线取决于这张图的地板纵深位置 —— **每换一张背景图这个值就变**。
所以构图参数必须跟着图一起配在后台，不能写死在客户端代码里。

| 字段 | 含义 | 单位 |
|---|---|---|
| `orthographicSize` | 正交可视半高，决定狗占画面多少 | m |
| `cameraPitch` | 相机俯角，俯视构图才看得到地面 | ° |
| `cameraDistance` | 相机水平距离，正交下不影响大小 | m |
| `floorFarLine` | 地板最远端在画面里的归一化纵向位置，**只作自检基准** | 0~1 |
| `dogGroundLine` | 狗脚底线位置，**相机按它反推高度** | 0~1 |
| `wanderHalfX` / `wanderHalfZ` | 活动区半宽 / 半深 | m |
| `wanderCenterX` / `wanderCenterZ` | 活动区中心偏移（用来避开画面里的家具） | m |
| `lightEulerX/Y/Z` | 主光方向，必须与背景图光源一致 | ° |
| `lightColorR/G/B` | 主光颜色 | 0~1 |
| `ambientIntensity` | 环境光强度，夜间必须压低 | — |
| `shadowOpacity` | 脚下椭圆假阴影不透明度 | 0~1 |
| `layoutNote` | 实测记录（量的是什么东西、怎么量的），纯文本 | — |

**`floorFarLine` 与 `dogGroundLine` 是两个不同的量，不能混用。**
前者量的是墙脚线（户外量地面与远处植被的交界），只是构图自检的参考基准；
后者才是相机对齐的目标。拿前者当对齐目标等于强迫狗永远站在贴墙处，数值算得再准语义也是错的。
发布时服务端会校验 `dogGroundLine < floorFarLine`，不满足直接拒绝。

三张图的实测值（`GetPixels32` 返回自下而上的数组，图像坐标 y = 1920 − 数组行号）：

| 场景 | floorFarLine | dogGroundLine | orthographicSize | 地面线量法 |
|---|---|---|---|---|
| `home_day` | 0.433 | 0.30 | 1.9 | 踢脚线暗缝，图像 y≈1085~1090，x=420/450/480/510 四列一致 |
| `home_night` | 0.435 | 0.30 | 1.9 | 踢脚线暗缝，图像 y≈1067~1085，x=430/480/530/580 四列一致 |
| `park_day` | 0.549 | 0.32 | 2.2 | 户外无踢脚线，取远处草地与灌木交界，图像 y≈865 |

量地面线的坑：**找亮度「局部最暗点」（暗缝），不要找「最大亮度跳变」**。
后者会选中地板上的阳光斑（home_day 实测 y=1000，亮度 236），导致狗被设成悬空。

管理端「配置 - 场景」页每张图一张卡片，按取景 / 地面 / 活动区 / 光照分组呈现这些值。

### 运行测试

```bash
go test ./test/ -v
```

测试直连真实 MongoDB，每个用例用独立库 `pets_test_<用例名>`，跑完即删，不污染 `pets_game`。

---

## 3. 目录结构

```
pets_server/
├── cmd/server/main.go            进程入口：加载配置 → 装配 → 监听 / 优雅退出
├── internal/
│   ├── app/app.go                装配（main 与测试共用，避免两边行为不一致）
│   ├── config/config.go          环境变量 + .env
│   ├── auth/auth.go              JWT 签发校验 + bcrypt 密码
│   ├── timeutil/timeutil.go      UTC+8 自然日、毫秒时间戳
│   ├── store/
│   │   ├── mongo.go              连接 + 全部索引
│   │   └── bson.go               bson.D / bson.M 构造糖
│   ├── domain/
│   │   ├── model.go              实体 + 枚举 + Assets 聚合根
│   │   ├── config.go             配置快照结构 + 默认基线（等级表为纯种子，不推导）
│   │   ├── level.go              等级解析（逐级扣减）
│   │   ├── decay.go              状态衰减
│   │   └── errors.go             业务错误码
│   ├── service/
│   │   ├── config.go             生效态读写、草稿、diff
│   │   ├── config_publish.go     校验、发布、回滚、历史版本
│   │   ├── assets.go             积分账本与库存（无事务下的原子性）
│   │   ├── pet.go                登录、领养、喂食、互动、性格、同步
│   │   ├── shop.go               购买、解锁场景、签到
│   │   ├── ad.go                 激励视频发奖、插屏曝光
│   │   ├── idem.go               request_id 幂等
│   │   ├── admin.go              管理员登录、用户查询、积分调整、审计
│   │   ├── session.go            登录会话统计（登录次数 / 在线时长 / 日均指标）
│   │   ├── dashboard.go          看板聚合
│   │   └── seed.go               首次初始化
│   ├── api/
│   │   ├── router.go             路由注册（声明式，自动记录路由表）
│   │   ├── client.go             客户端 15 个接口
│   │   ├── admin.go              管理端 17 个接口
│   │   ├── apidoc.go             接口规格表（文档页数据源）
│   │   ├── middleware.go         鉴权、频控、指标、recover、CORS
│   │   ├── resp.go               {code,msg,data} 信封 + 错误映射
│   │   └── wx.go                 wx.login code → openid
│   └── web/
│       ├── embed.go              管理端静态资源内嵌
│       └── admin/                单页应用（index.html / app.js / style.css）
├── test/                         端到端验收测试
├── .env.example
└── README.md
```

---

## 4. 数据模型

### 4.1 集合清单

| 集合 | 说明 | 关键索引 |
|---|---|---|
| `users` | 账号（openid 唯一） | `openid` 唯一 |
| `pets` | 宠物，各自独立状态 | `(user_id)`、`(user_id,is_active)` |
| `user_assets` | **积分 + 库存 + 场景解锁**（聚合根） | `points.balance` |
| `point_ledger` | 积分流水，只增不改 | `(user_id,created_at)`、`(reason,created_at)` |
| `daily_checkin` | 签到 | `(user_id,date)` **唯一** |
| `ad_events` | 广告曝光与发奖记录 | `(user_id,created_at)`、`(ad_type,created_at)` |
| `orders` | 订单 | `order_id` **唯一** |
| `request_dedup` | 幂等去重 | `(user_id,request_id)` **唯一** + 30 天 TTL |
| `config_food` / `config_scene` / `config_ad` / `config_level` | 生效态配置 | `id` / `level` 唯一 |
| `config_points` / `config_meta` / `config_draft` | 单文档配置 | `_id = "singleton"` |
| `config_flags` | 功能开关 | `key` 唯一 |
| `config_release` | 发布快照，保留 10 版 | `version` 唯一 |
| `admin_users` / `admin_audit_log` | 管理端账号与操作日志 | `username` 唯一 |
| `daily_active` | 活跃日（DAU / 留存口径） | `(user_id,date)` 唯一 |
| `action_log` | 喂食/购买/互动等行为计数 | `(action,created_at)` |
| `event_log` | `reportEvent` 埋点 | `(event,created_at)` |

### 4.2 对文档数据模型的偏离（有意为之，请确认）

**① `user_points` / `inventory` / `scene_unlock` 三张表合并为 `user_assets` 单文档。**

原因：当前生产 Mongo 是 **standalone，不是副本集，不支持多文档事务**。购买要同时「扣积分 + 加库存」，拆成三张表只能靠两阶段补偿，中间存在「扣了钱没加货」的窗口。

合并后，一次 `findOneAndUpdate` 就把「校验余额 + 扣减 + 加库存」做完：

```js
filter: { _id: userId, "points.balance": { $gte: cost } }
update: { $inc: { "points.balance": -cost, "points.total_spent": cost,
                  "points.version": 1, "inventory.food_normal": 3 } }
```

命中即扣，不命中即余额不足——失败方向只可能是「不扣也不加」，不会出现半个状态。逻辑视图仍是三个子对象，**接口出参结构没变**，客户端感知不到差异。

**② 文档 6.1 的「读 version → 失败重试 3 次」没有照搬。**

单文档条件更新比乐观锁重试更强：不需要重试，也不存在 ABA。`version` 字段仍然保留并自增，用途变成对账与将来迁到副本集时切回真事务。

**③ 新增 `interaction` 配置块与 `POST /api/v1/pet/interaction` 接口。**

文档第 5 节的 13 个接口里没有互动经验入口，但 PRD 4.3 明确「单指点击 5 次 +1 经验、抚摸 +2 快乐、多指挠动 +5 快乐 +1 经验」。若交给客户端算，等于允许改包刷经验与积分，与文档 1.3「客户端数值一律不用」直接冲突。所以补一个接口：客户端只报手势与次数，服务端按配置算数值，并加每日上限（默认经验 100 / 快乐 300）。

**④ 新增 `POST /api/v1/ad/impression` 接口。**

文档 13.8 ② 要求插屏「只写 ad_events 记录曝光，供看板统计」，但没有对应接口，客户端无处上报。补一个只写记录、不发奖的接口。

**⑤ 新增 `config_points` 里的三项衰减参数**（`satietyDecayPerHour` / `happinessDecayPerHour` / `offlineCapHours`）。

文档 6.4 把速率写死为 10 / 5，这违反文档自己「关键数值不进代码」的原则，一并放进配置。

---

## 5. 接口

统一返回 `{"code":0,"msg":"ok","data":{...}}`。业务错误 HTTP 仍 200、`code` 非 0；未登录 401、越权 403。所有写接口都要带 `requestId`。

### 5.1 客户端（15 个）

| # | 方法 | 路径 | 说明 |
|---|---|---|---|
| 1 | POST | `/api/v1/login` | `code` 换 openid 并 upsert 账号；开发期可传 `devOpenid` |
| 2 | POST | `/api/v1/config/get` | 版本一致回 `unchanged`，不一致回全量 |
| 3 | POST | `/api/v1/checkin/claim` | 签到 |
| 4 | POST | `/api/v1/ad/reward` | 激励视频发奖 |
| 5 | POST | `/api/v1/shop/buy-food` | 购买食物 |
| 6 | POST | `/api/v1/scene/unlock` | 解锁场景 |
| 7 | POST | `/api/v1/scene/use` | 切换场景 |
| 8 | POST | `/api/v1/pet/feed` | 喂食 |
| 9 | POST | `/api/v1/pet/switch` | 切换宠物 |
| 10 | POST | `/api/v1/pet/adopt` | 领养 |
| 11 | POST | `/api/v1/pet/personality` | 修改性格（扣分 + 冷却） |
| 12 | POST | `/api/v1/sync-state` | 拉服务端权威状态（含离线衰减结算） |
| 13 | POST | `/api/v1/event/report` | 埋点 |
| 14 | POST | `/api/v1/pet/interaction` | **补充**：互动收益 |
| 15 | POST | `/api/v1/ad/impression` | **补充**：插屏曝光 |

### 5.2 管理端（17 个）

| 页面 | 方法 | 路径 | 允许角色 |
|---|---|---|---|
| A1 | POST | `/api/admin/login` | 公开 |
| — | GET | `/api/admin/me` | 全部 |
| A2–A6/A12 | GET | `/api/admin/config/effective` | 全部 |
| | GET | `/api/admin/config/draft` | 全部 |
| | PUT | `/api/admin/config/draft` | 超管 / 配置运营 |
| | DELETE | `/api/admin/config/draft` | 超管 / 配置运营 |
| A7 | GET | `/api/admin/config/diff` | 全部 |
| | POST | `/api/admin/config/publish` | 超管 / 配置运营 |
| | GET | `/api/admin/config/releases` | 全部 |
| | POST | `/api/admin/config/rollback` | 超管 / 配置运营 |
| A8 | GET | `/api/admin/users` | 全部 |
| | GET | `/api/admin/users/{id}` | 全部 |
| A9 | POST | `/api/admin/points/adjust` | **仅超管** |
| A10 | GET | `/api/admin/dashboard` | 全部 |
| A11 | GET | `/api/admin/audit-logs` | 全部 |
| — | GET | `/api/admin/api-doc` | 全部（文档页数据源） |

角色约束按文档 7.4：配置运营即使能保存草稿，也**不能改 `points` 与 `ad` 两块**——服务端在保存草稿时逐字段比对，改了直接 403。

### 5.3 在线接口文档

管理端左侧导航「帮助 → 接口文档」（`http://<host>:8080/admin/#/apidoc`）。

上面两张表只是索引，**字段级说明、请求示例、可能返回的错误码都在这个页面里**，并且可以直接在线发请求验证。

规格声明在 `internal/api/apidoc.go`，与路由注册同包。页面通过 `GET /api/admin/api-doc` 拉取，所以文档与代码同版本，不会出现「文档写的和实际行为不一致」。

`test/apidoc_test.go` 把规格表与实际注册的路由**逐条双向比对**：

- 加了新路由但忘了写文档 → 测试失败
- 文档里写了不存在的路由 → 测试失败
- 标了「幂等」的接口没声明 `requestId` 字段 → 测试失败
- 客户端接口路径不在 `/api/v1/` 下、管理端不在 `/api/admin/` 下 → 测试失败

这是必要的：规格是人写的声明，不是反射生成的，光靠编译发现不了「漏写文档」。

---

## 6. 关键业务规则的实现位置

| 规则（文档节） | 实现 |
|---|---|
| 客户端余额只作显示，扣减全在服务端（1.3） | 所有接口只接受 `requestId` 与标识，不接受金额/余额 |
| 请求幂等（5） | `service/idem.go`：唯一索引插入成功者才执行；失败不缓存，允许重试 |
| 乐观锁 / 余额不得为负（6.1） | `service/assets.go`：条件更新，余额直接写进 filter |
| 签到按 UTC+8 自然日防重（6.1） | `daily_checkin` 唯一索引 + `timeutil.Today()` |
| 升级发奖（6.1 / 13.2） | `service/pet.go`：逐级扣减算新等级，对每级发 `config_level.reward_points` |
| 广告三项兜底（6.2） | `service/ad.go`：每日上限 + 最小间隔 + requestId 一次 |
| 购买/喂食顺序固定（6.3） | `shop.go` 先扣后加订单；`pet.go` 校验库存→扣 1→更新状态→判升级→发奖 |
| 状态衰减与 24h 封顶（6.4） | `domain/decay.go`：整小时扣、基准按整小时推进、封顶 24h |
| 配置版本比对（6.5） | `getConfig`：版本相同回 `unchanged` |
| 频控（6.6） | `api/middleware.go`：滑动窗口，按 (user,path) 计 |
| 草稿/发布/回滚/保留 10 版（7.2） | `service/config_publish.go` |
| 发布前数值校验（7.2） | `service.ValidateSnapshot` |
| 用户活跃度统计（A8） | `service/session.go`：`Session` 登录时累计、`StatsOf` 汇总 |

---

## 6.1 用户资料统计口径（A8）

管理端「用户查询」的列表与详情都带活跃度指标。三个口径都是**估算**，页面上直接标了说明，避免运营误读成精确值。

| 指标 | 口径 |
|---|---|
| 累计登录次数 | 每次 `/api/v1/login` 成功 +1，含同一天重复登录 |
| 活跃天数 | 有过登录的 UTC+8 自然日数，取自 `daily_active`（一天一条） |
| 平均每天 X | **累计值 ÷ 活跃天数**。不用「注册至今天数」——后者把未活跃的日子算进分母，新用户首日均分被大幅拉低，反映不出真实使用强度 |
| 最长 / 累计在线时长 | 按**相邻两次登录的时间差**近似一次会话，本次登录时结算上一次。**单次封顶 2 小时** |
| 喂养次数 | `action_log` 里 `action=feed` 的条数 |
| 花费积分 | `point_ledger` 里所有负数变动求和（买食物、解锁场景、改性格、人工扣减都算） |
| 喂养记录花费积分 | **按该食物当前单价估算**，不是实付金额 |

**两处已知偏差：**

1. **在线时长是近似值。** 客户端还没上报心跳，服务端拿不到真实在线时长。用户中途直接杀进程且当天不再登录时，这次会话不计时长；不封顶的话「第二天再来」会被算成一次超长在线，所以必须封顶。要改成精确值需要客户端接 `/api/v1/session/heartbeat`（心跳 5 分钟、15 分钟无心跳视为离线），届时只需替换 `session.go` 的 `Session`，`users` 上的字段结构不用动。

2. **喂养记录的花费是估算。** 积分实际在**买食物**时扣，喂食只扣库存，所以「喂一次花了多少积分」严格说不存在。当前按食物当前单价显示，运营改价后历史记录会与当时实付不一致。真要精确得做库存批次先进先出，V1 不值得。

**性能注意**：列表页的统计用 3 次聚合批量算整页（`aggregateBriefStats`），不是逐用户查——否则 20 个用户就是上百次 count。

---

## 7. 设计决策与待确认

### 7.1 状态下限设为 1（2026-10-18 已定）

文档 6.4 定的速率是饱腹 −10/h、快乐 −5/h、离线封顶 24h。按这个速率，**离线 10 小时饱腹就会算到 0，20 小时快乐算到 0**，24 小时封顶来不及起作用——封顶只对「超过 24 小时」的离线有效，而那时两个值早已到底。

产品决策：**状态值下限设为 1，不归零**。衰减与加值一律夹在 `[1, 100]`，状态只会在低位徘徊，不会变成 0。这样「48 小时不登录状态不空」这条验收达标：回来时饱腹 = 快乐 = 1，宠物不死、经验不丢。

实现位置：

- `domain.MinStateValue = 1`（`internal/domain/decay.go`）
- `Settle` 用 `clampRange(v, MinStateValue, 100)`，不再夹到 0
- 喂养与互动加值走 `ClampState`，避免「加 0 还是 0」把历史 0 值卡在下限之下
- `Test08` 断言改为「饱腹、快乐都 ≥ 1」

### 7.2 等级表纯配置，服务端不推导（2026-10-18 已定）

等级的所有数值都是配置项，**服务端不做任何计算**。文档 13.8 ① 原文也要求「服务端只读表，不写死公式」。

- 数据在 `config_level`：`level` / `exp_required` / `reward_points` / `scale` / `enabled` / `note`
- 管理端 A12 可增删行、逐行改值；新增行的经验值不推导，必须手填，留 0 会在发布时被拦并高亮
- 发布校验：等级从 1 连续、经验严格递增且 > 0、奖励非负
- 种子值逐字锁定文档 13.7 ② 的 9 行：`100 / 150 / 225 / 338 / 507 / 761 / 1141 / 1712 / 2568`
- 那 9 个数字无法用单一 ×1.5 规则复现（`507×1.5=760.5` 四舍五入应为 760 但文档写 761；`761×1.5=1141.5` 应为 1142 但文档写 1141），故不做推导，只作为种子灌一次
- 原实现的「按 ×1.5 重新生成」按钮与 `POST /config/generate-levels` 接口**已移除**

### 7.3 部署前必须处理

- `ADMIN_INIT_PASSWORD` 改成强口令（默认 `admin` 仅供本地联调，见下方 7.4）。
- `ASSET_BASE_URL` 填已 ICP 备案的 https 域名（不填则场景只能走包内资源）。
- `JWT_SECRET` 改成随机长串；`ALLOW_DEV_LOGIN` 关掉；`WX_APPID` / `WX_SECRET` 填上。
- Mongo 不要用 `admin` 账号，建独立账号并只授权 `pets_game`。
- 管理端目前挂在同一端口的 `/admin/`。文档 9 节建议「放内网或加 IP 白名单」，上线时在前面加一层反代限制来源。

### 7.4 单管理员模型

按 2026-10-18 决策，**只保留 `admin` 一个管理员账号，不做多账号、不做新增管理员/改密页面**。`admin_users` 集合里只有一条记录。

改密码的唯一方式：改 `ADMIN_INIT_PASSWORD` → 清空 `admin_users` 集合 → 重启（会按新值重新种子）。种子只在集合为空时执行，已有记录不会被覆盖。

RBAC 的 `super` / `config` / `readonly` 三种角色权限矩阵仍在代码里生效（`api/router.go`），只是目前只有一个 `super` 账号，将来要多账号时不用改中间件。

### 7.5 未实现 / 后续

- 看板的「接口错误率」是进程内计数，重启清零；没有落库。文档 7.3 列了这个指标，但没要求历史留存。
- 排行榜、邀请/转发奖励按文档明确不做。
- 语音、训练系统按文档 13.7 ① 与 PRD 4.7 不做，服务端没有任何 ASR 接口。

---

## 8. 与文档的差异清单（回写用）

| 文档位置 | 文档写法 | 本实现 |
|---|---|---|
| 4.1 / 4.2 | `user_points` / `inventory` / `scene_unlock` 三张表 | 合并为 `user_assets`，理由见 4.2 |
| 5（13 个接口） | 无互动经验入口 | 增 `POST /api/v1/pet/interaction` |
| 5 / 13.8② | 插屏只记曝光，但无上报接口 | 增 `POST /api/v1/ad/impression` |
| 6.1 | 读 version → 失败重试 3 次 | 改为单文档条件更新，`version` 仅留痕 |
| 6.4 | 衰减速率写死在正文 | 移入 `config_points`（3 个字段） |
| 7.2 | 保留最近 10 个版本 | 已实现，`CONFIG_RELEASE_KEEP` 可调 |
| 7.3 | 活跃宠物数 | 口径定义为「今日活跃用户拥有的宠物数」，客户端不上报宠物活跃 |
| 4.3 | `config_food` / `config_scene` 用 `name_key`（i18n 键，如 `food.normal`） | 改为 `name`，直接存中文显示名（`普通狗粮`），管理端可随时改；客户端下发字段同步从 `nameKey` 变成 `name` |
| 4.3 | `config_scene` 只有 `thumb` / `scene_key`，未定义如何定位到实际文件 | 拆成三列：`thumb` + `scene_path`（CDN 相对路径）+ `scene_key`（包内兜底），下发时拼出 `thumbUrl` / `sceneUrl` |
