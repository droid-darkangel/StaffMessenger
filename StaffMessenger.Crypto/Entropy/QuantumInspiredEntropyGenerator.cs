using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using StaffMessenger.Contracts.Crypto;
using StaffMessenger.Crypto.Identity;

namespace StaffMessenger.Crypto.Entropy;

public sealed class QuantumInspiredEntropyGenerator : IQuantumEntropyGenerator
{
    private readonly object _gate = new();
    private readonly EntropyOptions _options;
    private readonly HttpClient _httpClient;
    private byte[] _pool;
    private long _counter;
    private long _reseedCounter;
    private DateTimeOffset _lastReseedAt;
    private string _activeSource = "OS CSPRNG";

    public QuantumInspiredEntropyGenerator()
        : this(new EntropyOptions(ReadEndpointFromEnvironment()), new HttpClient())
    {
    }

    public QuantumInspiredEntropyGenerator(EntropyOptions options, HttpClient httpClient)
    {
        _options = options;
        _httpClient = httpClient;
        _pool = RandomNumberGenerator.GetBytes(64);
        _lastReseedAt = DateTimeOffset.UtcNow;
    }

    public EntropySnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                var fingerprint = Base64Url.Encode(SHA256.HashData(_pool)[..10]);
                var sourceBonus = _activeSource.Contains("external", StringComparison.OrdinalIgnoreCase) ? 0.02 : 0;
                var health = Math.Clamp(0.96 + sourceBonus, 0, 0.99);
                return new EntropySnapshot(health, _reseedCounter, _activeSource, _lastReseedAt, fingerprint);
            }
        }
    }

    public byte[] GetBytes(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

        lock (_gate)
        {
            Reseed(CaptureJitter(), "OS CSPRNG + timing jitter");
            var output = Expand(length);
            Reseed(output.AsSpan(0, Math.Min(output.Length, 64)), "post-generation rekey");
            return output;
        }
    }

    public async ValueTask<byte[]> GetBytesAsync(int length, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

        var externalEntropy = await TryFetchExternalEntropyAsync(cancellationToken);

        lock (_gate)
        {
            if (externalEntropy.Length > 0)
                Reseed(externalEntropy, "OS CSPRNG + timing jitter + external entropy");
            else
                Reseed(CaptureJitter(), "OS CSPRNG + timing jitter");

            var output = Expand(length);
            Reseed(output.AsSpan(0, Math.Min(output.Length, 64)), "post-generation rekey");
            return output;
        }
    }

    public string CreateToken(int byteLength = 32)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(byteLength, 16);
        return Base64Url.Encode(GetBytes(byteLength));
    }

    private static Uri? ReadEndpointFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("STAFFMESSENGER_ENTROPY_ENDPOINT");
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
    }

    private byte[] Expand(int length)
    {
        var output = new byte[length];
        var offset = 0;

        while (offset < output.Length)
        {
            var counterBytes = new byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(counterBytes.AsSpan(0, 8), ++_counter);
            BinaryPrimitives.WriteInt64LittleEndian(counterBytes.AsSpan(8, 8), Stopwatch.GetTimestamp());

            var label = Encoding.UTF8.GetBytes("StaffMessenger entropy expand");
            var material = new byte[counterBytes.Length + label.Length];
            counterBytes.CopyTo(material.AsSpan());
            label.CopyTo(material.AsSpan(counterBytes.Length));

            var block = Hmac(_pool, material);
            var take = Math.Min(block.Length, output.Length - offset);
            block.AsSpan(0, take).CopyTo(output.AsSpan(offset, take));
            CryptographicOperations.ZeroMemory(block);
            offset += take;
        }

        return output;
    }

    private void Reseed(ReadOnlySpan<byte> material, string source)
    {
        var systemRandom = RandomNumberGenerator.GetBytes(64);
        var combined = new byte[_pool.Length + systemRandom.Length + material.Length + 16];

        _pool.CopyTo(combined, 0);
        systemRandom.CopyTo(combined, _pool.Length);
        material.CopyTo(combined.AsSpan(_pool.Length + systemRandom.Length));
        BinaryPrimitives.WriteInt64LittleEndian(combined.AsSpan(combined.Length - 16, 8), ++_reseedCounter);
        BinaryPrimitives.WriteInt64LittleEndian(combined.AsSpan(combined.Length - 8, 8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var nextPool = Hmac(_pool, combined);
        CryptographicOperations.ZeroMemory(_pool);
        CryptographicOperations.ZeroMemory(systemRandom);
        CryptographicOperations.ZeroMemory(combined);

        _pool = nextPool;
        _activeSource = source;
        _lastReseedAt = DateTimeOffset.UtcNow;
    }

    private byte[] CaptureJitter()
    {
        var rounds = Math.Clamp(_options.JitterRounds, 16, 256);
        var material = new byte[rounds * 40];
        var offset = 0;

        for (var i = 0; i < rounds; i++)
        {
            var start = Stopwatch.GetTimestamp();
            Thread.SpinWait(17 + i % 11);
            var end = Stopwatch.GetTimestamp();

            BinaryPrimitives.WriteInt64LittleEndian(material.AsSpan(offset, 8), start);
            BinaryPrimitives.WriteInt64LittleEndian(material.AsSpan(offset + 8, 8), end);
            BinaryPrimitives.WriteInt64LittleEndian(material.AsSpan(offset + 16, 8), end - start);
            BinaryPrimitives.WriteInt32LittleEndian(material.AsSpan(offset + 24, 4), Environment.CurrentManagedThreadId);
            BinaryPrimitives.WriteInt64LittleEndian(material.AsSpan(offset + 28, 8), GC.GetTotalMemory(false));
            BinaryPrimitives.WriteInt32LittleEndian(material.AsSpan(offset + 36, 4), Random.Shared.Next());
            offset += 40;
        }

        return SHA512.HashData(material);
    }

    private async ValueTask<byte[]> TryFetchExternalEntropyAsync(CancellationToken cancellationToken)
    {
        if (_options.ExternalEntropyEndpoint is null)
            return [];

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_options.Timeout);
            var payload = await _httpClient.GetByteArrayAsync(_options.ExternalEntropyEndpoint, cts.Token);

            if (payload.Length == 0)
                return [];

            var trimmed = Encoding.UTF8.GetString(payload).Trim();
            if (Base64Url.TryDecode(trimmed, out var decoded) || TryBase64Decode(trimmed, out decoded))
                return SHA512.HashData(decoded);

            return SHA512.HashData(payload);
        }
        catch
        {
            return [];
        }
    }

    private static bool TryBase64Decode(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch
        {
            bytes = [];
            return false;
        }
    }

    private static byte[] Hmac(byte[] key, ReadOnlySpan<byte> material)
    {
        using var hmac = new HMACSHA512(key);
        return hmac.ComputeHash(material.ToArray());
    }
}
