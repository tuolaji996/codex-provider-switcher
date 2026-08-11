using System.Net.Sockets;
using CodexProviderKimiRouter;

var options = KimiRouterOptions.LoadFromEnvironment();
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stop.Cancel();
};

await using var server = new KimiRouterServer(options);
try
{
    await server.RunAsync(stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    // Normal Ctrl+C shutdown.
}
catch (SocketException exception)
{
    Console.Error.WriteLine($"Kimi router could not listen on {server.ListenUri}: {exception.Message}");
    return 2;
}
catch (InvalidOperationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

return 0;
