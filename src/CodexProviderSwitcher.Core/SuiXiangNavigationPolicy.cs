namespace CodexProviderSwitcher.Core;

public enum EmbeddedNavigationAction
{
    AllowEmbedded,
    OpenExternal,
    Block
}

public static class SuiXiangNavigationPolicy
{
    public static EmbeddedNavigationAction Classify(
        string? rawUri,
        bool isUserInitiated)
    {
        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri))
        {
            return EmbeddedNavigationAction.Block;
        }

        if (IsAllowedEmbedded(uri))
        {
            return EmbeddedNavigationAction.AllowEmbedded;
        }

        return isUserInitiated &&
               uri.Scheme.Equals(
                   Uri.UriSchemeHttps,
                   StringComparison.OrdinalIgnoreCase)
            ? EmbeddedNavigationAction.OpenExternal
            : EmbeddedNavigationAction.Block;
    }

    public static bool IsAllowedEmbedded(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.Scheme.Equals(
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host;
        return host.Equals(
                   "sui-xiang.com",
                   StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(
                   ".sui-xiang.com",
                   StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(
                   ".qq.com",
                   StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(
                   ".qcloud.com",
                   StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(
                   ".tencent-cloud.com",
                   StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(
                   ".tencentcs.com",
                   StringComparison.OrdinalIgnoreCase);
    }
}
