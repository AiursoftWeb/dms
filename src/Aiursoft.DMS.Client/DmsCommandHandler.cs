using System.CommandLine;
using Aiursoft.AiurProtocol;
using Aiursoft.AiurProtocol.Exceptions;
using Aiursoft.AiurProtocol.Services;
using Aiursoft.CommandFramework.Framework;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.SDK;
using Microsoft.Extensions.DependencyInjection;

namespace Aiursoft.DMS.Client;

internal abstract class DmsCommandHandler : ExecutableCommandHandlerBuilder
{
    protected readonly Option<string?> _server = new("--server") { Description = "Controller URL; defaults to DMS_SERVER." };
    protected readonly Option<string?> _key = new("--api-key") { Description = "Personal API Key; defaults to DMS_API_KEY." };
    protected readonly Option<OutputFormat> _format = new("--output-format") { Description = "Output format: table, json, or csv." };
    protected virtual bool Callstack => false;
    protected virtual bool Catalog => false;
    protected virtual bool Audit => false;
    protected abstract IEnumerable<Option> GetActionOptions();
    protected abstract Task<DmsResponse> RequestAsync(DmsApiClient client, ParseResult context);

    protected sealed override IEnumerable<Option> GetCommandOptions()
    {
        yield return _server; yield return _key; yield return _format;
        foreach (var option in GetActionOptions()) yield return option;
    }

    public override Command BuildAsCommand()
    {
        var command = base.BuildAsCommand();
        command.SetAction(async context =>
        {
            try
            {
                await Execute(context);
                return 0;
            }
            catch (Exception exception) when (exception is AiurUnexpectedServerResponseException or AiurHttpResponseException or ArgumentException)
            {
                var message = exception switch
                {
                    AiurUnexpectedServerResponseException response => response.Response.Message ?? "Controller rejected the request.",
                    AiurHttpResponseException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => "Authentication failed. Check your personal API Key and its expiry.",
                    AiurHttpResponseException http => $"Controller returned HTTP {(int)http.StatusCode} ({http.StatusCode}).",
                    ArgumentException argument => argument.Message,
                    _ => throw new InvalidOperationException()
                };
                var key = context.GetValue(_key) ?? Environment.GetEnvironmentVariable("DMS_API_KEY");
                if (!string.IsNullOrEmpty(key)) message = message.Replace(key, "[redacted]", StringComparison.Ordinal);
                context.InvocationConfiguration.Error.WriteLine($"Error: {message}");
                return 1;
            }
        });
        return command;
    }

    protected sealed override async Task Execute(ParseResult context)
    {
        var format = context.GetValue(_format);
        if (!Enum.IsDefined(format)) throw new ArgumentException("Output format must be table, json, or csv.");
        var endpoint = context.GetValue(_server) ?? Environment.GetEnvironmentVariable("DMS_SERVER") ?? throw new ArgumentException("Set --server or DMS_SERVER.");
        var key = context.GetValue(_key) ?? Environment.GetEnvironmentVariable("DMS_API_KEY") ?? throw new ArgumentException("Set --api-key or DMS_API_KEY.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("Invalid Controller URL.");
        var services = new ServiceCollection(); services.AddLogging(); services.AddAiurProtocolClient();
        services.AddHttpClient(string.Empty).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        using var provider = services.BuildServiceProvider();
        var client = new DmsApiClient(provider.GetRequiredService<AiurProtocolClient>(), endpoint, key);
        var response = await RequestAsync(client, context);
        DmsOutputWriter.Write(context.InvocationConfiguration.Output, response, format,
            Callstack, !Console.IsOutputRedirected,
            Console.IsOutputRedirected ? 120 : Math.Max(60, Console.WindowWidth), catalog: Catalog, audit: Audit);
    }
}
