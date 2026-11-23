using ManagementPortal.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ManagementPortal.Pages;

[AllowAnonymous]
public sealed class LogoutModel : PageModel
{
    public IActionResult OnGet() => LocalRedirect("/");

    public IActionResult OnPost()
    {
        if (User.Identity?.IsAuthenticated != true) return LocalRedirect("/login");

        return SignOut(new AuthenticationProperties { RedirectUri = "/login" },
            CookieAuthenticationDefaults.AuthenticationScheme, PortalAuthentication.OidcScheme);
    }
}
