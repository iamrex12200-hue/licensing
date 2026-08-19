using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace LicenseClient
{
    public sealed class LicenseNetworkException : Exception
    {
        public LicenseNetworkException(string message, Exception inner)
            : base(message, inner) { }
    }

    public abstract class ApiResponseBase
    {
        [JsonIgnore] public int StatusCode { get; set; }
        [JsonIgnore] public int? RetryAfterSeconds { get; set; }
        [JsonProperty("success")] public bool Success { get; set; }
        [JsonProperty("error")] public string Error { get; set; }
    }

    public sealed class ActivationResponse : ApiResponseBase
    {
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("token")] public string Token { get; set; }
        [JsonProperty("product")] public string Product { get; set; }
        [JsonProperty("features")] public List<string> Features { get; set; }
        [JsonProperty("expires_at")] public long ExpiresAt { get; set; }
        [JsonProperty("expires_at_iso")] public string ExpiresAtIso { get; set; }
    }

    public sealed class ValidationResponse : ApiResponseBase
    {
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("product")] public string Product { get; set; }
        [JsonProperty("features")] public List<string> Features { get; set; }
        [JsonProperty("expires_at")] public long ExpiresAt { get; set; }
        [JsonProperty("expires_at_iso")] public string ExpiresAtIso { get; set; }
    }

    public sealed class AdminResponse : ApiResponseBase
    {
        [JsonProperty("keys")] public List<string> Keys { get; set; }
        [JsonProperty("expires_at")] public long ExpiresAt { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("key")] public string Key { get; set; }
        [JsonProperty("hwid_hash")] public string HwidHash { get; set; }
    }

    public sealed class FeatureResponse : ApiResponseBase
    {
        [JsonProperty("product")] public string Product { get; set; }
        [JsonProperty("data")] public Newtonsoft.Json.Linq.JObject Data { get; set; }
    }

    public sealed class UpgradeResponse : ApiResponseBase
    {
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("token")] public string Token { get; set; }
        [JsonProperty("product")] public string Product { get; set; }
        [JsonProperty("features")] public List<string> Features { get; set; }
        [JsonProperty("expires_at")] public long ExpiresAt { get; set; }
        [JsonProperty("expires_at_iso")] public string ExpiresAtIso { get; set; }
        [JsonProperty("previous")] public Dictionary<string, string> Previous { get; set; }
        [JsonProperty("new")] public Dictionary<string, string> New { get; set; }
    }

    public static class HardwareIdentity
    {
        public static string ComputeHwidHash()
        {
            var parts = new StringBuilder();
            parts.Append(Environment.MachineName).Append('|');
            try
            {
                parts.Append(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")).Append('|');
            }
            catch { }
            try
            {
                using (var registry = Microsoft.Win32.Registry.LocalMachine
                           .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    parts.Append(registry?.GetValue("MachineGuid")).Append('|');
                }
            }
            catch { }
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                        nic.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    {
                        parts.Append(nic.GetPhysicalAddress()).Append('|');
                    }
                }
            }
            catch { }
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(parts.ToString()));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }
    }

    public sealed class LicenseClient : IDisposable
    {
        private readonly HttpClient _http;

        public string Token { get; private set; }
        public string DeviceHwid { get; private set; }

        public LicenseClient(string baseUrl, string adminKey = null)
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            _http = new HttpClient
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(10)
            };
            if (!string.IsNullOrEmpty(adminKey))
            {
                _http.DefaultRequestHeaders.Add("X-Admin-Key", adminKey);
            }
            DeviceHwid = HardwareIdentity.ComputeHwidHash();
        }

        public void SetToken(string token) => Token = token;

        public void ClearToken() => Token = null;

        public void SetDeviceId(string deviceId)
        {
            var id = (deviceId ?? "").Trim().ToLowerInvariant();
            if (id.Length == 64 && id.All(c => (c >= '0' && c <= '9')
                                               || (c >= 'a' && c <= 'f')))
            {
                DeviceHwid = id;
                return;
            }
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(id));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) hex.Append(b.ToString("x2"));
                DeviceHwid = hex.ToString();
            }
        }

        private async Task<T> SendAsync<T>(HttpMethod method, string path,
                                           object body) where T : ApiResponseBase
        {
            using (var req = new HttpRequestMessage(method, path))
            {
                if (body != null)
                {
                    req.Content = new StringContent(JsonConvert.SerializeObject(body),
                        Encoding.UTF8, "application/json");
                }
                if (!string.IsNullOrEmpty(Token))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
                }
                req.Headers.Add("X-Device-Hwid", DeviceHwid);

                HttpResponseMessage resp;
                try
                {
                    resp = await _http.SendAsync(req);
                }
                catch (HttpRequestException exc)
                {
                    throw new LicenseNetworkException("network error: " + exc.Message, exc);
                }
                catch (TaskCanceledException exc)
                {
                    throw new LicenseNetworkException("request timed out", exc);
                }
                var text = await resp.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<T>(text)
                             ?? Activator.CreateInstance<T>();
                result.StatusCode = (int)resp.StatusCode;
                result.RetryAfterSeconds = (int?)resp.Headers.RetryAfter?.Delta?.TotalSeconds;
                return result;
            }
        }

        public Task<T> GetJsonAsync<T>(string path) where T : ApiResponseBase
            => SendAsync<T>(HttpMethod.Get, path, null);

        public Task<T> PostJsonAsync<T>(string path, object body) where T : ApiResponseBase
            => SendAsync<T>(HttpMethod.Post, path, body);

        public Task<ActivationResponse> ActivateAsync(string licenseKey, string hwidHash)
        {
            return PostAsync<ActivationResponse>("/api/v1/activate",
                new { key = licenseKey, hwid = hwidHash });
        }

        public Task<ValidationResponse> ValidateAsync(string token, string hwidHash)
        {
            return PostAsync<ValidationResponse>("/api/v1/validate",
                new { token = token, hwid = hwidHash });
        }

        public Task<ActivationResponse> DeactivateAsync(string licenseKey, string hwidHash)
        {
            return PostAsync<ActivationResponse>("/api/v1/deactivate",
                new { key = licenseKey, hwid = hwidHash });
        }

public Task<UpgradeResponse> UpgradeAsync(string oldKey, string newKey,
                                                   string hwidHash)
        {
            return PostAsync<UpgradeResponse>("/api/v1/upgrade",
                new { key = newKey, hwid = hwidHash, old_key = oldKey,
                      current_token = Token });
        }

        public Task<ActivationResponse> RegisterDeviceAsync(string hostname, string userId)
        {
            return PostAsync<ActivationResponse>("/api/v1/sentry/devices",
                new { hostname = hostname, user_id = userId });
        }

        public Task<AdminResponse> GenerateKeysAsync(string product, int days, int count = 1)
        {
            return PostAsync<AdminResponse>("/api/v1/admin/keys",
                new { product = product, days = days, count = count });
        }

        public Task<AdminResponse> RevokeKeyAsync(string licenseKey)
        {
            return PostAsync<AdminResponse>("/api/v1/admin/revoke",
                new { action = "revoke_key", key = licenseKey });
        }

        public Task<AdminResponse> RevokeBindingAsync(string licenseKey, string hwidHash)
        {
            return PostAsync<AdminResponse>("/api/v1/admin/revoke",
                new { action = "revoke_binding", key = licenseKey, hwid = hwidHash });
        }

        private Task<T> PostAsync<T>(string path, object body) where T : ApiResponseBase
            => SendAsync<T>(HttpMethod.Post, path, body);

        public void Dispose() => _http.Dispose();
    }

    public static class Program
    {
        public static async Task Main()
        {
            var hwid = HardwareIdentity.ComputeHwidHash();
            Console.WriteLine("HWID: " + hwid);

            using (var client = new LicenseClient("https://lic.yourdomain.com"))
            {
                var activation = await client.ActivateAsync(
                    "ABCDE-FGHJK-MNPQR-STVWX-Y2", hwid);
                if (!activation.Success)
                {
                    Console.WriteLine("Activation failed: " + activation.Error);
                    return;
                }
                Console.WriteLine("Activated as " + activation.Product);

                var validation = await client.ValidateAsync(activation.Token, hwid);
                if (!validation.Success)
                {
                    Console.WriteLine("Validation failed: " + validation.Status);
                    return;
                }
                Console.WriteLine("Valid until " + validation.ExpiresAtIso);
            }
        }
    }
}