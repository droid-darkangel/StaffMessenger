using StaffMessenger.Contracts.Crypto;

namespace StaffMessenger.Crypto.Encryption;

public sealed record DeviceKeyPair(PublicDeviceKey PublicKey, string PrivateKeyBase64);
