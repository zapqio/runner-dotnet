namespace Zapqio.Protocol;

/// <summary>
/// Protocol version negotiated on the WebSocket handshake. The runner sends the <see cref="Header"/>
/// request header carrying its protocol <b>major</b> version; the server accepts the connection only
/// if it supports that version. A missing header is treated as version <c>1</c> (the pre-versioning
/// baseline), so runners built before versioning was introduced keep working. The major version is
/// bumped only on a breaking change — see <c>protocol/PROTOCOL.md</c> §9.
/// </summary>
public static class ProtocolVersion
{
    /// <summary>Handshake header carrying the runner's protocol major version.</summary>
    public const string Header = "X-Zapqio-Protocol-Version";

    /// <summary>The protocol major version implemented by this build.</summary>
    public const int Current = 1;
}
