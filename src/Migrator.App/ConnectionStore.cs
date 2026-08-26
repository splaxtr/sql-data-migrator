namespace Migrator.App;

using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

public enum ServerKind { SqlServer, PostgreSql }

/// <summary>A saved server. The password is kept encrypted on disk; plain text is never written.</summary>
public sealed record ServerProfile(
    string Id,
    string Name,
    ServerKind Kind,
    string Host,
    int Port,
    string User);

internal sealed record StoredProfile(
    string Id, string Name, ServerKind Kind, string Host, int Port, string User, string ProtectedPassword);

/// <summary>
/// Keeps saved servers on the user's own machine.
///
/// Passwords are encrypted with a machine-bound key: copied to another computer, the
/// file yields unreadable passwords. This protects against the file being shared by
/// accident; it does not protect against someone with a session on this machine — the
/// app has to be able to decrypt them to connect.
/// </summary>
public sealed class ConnectionStore
{
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ConnectionStore(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("Migrator.ServerProfile.Password.v1");
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SqlToSqlMigrator");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "servers.json");
    }

    public string StorePath => _path;

    public async Task<List<ServerProfile>> ListAsync()
    {
        var stored = await ReadAsync();
        return stored.Select(s => new ServerProfile(s.Id, s.Name, s.Kind, s.Host, s.Port, s.User)).ToList();
    }

    public async Task<ServerProfile> SaveAsync(
        string? id, string name, ServerKind kind, string host, int port, string user, string? password)
    {
        await _lock.WaitAsync();
        try
        {
            var stored = await ReadAsync();
            var existing = id is null ? null : stored.FirstOrDefault(s => s.Id == id);

            // A blank password keeps the stored one — no retyping while editing.
            var protectedPassword = string.IsNullOrEmpty(password)
                ? existing?.ProtectedPassword ?? ""
                : _protector.Protect(password);

            var entry = new StoredProfile(
                existing?.Id ?? Guid.NewGuid().ToString("N"), name, kind, host, port, user, protectedPassword);

            stored.RemoveAll(s => s.Id == entry.Id);
            stored.Add(entry);
            await WriteAsync(stored);
            return new ServerProfile(entry.Id, entry.Name, entry.Kind, entry.Host, entry.Port, entry.User);
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var stored = await ReadAsync();
            var removed = stored.RemoveAll(s => s.Id == id) > 0;
            if (removed) await WriteAsync(stored);
            return removed;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Builds a working connection string from a saved profile.</summary>
    public async Task<string?> BuildConnectionStringAsync(string id, string? database)
    {
        var stored = (await ReadAsync()).FirstOrDefault(s => s.Id == id);
        if (stored is null) return null;

        var password = string.IsNullOrEmpty(stored.ProtectedPassword)
            ? ""
            : TryUnprotect(stored.ProtectedPassword);

        return stored.Kind switch
        {
            ServerKind.SqlServer =>
                $"Server={stored.Host},{stored.Port};Database={database ?? "master"};User Id={stored.User};" +
                $"Password={password};TrustServerCertificate=True;Encrypt=True",
            ServerKind.PostgreSql =>
                $"Host={stored.Host};Port={stored.Port};Database={database ?? "postgres"};Username={stored.User};" +
                $"Password={password};Command Timeout=0",
            _ => null,
        };
    }

    private string TryUnprotect(string value)
    {
        try { return _protector.Unprotect(value); }
        catch (Exception)
        {
            // The key came from another machine or is corrupt: return empty so the
            // connection fails with a clear authentication error — better than silently
            // trying a wrong password.
            return "";
        }
    }

    private async Task<List<StoredProfile>> ReadAsync()
    {
        if (!File.Exists(_path)) return new List<StoredProfile>();
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<StoredProfile>>(stream) ?? new List<StoredProfile>();
    }

    private async Task WriteAsync(List<StoredProfile> profiles)
    {
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, profiles, new JsonSerializerOptions { WriteIndented = true });
    }
}
