using System.Security.Cryptography;

namespace Whispernet.Core;

/// <summary>
/// Deterministic, I/O-free state machine validating and applying friend request, accept, reject, and remove transitions.
/// </summary>
public static class FriendGraphEngine
{
    public static bool TryValidateAuthorSpki(AuthorId expectedAuthorId, string spkiBase64, out byte[] spkiBytes, out string? error)
    {
        spkiBytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(spkiBase64))
        {
            error = "Missing SPKI public key.";
            return false;
        }

        try
        {
            spkiBytes = Convert.FromBase64String(spkiBase64);
        }
        catch (FormatException)
        {
            error = "Invalid Base64 SPKI public key.";
            return false;
        }

        var derived = new AuthorId(Convert.ToHexStringLower(SHA256.HashData(spkiBytes)));
        if (derived != expectedAuthorId)
        {
            error = $"SPKI hash ({derived}) does not match AuthorId ({expectedAuthorId}).";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryVerifyPacketSignature(
        AuthorId senderId,
        string senderSpkiBase64,
        byte[] signableBytes,
        string signatureBase64,
        GroupGovernanceEngine.SignatureVerifier verifySignature,
        out string? error)
    {
        if (!TryValidateAuthorSpki(senderId, senderSpkiBase64, out byte[] spkiBytes, out error))
        {
            return false;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException)
        {
            error = "Invalid Base64 signature.";
            return false;
        }

        if (!verifySignature(spkiBytes, signableBytes, signature))
        {
            error = "Invalid cryptographic signature on friend protocol packet.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryApplyIncomingRequest(
        FriendGraphState state,
        FriendRequestPacket packet,
        GroupGovernanceEngine.SignatureVerifier verifySignature,
        out bool autoAcceptedMutual,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(verifySignature);
        autoAcceptedMutual = false;

        if (packet.RecipientAuthorId != state.SelfAuthorId)
        {
            error = "FriendRequestPacket is not addressed to this account.";
            return false;
        }

        if (packet.SenderAuthorId == state.SelfAuthorId)
        {
            error = "Cannot send a friend request to self.";
            return false;
        }

        if (!TryVerifyPacketSignature(
                packet.SenderAuthorId,
                packet.SenderPublicKeySpkiBase64,
                packet.BuildSignableBytes(),
                packet.SignatureBase64,
                verifySignature,
                out error))
        {
            return false;
        }

        if (state.Entries.TryGetValue(packet.SenderAuthorId, out FriendEntry? existing))
        {
            // Simultaneous mutual friend request: promote PendingOutgoing directly to Accepted
            if (existing.Status == FriendStatus.PendingOutgoing)
            {
                ulong clock = Math.Max(existing.LogicalClock, packet.LogicalClock) + 1;
                state.MaxLogicalClock = Math.Max(state.MaxLogicalClock, clock);
                state.Entries[packet.SenderAuthorId] = existing with
                {
                    PeerPublicKeySpkiBase64 = packet.SenderPublicKeySpkiBase64,
                    DisplayName = string.IsNullOrWhiteSpace(packet.SenderDisplayName) ? existing.DisplayName : packet.SenderDisplayName.Trim(),
                    LastKnownNodeId = packet.SenderNodeId ?? existing.LastKnownNodeId,
                    Status = FriendStatus.Accepted,
                    LogicalClock = clock,
                    UpdatedAtUtc = packet.SentAtUtc
                };
                autoAcceptedMutual = true;
                error = null;
                return true;
            }

            if (existing.Status == FriendStatus.Accepted)
            {
                state.Entries[packet.SenderAuthorId] = existing with
                {
                    LastKnownNodeId = packet.SenderNodeId ?? existing.LastKnownNodeId,
                    DisplayName = string.IsNullOrWhiteSpace(packet.SenderDisplayName) ? existing.DisplayName : packet.SenderDisplayName.Trim()
                };
                error = null;
                return true;
            }

            if (packet.LogicalClock <= existing.LogicalClock)
            {
                error = $"Stale FriendRequestPacket clock ({packet.LogicalClock} <= {existing.LogicalClock}).";
                return false;
            }
        }

        state.MaxLogicalClock = Math.Max(state.MaxLogicalClock, packet.LogicalClock);
        state.Entries[packet.SenderAuthorId] = new FriendEntry(
            PeerAuthorId: packet.SenderAuthorId,
            PeerPublicKeySpkiBase64: packet.SenderPublicKeySpkiBase64,
            DisplayName: string.IsNullOrWhiteSpace(packet.SenderDisplayName) ? packet.SenderAuthorId.Value : packet.SenderDisplayName.Trim(),
            LastKnownNodeId: packet.SenderNodeId,
            Status: FriendStatus.PendingIncoming,
            LogicalClock: packet.LogicalClock,
            UpdatedAtUtc: packet.SentAtUtc);

        error = null;
        return true;
    }

    public static bool TryApplyIncomingResponse(
        FriendGraphState state,
        FriendResponsePacket packet,
        GroupGovernanceEngine.SignatureVerifier verifySignature,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(verifySignature);

        if (packet.RecipientAuthorId != state.SelfAuthorId)
        {
            error = "FriendResponsePacket is not addressed to this account.";
            return false;
        }

        if (!TryVerifyPacketSignature(
                packet.SenderAuthorId,
                packet.SenderPublicKeySpkiBase64,
                packet.BuildSignableBytes(),
                packet.SignatureBase64,
                verifySignature,
                out error))
        {
            return false;
        }

        if (state.Entries.TryGetValue(packet.SenderAuthorId, out FriendEntry? existing) &&
            packet.LogicalClock <= existing.LogicalClock)
        {
            error = $"Stale FriendResponsePacket clock ({packet.LogicalClock} <= {existing.LogicalClock}).";
            return false;
        }

        FriendStatus newStatus = packet.Accepted ? FriendStatus.Accepted : FriendStatus.Rejected;
        state.MaxLogicalClock = Math.Max(state.MaxLogicalClock, packet.LogicalClock);
        state.Entries[packet.SenderAuthorId] = new FriendEntry(
            PeerAuthorId: packet.SenderAuthorId,
            PeerPublicKeySpkiBase64: packet.SenderPublicKeySpkiBase64,
            DisplayName: string.IsNullOrWhiteSpace(packet.SenderDisplayName) ? packet.SenderAuthorId.Value : packet.SenderDisplayName.Trim(),
            LastKnownNodeId: packet.SenderNodeId ?? existing?.LastKnownNodeId,
            Status: newStatus,
            LogicalClock: packet.LogicalClock,
            UpdatedAtUtc: packet.RespondedAtUtc);

        error = null;
        return true;
    }

    public static bool TryApplyIncomingRemove(
        FriendGraphState state,
        FriendRemovePacket packet,
        GroupGovernanceEngine.SignatureVerifier verifySignature,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(verifySignature);

        if (packet.RecipientAuthorId != state.SelfAuthorId)
        {
            error = "FriendRemovePacket is not addressed to this account.";
            return false;
        }

        if (!TryVerifyPacketSignature(
                packet.SenderAuthorId,
                packet.SenderPublicKeySpkiBase64,
                packet.BuildSignableBytes(),
                packet.SignatureBase64,
                verifySignature,
                out error))
        {
            return false;
        }

        if (state.Entries.TryGetValue(packet.SenderAuthorId, out FriendEntry? existing) &&
            packet.LogicalClock <= existing.LogicalClock)
        {
            error = $"Stale FriendRemovePacket clock ({packet.LogicalClock} <= {existing.LogicalClock}).";
            return false;
        }

        state.MaxLogicalClock = Math.Max(state.MaxLogicalClock, packet.LogicalClock);
        state.Entries[packet.SenderAuthorId] = new FriendEntry(
            PeerAuthorId: packet.SenderAuthorId,
            PeerPublicKeySpkiBase64: packet.SenderPublicKeySpkiBase64,
            DisplayName: existing?.DisplayName ?? packet.SenderAuthorId.Value,
            LastKnownNodeId: existing?.LastKnownNodeId,
            Status: FriendStatus.Removed,
            LogicalClock: packet.LogicalClock,
            UpdatedAtUtc: packet.RemovedAtUtc);

        error = null;
        return true;
    }

    public static ulong NextClock(FriendGraphState state, AuthorId peerAuthorId)
    {
        ulong peerClock = state.Entries.TryGetValue(peerAuthorId, out FriendEntry? existing)
            ? existing.LogicalClock
            : 0;
        ulong next = Math.Max(state.MaxLogicalClock, peerClock) + 1;
        state.MaxLogicalClock = next;
        return next;
    }
}
