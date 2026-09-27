using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PeerOnQ.Observability;

namespace PeerOnQ.Cloud.Api;

public interface ICustomerMailSender
{
    bool IsEnabled { get; }
    void EnsureEnabled();
    Task SendAsync(string recipient, string subject, string textBody, CancellationToken cancellationToken);
}

public sealed class CustomerMailSender(IOptions<CustomerPortalOptions> options, IHostEnvironment environment) : ICustomerMailSender
{
    private static readonly SemaphoreSlim FileGate = new(1, 1);
    public bool IsEnabled => !options.Value.Mail.Provider.Equals("Disabled", StringComparison.OrdinalIgnoreCase);

    public void EnsureEnabled()
    {
        if (!IsEnabled)
            throw new ApiProblemException(StatusCodes.Status503ServiceUnavailable, "customer_mail_disabled", "Customer email delivery is disabled.");
    }

    public async Task SendAsync(string recipient, string subject, string textBody, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var mail = options.Value.Mail;
        if (mail.Provider.Equals("FileSink", StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
                throw new InvalidOperationException("The development mail sink is not available in this environment.");
            var root = Path.GetFullPath(mail.FileSinkPath, environment.ContentRootPath);
            Directory.CreateDirectory(root);
            var file = Path.Combine(root, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
            var payload = JsonSerializer.Serialize(new { recipient, subject, textBody, createdAtUtc = DateTimeOffset.UtcNow });
            await FileGate.WaitAsync(cancellationToken);
            try { await File.WriteAllTextAsync(file, payload, cancellationToken); }
            finally { FileGate.Release(); }
            return;
        }

        using var message = new MailMessage(mail.FromAddress, recipient, subject, textBody);
        using var client = new SmtpClient(mail.SmtpHost, mail.SmtpPort)
        {
            EnableSsl = mail.SmtpUseTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
        };
        if (!string.IsNullOrWhiteSpace(mail.SmtpUsername))
            client.Credentials = new NetworkCredential(mail.SmtpUsername, mail.SmtpPassword);
        cancellationToken.ThrowIfCancellationRequested();
        await client.SendMailAsync(message, cancellationToken);
    }
}
