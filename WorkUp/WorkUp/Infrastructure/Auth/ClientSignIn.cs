using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using WorkUp.BusinessLogic.ClientAccounts;

namespace WorkUp.Infrastructure.Auth;

/// <summary>
/// Issues the client cookie from a <see cref="ClientSession"/> (RF-15). The cookie carries the tenant of the session
/// (id, slug and name) and the account/ficha pair that authorizes the panel, so
/// <see cref="WorkUp.Controllers.ClientPanelControllerBase"/> can refuse a session in another business's route. It
/// never carries the password nor its hash, and it is only issued by a successful sign-in of an active account.
/// </summary>
public static class ClientSignIn
{
    public static Task SignInAsync(HttpContext httpContext, ClientSession session, bool isPersistent = true)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, session.ClientAccountId.ToString()),
            new Claim(ClaimTypes.Name, session.Email),
            new Claim(ClaimTypes.Email, session.Email),
            new Claim(WorkUpClaims.BusinessId, session.BusinessId.ToString()),
            new Claim(WorkUpClaims.BusinessName, session.BusinessName),
            new Claim(WorkUpClaims.BusinessSlug, session.BusinessSlug),
            new Claim(WorkUpClaims.ClientAccountId, session.ClientAccountId.ToString()),
            new Claim(WorkUpClaims.ClientId, session.ClientId.ToString())
        };
        var identity = new ClaimsIdentity(claims, AuthSchemes.Client);
        var properties = new AuthenticationProperties { IsPersistent = isPersistent };

        return httpContext.SignInAsync(AuthSchemes.Client, new ClaimsPrincipal(identity), properties);
    }
}
