using RaaiCam.Core.Pairing;

namespace RaaiCam.Core.Phone;

/// <summary>
/// What a <see cref="PhoneSession"/> needs for the v2 authenticated link. With <see cref="Pairing"/> set the handshake runs
/// in pairing mode (mode 2, QR token as PSK; any existing pairing is replaced once the new phone is confirmed); otherwise
/// <see cref="Phone"/> (required) is the paired phone and the handshake runs in paired mode (mode 1).
/// </summary>
public sealed record SecureLinkOptions(PcIdentity Identity, PairingStore Store, PairedPhone? Phone, PairingSession? Pairing)
{
    internal void Validate()
    {
        if (Identity is null || Store is null) throw new ArgumentException("identity and store are required");
        if (Pairing is null && Phone is null) throw new ArgumentException("either a paired phone or a pairing session is required");
    }
}
