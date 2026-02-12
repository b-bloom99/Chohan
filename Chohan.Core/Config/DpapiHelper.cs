using System.Security.Cryptography;
using System.Text;

namespace Chohan.Core.Config;

/// <summary>
/// Windows DPAPI (DataProtectionScope.CurrentUser) を使った
/// 文字列の暗号化・復号ヘルパー。
/// 暗号化結果はBase64文字列としてJSONに安全に保存できる。
/// </summary>
public static class DpapiHelper
{
    /// <summary>
    /// 平文文字列をDPAPI暗号化し、Base64文字列として返す。
    /// </summary>
    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var encryptedBytes = ProtectedData.Protect(
            plainBytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encryptedBytes);
    }

    /// <summary>
    /// DPAPI暗号化されたBase64文字列を復号し、平文文字列を返す。
    /// 復号失敗時は空文字を返す。
    /// </summary>
    public static string Decrypt(string encryptedBase64)
    {
        if (string.IsNullOrEmpty(encryptedBase64)) return string.Empty;

        try
        {
            var encryptedBytes = Convert.FromBase64String(encryptedBase64);
            var plainBytes = ProtectedData.Unprotect(
                encryptedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException)
        {
            // 別ユーザーのデータ、またはデータ破損
            return string.Empty;
        }
        catch (FormatException)
        {
            // 不正なBase64
            return string.Empty;
        }
    }
}
