using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;

namespace LicenseClient
{
    public sealed class HardwareIdentity
    {
        public static string ComputeHwidHash()
        {
            var parts = new StringBuilder();
            parts.Append(Environment.MachineName).Append('|');
            try { parts.Append(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")).Append('|'); } catch { }
            try
            {
                using var registry = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                parts.Append(registry?.GetValue("MachineGuid")).Append('|');
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
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parts.ToString())));
        }
    }

    public sealed class ActivationResponse
    {
        public bool success { get; set; }
        public string status { get; set; }
        public string token { get; set; }
        public string product { get; set; }
        public long expires_at { get; set; }
        public string expires_at_iso { get; set; }
        public string error { get; set; }
    }

    public sealed class ValidationResponse
    {
        public bool success { get; set; }
        public string status { get; set; }
        public string product { get; set; }
        public long expires_at { get; set; }
        public string expires_at_iso { get; set; }
        public string error { get; set; }
    }

    public sealed class LicenseClient : IDisposable
    {
        private readonly HttpClient _http;

        public LicenseClient(string baseUrl)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            _http = new HttpClient
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(10)
            };
        }

        public async Task<ActivationResponse> ActivateAsync(string licenseKey, string hwidHash)
        {
            var body = JsonContent.Create(new { key = licenseKey, hwid = hwidHash });
            var resp = await _http.PostAsync("/api/v1/activate", body);
            return await resp.Content.ReadFromJsonAsync<ActivationResponse>();
        }

        public async Task<ValidationResponse> ValidateAsync(string token, string hwidHash)
        {
            var body = JsonContent.Create(new { token = token, hwid = hwidHash });
            var resp = await _http.PostAsync("/api/v1/validate", body);
            return await resp.Content.ReadFromJsonAsync<ValidationResponse>();
        }

        public async Task<ActivationResponse> DeactivateAsync(string licenseKey, string hwidHash)
        {
            var body = JsonContent.Create(new { key = licenseKey, hwid = hwidHash });
            var resp = await _http.PostAsync("/api/v1/deactivate", body);
            return await resp.Content.ReadFromJsonAsync<ActivationResponse>();
        }

        public void Dispose() => _http.Dispose();
    }

    public static class Program
    {
        public static async Task Main()
        {
            var hwid = HardwareIdentity.ComputeHwidHash();
            Console.WriteLine($"HWID: {hwid}");

            using var client = new LicenseClient("https://lic.yourdomain.com");

            var activation = await client.ActivateAsync("ABCDE-FGHJK-MNPQR-STVWX-Y2", hwid);
            if (!activation.success)
            {
                Console.WriteLine($"Activation failed: {activation.error}");
                return;
            }
            Console.WriteLine($"Activated as {activation.product}, token issued");

            var validation = await client.ValidateAsync(activation.token, hwid);
            if (!validation.success)
            {
                Console.WriteLine($"Validation failed: {validation.status} {validation.error}");
                return;
            }
            Console.WriteLine($"Valid until {validation.expires_at_iso}");
        }
    }
}