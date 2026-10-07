using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace CustomerDepositRemaining.Services
{
    public class AdminOptions
    {
        /// <summary>URL ของ AD gateway (POST {"user_name","password"} -> {"isSuccess":true,"item":{...}})</summary>
        public string AdAuthUrl { get; set; } = "";
        public int AdTimeoutSeconds { get; set; } = 10;
        public int AccessHours { get; set; } = 8;
        public int RememberDays { get; set; } = 30;
        public int MaxFailedAttempts { get; set; } = 5;
        public int LockoutMinutes { get; set; } = 5;
    }

    public record AdminIdentity(string Username, string DisplayName, string? Email, string? Domain, IReadOnlyList<string> Groups);

    public enum AdminAuthFailure { None, Rejected, Unavailable, Throttled }

    public record AdminAuthResult(AdminIdentity? Identity, AdminAuthFailure Failure)
    {
        public bool Ok => Identity is not null;
    }

    public interface IAdminAuthService
    {
        Task<AdminAuthResult> AuthenticateAsync(string username, string password, CancellationToken ct);
    }

    /// <summary>port ของ admin_ad_auth.py: ตรวจ username/password ผ่าน AD gateway ภายในบริษัท</summary>
    public class AdminAuthService : IAdminAuthService
    {
        private static readonly ConcurrentDictionary<string, (int Count, DateTime Since)> Failures = new();

        private readonly HttpClient _http;
        private readonly AdminOptions _opt;
        private readonly ILogger<AdminAuthService> _log;

        public AdminAuthService(HttpClient http, Microsoft.Extensions.Options.IOptions<AdminOptions> opt, ILogger<AdminAuthService> log)
        {
            _http = http; _opt = opt.Value; _log = log;
            _http.Timeout = TimeSpan.FromSeconds(Math.Max(1, _opt.AdTimeoutSeconds));
        }

        public async Task<AdminAuthResult> AuthenticateAsync(string username, string password, CancellationToken ct)
        {
            var key = username.Trim().ToLowerInvariant();
            if (Failures.TryGetValue(key, out var f))
            {
                if ((DateTime.UtcNow - f.Since).TotalMinutes > _opt.LockoutMinutes) Failures.TryRemove(key, out _);
                else if (f.Count >= _opt.MaxFailedAttempts) return new AdminAuthResult(null, AdminAuthFailure.Throttled);
            }

            if (string.IsNullOrWhiteSpace(_opt.AdAuthUrl))
            {
                _log.LogWarning("Admin AD authentication is not configured (Admin:AdAuthUrl)");
                return new AdminAuthResult(null, AdminAuthFailure.Unavailable);
            }

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, _opt.AdAuthUrl)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { user_name = username, password }), Encoding.UTF8, "application/json")
                };
                req.Headers.Accept.ParseAdd("application/json");
                using var res = await _http.SendAsync(req, ct);
                if ((int)res.StatusCode is 401 or 403) return Reject(key);
                if (!res.IsSuccessStatusCode)
                {
                    _log.LogWarning("AD authentication service returned HTTP {Status}", (int)res.StatusCode);
                    return new AdminAuthResult(null, AdminAuthFailure.Unavailable);
                }

                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return new AdminAuthResult(null, AdminAuthFailure.Unavailable);
                if (!(root.TryGetProperty("isSuccess", out var ok) && ok.ValueKind == JsonValueKind.True)) return Reject(key);
                if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
                    return new AdminAuthResult(null, AdminAuthFailure.Unavailable);

                var user = FirstText(item, "username", "userName", "user_name", "samAccountName");
                if (user is null) return new AdminAuthResult(null, AdminAuthFailure.Unavailable);

                Failures.TryRemove(key, out _);
                return new AdminAuthResult(new AdminIdentity(
                    user,
                    FirstText(item, "displayName", "display_name", "fullName", "full_name", "name") ?? user,
                    FirstText(item, "email", "mail"),
                    FirstText(item, "domain"),
                    Groups(item)), AdminAuthFailure.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _log.LogWarning("AD authentication service is unavailable: {Type}", ex.GetType().Name);
                return new AdminAuthResult(null, AdminAuthFailure.Unavailable);
            }
        }

        private static AdminAuthResult Reject(string key)
        {
            Failures.AddOrUpdate(key, (1, DateTime.UtcNow), (_, v) => (v.Count + 1, v.Since));
            return new AdminAuthResult(null, AdminAuthFailure.Rejected);
        }

        private static string? FirstText(JsonElement item, params string[] keys)
        {
            foreach (var k in keys)
                if (item.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()?.Trim() is { Length: > 0 } s)
                    return s;
            return null;
        }

        private static IReadOnlyList<string> Groups(JsonElement item)
        {
            var list = new List<string>();
            if (!item.TryGetProperty("groups", out var g)) return list;
            if (g.ValueKind == JsonValueKind.String && g.GetString()?.Trim() is { Length: > 0 } one) list.Add(one);
            else if (g.ValueKind == JsonValueKind.Array)
                foreach (var e in g.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.String && e.GetString()?.Trim() is { Length: > 0 } s) list.Add(s);
                    else if (e.ValueKind == JsonValueKind.Object && FirstText(e, "name", "groupName", "displayName") is { } n) list.Add(n);
                }
            return list.Distinct().ToList();
        }
    }
}
