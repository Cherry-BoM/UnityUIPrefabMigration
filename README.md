# Unity跨UI架构预制体移植工具

用于在旧UI框架迁移到新UI框架时，批量检查 Prefab 或 Scene 中的 Missing Script，并按配置将旧组件替换为新组件，或安全移除不再需要的旧组件。

这个工具适合处理如下场景：

- 旧项目脚本丢失，Prefab 上只剩 Missing Script。
- 新旧 UI 架构组件名、脚本 GUID 或组件类型不一致，需要批量替换。
- 希望先扫描出所有问题节点，再逐项确认或一键处理。
- 迁移大量 UI Prefab 时，需要通过旧项目 `.meta` 文件把 Missing Script 的 GUID 反查为旧脚本名。

## 功能特性

- 可加载旧项目 `Assets` 目录，建立 `GUID -> 脚本名` 缓存。
- 使用 `ScriptableObject` 配置迁移规则。
- 支持逐项替换、逐项移除和一键处理。
- 支持替换当前项目中已经存在但需要迁移的旧组件。

## 安装方式

### 方式一：导入 unitypackage

在你的 Unity 项目中导入：

```text
package/UiPrefabMigration.unitypackage
```

导入后会在 `Assets/Editor` 下生成工具脚本。

### 方式二：复制源码

将以下文件复制到目标 Unity 项目的 `Assets/Editor` 目录：

```text
Project/Assets/Editor/CrossUIPrefabMigratorWindow.cs
Project/Assets/Editor/CrossUIPrefabMigrationConfig.cs
```

如果没有配置资产，工具打开时会自动创建默认配置：

```text
Assets/Editor/CrossUIPrefabMigrationConfig.asset
```

## 环境要求

- 示例工程版本：Unity `2018.4.19f1`，2021也兼容。
- 工具为 Editor 扩展，需要放在 `Editor` 目录下使用。

## 截图说明

### 1. 创建迁移配置

在 Project 窗口中右键创建配置资产：

```text
Create > Tools > 创建预制体移植配置
```

![创建迁移配置](img/1.png)

### 2. 选择配置与加载旧项目映射

工具窗口会显示当前迁移配置、映射摘要、目标对象、旧项目 `Assets` 路径和扫描按钮。

旧项目路径用于读取旧项目中的 `.cs.meta` 文件，并将脚本 GUID 解析为脚本名。映射信息会缓存到当前项目的 `Library/CrossUIPrefabMigrator` 下。

![选择配置与加载旧项目映射](img/2.png)

### 3. 配置组件映射规则

每条规则代表一个迁移关系：

```text
sourceName/sourceGuid -> targetScripts + targetTypeNames
```

示例：

```text
Button    -> Image
ImageTest -> Image
```

![配置组件映射规则](img/3.png)

字段说明：

| 字段 | 说明 |
| --- | --- |
| `sourceName` | 原脚本名或原组件名。适合能够识别旧脚本名，或替换当前项目中已有旧组件的场景。 |
| `sourceGuid` | 原脚本 GUID。适合旧脚本已经丢失，只能从 Prefab/Scene YAML 中读取 GUID 的场景。 |
| `targetScripts` | 替换为的新脚本列表，直接拖入 `.cs` 脚本文件。 |
| `targetTypeNames` | 替换为的组件类型名，例如 `RectTransform`、`CanvasGroup`、`Image`、`Button`、`TextMeshProUGUI`。 |

如果 `targetScripts` 和 `targetTypeNames` 都为空，这条规则表示只移除旧组件，不添加新组件。

### 4. 扫描并处理

扫描完成后，工具会列出问题节点、旧组件名、GUID 和对应映射。可以逐项处理，也可以在确认结果无误后执行一键处理。

![扫描并处理](img/4.png)

## 处理模式

### Prefab Mode

在 Prefab Mode 中选择根对象时，工具会直接处理当前打开的 Prefab 内容。处理后会标记为已修改，需要手动保存。

## 注意事项

- 迁移前建议先提交 Git 或备份 Prefab/Scene。
- 替换逻辑是“移除旧组件或 Missing Script，然后添加新组件”，不会自动迁移旧组件上的序列化字段值。
- Unity 官方 API `GameObjectUtility.RemoveMonoBehavioursWithMissingScript` 会一次性移除同一个 GameObject 上的所有 Missing Script；工具会同步标记同节点上的其他缺失条目。
- `targetScripts` 中的脚本必须能解析为 `Component` 类型，否则不会被添加。
- `targetTypeNames` 会尝试在 `UnityEngine`、`UnityEngine.UI`、`UnityEngine.EventSystems`、`TMPro` 以及当前已加载程序集里解析。
- 如果扫描结果提示找不到节点，请确认拖入的是扫描时对应的根对象。
- 对复杂 Prefab 建议先逐项替换一遍，确认结果符合预期后再使用一键处理。

## 常见问题

### 为什么替换后新组件字段是默认值？

工具当前只负责组件级迁移，不负责字段级数据迁移。新组件添加后会使用 Unity 默认序列化值，字段数据需要手动补齐，或根据项目规则扩展工具。

### 一键处理安全吗？

一键处理适合映射规则已经确认无误的情况。第一次迁移某类 Prefab 时，建议先使用逐项处理验证结果。
