using RemoteDesktop.Relay;

// Entry point for the Synology relay. Configuration comes from RELAY_PORT (set by
// the .spk start-stop-status script); logs go to stdout, which the package script
// redirects to the package var/ log file.

RelayOptions options = RelayOptions.FromEnvironment();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

var server = new RelayServer(options);
try
{
    await server.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    // clean shutdown
}

return 0;
