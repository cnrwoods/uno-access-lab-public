using ManagementPortal.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ManagementPortal.Pages;

[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    public bool AuthenticationFailed { get; private set; }

    public IActionResult OnGet(string? error)
    {
        if (User.Identity?.IsAuthenticated == true)
            return LocalRedirect(User.IsInRole(PortalAuthentication.AdminRole) ? "/" : "/access-denied");

        AuthenticationFailed = error == "authentication";
        return Page();
    }

    public IActionResult OnPost()
    {
        if (User.Identity?.IsAuthenticated == true)
            return LocalRedirect(User.IsInRole(PortalAuthentication.AdminRole) ? "/" : "/access-denied");

        // Always land on the overview, including when the original request was
        // for /Policies. No return URL supplied by a browser is accepted.
        return Challenge(new AuthenticationProperties { RedirectUri = "/" }, PortalAuthentication.OidcScheme);
    }
}
