using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public static partial class NetworkingOperations
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
    public static async Task<OperationResult> InterfacesAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        await OperationOutput.CommandAsync(context, agent, "ip", ["-json", "-statistics", "address", "show"], token);
        return new OperationResult("Network interface addresses, link state and error/drop counters collected.");
    }
    public static async Task<OperationResult> RoutesAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        foreach (var family in new[] { "-4", "-6" })
        {
            await OperationOutput.CommandAsync(context, agent, "ip", [family, "-json", "route", "show", "table", "all"], token);
            await OperationOutput.CommandAsync(context, agent, "ip", [family, "-json", "rule", "show"], token);
        }
        return new OperationResult("IPv4/IPv6 routes and policy routing collected.");
    }
    public static async Task<OperationResult> SocketsAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        var sockets = await OperationOutput.CommandAsync(context, agent, "ss", ["--tcp", "--udp", "--numeric", "--processes", "--all", "--info"], token);
        var pids = PidPattern().Matches(sockets.Output).Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
        var processes = (await PerformanceOperations.ReadProcessesAsync(OperationOutput.Host(agent), token)).Where(p => pids.Contains(p.Pid))
            .Select(p => new { p.Pid, p.Name, p.Cgroup, p.ContainerId }).ToArray();
        return await OperationOutput.ReportAsync(context, "Socket endpoints and visible process/container ownership collected.", processes);
    }
    public static async Task<OperationResult> DnsQueryAsync(MachineWorkflowContext context, DnsQueryInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var addresses = await Dns.GetHostAddressesAsync(input.Host, deadline.Token);
        return await OperationOutput.ReportAsync(context, "Name resolved through this machine's configured resolver.", new
        {
            input.Host, Addresses = addresses.Select(a => a.ToString()).ToArray(),
            ResolverConfiguration = await OperationOutput.Host(agent).ReadAsync("/etc/resolv.conf", token, 65536), At = DateTimeOffset.UtcNow
        });
    }
    public static async Task<OperationResult> ProbeAsync(MachineWorkflowContext context, NetworkProbeInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(input.TimeoutSeconds));
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var socket = new TcpClient(); await socket.ConnectAsync(input.Host, input.Port, deadline.Token);
        var elapsed = started.ElapsedMilliseconds;
        int? status = null;
        if (input.Url != null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, input.Url);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token); status = (int)response.StatusCode;
        }
        return await OperationOutput.ReportAsync(context, "Endpoint probe completed.", new { input.Host, input.Port, input.Url, TcpConnected = true, TcpMilliseconds = elapsed, HttpStatus = status, Remote = socket.Client.RemoteEndPoint?.ToString() });
    }
    public static async Task<OperationResult> TrafficSampleAsync(MachineWorkflowContext context, SampleInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var host = OperationOutput.Host(agent); var before = await PerformanceOperations.ReadNetworkAsync(host, token);
        return await PerformanceOperations.SampleAsync(context, input, async sampling =>
        {
            var after = await PerformanceOperations.ReadNetworkAsync(host, sampling);
            var sockets = await OperationOutput.CommandAsync(context, agent, "ss", ["--tcp", "--numeric", "--processes", "--info", "--no-header"], sampling, traceOutput: false);
            var pids = PidPattern().Matches(sockets.Output).Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
            var owners = (await PerformanceOperations.ReadProcessesAsync(host, sampling)).Where(p => pids.Contains(p.Pid)).Take(input.Limit).Select(p => new { p.Pid, p.Name, p.ContainerId }).ToArray();
            var observation = new
            {
                Interfaces = after.Select(p => new { Interface = p.Key, ReceivedBytes = Math.Max(0, p.Value[0] - before.GetValueOrDefault(p.Key, p.Value)[0]), SentBytes = Math.Max(0, p.Value[1] - before.GetValueOrDefault(p.Key, p.Value)[1]) }).ToArray(),
                TcpEndpointsAndCounters = sockets.Output, ProcessOwners = owners,
                Attribution = "TCP socket counters are cumulative; process visibility depends on namespaces and permissions. UDP payloads are not captured."
            };
            before = after; return observation;
        }, token);
    }
    public static async Task<OperationResult> FirewallInfoAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        List<object> reports = []; var available = 0;
        foreach (var command in new[]
        {
            new NativeCommand("ufw", ["status", "verbose"]), new NativeCommand("nft", ["--terse", "--json", "list", "ruleset"]),
            new NativeCommand("iptables-save", ["--counters"]), new NativeCommand("ip6tables-save", ["--counters"])
        })
        {
            try
            {
                var result = await OperationOutput.Host(agent).RunAsync(command, token);
                reports.Add(new { Tool = command.Executable, result.ExitCode, result.Output, result.Error });
                if (result.ExitCode == 0) available++;
            }
            catch (NotSupportedException) { reports.Add(new { Tool = command.Executable, State = "NotInstalled" }); }
        }
        return await OperationOutput.ReportAsync(context, "Firewall evidence collected for available backends.", reports, available > 0);
    }
    public static async Task<OperationResult> TlsCheckAsync(MachineWorkflowContext context, NetworkProbeInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(input.TimeoutSeconds));
        using var socket = new TcpClient(); await socket.ConnectAsync(input.Host, input.Port, deadline.Token);
        SslPolicyErrors errors = SslPolicyErrors.None; List<object> certificates = []; List<string> chainErrors = [];
        using var ssl = new SslStream(socket.GetStream(), false, (_, certificate, chain, policyErrors) =>
        {
            errors = policyErrors;
            if (chain != null)
            {
                foreach (var element in chain.ChainElements)
                    certificates.Add(new { element.Certificate.Subject, element.Certificate.Issuer, element.Certificate.NotBefore, element.Certificate.NotAfter, element.Certificate.Thumbprint });
                chainErrors.AddRange(chain.ChainStatus.Select(s => s.Status + ": " + s.StatusInformation.Trim()));
            }
            return certificate != null && policyErrors == SslPolicyErrors.None;
        });
        var authenticated = false;
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = input.Host, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, deadline.Token);
            authenticated = true;
        }
        catch (AuthenticationException) { }
        return await OperationOutput.ReportAsync(context, authenticated ? "TLS handshake, trust and hostname validation passed." : "TLS validation failed; inspect the certificate evidence.", new
        {
            input.Host, input.Port, Authenticated = authenticated, PolicyErrors = errors.ToString(), Certificates = certificates, ChainErrors = chainErrors,
            Protocol = authenticated ? ssl.SslProtocol.ToString() : null,
            CipherSuite = authenticated && OperatingSystem.IsLinux() ? ssl.NegotiatedCipherSuite.ToString() : null,
            Revocation = "Not checked: this operation does not require external OCSP/CRL access."
        }, authenticated);
    }
    [GeneratedRegex(@"pid=(\d+)")]
    private static partial Regex PidPattern();
}
