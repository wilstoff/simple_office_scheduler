using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Services.Auth;

public class AuthResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public AppUser? User { get; set; }

    public static AuthResult Succeeded(AppUser user) => new() { Success = true, User = user };
    public static AuthResult Failed(string message) => new() { Success = false, ErrorMessage = message };
}

public interface IAuthenticationService
{
    Task<AuthResult> ValidateAsync(string username, string password);
}
