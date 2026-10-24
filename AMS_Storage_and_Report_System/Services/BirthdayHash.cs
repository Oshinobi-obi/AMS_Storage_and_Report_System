// This is how I save the birthday for My accountability: only as a one-way PBKDF2 hash.
// The same code is in e-SRS and e-PINS, so I keep both files the same.
// Format: pbkdf2-sha256$<iterations>$<salt base64>$<hash base64>

using System.Security.Cryptography;
using System.Text;

namespace AMS_Storage_and_Report_System.Services;

public static class BirthdayHash
{
    private const int Iterations = 100_000, SaltBytes = 16, HashBytes = 32;
    private const string Scheme = "pbkdf2-sha256";

    /// Earliest and latest birthday allowed (an employee is 15 to 100 years old).
    public static DateOnly Earliest(DateOnly today) => today.AddYears(-100);
    public static DateOnly Latest(DateOnly today) => today.AddYears(-15);

    public static string Hash(long personnelId, DateOnly birthday)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(personnelId, birthday, salt, Iterations);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(long personnelId, DateOnly birthday, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations)) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            return CryptographicOperations.FixedTimeEquals(Derive(personnelId, birthday, salt, iterations), expected);
        }
        catch (FormatException) { return false; }
    }

    private static byte[] Derive(long personnelId, DateOnly birthday, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes($"{personnelId}|{birthday:yyyy-MM-dd}"), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}
