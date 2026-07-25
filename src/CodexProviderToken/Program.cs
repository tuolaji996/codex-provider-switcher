using CodexProviderSwitcher.Core;

var target = AppPaths.CredentialTarget;
for (var index = 0; index < args.Length - 1; index++)
{
    if (args[index].Equals("--credential-target", StringComparison.Ordinal))
    {
        target = args[index + 1];
        break;
    }
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
