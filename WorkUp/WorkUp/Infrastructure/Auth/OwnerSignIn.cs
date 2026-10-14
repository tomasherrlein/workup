using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using WorkUp.BusinessLogic.Businesses;

namespace WorkUp.Infrastructure.Auth;

/// <summary>
/// Issues the Owner cookie from an <see cref="OwnerSession"/> (RF-01). Used right after registration and login,
/// and again after the owner changes her business's name or slug so the layout shows the new values immediately.
/// </summary>
public static class OwnerSignIn
{
    public static Task SignInAsync(HttpContext httpContext, OwnerSession session, bool isPersistent = true)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, session.OwnerAccountId.ToString()),
            new Claim(ClaimTypes.Name, session.Username),
            new Claim(WorkUpClaims.BusinessId, session.BusinessId.ToString()),
            new Claim(WorkUpClaims.BusinessName, session.BusinessName),
            new Claim(WorkUpClaims.BusinessSlug, session.BusinessSlug)
        };
        var identity = new ClaimsIdentity(claims, AuthSchemes.Owner);
        var properties = new AuthenticationProperties { IsPersistent = isPersistent };

        return httpContext.SignInAsync(AuthSchemes.Owner, new ClaimsPrincipal(identity), properties);
    }
}
