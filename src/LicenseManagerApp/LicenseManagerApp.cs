using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using LicenseClient;

namespace LicenseManagerApp
{

    public sealed class FeatureGate
    {
        private readonly HashSet<string> _features = new HashSet<string>();

        public string Product { get; private set; }
        public bool IsActive { get; private set; }

        public void Apply(string product, List<string> features)
        {
            Product = product;
            IsActive = true;
            _features.Clear();
            if (features != null)
            {
                _features.UnionWith(features);
            }
        }

        public void Clear()
        {
            Product = null;
            IsActive = false;
            _features.Clear();
        }

        public bool IsEnabled(string feature) => _features.Contains(feature);
    }

    public sealed class ActivationDialog : Form
    {
        private readonly LicenseClient.LicenseClient _client;
        private readonly string _hwid;
        private readonly TextBox _keyBox = new TextBox();
        private readonly Label _infoLabel = new Label();
        private readonly Button _activateBtn = new Button();
        private readonly Button _cancelBtn = new Button();

        public string LicenseKey { get; private set; }

        public ActivationDialog(LicenseClient.LicenseClient client, string hwid)
        {
            _client = client;
            _hwid = hwid;

            Text = "Activate License";
            Width = 420;
            Height = 180;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            _infoLabel.Text = "No license found on this device. Enter your key "
                              + "(XXXXX-XXXXX-XXXXX-XXXXX-X) to activate.";
            _infoLabel.Location = new Point(12, 12);
            _infoLabel.Size = new Size(380, 40);

            _keyBox.Location = new Point(12, 60);
            _keyBox.Width = 380;
            _keyBox.Font = new Font(Font.FontFamily, 11f);

            _activateBtn.Text = "Activate";
            _activateBtn.Location = new Point(12, 95);
            _activateBtn.Width = 110;
            _activateBtn.Click += async (s, e) => await ActivateAsync();

            _cancelBtn.Text = "Cancel";
            _cancelBtn.Location = new Point(132, 95);
            _cancelBtn.Width = 90;
            _cancelBtn.DialogResult = DialogResult.Cancel;

            var hint = new Label
            {
                Text = "Network problems? Check the connection and try again.",
                Location = new Point(12, 122),
                Size = new Size(380, 20),
                ForeColor = Color.Gray
            };

            Controls.AddRange(new Control[]
            {
                _infoLabel, _keyBox, _activateBtn, _cancelBtn, hint
            });
            AcceptButton = _activateBtn;
            CancelButton = _cancelBtn;
        }

        private async Task ActivateAsync()
        {
            var key = _keyBox.Text.Trim();
            if (key.Length == 0)
            {
                _infoLabel.ForeColor = Color.Firebrick;
                _infoLabel.Text = "Please enter your license key.";
                return;
            }
            _activateBtn.Enabled = false;
            _infoLabel.ForeColor = Color.Gray;
            _infoLabel.Text = "Activating...";
            try
            {
                var resp = await _client.ActivateAsync(key, _hwid);
                if (resp.StatusCode == 429)
                {
                    var wait = resp.RetryAfterSeconds ?? 60;
                    _infoLabel.ForeColor = Color.Firebrick;
                    _infoLabel.Text = "Too many attempts. Retry in "
                                      + wait + " seconds.";
                }
                else if (resp.Success)
                {
                    LicenseKey = key;
                    DialogResult = DialogResult.OK;
                    Close();
                }
                else
                {
                    _infoLabel.ForeColor = Color.Firebrick;
                    _infoLabel.Text = "Activation failed: "
                                      + DescribeError(resp.StatusCode, resp.Error,
                                                      resp.Status);
                }
            }
            catch (LicenseNetworkException exc)
            {
                _infoLabel.ForeColor = Color.Firebrick;
                _infoLabel.Text = "Network error: " + exc.Message;
            }
            finally
            {
                _activateBtn.Enabled = true;
            }
        }

        private static string DescribeError(int code, string error, string status)
        {
            if (code == 401) return "session invalid - activate again";
            if (code == 403 && status == "device_mismatch")
                return "key is bound to another device";
            if (status == "revoked") return "license has been revoked";
            if (status == "expired") return "license expired";
            if (code == 404) return "invalid key";
            return error ?? "unknown error (" + code + ")";
        }
    }

    public sealed class UpgradeDialog : Form
    {
        private readonly LicenseClient.LicenseClient _client;
        private readonly string _currentKey;
        private readonly string _hwid;
        private readonly TextBox _keyBox = new TextBox();
        private readonly Label _infoLabel = new Label();
        private readonly Button _upgradeBtn = new Button();
        private readonly Button _cancelBtn = new Button();

        public string LicenseKey { get; private set; }
        public UpgradeResponse Result { get; private set; }

        public UpgradeDialog(LicenseClient.LicenseClient client,
                             string currentKey, string hwid)
        {
            _client = client;
            _currentKey = currentKey;
            _hwid = hwid;

            Text = "Upgrade License";
            Width = 420;
            Height = 180;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            _infoLabel.Text = "Enter your new license key. The current license "
                              + "will be replaced on this device.";
            _infoLabel.Location = new Point(12, 12);
            _infoLabel.Size = new Size(380, 40);

            _keyBox.Location = new Point(12, 60);
            _keyBox.Width = 380;
            _keyBox.Font = new Font(Font.FontFamily, 11f);

            _upgradeBtn.Text = "Upgrade";
            _upgradeBtn.Location = new Point(12, 95);
            _upgradeBtn.Width = 110;
            _upgradeBtn.Click += async (s, e) => await UpgradeAsync();

            _cancelBtn.Text = "Cancel";
            _cancelBtn.Location = new Point(132, 95);
            _cancelBtn.Width = 90;
            _cancelBtn.DialogResult = DialogResult.Cancel;

            var hint = new Label
            {
                Text = "Your current session stays intact until the upgrade succeeds.",
                Location = new Point(12, 122),
                Size = new Size(380, 20),
                ForeColor = Color.Gray
            };

            Controls.AddRange(new Control[]
            {
                _infoLabel, _keyBox, _upgradeBtn, _cancelBtn, hint
            });
            AcceptButton = _upgradeBtn;
            CancelButton = _cancelBtn;
        }

        private async Task UpgradeAsync()
        {
            var key = _keyBox.Text.Trim();
            if (key.Length == 0)
            {
                _infoLabel.ForeColor = Color.Firebrick;
                _infoLabel.Text = "Please enter your new license key.";
                return;
            }
            if (key == _currentKey)
            {
                _infoLabel.ForeColor = Color.Firebrick;
                _infoLabel.Text = "That is the key already in use on this device.";
                return;
            }
            _upgradeBtn.Enabled = false;
            _infoLabel.ForeColor = Color.Gray;
            _infoLabel.Text = "Upgrading...";
            try
            {
                var resp = await _client.UpgradeAsync(_currentKey, key, _hwid);
                if (resp.StatusCode == 429)
                {
                    var wait = resp.RetryAfterSeconds ?? 60;
                    _infoLabel.ForeColor = Color.Firebrick;
                    _infoLabel.Text = "Too many attempts. Retry in " + wait
                                      + " seconds.";
                }
                else if (resp.Success)
                {
                    LicenseKey = key;
                    Result = resp;
                    DialogResult = DialogResult.OK;
                    Close();
                }
                else
                {
                    _infoLabel.ForeColor = Color.Firebrick;
                    _infoLabel.Text = "Upgrade failed: " + DescribeError(resp);
                }
            }
            catch (LicenseNetworkException exc)
            {
                _infoLabel.ForeColor = Color.Firebrick;
                _infoLabel.Text = "Network error: " + exc.Message;
            }
            finally
            {
                _upgradeBtn.Enabled = true;
            }
        }

        private static string DescribeError(UpgradeResponse resp)
        {
            if (resp.StatusCode == 401) return "session proof invalid - reactivate first";
            if (resp.StatusCode == 403 && resp.Status == "device_locked")
                return "new key is already bound to another device";
            if (resp.StatusCode == 403 && resp.Status == "revoked")
                return "new key has been revoked";
            if (resp.StatusCode == 403 && resp.Status == "expired")
                return "new key has expired";
            if (resp.StatusCode == 404) return "invalid key";
            return resp.Error ?? "unknown error (" + resp.StatusCode + ")";
        }
    }

    public sealed class LicenseForm : Form
    {
        private readonly LicenseClient.LicenseClient _client;
        private readonly FeatureGate _gate = new FeatureGate();
        private readonly string _hwid;
        private readonly string _keyFile;
        private readonly string _endpointFile;
        private readonly TextBox _keyBox = new TextBox();
        private readonly Button _activateBtn = new Button();
        private readonly Button _refreshBtn = new Button();
        private readonly Button _deactivateBtn = new Button();
        private readonly Button _upgradeBtn = new Button();
        private readonly Button _featureABtn = new Button();
        private readonly Button _featureBBtn = new Button();
        private readonly ToolStripStatusLabel _licenseStatus = new ToolStripStatusLabel();
        private readonly Timer _countdownTimer = new Timer();
        private readonly Timer _autoRefreshTimer = new Timer();
        private string _token;
        private DateTime _retryUntil;
        private bool _modalOpen;

        public LicenseForm()
        {
            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LicenseManager");
            _endpointFile = Path.Combine(configDir, "endpoint.txt");
            var endpoint = File.Exists(_endpointFile)
                ? File.ReadAllText(_endpointFile).Trim()
                : "https://licensing-live.onrender.com";
            _client = new LicenseClient.LicenseClient(endpoint);
            _hwid = HardwareIdentity.ComputeHwidHash();
            _keyFile = Path.Combine(configDir, "key.txt");

            BuildUi();
            _countdownTimer.Interval = 500;
            _countdownTimer.Tick += OnCountdownTick;
            _autoRefreshTimer.Interval = 5 * 60 * 1000;
            _autoRefreshTimer.Tick += async (s, e) => await RefreshAsync();
            Load += async (s, e) => await OnStartupAsync();
        }

        private void BuildUi()
        {
            Text = "License Manager";
            Width = 460;
            Height = 235;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            var keyLabel = new Label
            {
                Text = "License key:", Location = new Point(12, 15), AutoSize = true
            };
            _keyBox.Location = new Point(95, 12);
            _keyBox.Width = 255;

            _activateBtn.Text = "Activate";
            _activateBtn.Location = new Point(360, 11);
            _activateBtn.Width = 80;
            _activateBtn.Click += async (s, e) => await ActivateAsync();

            _refreshBtn.Text = "Refresh License";
            _refreshBtn.Location = new Point(12, 45);
            _refreshBtn.Width = 110;
            _refreshBtn.Enabled = false;
            _refreshBtn.Click += async (s, e) => await RefreshAsync();

            _deactivateBtn.Text = "Deactivate Device";
            _deactivateBtn.Location = new Point(132, 45);
            _deactivateBtn.Width = 130;
            _deactivateBtn.Enabled = false;
            _deactivateBtn.Click += async (s, e) => await DeactivateAsync();

            _upgradeBtn.Text = "Upgrade Key";
            _upgradeBtn.Location = new Point(272, 45);
            _upgradeBtn.Width = 110;
            _upgradeBtn.Enabled = false;
            _upgradeBtn.Click += async (s, e) => await UpgradeKeyAsync();

            _featureABtn.Text = "Launch Feature A";
            _featureABtn.Location = new Point(12, 82);
            _featureABtn.Width = 140;
            _featureABtn.Enabled = false;
            _featureABtn.Click += async (s, e) =>
                await LaunchFeatureAsync("/api/v1/data/summary", "Feature A");

            _featureBBtn.Text = "Launch Feature B";
            _featureBBtn.Location = new Point(162, 82);
            _featureBBtn.Width = 140;
            _featureBBtn.Enabled = false;
            _featureBBtn.Click += async (s, e) =>
                await LaunchFeatureAsync("/api/v1/data/advanced", "Feature B");

            var hwidLabel = new Label
            {
                Text = "Device: " + Environment.MachineName + "  (HWID "
                       + _hwid.Substring(0, 12) + "...)",
                Location = new Point(12, 118), AutoSize = true, ForeColor = Color.Gray
            };

            var statusStrip = new StatusStrip();
            statusStrip.Items.Add(_licenseStatus);
            statusStrip.SizingGrip = false;

            Controls.AddRange(new Control[]
            {
                keyLabel, _keyBox, _activateBtn, _refreshBtn, _deactivateBtn,
                _upgradeBtn, _featureABtn, _featureBBtn, hwidLabel, statusStrip
            });

            SetStatus("Not activated - enter a key or press Activate.");
            UpdateFeatureButtons();
        }

        private async Task OnStartupAsync()
        {
            if (!File.Exists(_keyFile))
            {
                ShowActivationModal();
                return;
            }
            _keyBox.Text = File.ReadAllText(_keyFile).Trim();
            await ActivateAsync();
        }

        private void ShowActivationModal()
        {
            if (_modalOpen) return;
            _modalOpen = true;
            try
            {
                using (var dialog = new ActivationDialog(_client, _hwid))
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        _keyBox.Text = dialog.LicenseKey;
                        File.WriteAllText(_keyFile, dialog.LicenseKey);
                        SetStatus("Activated - checking server...");
                        ActivateAsync().Wait();
                    }
                    else
                    {
                        SetStatus("Not activated - features are locked.");
                    }
                }
            }
            finally
            {
                _modalOpen = false;
            }
        }

        private async Task ActivateAsync()
        {
            var key = _keyBox.Text.Trim();
            if (key.Length == 0)
            {
                SetStatus("Enter a license key.");
                return;
            }
            SetBusy(true, "Activating...");
            try
            {
                var resp = await _client.ActivateAsync(key, _hwid);
                if (resp.StatusCode == 429)
                {
                    StartRateLimitCountdown(resp.RetryAfterSeconds ?? 60);
                    return;
                }
                if (resp.Success)
                {
                    _token = resp.Token;
                    _client.SetToken(resp.Token);
                    File.WriteAllText(_keyFile, key);
                    _gate.Apply(resp.Product, resp.Features);
                    SetStatus("ACTIVATED: " + resp.Product + " - "
                              + DescribeExpiry(resp.ExpiresAt));
                    _refreshBtn.Enabled = true;
                    _deactivateBtn.Enabled = true;
                    _autoRefreshTimer.Start();
                    UpdateFeatureButtons();
                }
                else
                {
                    SetStatus("ACTIVATION FAILED: "
                              + DescribeError(resp.StatusCode, resp.Error, resp.Status));
                }
            }
            catch (LicenseNetworkException exc)
            {
                SetStatus("OFFLINE: " + exc.Message);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private async Task RefreshAsync()
        {
            if (_token == null) return;
            SetBusy(true, "Validating...");
            try
            {
                var resp = await _client.ValidateAsync(_token, _hwid);
                if (resp.StatusCode == 429)
                {
                    StartRateLimitCountdown(resp.RetryAfterSeconds ?? 60);
                    return;
                }
                if (resp.Success)
                {
                    _gate.Apply(resp.Product, resp.Features);
                    SetStatus("LICENSED to " + Environment.MachineName + " - "
                              + DescribeExpiry(resp.ExpiresAt));
                    _autoRefreshTimer.Start();
                    UpdateFeatureButtons();
                }
                else if (resp.Status == "expired")
                {
                    RevokeSession();
                    SetStatus("EXPIRED: license expired on " + resp.ExpiresAtIso);
                    MessageBox.Show(
                        "Your license expired on " + resp.ExpiresAtIso
                        + ". Please renew to continue using this product.",
                        "License expired", MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                else
                {
                    RevokeSession();
                    SetStatus("INVALID: "
                              + DescribeError(resp.StatusCode, resp.Error, resp.Status));
                }
            }
            catch (LicenseNetworkException exc)
            {
                SetStatus("OFFLINE (features stay cached): " + exc.Message);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private async Task LaunchFeatureAsync(string path, string label)
        {
            SetBusy(true, "Requesting " + label + "...");
            try
            {
                var resp = await _client.GetJsonAsync<FeatureResponse>(path);
                if (resp.StatusCode == 401 || resp.StatusCode == 403)
                {
                    await HandleProtectedUnauthorizedAsync(path, label);
                    return;
                }
                if (resp.Success)
                {
                    SetStatus(label + " served: " + resp.Data);
                    MessageBox.Show("Server returned: " + resp.Data,
                                    label + " - licensed", MessageBoxButtons.OK,
                                    MessageBoxIcon.Information);
                }
                else
                {
                    SetStatus(label + " denied: " + resp.Error);
                }
            }
            catch (LicenseNetworkException exc)
            {
                SetStatus("OFFLINE: " + exc.Message);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private async Task HandleProtectedUnauthorizedAsync(string path, string label)
        {
            SetStatus(label + " denied - re-validating license...");
            await RefreshAsync();
            if (_token != null)
            {
                var retry = await _client.GetJsonAsync<FeatureResponse>(path);
                if (retry.Success)
                {
                    SetStatus(label + " served after refresh: " + retry.Data);
                    return;
                }
                SetStatus(label + " denied: " + retry.Error);
                return;
            }
            SetStatus("License invalidated remotely - please activate again.");
            ShowActivationModal();
        }

        private async Task UpgradeKeyAsync()
        {
            if (_token == null && !File.Exists(_keyFile))
            {
                SetStatus("No active license to upgrade.");
                return;
            }
            var currentKey = File.Exists(_keyFile)
                ? File.ReadAllText(_keyFile).Trim() : _keyBox.Text.Trim();
            using (var dialog = new UpgradeDialog(_client, currentKey, _hwid))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                ApplyUpgrade(dialog);
            }
        }

        private void ApplyUpgrade(UpgradeDialog dialog)
        {
            SaveKeyAtomic(dialog.LicenseKey);
            _keyBox.Text = dialog.LicenseKey;
            _token = dialog.Result.Token;
            _client.SetToken(_token);
            _gate.Apply(dialog.Result.Product, dialog.Result.Features);
            SetStatus("UPGRADED: " + dialog.Result.Product + " - "
                      + DescribeExpiry(dialog.Result.ExpiresAt));
            _refreshBtn.Enabled = true;
            _deactivateBtn.Enabled = true;
            _autoRefreshTimer.Start();
            UpdateFeatureButtons();
            MessageBox.Show(
                "Upgraded to " + dialog.Result.Product + " from "
                + dialog.Result.Previous["product"]
                + ". New features are unlocked.",
                "License upgraded", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void SaveKeyAtomic(string key)
        {
            var tmp = _keyFile + ".tmp";
            File.WriteAllText(tmp, key);
            if (File.Exists(_keyFile))
            {
                File.Replace(tmp, _keyFile, null);
            }
            else
            {
                File.Move(tmp, _keyFile);
            }
        }

        private async Task DeactivateAsync()
        {
            if (_token == null && !File.Exists(_keyFile)) return;
            var confirm = MessageBox.Show(
                "Deactivate this device? This releases the license "
                + "for use on another machine.",
                "Deactivate Device", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            var key = File.Exists(_keyFile)
                ? File.ReadAllText(_keyFile).Trim() : _keyBox.Text.Trim();
            try
            {
                await _client.DeactivateAsync(key, _hwid);
            }
            catch (LicenseNetworkException exc)
            {
                SetStatus("Deactivation failed (offline): " + exc.Message);
            }
            RevokeSession();
            File.Delete(_keyFile);
            SetStatus("Device deactivated - license released.");
        }

        private void RevokeSession()
        {
            _token = null;
            _client.ClearToken();
            _gate.Clear();
            _refreshBtn.Enabled = false;
            _deactivateBtn.Enabled = false;
            _autoRefreshTimer.Stop();
            UpdateFeatureButtons();
        }

        private void UpdateFeatureButtons()
        {
            _featureABtn.Enabled = _gate.IsEnabled("feature_a");
            _featureBBtn.Enabled = _gate.IsEnabled("feature_b");
            if (_gate.IsActive && !_gate.IsEnabled("feature_a")
                              && !_gate.IsEnabled("feature_b"))
            {
                SetStatus("LICENSED (" + _gate.Product + ") - no features "
                          + "unlocked for this tier.");
            }
        }

        private static string DescribeExpiry(long expiresAt)
        {
            var days = (int)Math.Ceiling(
                (DateTimeOffset.FromUnixTimeSeconds(expiresAt) - DateTimeOffset.UtcNow)
                .TotalDays);
            var date = DateTimeOffset.FromUnixTimeSeconds(expiresAt)
                .ToLocalTime().ToString("yyyy-MM-dd");
            if (days <= 0) return "expires today (" + date + ")";
            if (days == 1) return "expires in 1 day (" + date + ")";
            return "expires in " + days + " days (" + date + ")";
        }

        private void StartRateLimitCountdown(int seconds)
        {
            _retryUntil = DateTime.UtcNow.AddSeconds(seconds);
            _countdownTimer.Start();
            SetBusy(true, "Rate limited, retry in " + seconds + "s...");
        }

        private void OnCountdownTick(object sender, EventArgs e)
        {
            var remaining = (int)(_retryUntil - DateTime.UtcNow).TotalSeconds;
            if (remaining <= 0)
            {
                _countdownTimer.Stop();
                SetBusy(false, null);
                SetStatus("Rate limit window elapsed - you can retry.");
                return;
            }
            SetStatus("Rate limited - retry in " + remaining + "s");
        }

        private static string DescribeError(int code, string error, string status)
        {
            if (code == 429) return "too many requests";
            if (code == 401) return "session invalid - activate again";
            if (code == 403 && status == "device_mismatch")
                return "key is bound to another device";
            if (status == "revoked") return "license has been revoked";
            if (status == "not_activated") return "no active activation on this device";
            if (status == "expired") return "license expired";
            if (status == "rate_limited") return "rate limited";
            return error ?? "unknown error (" + code + ")";
        }

        private void SetBusy(bool busy, string message)
        {
            _activateBtn.Enabled = !busy;
            if (message != null) SetStatus(message);
        }

        private void SetStatus(string text) => _licenseStatus.Text = text;
    }

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LicenseForm());
        }
    }
}