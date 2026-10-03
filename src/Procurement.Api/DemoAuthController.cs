using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Procurement.Persistence;

namespace Procurement.Api;

public sealed record LoginRequest([Required, MaxLength(100)] string Username, [Required, MaxLength(200)] string Password);
public sealed record LoginResult(string Token, string Username, string Role, Guid? FactoryId, DateTimeOffset ExpiresAt);

[ApiController, Route("api/demo-auth")]
public sealed class DemoAuthController(ProcurementDbContext db, PasswordHasher<DemoUser> hasher,
    IConfiguration configuration, IWebHostEnvironment environment, TimeProvider clock) : ControllerBase
{
    [HttpPost("login"), EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        if (!environment.IsDevelopment()) return NotFound();
        var username = request.Username.Trim().ToLowerInvariant();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Username == username, ct);
        if (user is null || hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            return Unauthorized();
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(30);
        var claims = new List<Claim>
        {
            new("sub", user.Id), new("name", user.Username), new("role", user.Role),
            new("jti", Guid.NewGuid().ToString())
        };
        if (user.FactoryId is { } factoryId) claims.Add(new("factory_id", factoryId.ToString()));
        var jwt = new JwtSecurityToken(configuration["Auth:Issuer"], configuration["Auth:Audience"], claims,
            now.UtcDateTime, expires.UtcDateTime, new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(configuration["Auth:SigningKey"]!)), SecurityAlgorithms.HmacSha256));
        return Ok(new LoginResult(new JwtSecurityTokenHandler().WriteToken(jwt), user.Username, user.Role, user.FactoryId, expires));
    }
}
