using System.Security.Cryptography;
using System.Text;
using StaffMessenger.Crypto.Entropy;

namespace StaffMessenger.Server.Security;

public sealed class TotpService
{
    private const int StepSeconds = 30;
    private const int CodeDigits = 6;
    private readonly IQuantumEntropyGenerator _entropy;

    public TotpService(IQuantumEntropyGenerator entropy)
        => _entropy = entropy;

    public string CreateSecret()
        => ToBase32(RandomNumberGenerator.GetBytes(20));

    public string CreateOtpAuthUri(string issuer, string account, string secret)
    {
        var label = Uri.EscapeDataString($"{issuer}:{account}");
        var queryIssuer = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{label}?secret={secret}&issuer={queryIssuer}&algorithm=SHA1&digits={CodeDigits}&period={StepSeconds}";
    }

    public bool ValidateCode(string secret, string code, DateTimeOffset? now = null)
    {
        var normalized = new string(code.Where(char.IsDigit).ToArray());
        if (normalized.Length != CodeDigits)
            return false;

        var instant = now ?? DateTimeOffset.UtcNow;
        var step = instant.ToUnixTimeSeconds() / StepSeconds;
        for (var offset = -1; offset <= 1; offset++)
        {
            var expected = ComputeCode(secret, step + offset);
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected),
                    Encoding.ASCII.GetBytes(normalized)))
                return true;
        }

        return false;
    }

    private static string ComputeCode(string secret, long counter)
    {
        var key = FromBase32(secret);
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(counterBytes);

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counterBytes);
        var offset = hash[^1] & 0x0f;
        var binary =
            ((hash[offset] & 0x7f) << 24)
            | ((hash[offset + 1] & 0xff) << 16)
            | ((hash[offset + 2] & 0xff) << 8)
            | (hash[offset + 3] & 0xff);
        var value = binary % 1_000_000;
        return value.ToString("D6");
    }

    private static string ToBase32(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder((bytes.Length + 4) / 5 * 8);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                output.Append(alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
            output.Append(alphabet[(buffer << (5 - bitsLeft)) & 31]);

        return output.ToString();
    }

    private static byte[] FromBase32(string input)
    {
        var clean = input.Trim().TrimEnd('=').Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        var bytes = new List<byte>();
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var character in clean)
        {
            var value = character switch
            {
                >= 'A' and <= 'Z' => character - 'A',
                >= '2' and <= '7' => character - '2' + 26,
                _ => -1
            };

            if (value < 0)
                continue;

            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bytes.Add((byte)((buffer >> (bitsLeft - 8)) & 0xff));
                bitsLeft -= 8;
            }
        }

        return bytes.ToArray();
    }
}
