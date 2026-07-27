using CodexProviderSwitcher.Core;

var target = AppPaths.LegacySuiXiangCredentialTarget;
var credentialTargetSpecified = false;
for (var index = 0; index < args.Length; index++)
{
    if (args[index].Equals("--credential-target", StringComparison.Ordinal))
    {
        if (credentialTargetSpecified || index + 1 >= args.Length)
        {
            Console.Error.WriteLine("Credential target argument is invalid.");
            return 3;
        }

        target = args[index + 1];
        credentialTargetSpecified = true;
        index++;
    }
}

if (!CredentialTargetFactory.IsValid(target))
{
    Console.Error.WriteLine("Credential target is not managed by Codex Provider Switcher.");
    return 3;
}

try
{
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
