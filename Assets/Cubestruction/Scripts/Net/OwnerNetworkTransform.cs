using Unity.Netcode.Components;

namespace Cubestruction.Net
{
    /// <summary>Owner-authoritative NetworkTransform: the owning client moves its own player
    /// (head, hands, body) for zero-latency VR, and the server relays it to everyone else.</summary>
    public class OwnerNetworkTransform : NetworkTransform
    {
        protected override bool OnIsServerAuthoritative() => false;
    }
}
