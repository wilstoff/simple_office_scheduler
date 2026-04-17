using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimpleOfficeScheduler.Data;
using SimpleOfficeScheduler.Models;
using SimpleOfficeScheduler.Services;

namespace SimpleOfficeScheduler.Controllers;

[ApiController]
[Route("api/user/settings")]
[Authorize]
public class UserSettingsApiController : ControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public UserSettingsApiController(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    private int GetUserId() =>
        int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IActionResult> GetSettings()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(GetUserId());
        if (user is null) return NotFound();

        return Ok(new UserSettingsResponse
        {
            ThemePreference = user.ThemePreference,
            TimeZonePreference = user.TimeZonePreference,
            DisplayName = user.DisplayName,
            Email = user.Email,
            IsLocalAccount = user.IsLocalAccount
        });
    }

    [HttpPut("theme")]
    public async Task<IActionResult> UpdateTheme([FromBody] UpdateThemeRequest request)
    {
        if (request.Theme is not ("dark" or "light"))
            return BadRequest(new { error = "Invalid theme. Must be 'dark' or 'light'." });

        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(GetUserId());
        if (user is null) return NotFound();

        user.ThemePreference = request.Theme;
        await db.SaveChangesAsync();
        return Ok();
    }

    [HttpPut("timezone")]
    public async Task<IActionResult> UpdateTimezone([FromBody] UpdateTimezoneRequest request)
    {
        if (string.IsNullOrEmpty(request.TimeZoneId) || !TimeZoneHelper.IsValidTimeZoneId(request.TimeZoneId))
            return BadRequest(new { error = "Invalid timezone ID." });

        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(GetUserId());
        if (user is null) return NotFound();

        user.TimeZonePreference = request.TimeZoneId;
        await db.SaveChangesAsync();
        return Ok();
    }
}
