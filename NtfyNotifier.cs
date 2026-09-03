using System.Net.Http.Headers;
using System.Text;

namespace _0dtes_agent;

public sealed class NtfyNotifier
{
    private readonly string _topic;
    private readonly string? _authToken;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public NtfyNotifier(string topic, string? authToken = null)
    {
        _topic = topic;
        _authToken = string.IsNullOrWhiteSpace(authToken) ? null : authToken;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_topic);

    public async Task<bool> SendAsync(string title, string message, string[] tags, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return false;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"https://ntfy.sh/{Uri.EscapeDataString(_topic)}");
        request.Headers.TryAddWithoutValidation("Title", ToAscii(title));
        request.Headers.TryAddWithoutValidation("Tags", ToAscii(string.Join(",", tags)));
        request.Headers.TryAddWithoutValidation("Priority", "default");
        if (_authToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
        }
        request.Content = new StringContent(message, Encoding.UTF8, "text/plain");

        try
        {
            var response = await _http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ntfy] send failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Headers must be ASCII; strip non-ASCII (e.g. '·') to avoid broken requests.</summary>
    private static string ToAscii(string value)
        => string.Create(value.Length, value, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                span[i] = src[i] < 128 ? src[i] : '-';
            }
        });
}
