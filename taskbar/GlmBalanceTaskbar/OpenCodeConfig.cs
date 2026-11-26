using System.Text.Json;

namespace GlmBalanceTaskbar;

/// <summary>
/// opencode 配置文件统一助手（部署器 / 发现 / key 链三处共用）：
/// 1. 路径解析（jsonc 优先）：opencode 官方语义为同目录 opencode.json / opencode.jsonc 并存时
///    静默合并且 jsonc 覆盖 json（config.json &lt; opencode.json &lt; opencode.jsonc），
///    故读/写永远落在 opencode 实际读取的高优先级文件上；两者皆缺 → 返回 jsonc 路径
///    作为部署自动创建的目标（与合并语义保持同一高优先级）。
/// 2. JSONC 容错解析：.jsonc 合法允许注释与尾逗号，解析选项统一在此定义
///    （对纯 JSON 文件零影响）。
/// </summary>
public static class OpenCodeConfig
{
    public const string JsoncFileName = "opencode.jsonc";
    public const string JsonFileName = "opencode.json";

    /// <summary>JSONC 容错解析选项：跳过注释 + 允许尾逗号。</summary>
    public static readonly JsonDocumentOptions Parsing = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>解析现有配置文件路径：jsonc 存在 → jsonc；否则 json 存在 → json；
    /// 都不存在 → jsonc（部署时自动创建的目标）。home 来源遵循 HomeOverride（§2.3）。</summary>
    public static string ResolveConfigPath(string home)
    {
        var dir = Path.Combine(home, ".config", "opencode");
        var jsonc = Path.Combine(dir, JsoncFileName);
        if (File.Exists(jsonc)) return jsonc;
        var json = Path.Combine(dir, JsonFileName);
        return File.Exists(json) ? json : jsonc;
    }
}
