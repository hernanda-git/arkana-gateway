using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Arkana.Gateway.Api.Pages;

/// <summary>
/// Public privacy policy page. Required by Google's OAuth verification review
/// (the Gemini consent screen asks for a published Privacy Policy URL).
/// Must be reachable anonymously (no API key, no cookie) so Google's crawler
/// and any reviewer can fetch it. Also exempted from the API-key auth
/// middleware in Program.cs so it is never gated.
/// </summary>
[AllowAnonymous]
public sealed class PrivacyModel : PageModel
{
    public void OnGet()
    {
    }
}
