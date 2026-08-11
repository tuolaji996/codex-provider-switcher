using CodexProviderSwitcher.Core;

var target = AppPaths.LegacySuiXiangCredentialTarget;
var credentialTargetSpecified = false;
var ensureKimiRouter = false;
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--credential-target":
            if (credentialTargetSpecified || index + 1 >= args.Length ||
                args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Credential target argument is invalid.");
                return 3;
            }

            target = args[++index];
            credentialTargetSpecified = true;
            break;
        case "--ensure-kimi-router":
            if (ensureKimiRouter)
            {
                Console.Error.WriteLine("Router ensure argument is invalid.");
                return 3;
            }

            ensureKimiRouter = true;
            break;
        default:
            Console.Error.WriteLine("Unknown credential broker argument.");
            return 3;
    }
}

if (!CredentialTargetFactory.IsValid(target))
{
    Console.Error.WriteLine("Credential target is not managed by Codex Provider Switcher.");
    return 3;
}

try
{
    if (ensureKimiRouter)
    {
        // v1.4.0 generated auth commands with --ensure-kimi-router. Preserve
        // those configs during an in-place upgrade, but route the compatibility
        // action through the WSL-local launcher used by v1.4.1. Starting the
        // legacy Windows listener would put it in the wrong loopback namespace
        // and could also keep the credential command's stdout pipe open.
        var launcherPath = Path.Combine(
            AppContext.BaseDirectory,
            AppPaths.KimiWslLauncherFileName);
        var routerService = new WslKimiRouterService();
        var router = await routerService.EnsureRunningAsync(launcherPath);
        if (!router.Success)
        {
            Console.Error.WriteLine(router.Summary);
            return 4;
        }
    }

    var token = CredentialVault.Read(target);
    if (string.IsNullOrWhiteSpace(token))
    {
        Console.Error.WriteLine("Credential was not found.");
        return 2;
    }

    Console.Out.Write(token);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
