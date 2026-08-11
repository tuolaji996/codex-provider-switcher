using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CodexProviderSwitcher.Core;

public sealed record KimiRouterEnsureResult(
    bool Success,
    string Summary,
    bool Started);

/// <summary>
/// Starts the optional loopback Kimi router only when its health endpoint is
/// not already serving the expected service. The launcher never supplies an
/// API key or any other secret to the child process.
/// </summary>
public sealed class KimiRouterProcessService : IDisposable
{
    public const string RouterExecutableName = AppPaths.KimiRouterExecutableName;
    public static readonly string HealthUrl =
        $"http://127.0.0.1:{AppPaths.KimiRouterPort}/health";
    public const string RoutedBaseUrl = AppPaths.KimiRouterBaseUrl;
    public const int Port = AppPaths.KimiRouterPort;
    private const int MaxHealthResponseBytes = 8 * 1024;

    private static readonly Uri HealthEndpoint = new(HealthUrl);
    private static readonly TimeSpan DefaultHealthTimeout =
        TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan DefaultStartupTimeout =
        TimeSpan.FromSeconds(12);
    private static readonly TimeSpan DefaultPollInterval =
        TimeSpan.FromMilliseconds(250);
    private static readonly SemaphoreSlim StartupGate = new(1, 1);
    private const string StartupSemaphoreName = "Local\\CodexProviderSwitcher.KimiRouter.Startup";

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly TimeSpan _healthTimeout;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _pollInterval;

    public KimiRouterProcessService()
        : this(CreateDefaultHttpClient(),
            DefaultHealthTimeout,
            DefaultStartupTimeout,
            DefaultPollInterval,
            disposeHttpClient: true)
    {
    }

    public KimiRouterProcessService(HttpClient httpClient)
        : this(httpClient,
            DefaultHealthTimeout,
            DefaultStartupTimeout,
            DefaultPollInterval,
            disposeHttpClient: false)
    {
    }

    internal KimiRouterProcessService(
        HttpClient httpClient,
        TimeSpan healthTimeout,
        TimeSpan startupTimeout,
        TimeSpan pollInterval,
        bool disposeHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _healthTimeout = RequirePositive(healthTimeout, nameof(healthTimeout));
        _startupTimeout = RequirePositive(startupTimeout, nameof(startupTimeout));
        _pollInterval = RequirePositive(pollInterval, nameof(pollInterval));
        _disposeHttpClient = disposeHttpClient;
    }

    public async Task<KimiRouterEnsureResult> EnsureRunningAsync(
        string routerExePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routerExePath);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(routerExePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Failure(
                F(
                    "Kimi 路由器路径无效：{0}",
                    "The Kimi router path is invalid: {0}",
                    exception.Message));
        }

        if (!File.Exists(fullPath))
        {
            return Failure(
                F(
                    "未找到 Kimi 路由器：{0}",
                    "Kimi router executable was not found: {0}",
                    fullPath));
        }

        var initialProbe = await ProbeHealthAsync(fullPath, cancellationToken)
            .ConfigureAwait(false);
        if (initialProbe == HealthProbe.Healthy)
        {
            return Success(
                T(
                    "Kimi 路由器已经在运行。",
                    "The Kimi router is already running."),
                started: false);
        }

        if (initialProbe == HealthProbe.WrongService)
        {
            return Failure(
                T(
                    "17866 端口已被其他服务占用，未启动 Kimi 路由器。",
                    "Port 17866 is occupied by another service; the Kimi router was not started."));
        }

        await StartupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Semaphore? startupSemaphore = null;
        var ownsStartupSemaphore = false;
        try
        {
            try
            {
                // Named Semaphore is safe to release after an await; Mutex is
                // thread-affine and can remain owned if the continuation hops.
                startupSemaphore = new Semaphore(1, 1, StartupSemaphoreName);
                ownsStartupSemaphore = startupSemaphore.WaitOne(_startupTimeout);

                if (!ownsStartupSemaphore)
                {
                    return Failure(
                        T(
                            "等待 Kimi 路由器启动锁超时。",
                            "Timed out waiting for the Kimi router startup lock."));
                }

                var lockedProbe = await ProbeHealthAsync(fullPath, cancellationToken)
                    .ConfigureAwait(false);
                if (lockedProbe == HealthProbe.Healthy)
                {
                    return Success(
                        T(
                            "Kimi 路由器已经在运行。",
                            "The Kimi router is already running."),
                        started: false);
                }

                if (lockedProbe == HealthProbe.WrongService)
                {
                    return Failure(
                        T(
                            "17866 端口已被其他服务占用，未启动 Kimi 路由器。",
                            "Port 17866 is occupied by another service; the Kimi router was not started."));
                }
            }
            catch (PlatformNotSupportedException)
            {
                startupSemaphore?.Dispose();
                startupSemaphore = null;
                ownsStartupSemaphore = true;
            }

            Process? process = null;
            var keepStartedProcess = false;
            try
            {
                process = Process.Start(BuildStartInfo(fullPath));
                if (process is null)
                {
                    return Failure(
                        T(
                            "无法启动 Kimi 路由器进程。",
                            "The Kimi router process could not be started."),
                        started: false);
                }

                var deadline = DateTimeOffset.UtcNow + _startupTimeout;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (HasExited(process, out var exitCode))
                    {
                        var lateProbe = await ProbeHealthAsync(fullPath, cancellationToken)
                            .ConfigureAwait(false);
                        if (lateProbe == HealthProbe.Healthy)
                        {
                            return Success(
                                T(
                                    "Kimi 路由器已由其他进程启动并通过健康检查。",
                                    "Another process started the Kimi router and it passed its health check."),
                                started: false);
                        }

                        if (lateProbe == HealthProbe.WrongService)
                        {
                            return Failure(
                                T(
                                    "17866 端口已被其他服务占用；Kimi 路由器进程已提前退出。",
                                    "Port 17866 is occupied by another service; the Kimi router exited early."),
                                started: true);
                        }

                        return Failure(
                            F(
                                "Kimi 路由器进程已提前退出（代码 {0}）。",
                                "The Kimi router exited before becoming ready (code {0}).",
                                exitCode),
                            started: true);
                    }

                    var probe = await ProbeHealthAsync(fullPath, cancellationToken)
                        .ConfigureAwait(false);
                    if (probe == HealthProbe.Healthy)
                    {
                        keepStartedProcess = true;
                        return Success(
                            T(
                                "Kimi 路由器已启动并通过健康检查。",
                                "The Kimi router started and passed its health check."),
                            started: true);
                    }

                    if (probe == HealthProbe.WrongService)
                    {
                        return Failure(
                            T(
                                "17866 端口已被其他服务占用，未确认 Kimi 路由器。",
                                "Port 17866 is occupied by another service; the Kimi router was not confirmed."),
                            started: true);
                    }

                    await Task.Delay(_pollInterval, cancellationToken)
                        .ConfigureAwait(false);
                }

                var finalProbe = await ProbeHealthAsync(fullPath, cancellationToken)
                    .ConfigureAwait(false);
                return finalProbe == HealthProbe.WrongService
                    ? Failure(
                        T(
                            "Kimi 路由器未就绪：17866 端口由其他服务占用。",
                            "The Kimi router is not ready: port 17866 is occupied by another service."),
                        started: true)
                    : Failure(
                        T(
                            "Kimi 路由器启动超时，健康检查仍未通过。",
                            "The Kimi router did not pass its health check before the startup timeout."),
                        started: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return Failure(
                    F(
                        "无法启动 Kimi 路由器：{0}",
                        "Could not start the Kimi router: {0}",
                        exception.Message),
                    started: process is not null);
            }
            finally
            {
                if (!keepStartedProcess && process is not null)
                {
                    TryTerminateStartedProcess(process);
                }

                process?.Dispose();
            }
        }
        finally
        {
            if (ownsStartupSemaphore && startupSemaphore is not null)
            {
                try
                {
                    startupSemaphore.Release();
                }
                catch (InvalidOperationException)
                {
                }
            }

            startupSemaphore?.Dispose();
            StartupGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<HealthProbe> ProbeHealthAsync(
        string expectedRouterPath,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_healthTimeout);

        try
        {
            using var response = await _httpClient.GetAsync(
                HealthEndpoint,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return HealthProbe.WrongService;
            }

            var body = await ReadBoundedHealthBodyAsync(
                response.Content,
                timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("status", out var status) &&
                   status.ValueKind == JsonValueKind.String &&
                   string.Equals(
                       status.GetString(),
                       "ok",
                       StringComparison.OrdinalIgnoreCase) &&
                   root.TryGetProperty("service", out var service) &&
                   service.ValueKind == JsonValueKind.String &&
                   string.Equals(
                       service.GetString(),
                       "codex-provider-kimi-router",
                       StringComparison.Ordinal) &&
                   root.TryGetProperty("upstream", out var upstream) &&
                   upstream.ValueKind == JsonValueKind.String &&
                   IsExpectedUpstream(upstream.GetString()) &&
                   root.TryGetProperty("pid", out var pid) &&
                   pid.TryGetInt32(out var processId) &&
                   IsExactRouterProcessRunning(expectedRouterPath, processId)
                ? HealthProbe.Healthy
                : HealthProbe.WrongService;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthProbe.Unavailable;
        }
        catch (HttpRequestException)
        {
            return await IsPortOccupiedAsync(cancellationToken)
                .ConfigureAwait(false)
                ? HealthProbe.WrongService
                : HealthProbe.Unavailable;
        }
        catch (JsonException)
        {
            return HealthProbe.WrongService;
        }
        catch (InvalidDataException)
        {
            return HealthProbe.WrongService;
        }
    }

    private static async Task<byte[]> ReadBoundedHealthBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxHealthResponseBytes)
        {
            throw new InvalidDataException();
        }

        await using var stream = await content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[2048];
        while (true)
        {
            var count = await stream.ReadAsync(
                chunk.AsMemory(),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + count > MaxHealthResponseBytes)
            {
                throw new InvalidDataException();
            }

            await buffer.WriteAsync(
                chunk.AsMemory(0, count),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> IsPortOccupiedAsync(
        CancellationToken cancellationToken)
    {
        using var client = new System.Net.Sockets.TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(400));
        try
        {
            await client.ConnectAsync("127.0.0.1", Port, timeout.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static ProcessStartInfo BuildStartInfo(string fullPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            WorkingDirectory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        // Do not pass arguments or API credentials. Remove inherited values
        // whose names conventionally carry secrets before launching the child.
        foreach (var name in startInfo.Environment.Keys.ToArray())
        {
            if (IsSensitiveEnvironmentName(name))
            {
                startInfo.Environment.Remove(name);
            }
        }

        // Pin the child to the supported SuiXiang upstream and loopback port;
        // inherited routing overrides must not redirect a Bearer credential.
        startInfo.Environment["KIMI_ROUTER_UPSTREAM_BASE_URL"] =
            AppPaths.KimiUpstreamBaseUrl;
        startInfo.Environment.Remove("KIMI_ROUTER_BASE_URL");
        startInfo.Environment.Remove("SUI_XIANG_BASE_URL");
        startInfo.Environment["KIMI_ROUTER_PORT"] =
            AppPaths.KimiRouterPort.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["KIMI_ROUTER_MODEL"] = AppPaths.DefaultKimiModel;

        return startInfo;
    }

    private static bool IsSensitiveEnvironmentName(string name) =>
        name.Contains("API_KEY", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("AUTH", StringComparison.OrdinalIgnoreCase);

    private static bool IsExpectedUpstream(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var upstream) &&
        string.Equals(
            upstream.ToString().TrimEnd('/'),
            AppPaths.KimiUpstreamBaseUrl,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsExactRouterProcessRunning(string expectedPath, int processId)
    {
        var normalizedExpectedPath = Path.GetFullPath(expectedPath);
        if (processId <= 0)
        {
            return false;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }

        using (process)
        {
            try
            {
                if (process.HasExited)
                {
                    return false;
                }

                var executablePath = process.MainModule?.FileName;
                return !string.IsNullOrWhiteSpace(executablePath) &&
                       string.Equals(
                           Path.GetFullPath(executablePath),
                           normalizedExpectedPath,
                           StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException or NotSupportedException)
            {
                // Access to another process' MainModule can be denied;
                // fail closed instead of trusting a same-name process.
                return false;
            }
        }
    }

    private static bool HasExited(Process process, out int exitCode)
    {
        try
        {
            if (!process.HasExited)
            {
                exitCode = 0;
                return false;
            }

            exitCode = process.ExitCode;
            return true;
        }
        catch (InvalidOperationException)
        {
            exitCode = -1;
            return true;
        }
    }

    private static void TryTerminateStartedProcess(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(milliseconds: 2_000);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The failed child either already exited or cannot be controlled.
            // Never obscure the original startup failure with cleanup noise.
        }
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static TimeSpan RequirePositive(TimeSpan value, string name) =>
        value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(name);

    private static KimiRouterEnsureResult Success(string summary, bool started) =>
        new(true, summary, started);

    private static KimiRouterEnsureResult Failure(
        string summary,
        bool started = false) =>
        new(false, summary, started);

    private static string T(string chinese, string english) =>
        Localizer.Text(chinese, english);

    private static string F(
        string chineseFormat,
        string englishFormat,
        params object?[] arguments) =>
        Localizer.Format(chineseFormat, englishFormat, arguments);

    private enum HealthProbe
    {
        Unavailable,
        Healthy,
        WrongService
    }
}
