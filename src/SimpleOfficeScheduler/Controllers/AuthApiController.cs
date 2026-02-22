using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimpleOfficeScheduler.Models;
using IAppAuthService = SimpleOfficeScheduler.Services.Auth.IAuthenticationService;

namespace SimpleOfficeScheduler.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthApiController : ControllerBase
{
    private readonly IAppAuthService _authService;

    public AuthApiController(IAppAuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var result = await _authService.ValidateAsync(request.Username, request.Password);
        if (!result.Success || result.User is null)
            return Unauthorized(new { error = result.ErrorMessage });

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.User.Id.ToString()),
            new(ClaimTypes.Name, result.User.Username),
            new("DisplayName", result.User.DisplayName),
            new(ClaimTypes.Email, result.User.Email),
            new("ThemePreference", result.User.ThemePreference)
        };

        var identity = new ClaimsIdentity(claims, "Cookies");
        var principal = new ClaimsPrincipal(identity);
        await HttpContext.SignInAsync("Cookies", principal);

        return Ok(new UserResponse
        {
            Id = result.User.Id,
            Username = result.User.Username,
            DisplayName = result.User.DisplayName,
            Email = result.User.Email,
            ThemePreference = result.User.ThemePreference,
            TimeZonePreference = result.User.TimeZonePreference
        });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync("Cookies");
        return Ok();
    }
}
