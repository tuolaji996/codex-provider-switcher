using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexProviderSwitcher.Core;

/// <summary>
/// Builds the small model catalog used by Codex when the Kimi compatibility
/// router is active. The catalog intentionally starts from the installed Sol
/// entry so Codex keeps its current instruction and tool metadata.
/// </summary>
public sealed class KimiModelCatalogService
{
    public const string TemplateModelSlug = AppPaths.DefaultOfficialModel;

    public const int MaxCacheBytes = 64 * 1024 * 1024;

    private static readonly string[] KimiReasoningEfforts = ["low", "high", "max"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string EnsureCatalog(
        string selectedModel,
        string? codexHome = null)
    {
        ValidateSelectedModel(selectedModel);
        codexHome = string.IsNullOrWhiteSpace(codexHome)
            ? AppPaths.CodexHome
            : Path.GetFullPath(codexHome);

        var cachePath = Path.Combine(codexHome, "models_cache.json");
        if (!File.Exists(cachePath))
        {
            throw new FileNotFoundException(
                Localizer.Text(
                    "未找到 Codex models_cache.json。",
                    "Codex models_cache.json was not found."),
                cachePath);
        }

        var cacheInfo = new FileInfo(cachePath);
        if (cacheInfo.Length > MaxCacheBytes)
        {
            throw new InvalidDataException(
                Localizer.Text(
                    "Codex models_cache.json 超过安全大小上限。",
                    "Codex models_cache.json exceeds the safety size limit."));
        }

        var cacheJson = File.ReadAllText(cachePath);
        var catalogJson = BuildCatalog(cacheJson, selectedModel);
        var catalogPath = Path.Combine(
            codexHome,
            AppPaths.KimiModelCatalogFileName);
        AtomicFile.WriteAllText(catalogPath, catalogJson);
        return catalogPath;
    }

    public static string GetCatalogPath(string? codexHome = null)
    {
        codexHome = string.IsNullOrWhiteSpace(codexHome)
            ? AppPaths.CodexHome
            : Path.GetFullPath(codexHome);
        return Path.Combine(codexHome, AppPaths.KimiModelCatalogFileName);
    }

    public static string BuildCatalog(string cacheJson, string selectedModel)
    {
        ValidateSelectedModel(selectedModel);
        ArgumentNullException.ThrowIfNull(cacheJson);

        try
        {
            var cacheRoot = JsonNode.Parse(cacheJson) as JsonObject
                ?? throw InvalidCache("The cache root was not a JSON object.");
            if (!cacheRoot.TryGetPropertyValue("models", out var modelsNode) ||
                modelsNode is not JsonArray models)
            {
                throw InvalidCache("The cache did not contain a models array.");
            }

            var template = models
                .OfType<JsonObject>()
                .FirstOrDefault(model =>
                    ReadString(model, "slug") == TemplateModelSlug);
            if (template is null)
            {
                throw InvalidCache(
                    $"The cache did not contain the {TemplateModelSlug} template.");
            }

            if (!template.TryGetPropertyValue("model_messages", out var messages) ||
                messages is not JsonObject modelMessages ||
                string.IsNullOrWhiteSpace(
                    ReadString(modelMessages, "instructions_template")))
            {
                throw InvalidCache(
                    "The Sol template did not contain model instruction metadata.");
            }

            var selected = CloneObject(template);
            var model = selectedModel.Trim();
            selected["slug"] = JsonValue.Create(model);
            selected["display_name"] = JsonValue.Create(model);
            selected["description"] = JsonValue.Create(
                $"K3 model {model} routed through SuiXiang by the Codex Provider Switcher.");
            selected["default_reasoning_level"] = JsonValue.Create("max");
            selected["supported_reasoning_levels"] =
                BuildReasoningLevels(template);

            // The first Kimi router supports ordinary function tools, not the
            // richer Codex-native plugin/app/patch transport or image parts.
            selected["tool_mode"] = JsonValue.Create("direct");
            selected["apply_patch_tool_type"] = JsonValue.Create("freeform");
            selected["include_plugin_usage_instructions"] = JsonValue.Create(false);
            selected["include_apps_usage_instructions"] = JsonValue.Create(false);
            selected["input_modalities"] = new JsonArray
            {
                JsonValue.Create("text")
            };
            selected["supports_image_detail_original"] = JsonValue.Create(false);
            selected["supports_search_tool"] = JsonValue.Create(false);
            selected["web_search_tool_type"] = JsonValue.Create("text");
            // The adapter consumes the standard Responses shape. Sol's lite
            // mode moves instructions and tool schemas into a different input
            // representation that this compatibility router does not accept.
            selected["use_responses_lite"] = JsonValue.Create(false);
            selected.Remove("multi_agent_version");
            selected.Remove("additional_speed_tiers");
            selected.Remove("service_tiers");
            selected.Remove("default_service_tier");
            selected.Remove("comp_hash");

            var contextWindow = ResolveContextWindow(model);
            selected["context_window"] = JsonValue.Create(contextWindow);
            selected["max_context_window"] = JsonValue.Create(contextWindow);

            var outputModels = new JsonArray
            {
                selected
            };
            var output = new JsonObject
            {
                ["models"] = outputModels
            };
            return output.ToJsonString(JsonOptions);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw InvalidCache(
                "The models cache was not valid JSON.",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw InvalidCache(
                "The models cache did not match the expected schema.",
                exception);
        }
    }

    private static JsonArray BuildReasoningLevels(JsonObject template)
    {
        var source = template["supported_reasoning_levels"] as JsonArray;
        var result = new JsonArray();
        foreach (var effort in KimiReasoningEfforts)
        {
            var sourceLevel = source?
                .OfType<JsonObject>()
                .FirstOrDefault(level =>
                    string.Equals(
                        ReadString(level, "effort"),
                        effort,
                        StringComparison.OrdinalIgnoreCase));
            var level = sourceLevel is null
                ? new JsonObject()
                : CloneObject(sourceLevel);
            level["effort"] = JsonValue.Create(effort);
            if (string.IsNullOrWhiteSpace(ReadString(level, "description")))
            {
                level["description"] = JsonValue.Create(
                    $"K3 {effort} reasoning");
            }

            result.Add(level);
        }

        return result;
    }

    private static int ResolveContextWindow(string model) =>
        string.Equals(model, AppPaths.DefaultKimiModel, StringComparison.OrdinalIgnoreCase)
            ? 1048576
            : 262144;

    private static JsonObject CloneObject(JsonObject value) =>
        JsonNode.Parse(value.ToJsonString())?.AsObject()
        ?? throw InvalidCache("A model template could not be cloned.");

    private static string? ReadString(JsonObject value, string propertyName)
    {
        if (!value.TryGetPropertyValue(propertyName, out var property) ||
            property is not JsonValue)
        {
            return null;
        }

        try
        {
            return property.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void ValidateSelectedModel(string selectedModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedModel);
        var normalized = selectedModel.Trim();
        if (!string.Equals(
                normalized,
                AppPaths.DefaultKimiModel,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                Localizer.Text(
                    "首个实验版 K3 线路仅支持 k3；其他模型的思考与工具参数不同。",
                    "The first experimental K3 route supports only k3; other models use different reasoning and tool parameters."),
                nameof(selectedModel));
        }
    }

    private static InvalidDataException InvalidCache(
        string message,
        Exception? inner = null) =>
        new(
            Localizer.Text(
                "Codex models_cache.json 无效，无法安全生成 Kimi 模型目录。",
                "Codex models_cache.json is invalid; the Kimi model catalog was not generated."),
            inner);
}
