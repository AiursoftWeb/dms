using System.CommandLine;
using System.Text;
using Aiursoft.AiurProtocol.Exceptions;
using Aiursoft.AiurProtocol.Services;
using Aiursoft.CommandFramework.Framework;
using Aiursoft.DMS.SDK;
using Microsoft.Extensions.DependencyInjection;

namespace Aiursoft.DMS.Client;

internal sealed class LoginCommand : ExecutableCommandHandlerBuilder
{
    protected override string Name => "login";
    protected override string Description => "Sign in to a Controller with a personal API key.";
    private readonly DmsEndpointOptions _endpoint =
        new("DMS Controller URL, such as https://dms.aiursoft.com. DMS prompts for it when omitted.");
    protected override IEnumerable<Option> GetCommandOptions() => _endpoint.Options;

    public override Command BuildAsCommand()
    {
        var command = base.BuildAsCommand();
        command.SetAction(async context =>
        {
            try { await Execute(context); return 0; }
            catch (Exception exception) when (exception is AiurUnexpectedServerResponseException or AiurHttpResponseException or ArgumentException or IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException)
            {
                // Server response bodies and I/O exceptions can contain credentials; do not echo them.
                context.InvocationConfiguration.Error.WriteLine(exception switch
                {
                    ArgumentException argument => $"Error: {argument.Message}",
                    IOException or UnauthorizedAccessException => "Error: Cannot save authentication to ~/.DMS/auth.json. Check file permissions.",
                    _ => "Error: Login failed. Check the Controller URL, personal API Key and its expiry. Existing saved authentication was not changed."
                });
                return 1;
            }
        });
        return command;
    }

    protected override async Task Execute(ParseResult context)
    {
        var output = context.InvocationConfiguration.Output;
        var endpoint = _endpoint.GetValue(context);
        if (endpoint == null)
        {
            var suggested = Environment.GetEnvironmentVariable("DMS_SERVER") ?? "https://dms.aiursoft.com/";
            output.Write($"Controller endpoint [{suggested}]: ");
            var input = Console.ReadLine() ?? throw new ArgumentException("Login cancelled: no Controller endpoint was entered.");
            endpoint = string.IsNullOrWhiteSpace(input) ? suggested : input;
        }
        endpoint = DmsCredentials.NormalizeEndpoint(endpoint);
        output.WriteLine($"Open {endpoint}/DmsAccess/Keys and create a personal API Key.");
        output.Write("Paste API Key (hidden): ");
        var key = ReadKey();
        output.WriteLine();
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Login cancelled: no API Key was entered.");
        using var provider = DmsCommandHandler.CreateProvider();
        var client = new DmsApiClient(provider.GetRequiredService<AiurProtocolClient>(), endpoint, key);
        await client.CurrentUserAsync();
        await new DmsCredentials(endpoint, key).SaveAsync();
        output.WriteLine($"Logged in to {endpoint}. Authentication saved to ~/.DMS/auth.json.");
        output.WriteLine("Run 'dms access whoami' or 'dms machine list' to get started.");
        if (Environment.GetEnvironmentVariable("DMS_SERVER") != null || Environment.GetEnvironmentVariable("DMS_API_KEY") != null)
            output.WriteLine("DMS_SERVER and DMS_API_KEY environment variables override saved authentication. Unset them to use this login.");
    }

    private static string ReadKey()
    {
        if (Console.IsInputRedirected) return Console.ReadLine()?.Trim() ?? string.Empty;
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) return value.ToString().Trim();
            if (key.Key == ConsoleKey.Escape) return string.Empty;
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
    }
}
