namespace Poyra.SharedKernel.Errors;


public class PoyraException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public static PoyraException NotFound(string code, string message) => new(404, code, message);
    public static PoyraException Conflict(string code, string message) => new(409, code, message);
    public static PoyraException Unauthorized(string code, string message) => new(401, code, message);
}
