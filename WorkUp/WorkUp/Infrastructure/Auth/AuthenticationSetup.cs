using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace WorkUp.Infrastructure.Auth;

public static class AuthenticationSetup
{
    public static IServiceCollection AddWorkUpAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(AuthSchemes.Owner)
            .AddCookie(AuthSchemes.Owner, options =>
            {
                options.Cookie.Name = "workup.owner";
                options.LoginPath = "/panel/Account/Login";
                options.AccessDeniedPath = "/panel/Account/Login";
                options.ExpireTimeSpan = TimeSpan.FromDays(14);
                options.SlidingExpiration = true;
            })
            .AddCookie(AuthSchemes.Client, options =>
            {
                options.Cookie.Name = "workup.client";
                options.ExpireTimeSpan = TimeSpan.FromDays(30);
                options.SlidingExpiration = true;
                // Client accounts belong to one business, so the login page depends on the /r/{slug} route.
                options.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = context => RedirectToClientLogin(context),
                    OnRedirectToAccessDenied = context => RedirectToClientLogin(context)
                };
            });

        services.AddAuthorization();
        return services;
    }

    private static Task RedirectToClientLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        var slug = context.HttpContext.GetRouteValue("slug") as string;
        var returnUrl = Uri.EscapeDataString(context.Request.Path + context.Request.QueryString);
        context.Response.Redirect(slug is null ? "/" : $"/r/{slug}/cuenta/ingresar?returnUrl={returnUrl}");
        return Task.CompletedTask;
    }
}
