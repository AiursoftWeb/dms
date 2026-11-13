using Aiursoft.AiurProtocol.Models;

namespace Aiursoft.DMS.Contracts;

public class DmsResponse : AiurResponse
{
    public object? Data { get; set; }
}
