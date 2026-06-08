namespace StaffMessenger.Crypto.Entropy;

public sealed record EntropyOptions(
    Uri? ExternalEntropyEndpoint = null,
    TimeSpan? ExternalEntropyTimeout = null,
    int JitterRounds = 48)
{
    public TimeSpan Timeout => ExternalEntropyTimeout ?? TimeSpan.FromSeconds(2);
}
