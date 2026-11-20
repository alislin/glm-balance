using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlmBalanceTaskbar;

/// <summary>
/// opencode 部署器：备份 opencode.json.bak-yyyyMMddHHmmss → 合并写入 localrelay provider
/// （npm @ai-sdk/openai-compatible、baseURL 指向本中转、apiKey=localToken、models=池并集）。
/// 「设为全局默认」把顶层 "model" 改为 localrelay/&lt;模型&gt;（独立开关）。
/// 红线：绝不删除/修改既有 provider 条目。
/// </summary>
public sealed class OpenCodeDeployer : IAgentDeployer
{
    public string AgentId => "opencode";

    public string ConfigPath { get; }

    public OpenCodeDeployer()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ConfigPath = Path.Combine(home, ".config", "opencode", "opencode.json");
    }

    public Task<DeployPreview> PreviewAsync(DeployRequest req, CancellationToken ct = default)
    {
        if (!File.Exists(ConfigPath))
        {
            return Task.FromResult(new DeployPreview(AgentId, ConfigPath, "配置文件不存在，无法部署", Ready: false));
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            var root = JsonNode.Parse(doc.RootElement.GetRawText())!.AsObject();
            var provider = EnsureProviderObject(root);
            var summary = BuildSummary(root, provider, req);
            return Task.FromResult(new DeployPreview(AgentId, ConfigPath, summary, Ready: true));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DeployPreview(AgentId, ConfigPath, $"解析失败：{ex.Message}", Ready: false));
        }
    }

    public async Task<DeployResult> DeployAsync(DeployRequest req, CancellationToken ct = default)
    {
        if (!File.Exists(ConfigPath))
        {
            return new DeployResult(false, null, "配置文件不存在，无法部署");
        }

        string backupPath;
        try
        {
            backupPath = ConfigPath + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Copy(ConfigPath, backupPath, overwrite: false);
        }
        catch (Exception ex)
        {
            return new DeployResult(false, null, $"备份失败，已中止写入：{ex.Message}");
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            var root = JsonNode.Parse(doc.RootElement.GetRawText())!.AsObject();
            var provider = EnsureProviderObject(root);

            // 只新增/替换 localrelay 一个条目；其余 provider 原样保留（含注释外的全部字段）
            provider["localrelay"] = BuildRelayNode(req);
            if (req.SetDefault)
            {
                var model = req.Models.Contains("glm-5.3-flash", StringComparer.Ordinal)
                    ? "glm-5.3-flash"
                    : req.Models.FirstOrDefault() ?? "glm-5.3-flash";
                root["model"] = $"localrelay/{model}";
            }

            await WriteAtomicAsync(ConfigPath, root.ToJsonString(RelayJson.Indented))
                .ConfigureAwait(false);
            return new DeployResult(true, backupPath,
                "已写入 localrelay provider" + (req.SetDefault ? " 并设为全局默认模型" : "") +
                $"；备份：{Path.GetFileName(backupPath)}");
        }
        catch (Exception ex)
        {
            return new DeployResult(false, backupPath, $"写入失败：{ex.Message}（备份保留在 {Path.GetFileName(backupPath)}）");
        }
    }

    /// <summary>原子写（M5）：临时文件 + File.Replace，避免写一半崩溃留半截 JSON。</summary>
    static async Task WriteAtomicAsync(string path, string content)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content).ConfigureAwait(false);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    string BuildSummary(JsonObject root, JsonObject provider, DeployRequest req)
    {
        var existing = provider["localrelay"] != null;
        var model = req.Models.Contains("glm-5.3-flash", StringComparer.Ordinal)
            ? "glm-5.3-flash"
            : req.Models.FirstOrDefault() ?? "glm-5.3-flash";
        var relay = new JsonObject
        {
            ["npm"] = "@ai-sdk/openai-compatible",
            ["options"] = new JsonObject
            {
                ["baseURL"] = req.BaseUrl,
                ["apiKey"] = RelayJson.MaskKey(req.Token) + "（完整值写回）",
            },
            ["models"] = new JsonObject(req.Models.Select(m =>
                new KeyValuePair<string, JsonNode?>(m, BuildModelNode(m)))),
        };
        return (existing ? "将替换现有 localrelay 条目：\n" : "将新增 provider 条目：\n")
            + relay.ToJsonString(RelayJson.Indented)
            + "\n\n将写入的模型并集：" + string.Join(", ", req.Models)
            + (req.SetDefault ? $"\n全局默认 model → localrelay/{model}" : "\n全局默认 model 保持不变（当前：" + (root["model"]?.ToString() ?? "(未设)") + "）")
            + "\n\n红线承诺：不删除/不修改既有 provider 条目；写入前自动备份 opencode.json.bak-<时间戳>。";
    }

    static JsonObject EnsureProviderObject(JsonObject root)
    {
        if (root["provider"] is not JsonObject p)
        {
            p = new JsonObject();
            root["provider"] = p;
        }
        return p;
    }

    static JsonObject BuildRelayNode(DeployRequest req) => new()
    {
        ["name"] = "Local Relay",
        ["npm"] = "@ai-sdk/openai-compatible",
        ["options"] = new JsonObject
        {
            ["baseURL"] = req.BaseUrl,
            ["apiKey"] = req.Token,
        },
        ["models"] = new JsonObject(req.Models.Select(m =>
            new KeyValuePair<string, JsonNode?>(m, BuildModelNode(m)))),
    };

    /// <summary>模型节点：name + limit.context（token）。缺省时 opencode 端上下文用量显示失真；
    /// 部署为 localrelay 条目整体覆盖写，每次部署随节点重新生成。</summary>
    static JsonObject BuildModelNode(string m) => new()
    {
        ["name"] = m,
        ["limit"] = new JsonObject { ["context"] = 1_000_000 },
    };
}
