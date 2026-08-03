using System.Text;

namespace CodexProviderSwitcher.Core;

public enum ManagedAgentState
{
    Missing,
    Installed,
    Conflict
}

public sealed record ManagedAgentStatus(ManagedAgentState State, string Path)
{
    public bool IsMissing => State == ManagedAgentState.Missing;

    public bool IsInstalled => State == ManagedAgentState.Installed;

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
        if (!File.Exists(path))
        {
            return new ManagedAgentStatus(
                Directory.Exists(path)
                    ? ManagedAgentState.Conflict
                    : ManagedAgentState.Missing,
                path);
        }

        var content = File.ReadAllText(path);
        var state = Normalize(content) == Normalize(Template)
            ? ManagedAgentState.Installed
            : ManagedAgentState.Conflict;
        return new ManagedAgentStatus(state, path);
    }

    public ManagedAgentStatus Install()
    {
        var current = Inspect();
        if (current.IsInstalled)
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

}
