using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CustomerDepositRemaining.Services
{
    public class SmsmktOptions
    {
        public string BaseUrl { get; set; } = "https://portal-otp.smsmkt.com/api";
        public string ApiKey { get; set; } = "";
        public string SecretKey { get; set; } = "";
        public string ProjectKey { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 10;
        /// <summary>อายุ OTP เมื่อใช้ SMSMKT (ระบบเดิม 180 วินาที)</summary>
        public int TtlSeconds { get; set; } = 180;
        public bool Configured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(SecretKey) && !string.IsNullOrWhiteSpace(ProjectKey);
    }

    public class OtpOptions
    {
        /// <summary>เปิดเฉพาะ Development: ใช้ OTP คงที่แทนการส่ง SMS จริง (ห้ามตั้งบน production)</summary>
        public string? DemoCode { get; set; }
        public int TtlSeconds { get; set; } = 300;
        public int ResendAfterSeconds { get; set; } = 60;
        public int MaxAttempts { get; set; } = 5;
    }

    public enum OtpFailure { None, Invalid, Expired, TooManyAttempts, RequestInvalid, Rejected, Unavailable }

    public record OtpStartResult(bool Ok, string RequestId = "", string MaskedPhone = "", string ReferenceCode = "",
                                 int ExpiresInSeconds = 0, int ResendAfterSeconds = 0, OtpFailure Failure = OtpFailure.None);
    public record OtpVerifyResult(bool Ok, string Phone = "", OtpFailure Failure = OtpFailure.None);

    public interface IOtpService
    {
        bool DemoMode { get; }
        Task<OtpStartResult> StartAsync(string phone, CancellationToken ct);
        Task<OtpVerifyResult> VerifyAsync(string requestId, string code, CancellationToken ct);
    }

    /// <summary>port ของ smsmkt_otp.py + /auth/otp/* ใน main.py (เก็บคำขอ OTP ในหน่วยความจำ)</summary>
    public class OtpService : IOtpService
    {
        private sealed class Entry
        {
            public string Phone = "";
            public string? Token;
            public string RefCode = "";
            public string? LocalCode;
            public DateTime ExpiresAt;
            public int Attempts;
            public bool Used;
        }

        private enum Kind { Ok, Rejected, Unavailable }
        private sealed record Reply(Kind Kind, string Code = "", string Detail = "", JsonElement Root = default);

        private static readonly ConcurrentDictionary<string, Entry> Store = new();
        private static readonly ConcurrentDictionary<string, DateTime> LastSent = new();

        private readonly SmsmktOptions _sms;
        private readonly OtpOptions _otp;
        private readonly HttpClient _http;
        private readonly ILogger<OtpService> _log;

        public OtpService(Microsoft.Extensions.Options.IOptions<SmsmktOptions> sms,
                          Microsoft.Extensions.Options.IOptions<OtpOptions> otp,
                          HttpClient http, ILogger<OtpService> log)
        {
            _sms = sms.Value; _otp = otp.Value; _http = http; _log = log;
            _http.Timeout = TimeSpan.FromSeconds(Math.Max(1, _sms.TimeoutSeconds));
        }

        public bool DemoMode => !_sms.Configured && !string.IsNullOrEmpty(_otp.DemoCode);

        public static string Mask(string phone) =>
            phone.Length >= 6 ? $"{phone[..3]}****{phone[^3..]}" : "****";

        public async Task<OtpStartResult> StartAsync(string phone, CancellationToken ct)
        {
            Purge();
            if (LastSent.TryGetValue(phone, out var last) && (DateTime.UtcNow - last).TotalSeconds < _otp.ResendAfterSeconds)
                return new OtpStartResult(false, Failure: OtpFailure.TooManyAttempts);

            var entry = new Entry { Phone = phone };
            int ttl = _otp.TtlSeconds;

            if (_sms.Configured)
            {
                // ระบบเดิมไม่ส่ง ref_code ไปเอง ใช้ค่าที่ SMSMKT ตอบกลับมาแสดงให้ผู้ใช้
                var reply = await PostAsync("otp-send", new { project_key = _sms.ProjectKey, phone }, ct);
                if (reply.Kind == Kind.Unavailable) return new OtpStartResult(false, Failure: OtpFailure.Unavailable);
                if (reply.Kind == Kind.Rejected) return new OtpStartResult(false, Failure: OtpFailure.Rejected);
                if (!reply.Root.TryGetProperty("result", out var r) || r.ValueKind != JsonValueKind.Object
                    || !r.TryGetProperty("token", out var t) || string.IsNullOrWhiteSpace(t.GetString()))
                    return new OtpStartResult(false, Failure: OtpFailure.Unavailable);
                entry.Token = t.GetString()!.Trim();
                entry.RefCode = r.TryGetProperty("ref_code", out var rc) ? rc.GetString()?.Trim() ?? "" : "";
                ttl = Math.Max(1, _sms.TtlSeconds);
            }
            else if (DemoMode)
            {
                entry.LocalCode = _otp.DemoCode;
            }
            else
            {
                // ไม่ได้ตั้งค่า SMSMKT และไม่ใช่โหมดทดสอบ: ปฏิเสธ (ห้ามมีรหัส OTP ตายตัวบน production)
                _log.LogError("OTP is not configured: set Smsmkt:ApiKey/SecretKey/ProjectKey");
                return new OtpStartResult(false, Failure: OtpFailure.Unavailable);
            }

            // คำขอเก่าของเบอร์เดียวกันที่ยังไม่ได้ใช้ ถือว่าใช้แล้ว (เหมือนระบบเดิม)
            foreach (var kv in Store.Where(kv => kv.Value.Phone == phone && !kv.Value.Used)) kv.Value.Used = true;

            var requestId = "otp_req_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
            entry.ExpiresAt = DateTime.UtcNow.AddSeconds(ttl);
            Store[requestId] = entry;
            LastSent[phone] = DateTime.UtcNow;
            return new OtpStartResult(true, requestId, Mask(phone), entry.RefCode, ttl, _otp.ResendAfterSeconds);
        }

        public async Task<OtpVerifyResult> VerifyAsync(string requestId, string code, CancellationToken ct)
        {
            if (!Store.TryGetValue(requestId, out var entry) || entry.Used)
                return new OtpVerifyResult(false, Failure: OtpFailure.RequestInvalid);
            if (DateTime.UtcNow >= entry.ExpiresAt)
                return new OtpVerifyResult(false, Failure: OtpFailure.Expired);
            if (Interlocked.Increment(ref entry.Attempts) > _otp.MaxAttempts)
                return new OtpVerifyResult(false, Failure: OtpFailure.TooManyAttempts);

            bool ok;
            if (entry.LocalCode is not null)
            {
                ok = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(entry.LocalCode), Encoding.UTF8.GetBytes(code));
            }
            else
            {
                var body = new Dictionary<string, string?> { ["token"] = entry.Token, ["otp_code"] = code };
                if (entry.RefCode.Length > 0) body["ref_code"] = entry.RefCode;
                var reply = await PostAsync("otp-validate", body, ct, retry: true);
                if (reply.Kind == Kind.Unavailable) return new OtpVerifyResult(false, Failure: OtpFailure.Unavailable);
                if (reply.Kind == Kind.Rejected)
                {
                    return reply.Code == "5000" && reply.Detail.Contains("expire", StringComparison.OrdinalIgnoreCase)
                        ? new OtpVerifyResult(false, Failure: OtpFailure.Expired)
                        : new OtpVerifyResult(false, Failure: OtpFailure.Invalid);
                }
                ok = reply.Root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object
                     && r.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.True;
            }

            if (!ok) return new OtpVerifyResult(false, Failure: OtpFailure.Invalid);
            entry.Used = true;
            return new OtpVerifyResult(true, entry.Phone);
        }

        private async Task<Reply> PostAsync(string path, object body, CancellationToken ct, bool retry = false)
        {
            for (int attempt = 0; attempt < (retry ? 2 : 1); attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, $"{_sms.BaseUrl.TrimEnd('/')}/{path}")
                    {
                        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
                    };
                    req.Headers.Add("api_key", _sms.ApiKey);
                    req.Headers.Add("secret_key", _sms.SecretKey);
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    using var res = await _http.SendAsync(req, ct);
                    if ((int)res.StatusCode is 400 or 401 or 403) return new Reply(Kind.Rejected);
                    if (!res.IsSuccessStatusCode)
                    {
                        _log.LogWarning("SMSMKT {Path} returned HTTP {Status}", path, (int)res.StatusCode);
                        return new Reply(Kind.Unavailable);
                    }
                    var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement.Clone();
                    if (root.ValueKind != JsonValueKind.Object) return new Reply(Kind.Unavailable);
                    var code = root.TryGetProperty("code", out var c) && c.ValueKind != JsonValueKind.Null ? c.ToString().Trim() : "";
                    var detail = root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()!.Trim() : "";
                    if (code.Length > 0 && code != "000") return new Reply(Kind.Rejected, code, detail, root);
                    return new Reply(Kind.Ok, code, detail, root);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                    _log.LogWarning("SMSMKT {Path} failed (attempt {N}): {Type}", path, attempt + 1, ex.GetType().Name);
                    if (attempt == 0 && retry) await Task.Delay(500, ct);
                }
            }
            return new Reply(Kind.Unavailable);
        }

        private static void Purge()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in Store) if (kv.Value.ExpiresAt < now.AddMinutes(-10)) Store.TryRemove(kv.Key, out _);
            foreach (var kv in LastSent) if ((now - kv.Value).TotalMinutes > 10) LastSent.TryRemove(kv.Key, out _);
        }
    }
}
