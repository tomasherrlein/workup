using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using WorkUp.Infrastructure.Auth;

namespace WorkUp.Controllers;

/// <summary>
/// Base for the optional client panel under <c>/r/{slug}/...</c>. A client account belongs to one business, so a
/// session from another business is sent to this business login.
/// </summary>
[Authorize(AuthenticationSchemes = AuthSchemes.Client)]
public abstract class ClientPanelControllerBase : Controller
{
    protected int BusinessId => User.GetBusinessId();

    protected int ClientId => User.GetClientId();

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var slug = RouteData.Values["slug"] as string;
        if (slug is not null && !string.Equals(slug, User.GetBusinessSlug(), StringComparison.OrdinalIgnoreCase))
            context.Result = Redirect($"/r/{slug}/cuenta/ingresar");

        base.OnActionExecuting(context);
    }
}
