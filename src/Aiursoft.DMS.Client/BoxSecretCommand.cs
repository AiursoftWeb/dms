using System.CommandLine;
using System.Text;
using System.Net;
using Aiursoft.AiurProtocol.Exceptions;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.SDK;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DMS.Client;

internal enum BoxSecretCommandKind { List, Summary, Set, Raw }

internal sealed class BoxSecretCommand(BoxSecretCommandKind kind) : DmsCommandHandler
{
    private readonly Option<Guid> _machine = new("--machine-id") { Description = "One target machine ID." };
    private readonly Option<string> _reason = new("--reason") { Description = "Reason recorded in the Lockbox." };
    private readonly Option<string> _name = new("--name") { Description = "Key name in .env.prod." };
    private readonly Option<string> _retrieve = new(kind == BoxSecretCommandKind.Raw ? "--reveal" : "--result")
        { Description = "Retrieve the result of your approved Lockbox (lb/<UUID>)." };
    private readonly Option<bool> _stdin = new("--stdin") { Description = "Read one secret value from standard input." };

    protected override string Name => kind switch
    {
        BoxSecretCommandKind.List => "list-secret",
        BoxSecretCommandKind.Summary => "get-secret-summary",
        BoxSecretCommandKind.Set => "set-secret",
        _ => "get-secret-raw"
    };

    protected override string Description => kind switch
    {
        BoxSecretCommandKind.List => "List private Box key names and whether they are configured.",
        BoxSecretCommandKind.Summary => "Request the configuration status and fingerprint of one Box secret.",
        BoxSecretCommandKind.Set => "Request a confidential update to one Box secret.",
        _ => "Request independent approval for one Box secret, or reveal the approved value once."
    };

    protected override IEnumerable<Option> GetActionOptions()
    {
        yield return _machine;
        yield return _reason;
        if (kind != BoxSecretCommandKind.List) yield return _name;
        if (kind != BoxSecretCommandKind.Set) yield return _retrieve;
        if (kind == BoxSecretCommandKind.Set) yield return _stdin;
    }

    protected override async Task<DmsResponse> RequestAsync(DmsApiClient client, ParseResult context)
    {
        var machine = context.GetValue(_machine);
        var name = context.GetValue(_name);
        var reason = context.GetValue(_reason);
        var existing = kind != BoxSecretCommandKind.Set ? context.GetValue(_retrieve) : null;
        if (existing != null)
        {
            if (!LockboxId.TryParse(existing, out var id) || machine != Guid.Empty || name != null || reason != null)
                throw new ArgumentException("Pass a valid Lockbox ID without submission options.");
            if (kind != BoxSecretCommandKind.Raw) return await ReadMetadataAsync(client, id);
            try
            {
                var revealed = await client.RevealBoxSecretAsync(id);
                revealed.Data = JObject.FromObject(revealed.Data!).ToObject<BoxSecretValue>();
                return revealed;
            }
            catch (AiurHttpResponseException exception) when (exception.StatusCode == HttpStatusCode.Gone)
            {
                throw new ArgumentException("The value has expired or was already revealed. Submit a new raw request.");
            }
            catch (AiurHttpResponseException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
            {
                throw new ArgumentException("The raw request must finish successfully after independent approval.");
            }
        }

        if (machine == Guid.Empty || string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Specify one --machine-id and a --reason.");
        var input = new BoxSecretInput(name ?? "");
        if (kind != BoxSecretCommandKind.List) input.Validate();
        if (kind == BoxSecretCommandKind.Set)
        {
            var value = ReadSecret(context.GetValue(_stdin), context.InvocationConfiguration.Error);
            new BoxSecretValue(value).Validate();
            return await client.SetBoxSecretAsync(new BoxSecretSubmission(machine, reason, input, value));
        }

        var response = kind switch
        {
            BoxSecretCommandKind.List => await client.SubmitAsync(MachineWorkflows.BoxListSecret, new EmptyInput(), reason, [machine]),
            BoxSecretCommandKind.Summary => await client.SubmitAsync(MachineWorkflows.BoxGetSecretSummary, input, reason, [machine]),
            _ => await client.SubmitAsync(MachineWorkflows.BoxGetSecretRaw, input, reason, [machine])
        };
        var receipt = JObject.FromObject(response.Data!).ToObject<LockboxReceipt>()!;
        if (kind != BoxSecretCommandKind.Raw && receipt.WorkflowId != null)
            return await ReadMetadataAsync(client, receipt.LockboxId);
        return response;
    }

    private async Task<DmsResponse> ReadMetadataAsync(DmsApiClient client, LockboxId id)
    {
        var response = await client.BoxSecretMetadataAsync(id);
        var metadata = JObject.FromObject(response.Data!).ToObject<OpenWorkflowResult>()!;
        if (metadata.State == RunState.Succeeded && !string.IsNullOrEmpty(metadata.Result))
            response.Data = kind == BoxSecretCommandKind.List
                ? MachineWorkflows.BoxListSecret.ReadResult(metadata.Result)
                : MachineWorkflows.BoxGetSecretSummary.ReadResult(metadata.Result);
        else
            response.Data = new { LockboxId = id, metadata.WorkflowId, metadata.State };
        return response;
    }

    protected override void WriteResponse(ParseResult context, DmsResponse response, OutputFormat format, bool color, int width)
    {
        if (response.Data is BoxSecretValue raw)
            context.InvocationConfiguration.Output.Write(raw.Value);
        else
            base.WriteResponse(context, response, format, color, width);
    }

    private static string ReadSecret(bool stdin, TextWriter error)
    {
        if (stdin)
        {
            var value = new StringBuilder();
            int code;
            while ((code = Console.In.Read()) >= 0 && code != '\n')
            {
                if (value.Length >= 4096) throw new ArgumentException("The secret value exceeds the input limit.");
                value.Append((char)code);
            }
            if (code >= 0 && Console.In.Read() >= 0) throw new ArgumentException("Provide only one line of secret input.");
            if (value.Length > 0 && value[^1] == '\r') value.Length--;
            return value.ToString();
        }
        if (Console.IsInputRedirected) throw new ArgumentException("Use --stdin to read a secret from redirected input.");
        error.Write("Secret value (hidden): ");
        try
        {
            var value = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) return value.ToString();
                if (key.Key == ConsoleKey.Escape) throw new ArgumentException("Secret entry cancelled.");
                if (key.Key == ConsoleKey.Backspace && value.Length != 0) value.Length--;
                if (char.IsControl(key.KeyChar)) continue;
                if (value.Length >= 4096) throw new ArgumentException("The secret value exceeds the input limit.");
                value.Append(key.KeyChar);
            }
        }
        finally { error.WriteLine(); }
    }
}
