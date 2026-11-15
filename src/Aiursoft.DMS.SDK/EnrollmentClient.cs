using Aiursoft.AiurProtocol;
using Aiursoft.AiurProtocol.Services;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.SDK;

public sealed class EnrollmentClient(AiurProtocolClient http, string controller, string key)
{
    private AiurApiEndpoint Endpoint => new(controller.TrimEnd('/'), "/api/v1/agent/enrollment", new { });
    private IDictionary<string, string> Headers => new Dictionary<string, string> { ["Authorization"] = "Bearer " + key };
    public Task<EnrollmentResponse> SubmitAsync(EnrollmentInput input) => http.Post<EnrollmentResponse>(Endpoint, new AiurApiPayload(input), BodyFormat.HttpJsonBody, autoRetry: false, headers: Headers);
    public Task<EnrollmentResponse> StatusAsync() => http.Get<EnrollmentResponse>(Endpoint, autoRetry: false, headers: Headers);
}
