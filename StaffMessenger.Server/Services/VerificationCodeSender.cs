using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using StaffMessenger.Contracts.Auth;

namespace StaffMessenger.Server.Services;

public interface IVerificationCodeSender
{
    Task SendAsync(AuthProvider provider, string identifier, string code, CancellationToken cancellationToken = default);
}

public sealed class VerificationCodeSender : IVerificationCodeSender
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<VerificationCodeSender> _logger;

    public VerificationCodeSender(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<VerificationCodeSender> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendAsync(
        AuthProvider provider,
        string identifier,
        string code,
        CancellationToken cancellationToken = default)
    {
        switch (provider)
        {
            case AuthProvider.Email:
                await SendEmailAsync(identifier, code, cancellationToken);
                return;
            case AuthProvider.Phone:
                await SendSmsAsync(identifier, code, cancellationToken);
                return;
            default:
                throw new InvalidOperationException("Verification delivery is available only for email and phone.");
        }
    }

    private async Task SendEmailAsync(string email, string code, CancellationToken cancellationToken)
    {
        var sendGridApiKey = _configuration["Verification:Email:SendGrid:ApiKey"];
        if (!string.IsNullOrWhiteSpace(sendGridApiKey))
        {
            await SendEmailViaSendGridAsync(email, code, sendGridApiKey, cancellationToken);
            return;
        }

        var smtpHost = _configuration["Verification:Email:Smtp:Host"];
        if (!string.IsNullOrWhiteSpace(smtpHost))
        {
            await SendEmailViaSmtpAsync(email, code, cancellationToken);
            return;
        }

        DevelopmentFallback(AuthProvider.Email, email, code);
    }

    private async Task SendEmailViaSendGridAsync(
        string email,
        string code,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.sendgrid.com/v3/mail/send");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                personalizations = new[]
                {
                    new
                    {
                        to = new[] { new { email } }
                    }
                },
                from = new
                {
                    email = _configuration["Verification:Email:FromEmail"] ?? "security@staffmessenger.local",
                    name = _configuration["Verification:Email:FromName"] ?? "StaffMessenger"
                },
                subject = "Код подтверждения StaffMessenger",
                content = new[]
                {
                    new
                    {
                        type = "text/plain",
                        value = $"Ваш код подтверждения StaffMessenger: {code}. Код действует 10 минут."
                    }
                }
            }),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task SendEmailViaSmtpAsync(string email, string code, CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(
            _configuration["Verification:Email:FromName"] ?? "StaffMessenger",
            _configuration["Verification:Email:FromEmail"] ?? "security@staffmessenger.local"));
        message.To.Add(MailboxAddress.Parse(email));
        message.Subject = "Код подтверждения StaffMessenger";
        message.Body = new TextPart("plain")
        {
            Text = $"Ваш код подтверждения StaffMessenger: {code}. Код действует 10 минут."
        };

        var host = _configuration["Verification:Email:Smtp:Host"]
                   ?? throw new InvalidOperationException("SMTP host is not configured.");
        var port = _configuration.GetValue("Verification:Email:Smtp:Port", 587);
        var username = _configuration["Verification:Email:Smtp:Username"];
        var password = _configuration["Verification:Email:Smtp:Password"] ?? "";
        var secureSocketOptions = _configuration.GetValue("Verification:Email:Smtp:UseSsl", true)
            ? SecureSocketOptions.StartTls
            : SecureSocketOptions.Auto;

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(host, port, secureSocketOptions, cancellationToken);

        if (!string.IsNullOrWhiteSpace(username))
            await smtp.AuthenticateAsync(username, password, cancellationToken);

        await smtp.SendAsync(message, cancellationToken);
        await smtp.DisconnectAsync(true, cancellationToken);
    }

    private async Task SendSmsAsync(string phone, string code, CancellationToken cancellationToken)
    {
        var accountSid = _configuration["Verification:Sms:Twilio:AccountSid"];
        var authToken = _configuration["Verification:Sms:Twilio:AuthToken"];
        var from = _configuration["Verification:Sms:Twilio:From"];

        if (string.IsNullOrWhiteSpace(accountSid)
            || string.IsNullOrWhiteSpace(authToken)
            || string.IsNullOrWhiteSpace(from))
        {
            DevelopmentFallback(AuthProvider.Phone, phone, code);
            return;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.twilio.com/2010-04-01/Accounts/{Uri.EscapeDataString(accountSid)}/Messages.json");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{accountSid}:{authToken}")));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["To"] = phone,
            ["From"] = from,
            ["Body"] = $"StaffMessenger code: {code}. Valid for 10 minutes."
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private void DevelopmentFallback(AuthProvider provider, string identifier, string code)
    {
        if (!_configuration.GetValue("Verification:ExposeDevelopmentCodes", false))
            throw new InvalidOperationException("Verification delivery provider is not configured.");

        _logger.LogInformation(
            "Development verification code for {Provider} {Identifier}: {Code}",
            provider,
            identifier,
            code);
    }
}
