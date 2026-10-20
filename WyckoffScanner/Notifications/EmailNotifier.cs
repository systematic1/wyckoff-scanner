using System.Globalization;
using System.Net;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using WyckoffScanner.Configuration;
using WyckoffScanner.Data;
using WyckoffScanner.Models;

namespace WyckoffScanner.Notifications;

/// <summary>Builds and sends the daily list of flagged symbols.</summary>
public sealed class EmailNotifier
{
    private readonly AnalysisRepository _results;
    private readonly EmailOptions _options;
    private readonly ILogger<EmailNotifier> _log;

    public EmailNotifier(AnalysisRepository results, IOptions<EmailOptions> options, ILogger<EmailNotifier> log)
    {
        _results = results;
        _options = options.Value;
        _log = log;
    }

    public async Task SendDailyReportAsync(DateOnly analysisDate, CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("Email notification are disabled.");
            return;
        }
        
        var flagged = await _results.GetFlaggedAsync(analysisDate, ct);

        if (flagged.Count == 0 && !_options.SendWhenEmpty)
        {
            _log.LogInformation("MAIL: No flagged symbols and SendWhenEmpty=false; no e-mail sent.");
            return;
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_options.From));
        foreach (var to in _options.To.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            message.To.Add(MailboxAddress.Parse(to));

        message.Subject = $"Wyckoff scan {analysisDate:yyyy-MM-dd}: {flagged.Count} symbol(s) flagged";
        message.Body = new BodyBuilder
        {
            TextBody = BuildText(analysisDate, flagged),
            HtmlBody = BuildHtml(analysisDate, flagged)
        }.ToMessageBody();

        using var client = new SmtpClient();
        var mode = Enum.TryParse<SecureSocketOptions>(_options.SecureSocketMode, true, out var m) ? 
            m : 
            SecureSocketOptions.StartTls;

        await client.ConnectAsync(_options.SmtpHost, _options.SmtpPort, mode, ct);
        if (!string.IsNullOrWhiteSpace(_options.Username))
            await client.AuthenticateAsync(_options.Username, _options.Password, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);

        _log.LogInformation("MAIL: E-mail sent to '{To}' with {Count} flagged symbol(s).", _options.To, flagged.Count);
    }

    private static string Money(decimal? v) => v.HasValue ? v.Value.ToString("F2", CultureInfo.InvariantCulture) : "-";

    private static string BuildText(DateOnly date, IReadOnlyList<FlaggedSymbol> flagged)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Wyckoff scan for {date:yyyy-MM-dd}");
        sb.AppendLine();

        if (flagged.Count == 0)
            sb.AppendLine("No symbols are currently flagged.");
        else
        {
            foreach (var f in flagged)
                sb.AppendLine($"{f.Ticker,-8} {f.Pattern,-13} {f.FlagMessage,-32} last {Money(f.LastClose)}  support {Money(f.SupportPrice)}  resistance {Money(f.ResistancePrice)}");
        }

        sb.AppendLine();
        sb.AppendLine("Automated pattern screen - not investment advice.");
        return sb.ToString();
    }

    private static string BuildHtml(DateOnly date, IReadOnlyList<FlaggedSymbol> flagged)
    {
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append("<html><body style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px\">");
        sb.Append($"<h2>Wyckoff scan for {date:yyyy-MM-dd}</h2>");

        if (flagged.Count == 0)
            sb.Append("<p>No symbols are currently flagged.</p>");
        else
        {
            sb.Append("<table cellpadding=\"6\" cellspacing=\"0\" style=\"border-collapse:collapse;border:1px solid #ccc\">");
            sb.Append("<tr style=\"background:#f0f0f0;text-align:left\"><th>Symbol</th><th>Pattern</th><th>Flag</th><th>Last close</th><th>Support</th><th>Resistance</th></tr>");

            foreach (var f in flagged)
            {
                sb.Append("<tr style=\"border-top:1px solid #ddd\">");
                sb.Append($"<td><b>{E(f.Ticker)}</b></td><td style=\"color:{(f.Pattern == "Distribution" ? "#b00020" : "#1b7f3b")}\">{E(f.Pattern)}</td><td>{E(f.FlagMessage)}</td>");
                sb.Append($"<td>{Money(f.LastClose)}</td><td>{Money(f.SupportPrice)}</td><td>{Money(f.ResistancePrice)}</td></tr>");
                if (!string.IsNullOrWhiteSpace(f.Details))
                    sb.Append($"<tr><td></td><td colspan=\"5\" style=\"color:#555;font-size:12px\">{E(f.Details)}</td></tr>");
            }
            sb.Append("</table>");
        }

        sb.Append("<p style=\"color:#888;font-size:12px\">Automated pattern screen &ndash; not investment advice.</p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
