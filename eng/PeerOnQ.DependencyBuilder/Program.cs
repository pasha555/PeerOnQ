using PeerOnQ.DependencyBuilder;

try
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    return await PackageBuilder.RunAsync(args, cancellation.Token);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Dependency package build failed: {exception.Message}");
    return 1;
}
