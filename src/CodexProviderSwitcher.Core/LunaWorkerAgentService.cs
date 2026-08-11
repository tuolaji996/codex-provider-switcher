using System.Text;

namespace CodexProviderSwitcher.Core;

public enum ManagedAgentState
{
    Missing,
    Installed,
    Disabled,
    Conflict
}

public sealed record ManagedAgentStatus(ManagedAgentState State, string Path)
{
    public bool IsMissing => State == ManagedAgentState.Missing;

    public bool IsInstalled => State == ManagedAgentState.Installed;

    public bool IsDisabled => State == ManagedAgentState.Disabled;

    public bool IsConflict => State == ManagedAgentState.Conflict;
}

public sealed class LunaWorkerAgentService
{
    public const string AgentFileName = "luna-worker.toml";
    public const string AgentName = "luna_worker";
    public const string AgentModel = "gpt-5.6-luna";
    public const string AgentReasoningEffort = "max";

    // Keep this file deliberately narrow: it is an optional managed agent and
    // must never alter the user's main Codex config or any other agent.
    public const string Template =
        "name = \"luna_worker\"\n" +
        "description = \"Preferred agent for well-scoped, self-contained delegated tasks with explicit boundaries and a concrete deliverable.\"\n" +
        "model = \"gpt-5.6-luna\"\n" +
        "model_reasoning_effort = \"max\"\n" +
        "\n" +
        "developer_instructions = \"\"\"\n" +
        "Work only on the delegated subtask and stay within its stated scope, files, systems, and deliverables.\n" +
        "Do not redefine, reinterpret, or modify the parent task's overall objective.\n" +
        "Do not expand the work into adjacent tasks, broad refactors, unrelated fixes, or extra deliverables unless the parent agent explicitly delegates them.\n" +
        "Make only the changes required to complete the assigned subtask, preserve unrelated user work and configuration, and verify the result in proportion to the subtask's risk.\n" +
        "If the scope, authority, prerequisites, or expected outcome are materially unclear, stop at that boundary and report the exact ambiguity or blocker to the parent agent instead of guessing.\n" +
        "Return a concise completion report with the outcome, files or systems changed, checks performed, and any remaining scoped risk or blocker.\n" +
        "\"\"\"\n";

    public ManagedAgentStatus Inspect()
    {
        var path = AppPaths.LunaWorkerAgentPath;
        if (File.Exists(path))
        {
            return InspectManagedFile(path, ManagedAgentState.Installed);
        }

        if (Directory.Exists(path))
        {
            return new ManagedAgentStatus(ManagedAgentState.Conflict, path);
        }

        var disabledPath = AppPaths.DisabledLunaWorkerAgentPath;
        if (File.Exists(disabledPath))
        {
            return InspectManagedFile(disabledPath, ManagedAgentState.Disabled);
        }

        return new ManagedAgentStatus(
            Directory.Exists(disabledPath)
                ? ManagedAgentState.Conflict
                : ManagedAgentState.Missing,
            path);
    }

    public ManagedAgentStatus Reconcile(ConfigStatus configStatus)
    {
        ArgumentNullException.ThrowIfNull(configStatus);

        if (IsSuiXiangRoute(configStatus))
        {
            return DisableForUnsupportedProvider();
        }

        return configStatus.Mode is ProviderMode.Official or ProviderMode.ThirdParty
            ? RestoreManagedAgent()
            : Inspect();
    }

    public static bool IsSuiXiangRoute(ConfigStatus configStatus) =>
        configStatus.Mode == ProviderMode.ThirdParty &&
        Uri.TryCreate(configStatus.BaseUrl, UriKind.Absolute, out var uri) &&
        uri.Host.Equals("sui-xiang.com", StringComparison.OrdinalIgnoreCase);

    public ManagedAgentStatus DisableForUnsupportedProvider()
    {
        var current = Inspect();
        if (!current.IsInstalled)
        {
            return current;
        }

        var disabledPath = AppPaths.DisabledLunaWorkerAgentPath;
        if (File.Exists(disabledPath) || Directory.Exists(disabledPath))
        {
            return new ManagedAgentStatus(ManagedAgentState.Conflict, disabledPath);
        }

        try
        {
            File.Move(current.Path, disabledPath, overwrite: false);
        }
        catch (Exception exception) when (IsFileAccessException(exception))
        {
            return new ManagedAgentStatus(ManagedAgentState.Conflict, disabledPath);
        }

        return Inspect();
    }

    public ManagedAgentStatus RestoreManagedAgent()
    {
        var current = Inspect();
        if (!current.IsDisabled)
        {
            return current;
        }

        var activePath = AppPaths.LunaWorkerAgentPath;
        if (File.Exists(activePath) || Directory.Exists(activePath))
        {
            return new ManagedAgentStatus(ManagedAgentState.Conflict, activePath);
        }

        try
        {
            File.Move(current.Path, activePath, overwrite: false);
        }
        catch (Exception exception) when (IsFileAccessException(exception))
        {
            return new ManagedAgentStatus(ManagedAgentState.Conflict, activePath);
        }

        return Inspect();
    }

    public ManagedAgentStatus Install()
    {
        var current = Inspect();
        if (current.IsInstalled || current.IsDisabled)
        {
            return current;
        }

        if (current.IsConflict)
        {
            return current;
        }

        var path = current.Path;
        Directory.CreateDirectory(AppPaths.AgentsDirectory);
        var temporaryPath = Path.Combine(
            AppPaths.AgentsDirectory,
            $".{AgentFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                       stream,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                       4096,
                       leaveOpen: true))
            {
                writer.Write(Template);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            try
            {
                // A non-overwriting rename keeps a concurrent user-created
                // file safe even after the initial missing-state inspection.
                File.Move(temporaryPath, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path))
            {
                return Inspect();
            }

            return Inspect();
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string Normalize(string content) =>
        content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n');

    private static ManagedAgentStatus InspectManagedFile(
        string path,
        ManagedAgentState managedState)
    {
        try
        {
            var content = File.ReadAllText(path);
            return new ManagedAgentStatus(
                Normalize(content) == Normalize(Template)
                    ? managedState
                    : ManagedAgentState.Conflict,
                path);
        }
        catch (Exception exception) when (IsFileAccessException(exception))
        {
            return new ManagedAgentStatus(ManagedAgentState.Conflict, path);
        }
    }

    private static bool IsFileAccessException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
        System.Security.SecurityException;

}
