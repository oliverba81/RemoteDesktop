using System.Security.Cryptography;
using System.Text;

namespace RemoteDesktop.Shared.Util;

/// <summary>Generates session IDs and per-session passwords.</summary>
public static class SessionId
{
    // Unambiguous alphabet: no 0/O/1/l/I.
    private const string PasswordAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
    private const string Base32Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>Nine digits, grouped for display as "123 456 789". Used for
    /// attended (short-lived) sessions.</summary>
    public static string NewNumericId()
    {
        Span<byte> b = stackalloc byte[4];
        RandomNumberGenerator.Fill(b);
        uint value = BitConverter.ToUInt32(b) % 1_000_000_000u;
        return value.ToString("D9");
    }

    /// <summary>High-entropy, non-enumerable id for unattended hosts (~65 bits).</summary>
    public static string NewUnattendedId(int chars = 13)
    {
        var sb = new StringBuilder(chars);
        Span<byte> b = stackalloc byte[1];
        for (int i = 0; i < chars; i++)
        {
            RandomNumberGenerator.Fill(b);
            sb.Append(Base32Alphabet[b[0] % Base32Alphabet.Length]);
        }
        return sb.ToString();
    }

    /// <summary>Ten-character session password (~59 bits) — also keys the E2E crypto.</summary>
    public static string NewPassword(int chars = 10)
    {
        var sb = new StringBuilder(chars);
        Span<byte> b = stackalloc byte[1];
        for (int i = 0; i < chars; i++)
        {
            RandomNumberGenerator.Fill(b);
            sb.Append(PasswordAlphabet[b[0] % PasswordAlphabet.Length]);
        }
        return sb.ToString();
    }

    public static string FormatNumericForDisplay(string id) =>
        id.Length == 9 ? $"{id[..3]} {id[3..6]} {id[6..]}" : id;
}
