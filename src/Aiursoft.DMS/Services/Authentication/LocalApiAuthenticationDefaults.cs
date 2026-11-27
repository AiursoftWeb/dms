using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Aiursoft.DMS.Services.Authentication;

public static class LocalApiAuthenticationDefaults
{
    public const string AuthenticationScheme = "LocalApi";
    public const string TokenPrefix = "dmsl_";
    internal const string TokenExpiresAtProperty = "DmsTokenExpiresAt";

    public const string HumanApiSchemes =
        $"{DmsKeyAuthenticationHandler.SchemeName},{JwtBearerDefaults.AuthenticationScheme},{AuthenticationScheme}";
}
