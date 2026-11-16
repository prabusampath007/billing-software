using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace BillingSoftware.Infrastructure.Persistence;

public sealed class KeyMissingException(string message) : Exception(message);

/// <summary>
/// Database encryption key (decision D3): a random 256-bit key protected by Windows DPAPI (machine scope).
/// A recovery key (the same key in readable form) is shown once at first run so the owner can restore on another PC.
/// </summary>
public sealed partial class DbKeyProvider(AppPaths paths)
{
    public string GetOrCreateKey(out bool created)
    {
        created = false;
        if (File.Exists(paths.KeyPath))
        {
            var protectedBytes = File.ReadAllBytes(paths.KeyPath);
            var raw = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.LocalMachine);
            return Convert.ToHexString(raw);
        }

        if (File.Exists(paths.DbPath))
            throw new KeyMissingException("The database exists but its encryption key file is missing. Enter the recovery key to continue.");

        var key = RandomNumberGenerator.GetBytes(32);
        WriteProtected(key);
        created = true;
        return Convert.ToHexString(key);
    }

    /// <summary>Re-creates the protected key file from the recovery key (for example after moving to a new PC).</summary>
    public void RestoreFromRecoveryKey(string recoveryKey)
    {
        var hex = (recoveryKey ?? "").Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant();
        if (!HexKey().IsMatch(hex)) throw new ArgumentException("The recovery key is not valid. It has 64 letters and digits (0-9, A-F).");
        WriteProtected(Convert.FromHexString(hex));
    }

    public static string FormatRecoveryKey(string hexKey) =>
        string.Join("-", Enumerable.Range(0, hexKey.Length / 4).Select(i => hexKey.Substring(i * 4, 4)));

    private void WriteProtected(byte[] key)
    {
        var protectedBytes = ProtectedData.Protect(key, null, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(paths.KeyPath, protectedBytes);
    }

    [GeneratedRegex("^[0-9A-F]{64}$")]
    private static partial Regex HexKey();
}
