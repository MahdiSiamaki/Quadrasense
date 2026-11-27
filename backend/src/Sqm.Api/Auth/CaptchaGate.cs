using System.Threading.RateLimiting;
using Sqm.Application.Identity;

namespace Sqm.Api.Auth;

/// <summary>What a form learns from the captcha before it goes on.</summary>
public enum CaptchaVerdict
{
    /// <summary>Not enough recent failures to ask for one.</summary>
    NotNeeded,

    /// <summary>Asked for, and answered.</summary>
    Passed,

    /// <summary>Asked for, and not sent: the page did not know yet. Counts as nothing.</summary>
    Missing,

    /// <summary>Asked for, and answered wrongly: counted, and the picture is gone.</summary>
    Wrong,
}

/// <summary>
/// Where a public form meets the Persian captcha (ADR-026): sign-in after three failures, recovery after
/// five. Checked before anything else the form does, so a wrong answer never reaches a password check -
/// never counts towards locking an account, and never costs an Argon2 verification.
/// </summary>
public sealed class CaptchaGate : IDisposable
{
    private readonly ICaptchaService _captcha;
    private readonly IAuditLog _audit;

    // Five wrong answers from one address in ten minutes reach the audit trail, then no more until the
    // window turns: a flood of guesses is one line of evidence repeated, and the trail is not the thing
    // it should fill.
    private readonly PartitionedRateLimiter<string> _auditLimit =
        PartitionedRateLimiter.Create<string, string>(address => RateLimitPartition.GetFixedWindowLimiter(
            address, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
            }));

    /// <summary>Creates the gate.</summary>
    public CaptchaGate(ICaptchaService captcha, IAuditLog audit)
    {
        _captcha = captcha;
        _audit = audit;
    }

    /// <summary>Whether the attempt may go on, judged on the address and the name tried.</summary>
    public async Task<CaptchaVerdict> CheckAsync(
        CaptchaScope scope, HttpContext http, string? username, string? id, string? answer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        var address = Address(http);

        if (!await _captcha.IsRequiredAsync(scope, address, username, ct).ConfigureAwait(false))
        {
            return CaptchaVerdict.NotNeeded;
        }

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(answer))
        {
            return CaptchaVerdict.Missing;
        }

        if (await _captcha.RedeemAsync(id, answer, ct).ConfigureAwait(false))
        {
            return CaptchaVerdict.Passed;
        }

        await _captcha.RecordFailureAsync(scope, address, username, ct).ConfigureAwait(false);

        using var lease = _auditLimit.AttemptAcquire(address ?? "unknown");
        if (lease.IsAcquired)
        {
            await _audit.WriteAsync(new AuditEntry(
                // The name as typed for a sign-in, as a failed password records it. Not for recovery,
                // where what is typed may be an e-mail address or a phone number.
                ActorName: scope == CaptchaScope.Login ? Truncate(username) ?? "(empty)" : "(recovery)",
                Action: "auth.captcha",
                Category: AuditCategory.Authentication,
                Outcome: AuditOutcome.Failure,
                Ip: address,
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                CorrelationId: http.TraceIdentifier,
                Detail: new Dictionary<string, object?>
                {
                    ["scope"] = scope == CaptchaScope.Login ? "login" : "recovery",
                }), ct).ConfigureAwait(false);
        }

        return CaptchaVerdict.Wrong;
    }

    /// <summary>Whether the next attempt will need a captcha.</summary>
    public Task<bool> RequiredAsync(CaptchaScope scope, HttpContext http, string? username, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        return _captcha.IsRequiredAsync(scope, Address(http), username, ct);
    }

    /// <summary>Counts a failure of the form itself: a wrong password, a wrong code.</summary>
    public Task FailedAsync(CaptchaScope scope, HttpContext http, string? username, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        return _captcha.RecordFailureAsync(scope, Address(http), username, ct);
    }

    /// <summary>Forgets the name's failures once it proved itself.</summary>
    public Task SucceededAsync(CaptchaScope scope, string? username, CancellationToken ct) =>
        _captcha.ForgetAsync(scope, username, ct);

    private static string? Address(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    private static string? Truncate(string? value) =>
        value is null ? null : value.Length <= 200 ? value : value[..200];

    /// <inheritdoc />
    public void Dispose() => _auditLimit.Dispose();
}
