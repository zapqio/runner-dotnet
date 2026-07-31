using System.Text.RegularExpressions;

namespace Zapqio.Runner.Protocol.Tests;

/// <summary>
/// PROTOCOL.md §3/§9 — version negotiation on the handshake. The header name and the major version
/// are part of the wire contract, so they are pinned here and cross-checked against the spec text.
/// </summary>
public partial class ProtocolVersionTests
{
    private static string Spec { get; } = File.ReadAllText(Path.Combine(ProtocolRepo.Dir, "PROTOCOL.md"));

    /// <summary>
    /// The major version the spec declares in its title, e.g. "… — v1 (…)". Matched on the version
    /// alone rather than on the whole title, so the assertion does not depend on the language the
    /// spec happens to be written in.
    /// </summary>
    [GeneratedRegex(@"\bv(\d+)\b")]
    private static partial Regex VersionInTitle();

    [Fact]
    public void Handshake_header_is_the_one_the_spec_names()
    {
        Assert.Equal("X-Zapqio-Protocol-Version", ProtocolVersion.Header);
        Assert.Contains(ProtocolVersion.Header, Spec);
    }

    [Fact]
    public void Implemented_major_version_is_the_spec_version()
    {
        var title = Spec.Split('\n').First(line => line.StartsWith("# "));
        var declared = VersionInTitle().Match(title);

        Assert.True(declared.Success, $"The spec's title declares no version like 'v1': {title}");
        Assert.Equal(ProtocolVersion.Current.ToString(), declared.Groups[1].Value);
    }
}
