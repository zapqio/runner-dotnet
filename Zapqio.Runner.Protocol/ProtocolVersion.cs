namespace Zapqio.Runner.Protocol;

/// <summary>
/// Protocol version negotiated on the WebSocket handshake. The runner sends the <see cref="Header"/>
/// request header carrying its protocol <b>major</b> version; the server accepts the connection only
/// if it supports that version. A missing header is treated as version <c>1</c> (the pre-versioning
/// baseline), so runners built before versioning was introduced keep working. The major version is
/// bumped only on a breaking change â€” see <c>protocol/PROTOCOL.md</c> Â§9.
/// </summary>
public static class ProtocolVersion
{
    /// <summary>Handshake header carrying the runner's protocol major version.</summary>
    public const string Header = "X-Zapqio-Protocol-Version";

    /// <summary>
    /// The protocol major version implemented by this build.
    ///
    /// v2 added the required <c>attemptId</c> on Job/Log/JobReturn, the <c>JobAccepted</c> message
    /// with its acceptance deadline, and the one-outstanding-job-per-runner rule. A v1 runner cannot
    /// talk to a v2 server: the fields are required in both directions.
    /// </summary>
    public const int Current = 2;
}
