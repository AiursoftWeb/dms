namespace Aiursoft.DMS.Services.Dms;

public sealed class DmsException(int statusCode, string message) : Exception(message)
{
    public int StatusCode => statusCode;
}
