using Aiursoft.AiurProtocol;
using Aiursoft.AiurProtocol.Services;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.SDK;

public sealed class DmsApiClient(AiurProtocolClient http, string endpoint, string key)
{
    private AiurApiEndpoint Endpoint(string route, object? query = null) => new(endpoint.TrimEnd('/'), "/api/v1/dms/" + route, query ?? new { });
    private IDictionary<string, string> Headers => new Dictionary<string, string> { ["Authorization"] = "Bearer " + key };
    public Task<DmsResponse> SubmitAsync(OperationRequest request) => http.Post<DmsResponse>(Endpoint("lockboxes"), new AiurApiPayload(request), BodyFormat.HttpJsonBody, autoRetry: false, headers: Headers);
    public Task<DmsResponse> SubmitAsync<TInput, TResult>(WorkflowDefinition<TInput, TResult> definition,
        Guid cluster, TInput input, string reason, IEnumerable<Guid>? machines = null, bool allMachines = false,
        Guid? requestId = null) => SubmitAsync(definition.Request(cluster, input, reason, machines, allMachines, requestId));
    public Task<DmsResponse> GetLockboxAsync(Guid id) => http.Get<DmsResponse>(Endpoint($"lockboxes/{id}"), headers: Headers);
    public Task<DmsResponse> DecideAsync(Guid id, bool approve, string reason) => http.Post<DmsResponse>(Endpoint($"lockboxes/{id}/decision"), new AiurApiPayload(new { Approve = approve, Reason = reason }), BodyFormat.HttpJsonBody, autoRetry: false, headers: Headers);
    public Task<DmsResponse> GetWorkflowAsync(Guid id) => http.Get<DmsResponse>(Endpoint($"operations/{id}"), headers: Headers);
    public Task<DmsResponse> ListDefinitionsAsync(Guid clusterId) => http.Get<DmsResponse>(Endpoint("workflow-definitions", new { clusterId }), headers: Headers);
    public Task<DmsResponse> CallstackAsync(Guid id) => http.Get<DmsResponse>(Endpoint($"operations/{id}/callstack"), headers: Headers);
    public Task<DmsResponse> TraceAsync(Guid id, int skip) => http.Get<DmsResponse>(Endpoint($"operations/{id}/trace", new { skip }), headers: Headers);
}
