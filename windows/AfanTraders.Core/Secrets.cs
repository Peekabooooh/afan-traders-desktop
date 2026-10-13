namespace AfanTraders.Core;

/// <summary>Where sign-in tokens and the office code are kept (Windows: DPAPI-encrypted).</summary>
public interface ISecretStore
{
    string? Get(string account);
    void Set(string account, string value);
    void Delete(string account);
}

/// <summary>In-memory store for tests.</summary>
public sealed class MemorySecrets : ISecretStore
{
    readonly Dictionary<string, string> map = new();
    public string? Get(string a) { lock (map) return map.GetValueOrDefault(a); }
    public void Set(string a, string v) { lock (map) map[a] = v; }
    public void Delete(string a) { lock (map) map.Remove(a); }
}
