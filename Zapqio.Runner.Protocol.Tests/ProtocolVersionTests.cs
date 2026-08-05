using System.Text.RegularExpressions;

namespace Zapqio.Runner.Protocol.Tests;

/// <summary>
/// PROTOCOL.md §3/§9 — version negotiation on the handshake. The header name and the major version
/// are part of the wire contract, so they are pinned here and cross-checked against the spec text.
/// </summary>
public partial class ProtocolVersionTests
{
    private static string Spec { get; } = File.ReadAllText(Path.Combine(ProtocolRepo.Dir, "PROTOCOL.md"));

    /// <summary>The heading of the section where the spec declares its major version.</summary>
    private const string VersioningHeading = "## 1.";

    /// <summary>
    /// The major version the spec declares in §1, e.g. "To jest <b>v2</b> protokołu". Matched on the
    /// version alone rather than on the whole sentence, so the assertion does not depend on the
    /// language the spec happens to be written in.
    ///
    /// Excluding a preceding hyphen is not cosmetic: without it the pattern matches
    /// <c>stateDiagram-v2</c> from the Mermaid diagrams and the assertion passes while comparing the
    /// protocol version against a diagram syntax version.
    /// </summary>
    [GeneratedRegex(@"(?<![\w-])v(\d+)\b")]
    private static partial Regex DeclaredVersion();

    /// <summary>
    /// The body of §1 — from its heading to the next section. Searching the rest of the document is
    /// no basis for an assertion: "v2" appears later in behaviour descriptions, so a spec that never
    /// declares its version would still pass.
    /// </summary>
    private static string OverviewSection()
    {
        var start = Spec.IndexOf(VersioningHeading, StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        var next = Spec.IndexOf("\n## ", start + VersioningHeading.Length, StringComparison.Ordinal);
        return next < 0 ? Spec[start..] : Spec[start..next];
    }

    [Fact]
    public void Handshake_header_is_the_one_the_spec_names()
    {
        Assert.Equal("X-Zapqio-Protocol-Version", ProtocolVersion.Header);
        Assert.Contains(ProtocolVersion.Header, Spec);
    }

    [Fact]
    public void Implemented_major_version_is_the_spec_version()
    {
        var overview = OverviewSection();
        Assert.False(overview.Length == 0, $"The spec has no '{VersioningHeading}' section declaring its version.");

        var declared = DeclaredVersion().Match(overview);

        Assert.True(declared.Success, "The spec's §1 declares no version like 'v2'.");
        Assert.Equal(ProtocolVersion.Current.ToString(), declared.Groups[1].Value);
    }
}
