using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimpleOfficeScheduler.Services.Users;

namespace SimpleOfficeScheduler.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
public class UsersApiController : ControllerBase
{
    private readonly IUserSearchService _userSearchService;

    public UsersApiController(IUserSearchService userSearchService)
    {
        _userSearchService = userSearchService;
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string q = "",
        [FromQuery] int? excludeUserId = null)
    {
        var results = await _userSearchService.SearchUsersAsync(q, excludeUserId);
        return Ok(results);
    }

    [HttpPost("ensure")]
    public async Task<IActionResult> EnsureUser([FromBody] EnsureUserRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
            return BadRequest(new { error = "Username is required." });

        var result = await _userSearchService.EnsureUserAsync(request.Username);
        if (result is null)
            return NotFound(new { error = "User not found." });

        return Ok(result);
    }
}

public class EnsureUserRequest
{
    public string Username { get; set; } = string.Empty;
}
