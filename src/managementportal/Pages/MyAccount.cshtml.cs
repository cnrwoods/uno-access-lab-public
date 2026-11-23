using System.Globalization;
using System.Security.Claims;
using ManagementPortal.Authentication;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ManagementPortal.Pages;

public sealed class MyAccountModel : PageModel
{
    public string DisplayName { get; private set; } = "Your account";
    public string Initials { get; private set; } = "?";
    public string? Username { get; private set; }
    public string? GivenName { get; private set; }
    public string? FamilyName { get; private set; }
    public string? Email { get; private set; }
    public string EmailVerification { get; private set; } = "Not provided";
    public IReadOnlyList<string> PortalRoles { get; private set; } = [];

    public void OnGet()
    {
        Username = ProfileValue("preferred_username");
        GivenName = ProfileValue("given_name");
        FamilyName = ProfileValue("family_name");
        Email = ProfileValue("email");

        var fullName = string.Join(" ", new[] { GivenName, FamilyName }.Where(value => value is not null));
        var name = ProfileValue("name")
            ?? (fullName.Length > 0 ? fullName : null)
            ?? Username
            ?? Email;
        DisplayName = name ?? "Your account";
        if (name is not null)
        {
            var words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            Initials = string.Concat(words.Take(2).Select(word => StringInfo.GetNextTextElement(word))).ToUpperInvariant();
        }

        // An absent verification claim is unknown, rather than a failed check.
        if (Email is not null && bool.TryParse(ProfileValue("email_verified"), out var verified))
            EmailVerification = verified ? "Verified" : "Not verified";

        var roles = new List<string>();
        if (User.IsInRole(PortalAuthentication.AdminRole)) roles.Add("Administrator");
        if (User.IsInRole("platform-operator")) roles.Add("Operator");
        if (User.IsInRole("platform-auditor")) roles.Add("Auditor");
        PortalRoles = roles;
    }

    private string? ProfileValue(string claimType)
    {
        var value = User.FindFirstValue(claimType)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
