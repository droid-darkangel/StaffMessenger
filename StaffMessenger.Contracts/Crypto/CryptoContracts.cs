namespace StaffMessenger.Contracts.Crypto;

public sealed record PublicDeviceKey(
    string Algorithm,
    string KeyId,
    string PublicKeyBase64,
    DateTimeOffset CreatedAt);

public sealed record CryptoEnvelope(
    string Algorithm,
    string KeyId,
    string NonceBase64,
    string CipherTextBase64,
    string TagBase64,
    string AssociatedDataBase64,
    string SenderEphemeralPublicKeyBase64);

public sealed record EntropySnapshot(
    double HealthScore,
    long ReseedCounter,
    string ActiveSource,
    DateTimeOffset LastReseedAt,
    string PoolFingerprint);
