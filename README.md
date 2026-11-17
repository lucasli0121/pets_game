# 宠屋养成记 / pets_game

3D 宠物养成游戏的工程仓库，包含 Unity 客户端、Go 服务端与配套文档。

## 目录

| 路径 | 内容 |
| --- | --- |
| `unity_3d_pets/unity_3d_pets/` | Unity 6 工程（URP）。`Assets/Scripts/` 为业务代码 |
| `pets_server/` | Go + net/http + MongoDB 服务端，内置管理端 |
| `docs/` | 需求文档、技术方案、后台设计 |
| `art/` | 图标、场景背景与生成脚本 |

## 环境

- Unity `6000.6.4f1`（见 `ProjectSettings/ProjectVersion.txt`）
- Go 1.2x，MongoDB 5.x 以上
- 服务端环境变量见 `pets_server/.env.example`，复制为 `.env` 后按需修改

## 第三方资产

以下两项不在仓库内，缺失时工程无法直接编译或构建，需自行获取后放到指定路径：

| 路径 | 资产 | 获取方式 |
| --- | --- | --- |
| `unity_3d_pets/unity_3d_pets/Assets/ithappy/Animals_FREE/` | **ithappy Animals FREE** | Unity Asset Store 获取后导入到同一路径 |
| `unity_3d_pets/unity_3d_pets/Assets/WX-WASM-SDK-V2/` | **微信小游戏 Unity 转换 SDK** | 从微信官方获取后导入到同一路径 |

两者都不在本仓库的 MIT 许可范围内，具体见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
工程其余部分（脚本、场景、渲染设置、WebGL 模板）均已入库。

## 许可

- 自写代码：MIT，见 [LICENSE](LICENSE)
- 第三方资产、美术素材、文档：**不在 MIT 范围内**，具体见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)

整体再分发本仓库会同时构成对第三方资产与美术素材的再分发，请先阅读上述文件。
