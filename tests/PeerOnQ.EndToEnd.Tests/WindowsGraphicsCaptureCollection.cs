using Xunit;

namespace PeerOnQ.EndToEnd.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WindowsGraphicsCaptureCollection
{
    public const string Name = "Windows graphics capture";
}
