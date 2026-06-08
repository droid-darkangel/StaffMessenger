using StaffMessenger.Crypto.Entropy;
using StaffMessenger.Crypto.Identity;

namespace StaffMessenger.Server.Security;

public sealed class TokenService
{
    private readonly IQuantumEntropyGenerator _entropy;

    public TokenService(IQuantumEntropyGenerator entropy)
        => _entropy = entropy;

    public (string Token, string Hash, DateTimeOffset ExpiresAt) CreateUserToken()
    {
        var token = $"smu_{_entropy.CreateToken(40)}";
        return (token, PasswordHasher.HashOpaqueToken(token), DateTimeOffset.UtcNow.AddMinutes(15));
    }

    public (string Token, string Hash, DateTimeOffset ExpiresAt) CreateBotToken()
    {
        var token = $"smb_{_entropy.CreateToken(48)}";
        return (token, PasswordHasher.HashOpaqueToken(token), DateTimeOffset.UtcNow.AddDays(365));
    }

    public string CreateSigningSecret()
        => $"sms_{_entropy.CreateToken(48)}";
}
