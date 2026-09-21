using Arkana.Gateway.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;

namespace Arkana.Gateway.Api.Pages;

[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    private readonly AuthService _auth;

    [BindProperty]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    public string? Error { get; set; }

    // Google login availability (set from GoogleLogin:Enabled config / env).
    public bool GoogleEnabled { get; set; }

    public LoginModel(AuthService auth, IConfiguration configuration)
    {
        _auth = auth;
        GoogleEnabled = configuration.GetValue<bool>("GoogleLogin:Enabled")
                       || string.Equals(configuration["GOOGLE_LOGIN_ENABLED"], "1", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IActionResult> OnGetAsync()
    {
        // A login form can remain open while another tab completes Google login,
        // changing the claims identity used by ASP.NET antiforgery. That makes
        // the old hidden token fail with "meant for a different claims-based
        // user" (HTTP 400). Login must always issue a fresh anonymous form.
        Response.Headers.CacheControl = "no-store, no-cache";
        Response.Headers.Pragma = "no-cache";

        if (User.Identity?.IsAuthenticated == true)
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Redirect("/login");
        }

        if (!string.IsNullOrEmpty(Request.Query["error"]))
            Error = Request.Query["error"].ToString() switch
            {
                "domain" => "That Google account's domain is not allowed.",
                _ => "Google sign-in failed. Please try again or use your password."
            };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            Error = "Username and password are required.";
            return Page();
        }

        var user = await _auth.ValidateAsync(Username.Trim(), Password);
        if (user is null)
        {
            Error = "Invalid username or password.";
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
            new("TenantId", user.TenantId.ToString()),
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8),
            });

        return Redirect("/");
    }
}
