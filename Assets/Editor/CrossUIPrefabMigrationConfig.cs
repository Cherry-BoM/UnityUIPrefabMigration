using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;

/// <summary>
/// 跨 UI 架构预制体移植映射配置 (ScriptableObject)
///
/// 每条映射: 原脚本名/GUID → 目标组件列表(MonoScript + 内置组件名)
/// targetScripts + targetTypeNames 都为空 = 仅移除
/// </summary>
[CreateAssetMenu(fileName = "CrossUIPrefabMigrationConfig", menuName = "Tools/创建预制体移植配置", order = 0)]
public class CrossUIPrefabMigrationConfig : ScriptableObject
{
    public List<ComponentMigrationRule> mappings = new List<ComponentMigrationRule>();

    private void OnValidate()
    {
        if (mappings == null)
            mappings = new List<ComponentMigrationRule>();
    }

    public ComponentMigrationRule Find(string sourceName, string sourceGuid = "")
    {
        if ((string.IsNullOrWhiteSpace(sourceName) && string.IsNullOrWhiteSpace(sourceGuid)) || mappings == null)
            return null;

        sourceName = sourceName?.Trim();
        sourceGuid = sourceGuid?.Trim();
        foreach (var m in mappings)
        {
            if (m == null) continue;
            if (!string.IsNullOrWhiteSpace(sourceGuid)
                && string.Equals(m.sourceGuid?.Trim(), sourceGuid, StringComparison.OrdinalIgnoreCase))
                return m;

            if (!string.IsNullOrWhiteSpace(sourceName)
                && string.Equals(m.sourceName?.Trim(), sourceName, StringComparison.Ordinal))
                return m;
        }
        return null;
    }

    private const string DefaultAssetPath = "Assets/Editor/CrossUIPrefabMigrationConfig.asset";

    /// <summary>
    /// 获取或创建配置资产（空配置，不含任何映射）
    /// </summary>
    public static CrossUIPrefabMigrationConfig GetOrCreate()
    {
        var cfg = AssetDatabase.LoadAssetAtPath<CrossUIPrefabMigrationConfig>(DefaultAssetPath);
        if (cfg != null) return cfg;

        string[] guids = AssetDatabase.FindAssets("t:CrossUIPrefabMigrationConfig");
        if (guids.Length > 0)
        {
            string p = AssetDatabase.GUIDToAssetPath(guids[0]);
            return AssetDatabase.LoadAssetAtPath<CrossUIPrefabMigrationConfig>(p);
        }

        var newCfg = ScriptableObject.CreateInstance<CrossUIPrefabMigrationConfig>();
        if (!AssetDatabase.IsValidFolder("Assets/Editor"))
            AssetDatabase.CreateFolder("Assets", "Editor");

        AssetDatabase.CreateAsset(newCfg, DefaultAssetPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[CrossUIPrefabMigrationConfig] 已创建空配置: {DefaultAssetPath}");
        return newCfg;
    }
}

[System.Serializable]
public class ComponentMigrationRule
{
    [Tooltip("原脚本名或原组件名。用于脚本名可识别、或替换当前项目中已有组件时匹配。")]
    public string sourceName;

    [Tooltip("可选。原脚本 GUID。用于没有旧项目源码、只能从 Missing Script 上看到 GUID 时匹配。")]
    public string sourceGuid;

    [Tooltip("替换为的脚本列表（拖入 .cs 文件）")]
    public List<MonoScript> targetScripts = new List<MonoScript>();

    [Tooltip("内置组件名（如 RectTransform, CanvasGroup），填写类名即可")]
    public List<string> targetTypeNames = new List<string>();

    public bool IsRemoveOnly
    {
        get
        {
            bool hasScriptTarget = targetScripts != null && targetScripts.Exists(s => s != null);
            bool hasTypeNameTarget = targetTypeNames != null && targetTypeNames.Exists(s => !string.IsNullOrWhiteSpace(s));
            return !hasScriptTarget && !hasTypeNameTarget;
        }
    }
}
