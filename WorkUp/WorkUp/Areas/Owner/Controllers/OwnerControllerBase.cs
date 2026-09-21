using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WorkUp.Infrastructure.Auth;

namespace WorkUp.Areas.Owner.Controllers;

/// <summary>
/// Base for every owner panel controller. Everything is scoped to the signed-in business: always pass
/// <see cref="BusinessId"/> to the business layer, never an id taken from the request.
/// </summary>
[Area("Owner")]
[Authorize(AuthenticationSchemes = AuthSchemes.Owner)]
public abstract class OwnerControllerBase : Controller
{
    protected int BusinessId => User.GetBusinessId();

    protected string OwnerUsername => User.GetOwnerUsername();
}
