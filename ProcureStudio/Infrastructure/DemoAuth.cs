using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ProcureStudio.Infrastructure;

public static class DemoAuth { public const string Scheme = "Demo"; }

/// <summary>
/// ⚠ DEMO ONLY. Trusts two request headers (X-Demo-User, X-Demo-Role) to build a ClaimsPrincipal,
/// which lets the UI switch users without a login server. ANYONE can claim to be a manager this way.
/// In production replace this single class with real authentication (JWT bearer / Entra ID / cookies):
/// nothing else in the app changes, because everything downstream only reads standard claims.
/// </summary>
public sealed class DemoAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static readonly HashSet<string> KnownRoles = ["buyer", "manager", "viewer"];

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Demo-User"].ToString().Trim();
        if (user.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());   // -> 401 on protected endpoints

        var role = Request.Headers["X-Demo-Role"].ToString().Trim().ToLowerInvariant();
        if (!KnownRoles.Contains(role)) role = "viewer";                               // least privilege by default

        // NameIdentifier is the claim the framework's session isolation partitions conversations by.
        Claim[] claims = [new(ClaimTypes.NameIdentifier, user), new(ClaimTypes.Name, user), new(ClaimTypes.Role, role)];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
