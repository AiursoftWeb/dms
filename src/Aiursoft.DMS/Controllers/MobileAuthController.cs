using System.ComponentModel.DataAnnotations;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.DMS.Configuration;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.WebTools.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Aiursoft.DMS.Controllers;

[AllowAnonymous]
[LimitPerMin]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MobileAuthController(
    UserManager<User> userManager,
    SignInManager<User> signInManager,
    LocalApiTokenService tokenService,
    IOptions<AppSettings> appSettings) : ControllerBase
{
    public sealed class MobileConfigResponse
    {
        public Code Code { get; init; }
        public string Message { get; init; } = string.Empty;
        public string AuthenticationMode { get; init; } = string.Empty;
        public bool AllowRegistration { get; init; }
        public bool SupportsPersonalApiKey { get; init; } = true;
        public string Authority { get; init; } = string.Empty;
        public string ClientId { get; init; } = string.Empty;
        public string RedirectUri { get; init; } = string.Empty;
        public List<string> Scopes { get; init; } = [];
    }

    public sealed class LocalLoginInput
    {
        [Required]
        public string EmailOrUserName { get; init; } = string.Empty;

        [Required]
        public string Password { get; init; } = string.Empty;
    }

    public sealed class LocalRegisterInput
    {
        [Required]
        [EmailAddress]
        public string Email { get; init; } = string.Empty;

        [Required]
        public string Password { get; init; } = string.Empty;
    }

    public sealed class LocalAuthResponse
    {
        public Code Code { get; init; }
        public string Message { get; init; } = string.Empty;
        public string AccessToken { get; init; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; init; }
        public string DisplayName { get; init; } = string.Empty;
    }

    [HttpGet("/api/v1/config")]
    public IActionResult Configuration()
    {
        var settings = appSettings.Value;
        var oidc = settings.OIDC;
        var scopes = new List<string> { "openid", "profile", "email", "offline_access" };
        if (!string.IsNullOrWhiteSpace(oidc.ApiScope))
        {
            scopes.Add(oidc.ApiScope);
        }

        return Ok(new MobileConfigResponse
        {
            Code = Code.ResultShown,
            Message = "Mobile client configuration.",
            AuthenticationMode = settings.AuthProvider,
            AllowRegistration = settings.LocalEnabled && settings.Local.AllowRegister,
            SupportsPersonalApiKey = true,
            Authority = settings.OIDCEnabled ? oidc.GetMobileAuthority() : string.Empty,
            ClientId = settings.OIDCEnabled ? oidc.MobileClientId : string.Empty,
            RedirectUri = settings.OIDCEnabled ? oidc.MobileRedirectUri : string.Empty,
            Scopes = settings.OIDCEnabled ? scopes : []
        });
    }

    [HttpPost("/api/v1/auth/local/login")]
    public async Task<IActionResult> Login([FromBody] LocalLoginInput? request)
    {
        if (!appSettings.Value.LocalEnabled)
        {
            return Unauthorized(new AiurResponse
            {
                Code = Code.Unauthorized,
                Message = "Local authentication is disabled on this server."
            });
        }

        if (request == null || !ModelState.IsValid)
        {
            return BadRequest(new AiurResponse
            {
                Code = Code.InvalidInput,
                Message = "Username and password are required."
            });
        }

        var identifier = request.EmailOrUserName.Trim();
        var user = await userManager.FindByEmailAsync(identifier)
            ?? await userManager.FindByNameAsync(identifier);
        if (user == null || !(await signInManager.CheckPasswordSignInAsync(
                user, request.Password, lockoutOnFailure: true)).Succeeded)
        {
            return Unauthorized(new AiurResponse
            {
                Code = Code.Unauthorized,
                Message = "Invalid username or password."
            });
        }

        return IssueToken(user, "Signed in.");
    }

    [HttpPost("/api/v1/auth/local/register")]
    public async Task<IActionResult> Register([FromBody] LocalRegisterInput? request)
    {
        var settings = appSettings.Value;
        if (!settings.LocalEnabled || !settings.Local.AllowRegister)
        {
            return Unauthorized(new AiurResponse
            {
                Code = Code.Unauthorized,
                Message = "Local registration is disabled on this server."
            });
        }

        if (request == null || !ModelState.IsValid)
        {
            return BadRequest(new AiurResponse
            {
                Code = Code.InvalidInput,
                Message = "A valid email and password are required."
            });
        }

        var email = request.Email.Trim();
        var displayName = email.Split('@')[0];
        if (displayName.Length < 2)
        {
            displayName = email;
        }
        displayName = displayName[..Math.Min(displayName.Length, 30)];

        var user = new User
        {
            UserName = email,
            Email = email,
            DisplayName = displayName
        };
        var result = await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            return BadRequest(new AiurResponse
            {
                Code = Code.InvalidInput,
                Message = string.Join(" ", result.Errors.Select(error => error.Description))
            });
        }

        if (!string.IsNullOrWhiteSpace(settings.DefaultRole))
        {
            var roleResult = await userManager.AddToRoleAsync(user, settings.DefaultRole);
            if (!roleResult.Succeeded)
            {
                return BadRequest(new AiurResponse
                {
                    Code = Code.InvalidInput,
                    Message = string.Join(" ", roleResult.Errors.Select(error => error.Description))
                });
            }
        }

        return IssueToken(user, "Account created.");
    }

    private IActionResult IssueToken(User user, string message)
    {
        var token = tokenService.Issue(user.Id);
        return Ok(new LocalAuthResponse
        {
            Code = Code.JobDone,
            Message = message,
            AccessToken = token.AccessToken,
            ExpiresAt = token.ExpiresAt,
            DisplayName = user.DisplayName
        });
    }
}
