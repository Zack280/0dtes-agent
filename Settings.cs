using _0dtes_app.Services;

namespace _0dtes_agent;

public sealed class AgentSettings : ITradingSettings
{
    public BrokerMode Mode { get; set; }

    public bool IsPaper { get; set; } = true;

    public string ActiveSymbol { get; set; } = "SOFI";
}

/// <summary>Simple file-backed token storage so the agent needs no Windows.Storage.</summary>
public sealed class FileTokenStorage : ITokenStorage
{
    private readonly string _path;

    public FileTokenStorage(string path) => _path = path;

    public Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        try
        {
            return Task.FromResult(File.Exists(_path) ? File.ReadAllText(_path).Trim() : null);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task SaveTokenAsync(string token, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(_path, token);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
        return Task.CompletedTask;
    }
}
