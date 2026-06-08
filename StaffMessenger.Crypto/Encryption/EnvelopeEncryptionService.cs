using System.Security.Cryptography;
using System.Text;
using StaffMessenger.Contracts.Crypto;
using StaffMessenger.Crypto.Entropy;
using StaffMessenger.Crypto.Identity;

namespace StaffMessenger.Crypto.Encryption;

public sealed class EnvelopeEncryptionService
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly IQuantumEntropyGenerator _entropy;

    public EnvelopeEncryptionService(IQuantumEntropyGenerator entropy)
        =>  _entropy = entropy;

    public DeviceKeyPair CreateDeviceKeyPair()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var publicBytes = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var privateBytes = ecdh.ExportPkcs8PrivateKey();
        var keyId = Base64Url.Encode(SHA256.HashData(publicBytes)[..16]);

        var publicKey = new PublicDeviceKey(
            "ECDH-P256+A256GCM",
            keyId,
            Convert.ToBase64String(publicBytes),
            DateTimeOffset.UtcNow);

        return new DeviceKeyPair(publicKey, Convert.ToBase64String(privateBytes));
    }

    public CryptoEnvelope EncryptText(string plainText, PublicDeviceKey recipientKey, string associatedData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainText);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientKey.PublicKeyBase64);

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var aadBytes = Encoding.UTF8.GetBytes(associatedData);
        var nonce = _entropy.GetBytes(NonceSize);
        var cipherText = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var recipient = ECDiffieHellman.Create();
        recipient.ImportSubjectPublicKeyInfo(Convert.FromBase64String(recipientKey.PublicKeyBase64), out _);

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var shared = ephemeral.DeriveKeyFromHash(recipient.PublicKey, HashAlgorithmName.SHA256);
        var key = HkdfSha256(shared, nonce, aadBytes, 32);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherText, tag, aadBytes);

        CryptographicOperations.ZeroMemory(plainBytes);
        CryptographicOperations.ZeroMemory(shared);
        CryptographicOperations.ZeroMemory(key);

        return new CryptoEnvelope(
            "ECDH-P256+A256GCM",
            recipientKey.KeyId,
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(cipherText),
            Convert.ToBase64String(tag),
            Convert.ToBase64String(aadBytes),
            Convert.ToBase64String(ephemeral.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    public string DecryptText(CryptoEnvelope envelope, string privateKeyBase64)
    {
        var nonce = Convert.FromBase64String(envelope.NonceBase64);
        var cipherText = Convert.FromBase64String(envelope.CipherTextBase64);
        var tag = Convert.FromBase64String(envelope.TagBase64);
        var aad = Convert.FromBase64String(envelope.AssociatedDataBase64);
        var plainBytes = new byte[cipherText.Length];

        using var privateKey = ECDiffieHellman.Create();
        privateKey.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);

        using var ephemeral = ECDiffieHellman.Create();
        ephemeral.ImportSubjectPublicKeyInfo(Convert.FromBase64String(envelope.SenderEphemeralPublicKeyBase64), out _);

        var shared = privateKey.DeriveKeyFromHash(ephemeral.PublicKey, HashAlgorithmName.SHA256);
        var key = HkdfSha256(shared, nonce, aad, 32);

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipherText, tag, plainBytes, aad);

        CryptographicOperations.ZeroMemory(shared);
        CryptographicOperations.ZeroMemory(key);

        var text = Encoding.UTF8.GetString(plainBytes);
        CryptographicOperations.ZeroMemory(plainBytes);
        return text;
    }

    private static byte[] HkdfSha256(byte[] ikm, byte[] salt, byte[] info, int length)
    {
        using var extract = new HMACSHA256(salt);
        var prk = extract.ComputeHash(ikm);
        var okm = new byte[length];
        var previous = Array.Empty<byte>();
        var offset = 0;
        byte counter = 1;

        while (offset < length)
        {
            using var expand = new HMACSHA256(prk);
            var input = new byte[previous.Length + info.Length + 1];
            previous.CopyTo(input, 0);
            info.CopyTo(input.AsSpan(previous.Length));
            input[^1] = counter++;

            previous = expand.ComputeHash(input);
            var take = Math.Min(previous.Length, length - offset);
            previous.AsSpan(0, take).CopyTo(okm.AsSpan(offset, take));
            offset += take;
        }

        CryptographicOperations.ZeroMemory(prk);
        return okm;
    }
}
