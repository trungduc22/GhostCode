using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GhostCode.Core.Cloud;

public sealed record GoogleAuthTokens(
    string AccessToken,
    string RefreshToken,
    long ExpiresAtUnixMs,
    string Email,
    string DisplayName);

/// <summary>
/// Encrypts Google OAuth tokens using Windows DPAPI (CurrentUser scope)
/// so tokens are bound to the logged-in Windows account and never written in plaintext.
/// </summary>
public sealed class DpapiCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GhostCode.OAuth.DPAPI.v1");
    private readonly string _filePath;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public DpapiCredentialStore(string filePath)
    {
        _filePath = filePath;
    }

    public string FilePath => _filePath;

    public async Task SaveAsync(GoogleAuthTokens tokens, CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct);
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(tokens);
            byte[] protectedBytes = OperatingSystem.IsWindows()
                ? ProtectedData.Protect(jsonBytes, Entropy, DataProtectionScope.CurrentUser)
                : jsonBytes;

            var tmp = $"{_filePath}.tmp.{Guid.NewGuid():N}";
            await File.WriteAllBytesAsync(tmp, protectedBytes, ct);
            File.Move(tmp, _filePath, overwrite: true);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<GoogleAuthTokens?> LoadAsync(CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct);
        try
        {
            if (!File.Exists(_filePath))
                return null;

            var protectedBytes = await File.ReadAllBytesAsync(_filePath, ct);
            var plainBytes = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser)
                : protectedBytes;

            return JsonSerializer.Deserialize<GoogleAuthTokens>(plainBytes);
        }
        catch
        {
            return null;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct);
        try
        {
            if (File.Exists(_filePath))
                File.Delete(_filePath);
        }
        finally
        {
            _ioLock.Release();
        }
    }
}
