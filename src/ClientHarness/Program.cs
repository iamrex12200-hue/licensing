using System;
using System.Threading.Tasks;
using LicenseClient;

namespace ClientHarness
{
    public static class Program
    {
        private static async Task Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: ClientHarness.exe <url> <key>");
                return;
            }
            var url = args[0];
            var key = args[1];

            using (var client = new LicenseClient.LicenseClient(url))
            {
                var hwid = client.DeviceHwid;
                Console.WriteLine("hwid: " + hwid.Substring(0, 16) + "...");

                var activation = await client.ActivateAsync(key, hwid);
                Console.WriteLine("activate: " + activation.StatusCode + " "
                                  + activation.Status + " features=["
                                  + string.Join(",", activation.Features ?? new System.Collections.Generic.List<string>())
                                  + "]");
                if (!activation.Success) return;

                client.SetToken(activation.Token);

                var summary = await client.GetJsonAsync<FeatureResponse>("/api/v1/data/summary");
                Console.WriteLine("summary (feature_a): " + summary.StatusCode + " "
                                  + (summary.Success ? "OK " + summary.Data : summary.Error));

                var advanced = await client.GetJsonAsync<FeatureResponse>("/api/v1/data/advanced");
                Console.WriteLine("advanced (feature_b): " + advanced.StatusCode + " "
                                  + (advanced.Success ? "OK " + advanced.Data : advanced.Error));

                client.ClearToken();
                var noToken = await client.GetJsonAsync<FeatureResponse>("/api/v1/data/summary");
                Console.WriteLine("summary without token: " + noToken.StatusCode + " " + noToken.Error);

                if (args.Length >= 3)
                {
                    var upgradeKey = args[2];
                    client.SetToken(activation.Token);
                    var up = await client.UpgradeAsync(key, upgradeKey, hwid);
                    Console.WriteLine("upgrade: " + up.StatusCode + " " + up.Status
                                      + " -> " + up.Product + " features=["
                                      + string.Join(",", up.Features ?? new System.Collections.Generic.List<string>())
                                      + "]");
                    if (up.Success)
                    {
                        client.SetToken(up.Token);
                        var advanced2 = await client.GetJsonAsync<FeatureResponse>("/api/v1/data/advanced");
                        Console.WriteLine("advanced after upgrade: " + advanced2.StatusCode + " "
                                          + (advanced2.Success ? "OK " + advanced2.Data : advanced2.Error));
                        var oldValid = await client.ValidateAsync(activation.Token, hwid);
                        Console.WriteLine("old token after upgrade: " + oldValid.StatusCode + " "
                                          + oldValid.Status);
                    }
                }
            }
        }
    }
}