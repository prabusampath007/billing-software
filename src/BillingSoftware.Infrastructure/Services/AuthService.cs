using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Services;

internal sealed partial class AuthService(DbFactory factory, ICurrentUser current, ApprovalLedger ledger) : ServiceBase(factory, current), IAuthService
{
    private const int Iterations = 210_000;
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);

    public async Task<bool> IsFirstRunAsync()
    {
        await using var db = Factory.Create();
        return !await db.Users.AnyAsync();
    }

    public async Task<SessionUser> CreateInitialAdminAsync(string username, string displayName, string password)
    {
        await using var db = Factory.Create();
        if (await db.Users.AnyAsync()) throw new BusinessRuleException("Setup has already been completed.");
        var user = await AddUserAsync(db, username, displayName, password, UserRole.Admin);
        var session = new SessionUser(user.Id, user.Username, user.DisplayName, user.Role);
        await AuditWriter.AppendAsync(db, session, "user.admin_created", "User", user.Id, "Initial Admin account created");
        Current.SignIn(session);
        return session;
    }

    public async Task<SessionUser> LoginAsync(string username, string password)
    {
        const string generic = "Incorrect username or password.";
        username = (username ?? "").Trim();
        await using var db = Factory.Create();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null || !user.IsActive)
        {
            await AuditWriter.AppendAsync(db, null, "login.failed", "User", null, $"Unknown or inactive user '{Truncate(username)}'");
            throw new ValidationException(generic);
        }

        if (user.LockedUntilUtc is { } until && until > DateTime.UtcNow)
            throw new BusinessRuleException($"This account is locked after too many wrong passwords. Try again in {Math.Ceiling((until - DateTime.UtcNow).TotalMinutes)} minute(s).");

        var session = new SessionUser(user.Id, user.Username, user.DisplayName, user.Role);
        if (!Verify(password, user))
        {
            user.FailedAttempts++;
            var locked = user.FailedAttempts >= MaxFailedAttempts;
            if (locked) { user.LockedUntilUtc = DateTime.UtcNow + LockDuration; user.FailedAttempts = 0; }
            await db.SaveChangesAsync();
            await AuditWriter.AppendAsync(db, session, locked ? "login.locked" : "login.failed", "User", user.Id, "Wrong password");
            throw new ValidationException(generic);
        }

        user.FailedAttempts = 0;
        user.LockedUntilUtc = null;
        await db.SaveChangesAsync();
        await AuditWriter.AppendAsync(db, session, "login.success", "User", user.Id, "");
        Current.SignIn(session);
        return session;
    }

    public async Task<long> VerifyAdminApprovalAsync(string adminPassword)
    {
        var user = RequireUser();
        if (ledger.IsThrottled())
            throw new BusinessRuleException("Too many wrong approval attempts. Please wait a minute and try again.");

        await using var db = Factory.Create();
        var admins = await db.Users.Where(u => u.Role == UserRole.Admin && u.IsActive).ToListAsync();
        var match = admins.FirstOrDefault(a => Verify(adminPassword ?? "", a));
        if (match == null)
        {
            ledger.RegisterFailure();
            await AuditWriter.AppendAsync(db, user, "approval.denied", "User", user.Id, "Wrong Admin password at approval prompt");
            throw new ValidationException("Incorrect Admin password.");
        }

        ledger.RegisterSuccess();
        ledger.Record(match.Id);
        await AuditWriter.AppendAsync(db, user, "approval.granted", "User", match.Id, $"Admin '{match.Username}' approved a restricted action");
        return match.Id;
    }

    public async Task<IReadOnlyList<UserDto>> ListUsersAsync()
    {
        RequireAdmin();
        await using var db = Factory.Create();
        return await db.Users.OrderBy(u => u.Username)
            .Select(u => new UserDto(u.Id, u.Username, u.DisplayName, u.Role, u.IsActive)).ToListAsync();
    }

    public async Task<UserDto> CreateCashierAsync(string username, string displayName, string password)
    {
        var admin = RequireAdmin();
        await using var db = Factory.Create();
        var user = await AddUserAsync(db, username, displayName, password, UserRole.Cashier);
        await AuditWriter.AppendAsync(db, admin, "user.cashier_created", "User", user.Id, $"Cashier '{user.Username}' created");
        return new UserDto(user.Id, user.Username, user.DisplayName, user.Role, user.IsActive);
    }

    public async Task ResetPasswordAsync(long userId, string newPassword)
    {
        var admin = RequireAdmin();
        ValidatePassword(newPassword);
        await using var db = Factory.Create();
        var user = await db.Users.FindAsync(userId) ?? throw new ValidationException("User not found.");
        SetPassword(user, newPassword);
        user.FailedAttempts = 0;
        user.LockedUntilUtc = null;
        await db.SaveChangesAsync();
        await AuditWriter.AppendAsync(db, admin, "user.password_reset", "User", user.Id, $"Password reset for '{user.Username}'");
    }

    public async Task SetActiveAsync(long userId, bool active)
    {
        var admin = RequireAdmin();
        await using var db = Factory.Create();
        var user = await db.Users.FindAsync(userId) ?? throw new ValidationException("User not found.");
        if (!active)
        {
            if (user.Id == admin.Id) throw new BusinessRuleException("You cannot deactivate your own account.");
            if (user.Role == UserRole.Admin && await db.Users.CountAsync(u => u.Role == UserRole.Admin && u.IsActive && u.Id != user.Id) == 0)
                throw new BusinessRuleException("At least one active Admin is required.");
        }
        user.IsActive = active;
        await db.SaveChangesAsync();
        await AuditWriter.AppendAsync(db, admin, active ? "user.activated" : "user.deactivated", "User", user.Id, user.Username);
    }

    // ---------- helpers ----------
    private async Task<User> AddUserAsync(AppDbContext db, string username, string displayName, string password, UserRole role)
    {
        username = (username ?? "").Trim();
        displayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim();
        if (!UsernameRule().IsMatch(username)) throw new ValidationException("Username must be 3 to 32 characters: letters, digits, dot, dash or underscore.");
        ValidatePassword(password);
        if (await db.Users.AnyAsync(u => u.Username == username)) throw new ValidationException("That username is already taken.");
        var user = new User { Username = username, DisplayName = displayName, Role = role };
        SetPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            throw new ValidationException("Password must be at least 8 characters.");
    }

    private static void SetPassword(User user, string password)
    {
        user.Salt = RandomNumberGenerator.GetBytes(16);
        user.Iterations = Iterations;
        user.PasswordHash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), user.Salt, user.Iterations, HashAlgorithmName.SHA256, 32);
    }

    private static bool Verify(string password, User user)
    {
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), user.Salt, user.Iterations, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(hash, user.PasswordHash);
    }

    private static string Truncate(string s) => s.Length <= 40 ? s : s[..40];

    [GeneratedRegex(@"^[A-Za-z0-9._-]{3,32}$")]
    private static partial Regex UsernameRule();
}
