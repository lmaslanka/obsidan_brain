using System.Security.Cryptography;
using System.Text;

namespace ObsidianBrain.App.Utils;

public static class Hashing
{
    public static string Sha256(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
