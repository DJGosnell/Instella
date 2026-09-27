using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Instella.Server.Auth;

/// <summary>
/// Answers 400 when <c>UseAntiforgery</c> found the token invalid. Without it, MVC form binding
/// throws on the invalid request and the caller sees a 500.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RejectInvalidAntiforgeryAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (context.HttpContext.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false })
            context.Result = new BadRequestResult();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
