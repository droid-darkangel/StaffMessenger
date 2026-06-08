namespace StaffMessenger.Services;

public sealed class TwoFactorRequiredException : Exception
{
    public TwoFactorRequiredException()
        : base("Для входа требуется одноразовый PIN 2FA.")
    {
    }
}
