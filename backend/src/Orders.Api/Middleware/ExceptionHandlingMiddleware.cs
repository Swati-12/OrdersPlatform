using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Orders.Api.Services;

namespace Orders.Api.Middleware;

/// <summary>Maps domain exceptions to RFC 7807 problem responses so controllers stay free of try/catch.</summary>
public class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (Exception ex)
        {
            var (status, title) = ex switch
            {
                ValidationException           => (StatusCodes.Status400BadRequest, "Validation failed"),
                AuthenticationFailedException => (StatusCodes.Status401Unauthorized, ex.Message),
                NotFoundException             => (StatusCodes.Status404NotFound, ex.Message),
                ConflictException             => (StatusCodes.Status409Conflict, ex.Message),
                BusinessRuleException         => (StatusCodes.Status422UnprocessableEntity, ex.Message),
                ServiceUnavailableException   => (StatusCodes.Status503ServiceUnavailable, ex.Message),
                _                             => (StatusCodes.Status500InternalServerError, "An unexpected error occurred")
            };

            if (status == 500) logger.LogError(ex, "Unhandled exception");

            var problem = new ProblemDetails { Status = status, Title = title };
            if (ex is ValidationException ve)
                problem.Extensions["errors"] = ve.Errors.GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsJsonAsync(problem);
        }
    }
}