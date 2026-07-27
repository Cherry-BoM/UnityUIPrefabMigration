using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// 跨 UI 架构的预制体移植工具
///
/// 用法:
/// 1. 打开 Tools/跨 UI 架构/预制体移植工具
/// 2. 选中或拖入目标根对象
/// 3. 在 CrossUIPrefabMigrationConfig 中配置 sourceName/sourceGuid 和替换目标
/// 4. 点击检查并逐项替换/移除
/// 5. 处理后检查 Inspector，并保存 Prefab 或 Scene
/// </summary>
public class CrossUIPrefabMigratorWindow : EditorWindow
{
    private GameObject targetRoot;
    private string sourceProjectPath = "";
    private Vector2 scrollPosition;
    private string lastScanNotice = "";
    private MessageType lastScanNoticeType = MessageType.Info;

    private List<MissingItem> items = new List<MissingItem>();
    private bool hasScanned;
    private string yamlSourcePath = "";  // YAML 分析用的预制体文件路径
    private bool yamlSourceIsScene;
    private bool isDirectEdit;           // true = 直接改 Hierarchy 对象，不自动保存

    // GUID 缓存
    private Dictionary<string, string> guidToNameCache = new Dictionary<string, string>();
    private string cachedSourcePath = "";

    private CrossUIPrefabMigrationConfig migrationConfig;

    [MenuItem("Tools/跨 UI 架构/预制体移植工具")]
    public static void ShowWindow()
    {
        var w = GetWindow<CrossUIPrefabMigratorWindow>("跨 UI 架构的预制体移植工具");
        w.minSize = new Vector2(600, 500);
    }

    private void OnEnable()
    {
        LoadFixConfig();
        RestoreGuidCache();
    }

    private void OnGUI()
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.HelpBox(
            "流程: 选择目标根对象 → 配置 sourceName/sourceGuid 与替换目标 → 检查 → 逐项处理。旧项目 Assets 路径只在需要把 Missing Script 的 GUID 解析成脚本名时填写。",
            MessageType.None);

        if (EditorSettings.serializationMode != SerializationMode.ForceText)
        {
            EditorGUILayout.HelpBox(
                "建议在 Edit > Project Settings > Editor 中将 Asset Serialization 设置为 Force Text。否则 Prefab/Scene 的 YAML 信息可能不可读，只能做降级扫描。",
                MessageType.Warning);
        }

        // FixConfig
        EditorGUILayout.BeginHorizontal();
        migrationConfig = (CrossUIPrefabMigrationConfig)EditorGUILayout.ObjectField("移植配置:", migrationConfig, typeof(CrossUIPrefabMigrationConfig), false);
        if (GUILayout.Button("刷新", GUILayout.Width(45))) LoadFixConfig();
        EditorGUI.BeginDisabledGroup(migrationConfig == null);
        if (GUILayout.Button("定位", GUILayout.Width(45))) PingObject(migrationConfig);
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();

        int mappingCount = migrationConfig != null && migrationConfig.mappings != null
            ? migrationConfig.mappings.Count(m => m != null)
            : 0;
        if (migrationConfig != null && mappingCount > 0)
        {
            var summaries = migrationConfig.mappings.Where(m => m != null).Select(m => {
                if (m.IsRemoveOnly) return $"{MappingSourceDisplay(m)}→[移除]";
                var names = new List<string>();
                names.AddRange(ValidTargetScripts(m).Select(s => s.name));
                names.AddRange(ValidTargetTypeNames(m));
                return $"{MappingSourceDisplay(m)}→{string.Join("+", names)}";
            });
            EditorGUILayout.HelpBox($"{mappingCount} 条映射:\n{string.Join("\n", summaries)}", MessageType.Info);
        }
        else
        {
            EditorGUILayout.HelpBox("当前配置没有映射。可以在配置资产中新增映射；未配置映射的 Missing Script 仍可逐项移除。", MessageType.Info);
        }

        var configWarnings = GetConfigWarnings();
        if (configWarnings.Count > 0)
            EditorGUILayout.HelpBox("配置检查:\n" + string.Join("\n", configWarnings), MessageType.Warning);

        EditorGUILayout.Space(6);

        // 目标对象 — allowSceneObjects: true, 从 Hierarchy 拖
        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginChangeCheck();
        targetRoot = (GameObject)EditorGUILayout.ObjectField("目标对象 (从 Hierarchy 拖入):", targetRoot, typeof(GameObject), true);
        if (EditorGUI.EndChangeCheck())
            ResetScanResults();

        if (GUILayout.Button("使用选中", GUILayout.Width(70)))
        {
            if (Selection.activeGameObject != null)
            {
                targetRoot = Selection.activeGameObject;
                ResetScanResults();
            }
            else
            {
                EditorUtility.DisplayDialog("没有选中对象", "请先在 Hierarchy 或 Prefab Mode 中选中一个 GameObject。", "确定");
            }
        }
        EditorGUILayout.EndHorizontal();

        if (targetRoot != null)
        {
            string hint = isDirectEdit ? "直接编辑模式: 处理后会标记当前场景/Prefab Mode 为已修改，请手动保存。" : "资产模式: 处理后会尝试保存 Prefab 资产。";
            EditorGUILayout.LabelField(hint, EditorStyles.miniLabel);
        }

        EditorGUILayout.Space(4);

        // 旧项目 Assets 路径
        EditorGUILayout.LabelField("旧项目 Assets 路径（可选）:", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("迁移旧项目时填写旧项目的 Assets 目录，用来把 Missing Script 的 GUID 反查为脚本名。只处理当前项目已有组件或只按 GUID 配置时可以留空。", MessageType.None);
        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginChangeCheck();
        string editedSourcePath = EditorGUILayout.TextField(sourceProjectPath);
        if (EditorGUI.EndChangeCheck())
            SetSourceProjectPath(editedSourcePath, false);
        if (GUILayout.Button("浏览", GUILayout.Width(50)))
        {
            string s = EditorUtility.OpenFolderPanel("选择源项目 Assets 目录", sourceProjectPath, "");
            if (!string.IsNullOrEmpty(s))
                SetSourceProjectPath(NormalizeSourceAssetsPath(s), true);
        }
        EditorGUILayout.EndHorizontal();

        if (!string.IsNullOrEmpty(sourceProjectPath) && !SamePath(sourceProjectPath, cachedSourcePath))
        {
            if (GUILayout.Button("加载源项目脚本映射", GUILayout.Height(22)))
                LoadGuidMapping(sourceProjectPath);
        }
        if (guidToNameCache.Count > 0)
            EditorGUILayout.LabelField($"  已加载 {guidToNameCache.Count} 条 GUID映射", EditorStyles.miniLabel);

        EditorGUILayout.Space(8);

        // 按钮
        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginDisabledGroup(targetRoot == null);
        if (GUILayout.Button("🔍 检查丢失组件", GUILayout.Height(32)))
        {
            if (migrationConfig == null) LoadFixConfig();
            if (!string.IsNullOrEmpty(sourceProjectPath) && !SamePath(sourceProjectPath, cachedSourcePath))
                LoadGuidMapping(sourceProjectPath);
            Scan();
        }
        EditorGUI.EndDisabledGroup();

        if (hasScanned && items.Exists(i => !i.replaced && !i.pathResolveFailed && i.CanApply))
        {
            GUI.backgroundColor = new Color(0.8f, 1f, 0.8f);
            if (GUILayout.Button("一键处理", GUILayout.Height(32), GUILayout.Width(90)))
                ReplaceAll();
            GUI.backgroundColor = Color.white;
        }
        EditorGUILayout.EndHorizontal();

        if (!string.IsNullOrEmpty(lastScanNotice))
            EditorGUILayout.HelpBox(lastScanNotice, lastScanNoticeType);

        EditorGUILayout.Space(8);

        // 结果
        if (hasScanned)
        {
            if (items.Count == 0)
            {
                EditorGUILayout.HelpBox("✓ 没有丢失组件！", MessageType.Info);
            }
            else
            {
                int done = items.FindAll(i => i.replaced).Count;
                EditorGUILayout.LabelField($"问题: {items.Count}  已处理: {done}", EditorStyles.boldLabel);

                scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
                for (int i = 0; i < items.Count; i++)
                    DrawItem(items[i], i);
                EditorGUILayout.EndScrollView();

                EditorGUILayout.Space(4);
                if (GUILayout.Button("复制结果到剪贴板", GUILayout.Height(24)))
                    CopyToClipboard();
            }
        }
    }

    private void DrawItem(MissingItem item, int index)
    {
        if (item.replaced)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.color = Color.gray;
            string t = item.fixIsRemoveOnly
                ? $"[{index + 1}] {item.nodePath}  →  [已移除] ✓"
                : $"[{index + 1}] {item.nodePath}  →  {item.FixTargetDisplay} ✓";
            EditorGUILayout.LabelField(t, EditorStyles.miniLabel);
            GUI.color = Color.white;
            EditorGUILayout.EndVertical();
            return;
        }

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        bool unresolvedTarget = false;

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField($"[{index + 1}]", GUILayout.Width(26));
        if (GUILayout.Button(item.nodePath, EditorStyles.linkLabel))
        {
            LocateNode(item);
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        if (item.isWrongComponent)
        {
            EditorGUILayout.LabelField("用错:", GUILayout.Width(35));
            GUI.color = new Color(1f, 0.6f, 0f); // 橙色
        }
        else
        {
            EditorGUILayout.LabelField("丢失:", GUILayout.Width(35));
            GUI.color = Color.red;
        }
        EditorGUILayout.LabelField(item.componentName, EditorStyles.boldLabel);
        GUI.color = Color.white;
        if (!string.IsNullOrEmpty(item.guid))
            EditorGUILayout.LabelField($"GUID: {item.guid}", EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        if (item.hasMapping)
        {
            if (item.fixIsRemoveOnly)
            {
                EditorGUILayout.LabelField($"映射: {item.fixSourceName} → [移除]", EditorStyles.miniLabel);
                GUI.backgroundColor = new Color(1f, 0.6f, 0.5f);
                if (GUILayout.Button("🗑 移除", GUILayout.Height(22), GUILayout.Width(80)))
                    ApplyFix(item, null);
                GUI.backgroundColor = Color.white;
            }
            else
            {
                EditorGUILayout.LabelField($"映射: {item.fixSourceName} → {item.FixTargetDisplay}", EditorStyles.miniLabel);
                GUI.backgroundColor = new Color(0.5f, 1f, 0.5f);
                EditorGUI.BeginDisabledGroup(!item.CanApply || item.pathResolveFailed);
                if (GUILayout.Button("✔ 替换", GUILayout.Height(22), GUILayout.Width(80)))
                    ApplyFix(item, item.fixTargetTypes);
                EditorGUI.EndDisabledGroup();
                GUI.backgroundColor = Color.white;
                unresolvedTarget = !item.CanApply;
            }
        }
        else
        {
            GUI.color = Color.gray;
            EditorGUILayout.LabelField(item.isWrongComponent ? "(未配置映射)" : "(未配置映射，可移除 Missing Script)", EditorStyles.miniLabel);
            GUI.color = Color.white;
            if (!item.isWrongComponent)
            {
                GUI.backgroundColor = new Color(1f, 0.75f, 0.55f);
                EditorGUI.BeginDisabledGroup(item.pathResolveFailed);
                if (GUILayout.Button("移除 Missing", GUILayout.Height(22), GUILayout.Width(100)))
                    ApplyFix(item, null);
                EditorGUI.EndDisabledGroup();
                GUI.backgroundColor = Color.white;
            }
        }
        EditorGUILayout.EndHorizontal();

        if (unresolvedTarget)
        {
            EditorGUILayout.HelpBox("替换目标无法解析，请检查配置中的脚本或组件名。", MessageType.Warning);
        }
        if (item.pathResolveFailed)
        {
            EditorGUILayout.HelpBox("找不到这个节点，无法安全处理。请确认拖入的是扫描时对应的根对象。", MessageType.Warning);
        }

        EditorGUILayout.EndVertical();

        // 点击整行空白区域定位节点
        Rect itemRect = GUILayoutUtility.GetLastRect();
        EditorGUILayout.Space(2);
        if (Event.current.type == EventType.MouseDown && Event.current.button == 0
            && itemRect.Contains(Event.current.mousePosition))
        {
            LocateNode(item);
            Event.current.Use();
        }
    }

    private void LocateNode(MissingItem item)
    {
        if (targetRoot == null || item == null) return;

        var go = FindGOByPath(targetRoot, item.nodePath);
        if (go != null)
        {
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
        }
        else
        {
            Selection.activeObject = targetRoot;
            EditorGUIUtility.PingObject(targetRoot);
        }
    }

    // ============================================================
    // 扫描
    // ============================================================

    private void Scan()
    {
        items.Clear();
        hasScanned = true;
        yamlSourcePath = "";
        yamlSourceIsScene = false;
        isDirectEdit = false;
        lastScanNotice = "";

        if (targetRoot == null) return;

        // 判断目标类型并找到 YAML 源文件
        DetermineYamlSource();

        // DetermineYamlSource 可能会提示进入 PrefabMode 并中止扫描
        if (!hasScanned) return;

        // 分析
        if (!string.IsNullOrEmpty(yamlSourcePath))
        {
            ScanFromYaml();
        }
        else
        {
            // 没有 YAML 源（纯场景对象），用运行时扫描
            ScanRuntime();
        }

        // 确定 nodePath 中引用的 GO 是否都能在 targetRoot 下找到
        // 如果找不到，尝试修正路径
        foreach (var item in items)
        {
            if (FindGOByPath(targetRoot, item.nodePath) == null)
            {
                item.pathResolveFailed = true;
            }
        }

        if (items.Count > 0)
        {
            int processable = items.FindAll(i => !i.pathResolveFailed && (i.CanApply || !i.isWrongComponent)).Count;
            Debug.Log($"[检查器] '{targetRoot.name}' 发现 {items.Count} 个问题组件 (可处理: {processable})");
            foreach (var it in items)
                Debug.Log($"  • {it.nodePath}  →  {it.componentName}" + (it.hasMapping ? $"  [{it.fixSourceName}→{it.FixTargetDisplay}]" : ""));
        }
        else
        {
            Debug.Log($"[检查器] '{targetRoot.name}' 无丢失/用错。");
        }

        Repaint();
    }

    /// <summary>
    /// 确定用于 YAML 分析的预制体文件路径
    /// </summary>
    private void DetermineYamlSource()
    {
        // 1) 如果是 PrefabMode 中的对象 → 最佳情况，随便改
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null && targetRoot.scene == stage.scene)
        {
            yamlSourcePath = stage.assetPath;
            yamlSourceIsScene = false;
            isDirectEdit = true;
            return; 
        }

        // 2) 如果是场景中的预制体实例，但不在 PrefabMode → 报错，引导用户进入 PrefabMode
        if (PrefabUtility.IsPartOfPrefabInstance(targetRoot))
        { 
            var source = PrefabUtility.GetCorrespondingObjectFromSource(targetRoot);
            string assetPath = (source != null) ? AssetDatabase.GetAssetPath(source) : "";

            // 尝试自动打开 Prefab Mode
            GameObject prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(targetRoot);
            if (prefabRoot != null && !string.IsNullOrEmpty(assetPath))
            {  
                if (EditorUtility.DisplayDialog("需要进入 Prefab Mode",
                    $"对象 '{targetRoot.name}' 是预制体 '{Path.GetFileNameWithoutExtension(assetPath)}' 的实例。\n\n" +
                    "在场景中无法直接修改预制体内部结构。\n点击「确定」自动打开 Prefab Mode。",
                    "打开 Prefab Mode", "取消"))
                {
                    // 打开对应预制体的 Prefab Mode
                    AssetDatabase.OpenAsset(AssetDatabase.LoadAssetAtPath<GameObject>(assetPath));
                    // 此时 Hierarchy 已切换到 PrefabMode，但 targetRoot 还是旧的场景对象
                    // 需要用户重新拖入
                    EditorUtility.DisplayDialog("提示",
                        "已打开 Prefab Mode。\n请重新从 Hierarchy 拖入根对象到工具窗口，然后点击检查。",
                        "确定");
                }
                yamlSourcePath = "";
                yamlSourceIsScene = false;
                isDirectEdit = false;
                hasScanned = false;
                items.Clear();
                return;
            }

            // fallback: 仍然尝试（虽然大概率失败）
            yamlSourcePath = assetPath;
            yamlSourceIsScene = false;
            isDirectEdit = true;
            return;
        }

        // 3) 如果是预制体资产 (从 Project 窗口拖入的)
        var prefabType = PrefabUtility.GetPrefabAssetType(targetRoot);
        if (prefabType != PrefabAssetType.NotAPrefab)
        {
            yamlSourcePath = AssetDatabase.GetAssetPath(targetRoot);
            yamlSourceIsScene = false;
            isDirectEdit = false;
            return;
        }

        // 4) 纯场景对象，优先读取已保存的 Scene YAML
        isDirectEdit = true;
        if (targetRoot.scene.IsValid() && !string.IsNullOrEmpty(targetRoot.scene.path))
        {
            yamlSourcePath = targetRoot.scene.path;
            yamlSourceIsScene = true;
            lastScanNotice = "当前目标是场景对象，工具会读取已保存的 Scene YAML 来识别 Missing Script。若刚修改过层级或组件，请先保存场景后再扫描。";
            lastScanNoticeType = MessageType.Info;
            return;
        }

        yamlSourcePath = "";
        yamlSourceIsScene = false;
        lastScanNotice = "当前目标来自未保存场景，无法读取 Scene YAML。工具只能检测 Missing Script，不能自动识别原脚本名/GUID；如需替换，请先保存场景、进入 Prefab Mode，或拖入 Prefab 资产。";
        lastScanNoticeType = MessageType.Warning;
    }

    /// <summary>
    /// 从 YAML 文本分析丢失组件 + 用错的组件
    /// </summary>
    private void ScanFromYaml()
    {
        string yaml;
        string yamlFilePath = AssetPathToFilePath(yamlSourcePath);
        try { yaml = File.ReadAllText(yamlFilePath); }
        catch (Exception e)
        {
            Debug.LogError($"[检查器] 读取 YAML 失败: {e.Message}");
            lastScanNotice = $"读取 YAML 失败，已降级为运行时扫描: {e.Message}";
            lastScanNoticeType = MessageType.Warning;
            ScanRuntime();
            return;
        }

        if (!LooksLikeUnityYaml(yaml))
        {
            lastScanNotice = "目标资产不是可解析的 Unity YAML 文本。请将 Asset Serialization 设置为 Force Text 后重新保存 Prefab/Scene，再执行扫描。";
            lastScanNoticeType = MessageType.Warning;
            Debug.LogWarning("[检查器] 目标资产不是可解析的 Unity YAML 文本，已降级为运行时扫描。");
            ScanRuntime();
            return;
        }

        var monoMap = ParseMonoBlocks(yaml);
        var goMap = ParseGameObjectBlocks(yaml);
        BuildPaths(yaml, goMap);
        var localGuidMap = BuildLocalGuidMap();

        // ===== 第一轮: 丢失的组件（GUID 在本地不存在） =====
        foreach (var kv in monoMap)
        {
            var mono = kv.Value;

            if (!string.IsNullOrEmpty(mono.scriptGuid) && localGuidMap.ContainsKey(mono.scriptGuid))
                continue;   // 脚本在本地存在 → 不丢失，第一轮跳过

            string displayName = ResolveScriptName(mono.scriptGuid);
            BuildItem(displayName, mono.scriptGuid, kv.Key, mono.gameObjectFileID, goMap, isWrong: false);
        }

        // ===== 第二轮: 用错的组件（GUID 在本地存在，但 FixConfig 要求替换） =====
        if (migrationConfig != null)
        {
            foreach (var kv in monoMap)
            {
                var mono = kv.Value;
                if (string.IsNullOrEmpty(mono.scriptGuid)) continue;
                if (!localGuidMap.TryGetValue(mono.scriptGuid, out string localName)) continue;

                var mapping = migrationConfig.Find(localName, mono.scriptGuid);
                if (mapping == null) continue;

                BuildItem(localName, mono.scriptGuid, kv.Key, mono.gameObjectFileID, goMap, isWrong: true);
            }
        }

        if (yamlSourceIsScene)
            FilterSceneYamlItemsToTargetRoot();

        // ===== 第三轮: 原生组件（BoxCollider等非MonoBehaviour，YAML解析不到） =====
        if (migrationConfig != null)
        {
            // 收集前两轮已覆盖的 (节点路径, 组件名) 避免重复
            var covered = new HashSet<string>();
            foreach (var it in items)
                covered.Add(it.nodePath + "|" + it.componentName);

            Transform[] all = targetRoot.GetComponentsInChildren<Transform>(true);
            foreach (Transform t in all)
            {
                Component[] comps = t.GetComponents<Component>();
                foreach (Component c in comps)
                {
                    if (c == null) continue;
                    string typeName = c.GetType().Name;

                    if (covered.Contains(GetTransformPath(t, targetRoot.transform) + "|" + typeName))
                        continue;

                    var mapping = migrationConfig.Find(typeName);
                    if (mapping == null) continue;

                    string path = GetTransformPath(t, targetRoot.transform);
                    int idx = System.Array.IndexOf(comps, c);

                    bool hasMapping = true;
                    bool isRemoveOnly = mapping.IsRemoveOnly;
                    var fixTypes = new List<Type>();
                    var fixNames = new List<string>();
                    if (!isRemoveOnly)
                        CollectFixTargets(mapping, fixTypes, fixNames);

                    items.Add(new MissingItem
                    {
                        nodePath = path,
                        componentName = typeName,
                        guid = "",
                        componentIndex = idx,
                        hasMapping = hasMapping,
                        fixSourceName = mapping.sourceName,
                        fixTargetNames = fixNames,
                        fixTargetTypes = fixTypes,
                        fixIsRemoveOnly = isRemoveOnly,
                        isWrongComponent = true,  // 原生组件当做"用错"处理
                    });
                }
            }
        }
    }

    /// <summary>
    /// 运行时扫描（无 YAML 时的 fallback）
    /// </summary>
    private void ScanRuntime()
    {
        Transform[] all = targetRoot.GetComponentsInChildren<Transform>(true);
        foreach (Transform t in all)
        {
            Component[] comps = t.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                if (comps[i] == null)
                {
                    string path = GetTransformPath(t, targetRoot.transform);

                    // 运行时只能知道是丢失组件，拿不到 GUID
                    items.Add(new MissingItem
                    {
                        nodePath = path,
                        componentName = "未知脚本 (无 YAML 信息)",
                        guid = "",
                        componentIndex = i,
                        hasMapping = false,
                    });
                }
            }
        }

        if (items.Count > 0 && string.IsNullOrEmpty(lastScanNotice))
        {
            lastScanNotice = "当前为降级扫描，只能显示“未知脚本”。可以逐项移除 Missing Script；如需替换，请扫描 Prefab 资产或进入 Prefab Mode。";
            lastScanNoticeType = MessageType.Warning;
        }
        Debug.LogWarning("[检查器] 无 YAML 源，仅能检测丢失但无法获取脚本名/GUID。");
    }

    private void BuildItem(string displayName, string guid, long monoFileID, long goFileID,
        Dictionary<long, GoBlock> goMap, bool isWrong)
    {
        string nodePath = "";
        int compIndex = -1;
        if (goMap.TryGetValue(goFileID, out var gi))
        {
            nodePath = string.IsNullOrEmpty(gi.path) ? gi.name : gi.path;
            compIndex = gi.componentFileIDs.IndexOf(monoFileID);
        }
        else
        {
            nodePath = $"(GO:{goFileID})";
        }

        bool hasMapping = false;
        bool isRemoveOnly = false;
        string fixSrc = "";
        var fixTypes = new List<Type>();
        var fixNames = new List<string>();

        if (!string.IsNullOrEmpty(displayName) && !displayName.StartsWith("未知脚本") && !displayName.StartsWith("无法识别"))
        {
            var mapping = migrationConfig?.Find(displayName, guid);
            if (mapping != null)
            {
                hasMapping = true;
                fixSrc = mapping.sourceName;
                isRemoveOnly = mapping.IsRemoveOnly;
                if (!isRemoveOnly)
                    CollectFixTargets(mapping, fixTypes, fixNames);
            }
        }

        items.Add(new MissingItem
        {
            nodePath = nodePath,
            componentName = displayName,
            guid = guid,
            monoFileID = monoFileID,
            gameObjectFileID = goFileID,
            componentIndex = compIndex,
            hasMapping = hasMapping,
            fixSourceName = fixSrc,
            fixTargetNames = fixNames,
            fixTargetTypes = fixTypes,
            fixIsRemoveOnly = isRemoveOnly,
            isWrongComponent = isWrong,
        });
    }

    // ============================================================
    // 移植处理 — 使用 GameObjectUtility.RemoveMonoBehavioursWithMissingScript
    // 因为 Unity 不允许通过 SerializedObject 直接删 m_Component 数组元素
    // 该 API 会一次性清掉同 GameObject 上所有缺失脚本，
    // 所以我们把同节点的其他条目一同处理
    // ============================================================

    private void ApplyFix(MissingItem item, List<Type> targetTypes)
    {
        if (targetRoot == null) return;
        bool removeMissingWithoutMapping = item != null && !item.isWrongComponent && targetTypes == null;
        if (item == null || (!item.CanApply && !removeMissingWithoutMapping))
        {
            EditorUtility.DisplayDialog("处理失败", "映射目标无法解析，请先检查移植配置。", "确定");
            return;
        }

        try
        {
            GameObject targetGO = FindGOByPath(targetRoot, item.nodePath);
            if (targetGO == null)
            {
                Debug.LogError($"[预制体移植] 在 Hierarchy 中找不到: {item.nodePath}");
                EditorUtility.DisplayDialog("处理失败", $"找不到节点:\n{item.nodePath}", "确定");
                return;
            }

            Undo.RegisterCompleteObjectUndo(targetGO, $"移植处理 {targetGO.name} 的组件");

            if (item.isWrongComponent)
            {
                // ---- 用错的组件：找到实际组件实例并删除 ----
                Component toRemove = FindComponentByName(targetGO, item.componentName, item.componentIndex);
                if (toRemove != null)
                {
                    Undo.DestroyObjectImmediate(toRemove);
                    Debug.Log($"[预制体移植] {item.nodePath}: 删除旧组件 {item.componentName}");
                }
                else
                {
                    Debug.LogWarning($"[预制体移植] {item.nodePath}: 找不到要删除的组件 {item.componentName}");
                }
            }
            else
            {
                // ---- 丢失的组件：用官方 API 移除所有缺失脚本 ----
                var siblings = items.FindAll(i =>
                    !i.replaced && i != item && i.nodePath == item.nodePath && !i.isWrongComponent);
                Debug.Log($"[预制体移植] {item.nodePath}: 当前节点还有 {siblings.Count} 个丢失条目，将一并处理");

                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(targetGO);
                Debug.Log($"[预制体移植] {item.nodePath}: 已移除所有缺失脚本");

                // 同节点其他丢失条目一并标记
                foreach (var sib in siblings)
                {
                    if (sib.CanApply && !sib.fixIsRemoveOnly && sib.fixTargetTypes.Count > 0)
                    {
                        foreach (var t in sib.fixTargetTypes)
                            Undo.AddComponent(targetGO, t);
                        Debug.Log($"[预制体移植] {item.nodePath}: 同时添加 {sib.FixTargetDisplay}");
                    }
                    sib.replaced = true;
                }
            }

            // 添加当前条目的所有替换组件
            if (targetTypes != null && targetTypes.Count > 0)
            {
                foreach (var t in targetTypes)
                {
                    Undo.AddComponent(targetGO, t);
                }
                Debug.Log($"[预制体移植] {item.nodePath}: 添加 {item.FixTargetDisplay}");
            }

            item.replaced = true;
            SaveTargetIfNeeded(targetGO);

            Repaint();
        }
        catch (Exception e)
        {
            Debug.LogError($"[预制体移植] 处理失败: {e.Message}\n{e.StackTrace}");
            EditorUtility.DisplayDialog("处理失败", $"{item.nodePath}\n{e.Message}", "确定");
        }
    }

    private void SaveTargetIfNeeded(GameObject modifiedGO)
    {
        if (modifiedGO == null) return;

        EditorUtility.SetDirty(modifiedGO);
        if (isDirectEdit || string.IsNullOrEmpty(yamlSourcePath))
        {
            if (modifiedGO.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(modifiedGO.scene);
            return;
        }

        string assetPath = AssetDatabase.GetAssetPath(targetRoot);
        if (string.IsNullOrEmpty(assetPath))
            assetPath = yamlSourcePath;

        GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (prefabAsset != null)
        {
            EditorUtility.SetDirty(prefabAsset);
            PrefabUtility.SavePrefabAsset(prefabAsset);
        }
        else
        {
            AssetDatabase.SaveAssets();
        }
    }

    /// <summary>
    /// 解析内置组件名（如 RectTransform, CanvasGroup）→ Type
    /// </summary>
    private Type ResolveBuiltinType(string typeName)
    {
        return ResolveComponentType(typeName, true);
    }

    private Type ResolveComponentType(string typeName, bool logWarning)
    {
        if (string.IsNullOrEmpty(typeName)) return null;

        // 尝试常见命名空间
        string[] tries = {
            $"UnityEngine.{typeName}, UnityEngine",
            $"UnityEngine.UI.{typeName}, UnityEngine.UI",
            $"UnityEngine.EventSystems.{typeName}, UnityEngine.UI",
            $"TMPro.{typeName}, Unity.TextMeshPro",
        };
        foreach (var t in tries)
        {
            var type = Type.GetType(t);
            if (type != null && typeof(Component).IsAssignableFrom(type))
                return type;
        }

        // 遍历所有程序集
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = asm.GetType($"UnityEngine.{typeName}");
                if (type != null && typeof(Component).IsAssignableFrom(type))
                    return type;
                type = asm.GetType(typeName);
                if (type != null && typeof(Component).IsAssignableFrom(type))
                    return type;
            }
            catch { }
        }

        if (logWarning)
            Debug.LogWarning($"[检查器] 无法解析内置组件类型: {typeName}");
        return null;
    }

    /// <summary>
    /// 在 GameObject 上按类型名查找组件实例
    /// </summary>
    private Component FindComponentByName(GameObject go, string typeName, int preferredIndex = -1)
    {
        Component[] comps = go.GetComponents<Component>();
        if (preferredIndex >= 0 && preferredIndex < comps.Length)
        {
            Component preferred = comps[preferredIndex];
            if (preferred != null && preferred.GetType().Name == typeName)
                return preferred;
        }

        foreach (var c in comps)
        {
            if (c != null && c.GetType().Name == typeName)
                return c;
        }
        return null;
    }

    private void ReplaceAll()
    {
        var pending = items.FindAll(i => !i.replaced && !i.pathResolveFailed && i.CanApply);
        if (pending.Count == 0)
        {
            EditorUtility.DisplayDialog("提示", "没有可处理的条目。", "确定");
            return;
        }

        if (!EditorUtility.DisplayDialog("一键处理可移植项",
            $"将对以下 {pending.Count} 条执行处理:\n\n" +
            string.Join("\n", pending.Select(p =>
                $"  {p.nodePath}: {p.fixSourceName} → {p.FixTargetDisplay}")) +
            "\n\n注意: 同节点上的所有缺失组件会一并处理。",
            "确定", "取消"))
            return;

        // 跳过已在前面批次中被处理的
        foreach (var item in pending)
        {
            if (item.replaced) continue;
            ApplyFix(item, item.fixIsRemoveOnly ? null : item.fixTargetTypes);
        }

        Repaint();
    }

    /// <summary>
    /// 按路径在 Hierarchy 中查找 GameObject
    /// </summary>
    private GameObject FindGOByPath(GameObject root, string path)
    {
        if (string.IsNullOrEmpty(path)) return root;
        string[] parts = path.Split('/');
        if (parts.Length == 0) return root;

        // 第一部分是根节点名
        if (parts[0] != root.name)
        {
            // 根名不匹配，尝试在所有子节点中按名称搜索
            // 可能是路径格式不对
            Transform found = root.transform.Find(path);
            if (found != null) return found.gameObject;

            // 再尝试: 忽略第一部分
            if (parts.Length > 1)
            {
                string subPath = string.Join("/", parts, 1, parts.Length - 1);
                found = root.transform.Find(subPath);
                if (found != null) return found.gameObject;
            }

            // 兜底: 按路径后缀匹配，避免同名节点选错
            found = FindBestByPathSuffix(root.transform, parts);
            if (found != null) return found.gameObject;

            return null;
        }

        Transform current = root.transform;
        for (int i = 1; i < parts.Length; i++)
        {
            Transform child = current.Find(parts[i]);
            if (child == null)
            {
                // 直接子节点找不到，按路径后缀匹配兜底
                // 把 parts[i..] 传给搜索方法
                string[] remaining = new string[parts.Length - i];
                Array.Copy(parts, i, remaining, 0, remaining.Length);
                child = FindBestByPathSuffix(current, remaining);
                if (child == null) return null;
            }
            current = child;
        }
        return current.gameObject;
    }

    private void FilterSceneYamlItemsToTargetRoot()
    {
        string targetPath = GetSceneTransformPath(targetRoot.transform);
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            if (!IsSameOrChildPath(item.nodePath, targetPath))
            {
                items.RemoveAt(i);
                continue;
            }

            item.nodePath = ToTargetRelativePath(item.nodePath, targetPath, targetRoot.name);
        }
    }

    private static bool IsSameOrChildPath(string path, string rootPath)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(rootPath)) return false;
        return string.Equals(path, rootPath, StringComparison.Ordinal)
            || path.StartsWith(rootPath + "/", StringComparison.Ordinal);
    }

    private static string ToTargetRelativePath(string fullPath, string targetFullPath, string targetName)
    {
        if (string.Equals(fullPath, targetFullPath, StringComparison.Ordinal))
            return targetName;

        return targetName + fullPath.Substring(targetFullPath.Length);
    }

    /// <summary>
    /// 在子树中搜索与目标路径后缀最匹配的 Transform。
    /// 收集所有"最后一段名字匹配"的候选，按路径后缀重合度排序，返回最佳匹配。
    /// 避免多层级下同名节点选错的问题。
    /// </summary>
    /// <param name="root">搜索根</param>
    /// <param name="targetParts">目标路径片段（例如 ["item","ruleMsg"]）</param>
    private Transform FindBestByPathSuffix(Transform root, string[] targetParts)
    {
        if (targetParts.Length == 0) return null;
        string targetName = targetParts[targetParts.Length - 1];

        // 收集所有名字匹配的 Transform
        var candidates = new List<Transform>();
        CollectByName(root, targetName, candidates);

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        // 多个同名节点: 对每个候选向上追溯路径，比较与 targetParts 后缀的重合度
        int bestScore = -1;
        Transform best = null;
        foreach (var t in candidates)
        {
            var actualParts = GetPathPartsUpTo(t, root); // e.g. ["root","a","b","title"]
            int score = MatchSuffixScore(actualParts, targetParts);
            if (score > bestScore) { bestScore = score; best = t; }
        }
        return best;
    }

    /// <summary>
    /// 收集子树中所有名字为 name 的 Transform
    /// </summary>
    private void CollectByName(Transform parent, string name, List<Transform> results)
    {
        if (parent.name == name) results.Add(parent);
        for (int i = 0; i < parent.childCount; i++)
            CollectByName(parent.GetChild(i), name, results);
    }

    /// <summary>
    /// 从 t 向上走到 root，返回路径名字列表（从 root 到 t）
    /// </summary>
    private List<string> GetPathPartsUpTo(Transform t, Transform root)
    {
        var parts = new List<string>();
        Transform cur = t;
        while (cur != null && cur != root)
        {
            parts.Insert(0, cur.name);
            cur = cur.parent;
        }
        if (cur == root)
            parts.Insert(0, root.name);
        return parts;
    }

    /// <summary>
    /// 计算两个路径后缀的重合度：从末尾向前比较，连续匹配的段数。
    /// 例如 actual=["root","view","panel","title"] target=["panel","title"] → score=2
    /// actual=["root","popup","title"]      target=["panel","title"] → score=1 (只有 "title" 匹配)
    /// </summary>
    private int MatchSuffixScore(List<string> actualParts, string[] targetParts)
    {
        int score = 0;
        int ai = actualParts.Count - 1;
        int ti = targetParts.Length - 1;
        while (ai >= 0 && ti >= 0 && actualParts[ai] == targetParts[ti])
        {
            score++;
            ai--;
            ti--;
        }
        return score;
    }

    private string GetTransformPath(Transform current, Transform root)
    {
        if (current == root) return root.name;
        string path = current.name;
        Transform parent = current.parent;
        while (parent != null && parent != root)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }
        return root.name + "/" + path;
    }

    private string GetSceneTransformPath(Transform current)
    {
        string path = current.name;
        Transform parent = current.parent;
        while (parent != null)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }
        return path;
    }

    // ============================================================
    // YAML 解析
    // ============================================================

    private Dictionary<long, MonoBlock> ParseMonoBlocks(string yaml)
    {
        var result = new Dictionary<long, MonoBlock>();
        var seen = new HashSet<long>();
        const string blockEnd = @"(?=\r?\n---\s*!u!|\z)";

        foreach (Match m in Regex.Matches(yaml,
            @"---\s*!u!114\s*&(-?\d+)\s*\r?\nMonoBehaviour:(.*?)" + blockEnd, RegexOptions.Singleline))
            AddMono(m, result, seen);

        return result;
    }

    private void AddMono(Match m, Dictionary<long, MonoBlock> result, HashSet<long> seen)
    {
        if (!long.TryParse(m.Groups[1].Value, out long id) || !seen.Add(id)) return;
        string body = m.Groups[2].Value;
        var info = new MonoBlock();

        var sm = Regex.Match(body, @"m_Script:\s*\{[^}]*fileID:\s*(-?\d+)[^}]*guid:\s*([a-fA-F0-9]+)");
        if (sm.Success) { info.scriptFileID = sm.Groups[1].Value; info.scriptGuid = sm.Groups[2].Value; }
        var gm = Regex.Match(body, @"m_GameObject:\s*\{[^}]*fileID:\s*(-?\d+)");
        if (gm.Success && long.TryParse(gm.Groups[1].Value, out long goFileID))
            info.gameObjectFileID = goFileID;

        result[id] = info;
    }

    private Dictionary<long, GoBlock> ParseGameObjectBlocks(string yaml)
    {
        var result = new Dictionary<long, GoBlock>();
        var seen = new HashSet<long>();
        const string blockEnd = @"(?=\r?\n---\s*!u!|\z)";

        foreach (Match m in Regex.Matches(yaml,
            @"---\s*!u!1\s*&(-?\d+)\s*\r?\nGameObject:(.*?)" + blockEnd, RegexOptions.Singleline))
            AddGo(m, result, seen);

        return result;
    }

    private void AddGo(Match m, Dictionary<long, GoBlock> result, HashSet<long> seen)
    {
        if (!long.TryParse(m.Groups[1].Value, out long id) || !seen.Add(id)) return;
        string body = m.Groups[2].Value;
        var info = new GoBlock();
        var nm = Regex.Match(body, @"m_Name:\s*(.+)");
        info.name = nm.Success ? nm.Groups[1].Value.Trim() : "(unnamed)";
        foreach (Match cm in Regex.Matches(body, @"-\s*component:\s*\{[^}]*fileID:\s*(-?\d+)"))
            if (long.TryParse(cm.Groups[1].Value, out long cid))
                info.componentFileIDs.Add(cid);
        result[id] = info;
    }

    private void BuildPaths(string yaml, Dictionary<long, GoBlock> goMap)
    {
        string p = @"---\s*!u!(?:4|224)\s*&(-?\d+)\s*\r?\n(?:Rect)?Transform:(.*?)(?=\r?\n---\s*!u!|\z)";

        var txToGo = new Dictionary<long, long>();
        var goParent = new Dictionary<long, long>();

        foreach (Match m in Regex.Matches(yaml, p, RegexOptions.Singleline).Cast<Match>())
        {
            if (!long.TryParse(m.Groups[1].Value, out long txID)) continue;
            string body = m.Groups[2].Value;
            var gm = Regex.Match(body, @"m_GameObject:\s*\{[^}]*fileID:\s*(-?\d+)");
            var fm = Regex.Match(body, @"m_Father:\s*\{[^}]*fileID:\s*(-?\d+)");
            if (gm.Success && long.TryParse(gm.Groups[1].Value, out long goID))
            {
                txToGo[txID] = goID;
                if (fm.Success && long.TryParse(fm.Groups[1].Value, out long fatherTx))
                    if (!goParent.ContainsKey(txID)) goParent[txID] = fatherTx;
            }
        }

        var goChildToParent = new Dictionary<long, long>();
        foreach (var kv in goParent)
            if (txToGo.TryGetValue(kv.Key, out long cgo) && txToGo.TryGetValue(kv.Value, out long fgo))
                goChildToParent[cgo] = fgo;

        foreach (var kv in goMap)
        {
            string path = kv.Value.name;
            long cur = kv.Key;
            var visited = new HashSet<long>();
            while (goChildToParent.TryGetValue(cur, out long parent) && visited.Add(cur))
            {
                if (goMap.TryGetValue(parent, out var pi)) path = pi.name + "/" + path;
                cur = parent;
            }
            kv.Value.path = path;
        }
    }

    // ============================================================
    // FixConfig
    // ============================================================

    private void LoadFixConfig()
    {
        if (migrationConfig == null)
            migrationConfig = CrossUIPrefabMigrationConfig.GetOrCreate();
        if (migrationConfig != null && migrationConfig.mappings == null)
            migrationConfig.mappings = new List<ComponentMigrationRule>();
    }

    private void ResetScanResults()
    {
        items.Clear();
        hasScanned = false;
        lastScanNotice = "";
        yamlSourcePath = "";
        yamlSourceIsScene = false;
        isDirectEdit = false;
    }

    private void PingObject(UnityEngine.Object obj)
    {
        if (obj == null) return;
        Selection.activeObject = obj;
        EditorGUIUtility.PingObject(obj);
    }

    private List<string> GetConfigWarnings()
    {
        var warnings = new List<string>();
        if (migrationConfig == null || migrationConfig.mappings == null)
            return warnings;

        for (int i = 0; i < migrationConfig.mappings.Count; i++)
        {
            var mapping = migrationConfig.mappings[i];
            if (mapping == null) continue;

            string source = MappingSourceDisplay(mapping);
            if (string.IsNullOrEmpty(source))
            {
                warnings.Add($"第 {i + 1} 条映射缺少 sourceName/sourceGuid。");
                continue;
            }

            if (mapping.IsRemoveOnly)
                continue;

            int resolvedCount = 0;
            foreach (var script in ValidTargetScripts(mapping))
            {
                Type type = script.GetClass();
                if (type != null && typeof(Component).IsAssignableFrom(type))
                    resolvedCount++;
                else
                    warnings.Add($"{source}: 脚本 {script.name} 不是可添加的 Component。");
            }

            foreach (string typeName in ValidTargetTypeNames(mapping))
            {
                if (ResolveComponentType(typeName, false) != null)
                    resolvedCount++;
                else
                    warnings.Add($"{source}: 无法解析组件类型 {typeName}。");
            }

            if (resolvedCount == 0)
                warnings.Add($"{source}: 没有可用替换目标；如需删除，请清空目标列表。");
        }

        const int maxVisibleWarnings = 8;
        if (warnings.Count > maxVisibleWarnings)
        {
            int hidden = warnings.Count - maxVisibleWarnings;
            warnings = warnings.Take(maxVisibleWarnings).ToList();
            warnings.Add($"还有 {hidden} 条配置问题未显示。");
        }

        return warnings.Select(w => "• " + w).ToList();
    }

    private static string MappingSourceDisplay(ComponentMigrationRule mapping)
    {
        if (mapping == null) return "";
        if (!string.IsNullOrWhiteSpace(mapping.sourceName)) return mapping.sourceName.Trim();
        if (!string.IsNullOrWhiteSpace(mapping.sourceGuid)) return $"GUID:{mapping.sourceGuid.Trim()}";
        return "";
    }

    private static IEnumerable<MonoScript> ValidTargetScripts(ComponentMigrationRule mapping)
    {
        return mapping?.targetScripts?.Where(s => s != null) ?? Enumerable.Empty<MonoScript>();
    }

    private static IEnumerable<string> ValidTargetTypeNames(ComponentMigrationRule mapping)
    {
        return mapping?.targetTypeNames?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())
            ?? Enumerable.Empty<string>();
    }

    private void CollectFixTargets(ComponentMigrationRule mapping, List<Type> fixTypes, List<string> fixNames)
    {
        foreach (var script in ValidTargetScripts(mapping))
        {
            fixNames.Add(script.name);
            Type type = script.GetClass();
            if (type != null && typeof(Component).IsAssignableFrom(type))
            {
                if (!fixTypes.Contains(type))
                    fixTypes.Add(type);
            }
            else
            {
                Debug.LogWarning($"[检查器] 脚本 {script.name} 不是 Component，或当前无法解析。");
            }
        }

        foreach (string typeName in ValidTargetTypeNames(mapping))
        {
            fixNames.Add(typeName);
            Type type = ResolveBuiltinType(typeName);
            if (type != null)
            {
                if (!fixTypes.Contains(type))
                    fixTypes.Add(type);
            }
        }
    }

    // ============================================================
    // GUID
    // ============================================================

    private Dictionary<string, string> BuildLocalGuidMap()
    {
        var map = new Dictionary<string, string>();
        foreach (string g in AssetDatabase.FindAssets("t:MonoScript"))
        {
            string p = AssetDatabase.GUIDToAssetPath(g);
            if (!string.IsNullOrEmpty(p) && !map.ContainsKey(g))
                map[g] = Path.GetFileNameWithoutExtension(p);
        }
        return map;
    }

    private string ResolveScriptName(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return "无法识别 (无 GUID)";
        string lp = AssetDatabase.GUIDToAssetPath(guid);
        if (!string.IsNullOrEmpty(lp)) return Path.GetFileNameWithoutExtension(lp);
        if (guidToNameCache.TryGetValue(guid, out string n)) return n;
        return $"未知脚本 (GUID: {guid})";
    }

    private void LoadGuidMapping(string sourcePath)
    {
        sourcePath = NormalizeSourceAssetsPath(sourcePath);
        SetSourceProjectPath(sourcePath, false);
        guidToNameCache.Clear();
        if (!Directory.Exists(sourcePath))
        {
            EditorUtility.DisplayDialog("错误", $"路径不存在:\n{sourcePath}", "确定");
            return;
        }
        try
        {
            int cnt = 0;
            foreach (string mf in Directory.GetFiles(sourcePath, "*.cs.meta", SearchOption.AllDirectories))
            {
                try
                {
                    string metaText = File.ReadAllText(mf);
                    Match guidMatch = Regex.Match(metaText, @"^\s*guid:\s*([a-fA-F0-9]+)\s*$", RegexOptions.Multiline);
                    string guid = guidMatch.Success ? guidMatch.Groups[1].Value.Trim() : "";
                    if (!string.IsNullOrEmpty(guid))
                    {
                        string name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(mf));
                        if (!guidToNameCache.ContainsKey(guid)) { guidToNameCache[guid] = name; cnt++; }
                    }
                }
                catch { }
            }
            if (cnt == 0)
            {
                cachedSourcePath = "";
                EditorUtility.DisplayDialog("未找到脚本映射",
                    "没有找到任何 .cs.meta。请确认选择的是旧项目的 Assets 目录，且旧项目脚本文件旁边保留了 .meta 文件。",
                    "确定");
                return;
            }

            cachedSourcePath = sourcePath;
            SaveGuidCache();
            Debug.Log($"[检查器] 源项目映射: {cnt} 条。");
            EditorUtility.DisplayDialog("加载完成", $"已从以下目录加载 {cnt} 条脚本 GUID 映射:\n{sourcePath}", "确定");
        }
        catch (Exception e) { EditorUtility.DisplayDialog("错误", e.Message, "确定"); }
    }

    private void SetSourceProjectPath(string path, bool normalize)
    {
        string nextPath = normalize ? NormalizeSourceAssetsPath(path) : path;
        if (sourceProjectPath == nextPath) return;

        sourceProjectPath = nextPath;
        if (!SamePath(sourceProjectPath, cachedSourcePath))
        {
            guidToNameCache.Clear();
            cachedSourcePath = "";
        }
    }

    private static string NormalizeSourceAssetsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        path = path.Trim().Trim('"');
        if (!Directory.Exists(path)) return path;

        var dir = new DirectoryInfo(path);
        if (string.Equals(dir.Name, "Assets", StringComparison.OrdinalIgnoreCase))
            return dir.FullName;

        string assetsPath = Path.Combine(dir.FullName, "Assets");
        return Directory.Exists(assetsPath) ? assetsPath : dir.FullName;
    }

    private static bool SamePath(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)) return true;
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

        try
        {
            string pa = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string pb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private void CopyToClipboard()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"对象: {targetRoot.name}");
        sb.AppendLine($"问题: {items.Count}  已处理: {items.FindAll(i => i.replaced).Count}");
        sb.AppendLine(new string('-', 50));
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            sb.AppendLine($"[{i + 1}] {it.nodePath}  {it.componentName}");
            sb.AppendLine($"    GUID: {it.guid}  " + (it.hasMapping ? $"映射: {it.fixSourceName}→{it.FixTargetDisplay}" : "无映射"));
            sb.AppendLine();
        }
        GUIUtility.systemCopyBuffer = sb.ToString();
        Debug.Log("[检查器] 已复制到剪贴板。");
    }

    // ============================================================
    // GUID 缓存持久化
    // ============================================================

    private static string ProjectRootPath
    {
        get
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
                projectRoot = Application.dataPath;

            return projectRoot;
        }
    }

    private static string CacheFilePath => Path.Combine(ProjectRootPath, "Library", "CrossUIPrefabMigrator", "ScriptGuidCache.json");

    private static string AssetPathToFilePath(string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath)) return "";
        if (Path.IsPathRooted(assetPath)) return assetPath;

        string normalizedAssetPath = assetPath.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(ProjectRootPath, normalizedAssetPath));
    }

    private static bool LooksLikeUnityYaml(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.StartsWith("%YAML", StringComparison.Ordinal) || text.Contains("--- !u!");
    }

    private void SaveGuidCache()
    {
        try
        {
            var data = new GuidCacheData
            {
                sourcePath = cachedSourcePath,
                mappings = guidToNameCache.Select(kv => new GuidCacheEntry { guid = kv.Key, name = kv.Value }).ToList(),
            };
            string dir = Path.GetDirectoryName(CacheFilePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(CacheFilePath, JsonUtility.ToJson(data, true), System.Text.Encoding.UTF8);
        }
        catch (Exception e) { Debug.LogWarning($"[检查器] 保存缓存失败: {e.Message}"); }
    }

    private void RestoreGuidCache()
    {
        try
        {
            if (!File.Exists(CacheFilePath)) return;
            var data = JsonUtility.FromJson<GuidCacheData>(File.ReadAllText(CacheFilePath, System.Text.Encoding.UTF8));
            if (data == null || data.mappings == null || data.mappings.Count == 0) return;

            // 恢复 path 到 UI 字段（如果当前为空或与缓存一致）
            if (!string.IsNullOrEmpty(data.sourcePath))
            {
                if (string.IsNullOrEmpty(sourceProjectPath) || sourceProjectPath == data.sourcePath)
                {
                    sourceProjectPath = data.sourcePath;
                }
            }

            // 只有当 UI 上的路径与缓存路径一致时才恢复映射
            if (sourceProjectPath == data.sourcePath)
            {
                guidToNameCache.Clear();
                foreach (var e in data.mappings)
                {
                    if (!string.IsNullOrEmpty(e.guid) && !guidToNameCache.ContainsKey(e.guid))
                        guidToNameCache[e.guid] = e.name;
                }
                cachedSourcePath = data.sourcePath;
                Debug.Log($"[检查器] 已从缓存恢复 {guidToNameCache.Count} 条 GUID 映射 (源: {data.sourcePath})");
            }
        }
        catch (Exception e) { Debug.LogWarning($"[检查器] 加载缓存失败: {e.Message}"); }
    }

    // ============================================================
    // 数据结构
    // ============================================================

    [System.Serializable]
    private class GuidCacheData
    {
        public string sourcePath = "";
        public List<GuidCacheEntry> mappings = new List<GuidCacheEntry>();
    }

    [System.Serializable]
    private class GuidCacheEntry
    {
        public string guid;
        public string name;
    }

    private class MonoBlock
    {
        public string scriptGuid = "";
        public string scriptFileID = "";
        public long gameObjectFileID;
    }

    private class GoBlock
    {
        public string name = "";
        public string path = "";
        public List<long> componentFileIDs = new List<long>();
    }

    private class MissingItem
    {
        public string nodePath;
        public string componentName;
        public string guid;
        public long monoFileID;
        public long gameObjectFileID;
        public int componentIndex = -1;
        public bool hasMapping;
        public string fixSourceName;
        public List<string> fixTargetNames = new List<string>();  // 替换目标组件名列表
        public List<Type> fixTargetTypes = new List<Type>();       // 替换目标组件类型列表
        public bool fixIsRemoveOnly;
        public bool replaced;
        public bool isWrongComponent;
        public bool pathResolveFailed;

        public bool CanApply => hasMapping && (fixIsRemoveOnly || (fixTargetTypes != null && fixTargetTypes.Count > 0));

        public string FixTargetDisplay => (fixIsRemoveOnly || (!hasMapping && !isWrongComponent)) ? "[移除]"
            : (fixTargetNames.Count > 0 ? string.Join("+", fixTargetNames) : "");
    }
}
