using System.Globalization;
using System.Text.Encodings.Web;
using Aiursoft.DMS.Configuration;
using Aiursoft.DMS.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Aiursoft.DMS.Services.Authentication;

public sealed class LocalApiAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    LocalApiTokenService tokenService,
    UserManager<User> userManager,
    IUserClaimsPrincipalFactory<User> principalFactory,
    IOptions<AppSettings> settings)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!settings.Value.LocalEnabled)
        {
            return AuthenticateResult.NoResult();
        }

        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = authorization["Bearer ".Length..].Trim();
        if (!token.StartsWith(LocalApiAuthenticationDefaults.TokenPrefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        if (!tokenService.TryRead(token, out var userId, out var expiresAt))
        {
            return AuthenticateResult.Fail("The local API token is invalid or expired.");
        }

        var user = await userManager.FindByIdAsync(userId);
        if (user == null || await userManager.IsLockedOutAsync(user))
        {
            return AuthenticateResult.Fail("The local API user is unavailable.");
        }

        var principal = await principalFactory.CreateAsync(user);
        var properties = new AuthenticationProperties();
        properties.Items[LocalApiAuthenticationDefaults.TokenExpiresAtProperty] =
            expiresAt.ToString("O", CultureInfo.InvariantCulture);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, properties, Scheme.Name));
    }
}
