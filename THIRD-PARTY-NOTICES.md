# 第三方资产与许可边界

本仓库不是单一许可。根目录 `LICENSE` 的 MIT 条款**只覆盖作者自写的代码**，
不覆盖以下任何一类内容。使用前请先确认自己要用的是哪一部分。

## 1. MIT 覆盖的部分

| 路径 | 内容 |
| --- | --- |
| `unity_3d_pets/unity_3d_pets/Assets/Scripts/` | 全部自写 C# 脚本（行为、动画编排、交互、UI、数据） |
| `pets_server/`（除第三方素材外） | Go 服务端源码、内置管理端前端、测试 |
| `art/**/*.py` | 图标与素材的本地生成脚本 |
| `unity_3d_pets/**/*.py` | 构建与辅助脚本 |

## 2. 不覆盖的部分

### 2.1 第三方 Unity 资产

| 路径 | 资产 | 说明 |
| --- | --- | --- |
| `unity_3d_pets/unity_3d_pets/Assets/ithappy/Animals_FREE/` | **ithappy Animals FREE**（含 7 个 fbx、7 个 prefab、7 个 Animator Controller、23 个 anim、材质与贴图，以及资产包自带的 demo 脚本） | 来源于 Unity Asset Store，按 Unity Asset Store 最终用户许可协议（EULA）授权给**该资产的购买者/获取者本人**使用。MIT 不适用于这些文件。 |

上述资产的权利不属于本仓库作者。本仓库保留该目录仅用于记录本地开发时的工程状态。
任何人在自己项目中使用这些资产，应从 Unity Asset Store 正规获取，并遵守对应 EULA。
若权利人要求移除，本仓库将删除该目录，改为在 README 中提供资产获取与导入说明。

### 2.2 字体

| 路径 | 字体 | 许可 |
| --- | --- | --- |
| `unity_3d_pets/unity_3d_pets/Assets/Resources/PetFont/NotoSansSC-Pet.otf` | Noto Sans SC 的子集化版本 | **SIL Open Font License 1.1**（OFL-1.1） |

该字体可以随软件一起分发、嵌入与再分发，前提是保留 OFL 许可声明，且不得单独销售字体本身。
本文件中的 `NotoSansSC-Pet.otf` 是原字体的字形子集（已修改版本），按 OFL 要求在此注明这一修改。
OFL 全文见 <https://scripts.sil.org/OFL>。

### 2.3 美术素材

| 路径 | 说明 |
| --- | --- |
| `art/`（png / jpg） | 图标、场景背景、道具图。部分由 AI 生成工具产出，商用条款以生成平台的服务条款为准，本仓库不授予再许可。 |
| `IconPreview/`、`unity_3d_pets/IconPreview/` | 图标生成的中间产物与切图预览。 |
| `docs/*.png`、`docs/*.html` | 设计展板与产品示意图。 |

以上素材**保留所有权利**（All rights reserved）。未经作者书面许可，不得复制、
再分发或用于商业用途。

### 2.4 文档

`docs/` 下的需求文档、技术方案、后台设计、资质清单等（含 5 份 docx 与 4 份 md），
以及根目录的实施路线文档，均为**保留所有权利**。可以阅读，不得转载、改编、
用于商业产品，也不得作为自己项目的需求文档直接使用。

### 2.5 服务端内置资源

`pets_server/assets/scenes/` 下的场景图沿用 2.2 的规则：保留所有权利。

## 3. 组合使用的含义

如果你 fork 本仓库用于自己的项目，实际可自由使用的是第 1 节列出的代码，
第 2 节列出的内容需要替换为自己的资产或另行取得授权。直接整体再分发本仓库，
会同时构成对第三方资产与美术素材的再分发。

## 4. 免责

本仓库按「现状」提供，不附带任何明示或默示担保。作者不对使用本仓库内容
导致的任何直接或间接损失承担责任，也不对使用者自行引入的第三方资产授权问题负责。
