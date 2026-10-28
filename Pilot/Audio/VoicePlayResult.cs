using System;

namespace NecoArcDesktopPet.Pilot.Audio;

/// <summary>
/// Immutable result of a voice play submission.
/// Indicates whether the request was admitted into the audio playback pipeline.
/// </summary>
public readonly struct VoicePlayResult : IEquatable<VoicePlayResult>
{
    public bool Accepted { get; }
    public long SessionId { get; }
    public string? RejectionReason { get; }

    private VoicePlayResult(bool accepted, long sessionId, string? rejectionReason)
    {
        Accepted = accepted;
        SessionId = sessionId;
        RejectionReason = rejectionReason;
    }

    public static VoicePlayResult Success(long sessionId) => new(true, sessionId, null);
    public static VoicePlayResult Rejected(string reason) => new(false, 0, reason ?? "Rejected");

    public bool Equals(VoicePlayResult other) =>
        Accepted == other.Accepted &&
        SessionId == other.SessionId &&
        string.Equals(RejectionReason, other.RejectionReason, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is VoicePlayResult other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Accepted, SessionId, RejectionReason);
    public override string ToString() => Accepted ? $"Accepted(Session={SessionId})" : $"Rejected({RejectionReason})";

    public static bool operator ==(VoicePlayResult left, VoicePlayResult right) => left.Equals(right);
    public static bool operator !=(VoicePlayResult left, VoicePlayResult right) => !left.Equals(right);
    public static implicit operator bool(VoicePlayResult result) => result.Accepted;
}
