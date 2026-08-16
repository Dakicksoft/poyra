using FluentValidation;
using Poyra.SharedKernel.Errors;

namespace Poyra.Api.Middleware;

public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger, IHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ValidationException ex) when (!context.Response.HasStarted)
        {
            await WriteAsync(context, 400, "validation_failed", "Doğrulama hatası.",
                ex.Errors.Select(e => new { field = e.PropertyName, message = e.ErrorMessage }));
        }
        catch (PoyraException ex) when (!context.Response.HasStarted)
        {
            await WriteAsync(context, ex.StatusCode, ex.Code, ex.Message, null);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            logger.LogError(ex, "İşlenmemiş hata: {Path}", context.Request.Path);
            var message = environment.IsProduction()
                ? "Beklenmeyen bir hata oluştu."
                : $"{ex.GetType().Name}: {ex.Message}";
            await WriteAsync(context, 500, "internal_error", message, null);
        }
    }

    private static Task WriteAsync(HttpContext ctx, int status, string code, string message, object? errors)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { code, message, errors });
    }
}
