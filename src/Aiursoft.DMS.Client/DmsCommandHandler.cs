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
    private readonly Option<OutputFormat> _format;
    private readonly bool _supportsLog;

    protected DmsCommandHandler(bool supportsLog = false)
    {
        _supportsLog = supportsLog;
        _format = new Option<OutputFormat>("--output-format")
        {
            Description = supportsLog
                ? "Choose log (default), table, json, or csv."
                : "Choose table (default), json, or csv."
        };
        if (supportsLog)
        {
            _format.DefaultValueFactory = _ => OutputFormat.Log;
        }
        else
        {
            _format.CompletionSources.Clear();
            _format.CompletionSources.Add(
                nameof(OutputFormat.Table), nameof(OutputFormat.Json), nameof(OutputFormat.Csv));
        }
    }

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

    protected OutputFormat SelectedFormat(ParseResult context) => context.GetValue(_format);

    private async Task ExecuteAsync(ParseResult context, DmsCredentials credentials)
    {
        var format = SelectedFormat(context);
        if (!Enum.IsDefined(format) || (format == OutputFormat.Log && !_supportsLog))
            throw new ArgumentException(_supportsLog
                ? "Output format must be table, json, csv, or log."
                : "Output format must be table, json, or csv.");
        using var provider = CreateProvider();
        var client = new DmsApiClient(provider.GetRequiredService<AiurProtocolClient>(), credentials.Endpoint, credentials.ApiKey);
        var response = await RequestAsync(client, context);
        WriteResponse(context, response, format, !Console.IsOutputRedirected,
            Console.IsOutputRedirected ? 120 : Math.Max(60, Console.WindowWidth));
    }

    protected virtual void WriteResponse(ParseResult context, DmsResponse response, OutputFormat format, bool color, int width) =>
        DmsOutputWriter.Write(context.InvocationConfiguration.Output, response, format, Callstack, color, width,
            catalog: Catalog, audit: Audit, lockboxStatus: LockboxStatus);

    internal static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddAiurProtocolClient();
        services.AddHttpClient(string.Empty).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services.BuildServiceProvider();
    }
}
