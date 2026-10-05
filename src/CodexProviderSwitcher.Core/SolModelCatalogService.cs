using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexProviderSwitcher.Core;

// Opt-in long-context metadata for the exact installed GPT-6.1 Sol entry.
// Do not edit models_cache.json, borrow another model's instructions, or invent Ultra.
public static class SolModelCatalogService
{
    // Official API specification: 1,050,000 total = 922,000 input + 128,000 output.
    // https://developers.openai.com/api/docs/models/gpt-6.1-sol
    public const long DocumentedMaxInputTokens = 922_000;
    public const long DefaultInputTokens = 272_000;
    private const int MaxCacheBytes = 64 * 1024 * 1024;

    public static bool RequiresCatalog(ConfigService service, string config)
    {
        var status = service.ParseStatus(config);
        var context = service.ParseSolContextWindowStatus(config);
        return status.Model == AppPaths.DefaultOfficialModel &&
               context.ContextWindow is > DefaultInputTokens &&
               (status.ModelCatalogJson is null ||
                status.ModelCatalogJson == AppPaths.SolModelCatalogFileName);
    }

    public static string BuildCatalog(string cacheJson)
    {
        try
        {
            var source = JsonNode.Parse(cacheJson) as JsonObject;
            if (source?["models"] is not JsonArray models)
            {
                throw new InvalidDataException("The installed model catalog has no models array.");
            }
            var matches = models.OfType<JsonObject>().Where(model =>
                model["slug"]?.GetValue<string>() == AppPaths.DefaultOfficialModel).ToList();
            if (matches.Count != 1)
            {
                throw new InvalidDataException("The installed catalog must contain exactly one GPT-6.1 Sol entry.");
            }

            var outputModels = (JsonArray)models.DeepClone();
            var selected = outputModels.OfType<JsonObject>().Single(model =>
                model["slug"]?.GetValue<string>() == AppPaths.DefaultOfficialModel);
            var existingLimit = selected["max_context_window"]?.GetValue<long>() ?? 0;
            selected["max_context_window"] = Math.Max(existingLimit, DocumentedMaxInputTokens);
            // All reasoning levels, model instructions, tools, and other models stay exact.
            return new JsonObject { ["models"] = outputModels }.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException("The installed GPT-6.1 Sol metadata is invalid.", exception);
        }
    }

    public static T WithPreparedCatalog<T>(
        ConfigService service, string updated, string configPath, string backupFolder,
        Func<string, T> apply)
    {
        if (!RequiresCatalog(service, updated))
        {
            // A user-owned catalog is authoritative and is never replaced.
            return apply(updated);
        }

        var codexHome = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        var cachePath = Path.Combine(codexHome, "models_cache.json");
        if (!File.Exists(cachePath) || new FileInfo(cachePath).Length > MaxCacheBytes)
        {
            throw new InvalidDataException(Localizer.Text(
                "缺少有效的 Codex 模型目录。请先打开 Codex 加载模型，再启用 6.1 Sol 长上下文。",
                "A valid Codex model catalog is required. Open Codex to load models before enabling GPT-6.1 Sol long context."));
        }
        var catalog = BuildCatalog(File.ReadAllText(cachePath));
        var prepared = service.BuildSolModelCatalogConfig(updated, enabled: true);
        var catalogPath = Path.Combine(codexHome, AppPaths.SolModelCatalogFileName);
        var previous = File.Exists(catalogPath) ? File.ReadAllBytes(catalogPath) : null;
        if (previous is not null)
        {
            AtomicFile.WriteAllBytes(Path.Combine(backupFolder, AppPaths.SolModelCatalogFileName), previous);
        }

        try
        {
            AtomicFile.WriteAllText(catalogPath, catalog);
            if (File.ReadAllText(catalogPath) != catalog)
            {
                throw new InvalidOperationException("The Sol model catalog read-back failed.");
            }
            return apply(prepared);
        }
        catch (Exception exception)
        {
            try
            {
                if (previous is not null)
                {
                    AtomicFile.WriteAllBytes(catalogPath, previous);
                }
                else if (File.Exists(catalogPath))
                {
                    File.Delete(catalogPath);
                }
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException("The previous Sol model catalog could not be restored.",
                    exception, rollbackException);
            }
            throw;
        }
    }
}
