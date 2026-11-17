using Aiursoft.AiurProtocol;
using Aiursoft.AiurProtocol.Services;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.SDK;

public sealed class DmsApiClient(AiurProtocolClient http, string endpoint, string key)
{
    public DmsArtifactClient Artifacts => new(endpoint, key);
    private AiurApiEndpoint Endpoint(string route, object? query = null) => new(endpoint.TrimEnd('/'), "/api/v1/dms/" + route, query ?? new { });
    private IDictionary<string, string> Headers => new Dictionary<string, string> { ["Authorization"] = "Bearer " + key };
    public Task<AgentReleasesResponse> AgentReleasesAsync() => http.Get<AgentReleasesResponse>(new AiurApiEndpoint(endpoint.TrimEnd('/'), "/agent/releases", new { }), autoRetry: false);
    public Task<DmsResponse> IssueJoinKeyAsync(JoinKeyInput input) => http.Post<DmsResponse>(Endpoint("access/join-keys"), new AiurApiPayload(input), BodyFormat.HttpJsonBody, autoRetry: false, headers: Headers);
    public Task<DmsResponse> JoinKeysAsync() => http.Get<DmsResponse>(Endpoint("access/join-keys"), autoRetry: false, headers: Headers);
    public Task<DmsResponse> RevokeJoinKeyAsync(Guid id) => http.Http<DmsResponse>(Endpoint($"access/join-keys/{id}"), new AiurApiPayload(new { }), HttpMethod.Delete, BodyFormat.HttpJsonBody, autoRetry: false, headers: Headers);
    public Task<DmsResponse> EnrollmentsAsync() => http.Get<DmsResponse>(Endpoint("access/enrollments"), autoRetry: false, headers: Headers);
    public Task<DmsResponse> CurrentUserAsync() => http.Get<DmsResponse>(Endpoint("access/identity"), headers: Headers);
    public Task<DmsResponse> AccessAsync() => http.Get<DmsResponse>(Endpoint("access"), headers: Headers);
    public Task<DmsResponse> IssueAgentKeyAsync(Guid id) => http.Post<DmsResponse>(Endpoint($"access/machines/{id}/key"), new AiurApiPayload(new { }), BodyFormat.HttpJsonBody, autoRetry: false, headers: Headers);
    public Task<DmsResponse> SubmitAsync(OperationRequest request) => http.Post<DmsResponse>(Endpoint("lockboxes"), new AiurApiPayload(request), BodyFormat.HttpJsonBody, autoRetry: false, headers: Headers);
    public Task<DmsResponse> SubmitAsync<TInput, TResult>(WorkflowDefinition<TInput, TResult> definition,
        TInput input, string reason, IEnumerable<Guid>? machines = null, bool allMachines = false,
        Guid? requestId = null) => SubmitAsync(definition.Request(input, reason, machines, allMachines, requestId));
    public Task<DmsResponse> GetLockboxAsync(Guid id) => http.Get<DmsResponse>(Endpoint($"lockboxes/{id}"), headers: Headers);
    public Task<DmsResponse> DecideAsync(Guid id, bool approve, string reason) => http.Post<DmsResponse>(Endpoint($"lockboxes/{id}/decision"), new AiurApiPayload(new { Approve = approve, Reason = reason }), BodyFormat.HttpJsonBody, autoRetry: false, headers: Headers);
    public Task<DmsResponse> GetWorkflowAsync(Guid id) => http.Get<DmsResponse>(Endpoint($"operations/{id}"), headers: Headers);
    public Task<DmsResponse> ListDefinitionsAsync() => http.Get<DmsResponse>(Endpoint("workflow-definitions"), headers: Headers);
    public Task<DmsResponse> AuditEventsAsync(WorkflowAuditQuery query) => http.Get<DmsResponse>(Endpoint("audit-events", query), autoRetry: false, headers: Headers);
    public Task<DmsResponse> CallstackAsync(Guid id) => http.Get<DmsResponse>(Endpoint($"operations/{id}/callstack"), headers: Headers);
    public Task<DmsResponse> TraceAsync(Guid id, int skip) => http.Get<DmsResponse>(Endpoint($"operations/{id}/trace", new { skip }), autoRetry: false, headers: Headers);
}
