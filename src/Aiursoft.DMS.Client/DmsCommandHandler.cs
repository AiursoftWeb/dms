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
    protected readonly Option<string?> _server = new("--server")
        { Description = "Controller URL. Defaults to DMS_SERVER or the saved login." };
    protected readonly Option<string?> _key = new("--api-key")
        { Description = "Personal API key. Defaults to DMS_API_KEY or the saved login." };
    protected readonly Option<OutputFormat> _format = new("--output-format")
        { Description = "Choose table (default), json, or csv." };
    protected virtual bool Callstack => false;
    protected virtual bool Catalog => false;
    protected virtual bool Audit => false;
    protected virtual bool LockboxStatus => false;
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
            string? key = null;
            try
            {
                var credentials = DmsCredentials.Resolve(context.GetValue(_server), context.GetValue(_key));
                key = credentials.ApiKey;
                await ExecuteAsync(context, credentials);
                return 0;
            }
            catch (Exception exception) when (exception is AiurUnexpectedServerResponseException or AiurHttpResponseException or ArgumentException or HttpRequestException or OperationCanceledException or IOException or System.Text.Json.JsonException)
            {
                var message = exception switch
                {
                    AiurUnexpectedServerResponseException response => response.Response.Message ?? "Controller rejected the request.",
                    AiurHttpResponseException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => "Authentication failed. Check your personal API Key and its expiry.",
                    AiurHttpResponseException http => $"Controller returned HTTP {(int)http.StatusCode} ({http.StatusCode}).",
                    ArgumentException argument => argument.Message,
                    IOException => "Local file or artifact transfer failed. Inspect the path, available space and artifact metadata.",
                    System.Text.Json.JsonException => "The input or Controller response is not valid JSON.",
                    _ => "Cannot reach the Controller. Check the endpoint and network connection."
                };
                if (!string.IsNullOrEmpty(key)) message = message.Replace(key, "[redacted]", StringComparison.Ordinal);
                context.InvocationConfiguration.Error.WriteLine($"Error: {message}");
                return 1;
            }
        });
        return command;
    }

    protected sealed override Task Execute(ParseResult context) =>
        ExecuteAsync(context, DmsCredentials.Resolve(context.GetValue(_server), context.GetValue(_key)));

    private async Task ExecuteAsync(ParseResult context, DmsCredentials credentials)
    {
        var format = context.GetValue(_format);
        if (!Enum.IsDefined(format)) throw new ArgumentException("Output format must be table, json, or csv.");
        using var provider = CreateProvider();
        var client = new DmsApiClient(provider.GetRequiredService<AiurProtocolClient>(), credentials.Endpoint, credentials.ApiKey);
        var response = await RequestAsync(client, context);
        WriteResponse(context.InvocationConfiguration.Output, response, format, !Console.IsOutputRedirected,
            Console.IsOutputRedirected ? 120 : Math.Max(60, Console.WindowWidth));
    }

    protected virtual void WriteResponse(TextWriter output, DmsResponse response, OutputFormat format, bool color, int width) =>
        DmsOutputWriter.Write(output, response, format, Callstack, color, width,
            catalog: Catalog, audit: Audit, lockboxStatus: LockboxStatus);

    internal static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddAiurProtocolClient();
        services.AddHttpClient(string.Empty).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services.BuildServiceProvider();
    }
}
