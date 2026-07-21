namespace Zapqio.Runner.Protocol.Tests;

/// <summary>
/// PROTOCOL.md §3/§9 — version negotiation on the handshake. The header name and the major version
/// are part of the wire contract, so they are pinned here and cross-checked against the spec text.
/// </summary>
public class ProtocolVersionTests
{
    private static string Spec { get; } = File.ReadAllText(Path.Combine(ProtocolRepo.Dir, "PROTOCOL.md"));

    [Fact]
    public void Handshake_header_is_the_one_the_spec_names()
    {
        Assert.Equal("X-Zapqio-Protocol-Version", ProtocolVersion.Header);
        Assert.Contains(ProtocolVersion.Header, Spec);
    }

    [Fact]
    public void Implemented_major_version_is_the_spec_version()
    {
        Assert.Equal(1, ProtocolVersion.Current);
        Assert.Contains("Zapqio Runner Protocol — v1", Spec);
    }
}
