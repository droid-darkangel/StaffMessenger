using StaffMessenger.Contracts.Crypto;

namespace StaffMessenger.Crypto.Entropy;

public interface IQuantumEntropyGenerator
{
    byte[] GetBytes(int length);

    ValueTask<byte[]> GetBytesAsync(int length, CancellationToken cancellationToken = default);

    string CreateToken(int byteLength = 32);

    EntropySnapshot Snapshot { get; }
}
