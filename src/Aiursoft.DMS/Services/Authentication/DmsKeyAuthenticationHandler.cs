using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Aiursoft.DMS.Entities;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aiursoft.DMS.Services.Authentication;

public sealed class DmsKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    TemplateDbContext db, UserManager<User> users, IUserClaimsPrincipalFactory<User> principalFactory)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DmsKey";
    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    public static string CreateKey(bool machine) => (machine ? "dmsa_" : "dmsu_") + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var raw = header[7..];
        if (raw.Length != 69 || !raw[5..].All(char.IsAsciiHexDigit)) return AuthenticateResult.Fail("Invalid API Key.");
        var hash = Hash(raw);
        if (raw.StartsWith("dmsj_", StringComparison.Ordinal))
        {
            var join = await db.JoinKeys.AsNoTracking().NotCacheable().SingleOrDefaultAsync(k => k.KeyHash == hash && !k.Revoked && k.ExpiresAt > DateTime.UtcNow);
            if (join == null) return AuthenticateResult.Fail("Invalid or expired join key.");
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("DmsJoinKey", join.Id.ToString())], SchemeName)), SchemeName));
        }
        if (raw.StartsWith("dmsr_", StringComparison.Ordinal))
        {
            var enrollment = await db.AgentEnrollments.AsNoTracking().NotCacheable().SingleOrDefaultAsync(e => e.ReceiptHash == hash);
            if (enrollment == null) return AuthenticateResult.Fail("Invalid enrollment receipt.");
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("DmsEnrollment", enrollment.Id.ToString())], SchemeName)), SchemeName));
        }
        if (raw.StartsWith("dmsa_", StringComparison.Ordinal))
        {
            var machine = await db.Machines.AsNoTracking().NotCacheable().SingleOrDefaultAsync(m => m.KeyHash == hash && !m.Disabled);
            if (machine == null) return AuthenticateResult.Fail("Invalid Agent Key.");
            var identity = new ClaimsIdentity([new Claim("DmsMachine", machine.Id.ToString())], SchemeName);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
        }
        if (!raw.StartsWith("dmsu_", StringComparison.Ordinal)) return AuthenticateResult.Fail("Invalid API Key.");
        var key = await db.PersonalApiKeys.AsNoTracking().NotCacheable().SingleOrDefaultAsync(k => k.KeyHash == hash && k.ExpiresAt > DateTime.UtcNow);
        if (key == null) return AuthenticateResult.Fail("Invalid or expired API Key.");
        var user = await users.FindByIdAsync(key.UserId);
        if (user == null || await users.IsLockedOutAsync(user)) return AuthenticateResult.Fail("Key owner unavailable.");
        var principal = await principalFactory.CreateAsync(user);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
