using System.Security.Cryptography;
using System.Text;
using SnapTrans.Core.Abstractions;

namespace SnapTrans.Platform.Windows;

public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SnapTrans.v1");
    public string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        var bytes = Encoding.UTF8.GetBytes(plainText);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        { throw new InvalidOperationException("无法解密此模型的 API Key。密钥与 Windows 用户绑定，请重新输入。", exception); }
    }
}
