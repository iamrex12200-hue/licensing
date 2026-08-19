using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace uid_bypass;

public class Form1 : Form
{
	private struct SECURITY_ATTRIBUTES
	{
		public int nLength;

		public IntPtr lpSecurityDescriptor;

		public bool bInheritHandle;
	}

	private struct COORD
	{
		public short X;

		public short Y;

		public COORD(short x, short y)
		{
			X = x;
			Y = y;
		}
	}

	private struct SMALL_RECT
	{
		public short Left;

		public short Top;

		public short Right;

		public short Bottom;
	}

	private struct CONSOLE_SCREEN_BUFFER_INFO
	{
		public COORD dwSize;

		public COORD dwCursorPosition;

		public ushort wAttributes;

		public SMALL_RECT srWindow;

		public COORD dwMaximumWindowSize;
	}

	[StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
	private struct INPUT_RECORD
	{
		[FieldOffset(0)]
		public ushort EventType;

		[FieldOffset(4)]
		public bool KeyDown;

		[FieldOffset(8)]
		public ushort RepeatCount;

		[FieldOffset(10)]
		public ushort VirtualKeyCode;

		[FieldOffset(12)]
		public ushort VirtualScanCode;

		[FieldOffset(14)]
		public char UnicodeChar;

		[FieldOffset(16)]
		public uint ControlKeyState;
	}

	private struct AccentPolicy
	{
		public int AccentState;

		public int AccentFlags;

		public int GradientColor;

		public int AnimationId;
	}

	private struct WindowCompositionAttributeData
	{
		public int Attribute;

		public IntPtr Data;

		public int SizeOfData;
	}

	private const int STD_INPUT_HANDLE = -10;

	private const int STD_OUTPUT_HANDLE = -11;

	private const ushort KEY_EVENT = 1;

	private const int SW_HIDE = 0;

	private const string MenuPrompt = "> Choice:";

	private const string ChoosePrompt = "> Choose:";

	private const string IpPrompt = "Enter new IPv4";

	private Process _engine;

	private IntPtr _hIn;

	private IntPtr _hOut;

	private readonly Queue<string> _menuQueue = new Queue<string>();

	private readonly object _sync = new object();

	private CancellationTokenSource _cancel;

	private bool _shuttingDown;

	private int _restartAttempts;

	private bool _restartScheduled;

	private DateTime _engineStartedAt;

	private const string LicensingEndpoint = "https://licensing-live.onrender.com";

	private bool _promptHandledThisScreen;

	private string _lastScreen = string.Empty;

	private string _partialLine = string.Empty;

	private COORD _lastCursor;

	private COORD _lastSize;

	private string _emuIndex = "1";

	private string _ipAnswer = string.Empty;

	private string _autoEmuIndex;

	private const int WCA_ACCENT_POLICY = 19;

	private const int ACCENT_ENABLE_BLURBEHIND = 3;

	private static readonly object _logLock = new object();

	private static readonly string _logPath = Path.Combine(Path.GetTempPath(), "verox_uid_bypass.log");

	private string _lastLoggedLine = string.Empty;

	private IContainer components;

	private VeroXChromePanel pnlTitle;

	private Label lblBrand;

	private Label lblBrandSub;

	private Label lblSub;

	private VeroXButton btnClose;

	private VeroXButton btnMax;

	private VeroXButton btnMin;

	private VeroXPanel pnlEmulator;

	private StatusDot dotDetected;

	private Label lblDetected;

	private VeroXComboBox cbEmulator;

	private VeroXButton btnGrant;

	private Label lblHint;

	private VeroXPanel pnlBypass;

	private VeroXTextBox txtIp;

	private VeroXButton btnConnect;

	private VeroXButton btnDisconnect;

	private VeroXButton btnInstallCert;

	private VeroXButton btnRemoveCert;

	private VeroXPanel pnlLog;

	private VeroXButton btnClearLog;

	private VeroXButton btnCopyLog;

	private VeroXLogInnerPanel pnlLogInner;

	private RichTextBox rtxtLog;

	private Label lblLogEmpty;

	private VeroXChromePanel pnlStatus;

	private Label lblStatusLbl;

	private Label lblStatusVal;

	private Label lblConnLbl;

	private Label lblConnVal;

	private Label lblUidLbl;

	private Label lblUidVal;

	private Label lblEngineLbl;

	private Label lblEngineVal;

	private VeroXButton btnRestart;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool AllocConsole();

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool FreeConsole();

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr GetConsoleWindow();

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool WriteConsoleInput(IntPtr hConsoleInput, INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsWritten);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool ReadConsoleOutputCharacter(IntPtr hConsoleOutput, [Out] StringBuilder lpCharacter, uint nLength, COORD dwReadCoord, out uint lpNumberOfCharsRead);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetConsoleScreenBufferSize(IntPtr hConsoleOutput, COORD dwSize);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetConsoleOutputCP(uint wCodePageID);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetConsoleCP(uint wCodePageID);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	private static extern bool ReleaseCapture();

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

	private void TitleBar_MouseDown(object sender, MouseEventArgs e)
	{
		if (e.Button == MouseButtons.Left)
		{
			ReleaseCapture();
			SendMessage(base.Handle, 161u, new IntPtr(2), IntPtr.Zero);
		}
	}

	private void TitleBar_DoubleClick(object sender, MouseEventArgs e)
	{
		btnMax_Click(sender, EventArgs.Empty);
	}

	private void btnMin_Click(object sender, EventArgs e)
	{
		base.WindowState = FormWindowState.Minimized;
	}

	private void btnMax_Click(object sender, EventArgs e)
	{
		base.WindowState = ((base.WindowState != FormWindowState.Maximized) ? FormWindowState.Maximized : FormWindowState.Normal);
		UpdateMaximizeGlyph();
	}

	private void UpdateMaximizeGlyph()
	{
		if (btnMax != null)
		{
			btnMax.Glyph = ((base.WindowState == FormWindowState.Maximized) ? VeroXButton.WindowGlyphKind.Restore : VeroXButton.WindowGlyphKind.Maximize);
		}
	}

	protected override void OnResize(EventArgs e)
	{
		base.OnResize(e);
		UpdateMaximizeGlyph();
	}

	private void btnClose_Click(object sender, EventArgs e)
	{
		Close();
	}

	public Form1()
	{
		ServicePointManager.SecurityProtocol =
			SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11
			| SecurityProtocolType.Tls;
		SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, value: true);
		InitializeComponent();
		cbEmulator.SelectedIndex = 0;
		btnMin.Glyph = VeroXButton.WindowGlyphKind.Minimize;
		btnMax.Glyph = VeroXButton.WindowGlyphKind.Maximize;
		btnClose.Glyph = VeroXButton.WindowGlyphKind.Close;
		Control[] array = new Control[3] { lblBrand, lblBrandSub, lblSub };
		foreach (Control obj in array)
		{
			obj.MouseDown += TitleBar_MouseDown;
			obj.MouseDoubleClick += TitleBar_DoubleClick;
		}
		UpdateLogEmptyState();
	}

	protected override void OnHandleCreated(EventArgs e)
	{
		base.OnHandleCreated(e);
		base.MaximizedBounds = Screen.FromHandle(base.Handle).WorkingArea;
		EnableGlassBackdrop();
	}

	[DllImport("user32.dll")]
	private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

	private void EnableGlassBackdrop()
	{
		try
		{
			AccentPolicy accentPolicy = default(AccentPolicy);
			accentPolicy.AccentState = 3;
			accentPolicy.AccentFlags = 0;
			accentPolicy.GradientColor = -669639900;
			AccentPolicy structure = accentPolicy;
			int num = Marshal.SizeOf<AccentPolicy>();
			IntPtr intPtr = Marshal.AllocHGlobal(num);
			try
			{
				Marshal.StructureToPtr(structure, intPtr, fDeleteOld: false);
				WindowCompositionAttributeData windowCompositionAttributeData = default(WindowCompositionAttributeData);
				windowCompositionAttributeData.Attribute = 19;
				windowCompositionAttributeData.Data = intPtr;
				windowCompositionAttributeData.SizeOfData = num;
				WindowCompositionAttributeData data = windowCompositionAttributeData;
				SetWindowCompositionAttribute(base.Handle, ref data);
			}
			finally
			{
				Marshal.FreeHGlobal(intPtr);
			}
		}
		catch
		{
		}
	}

	protected override void OnShown(EventArgs e)
	{
		base.OnShown(e);
		BeginInvoke((Action)delegate
		{
			base.ClientSize = new Size(760, 752);
		});
	}

	protected override void OnPaintBackground(PaintEventArgs e)
	{
		CyberBackdrop.PaintScene(e.Graphics, base.ClientRectangle, base.Width, base.Height);
		using Pen pen = new Pen(Color.FromArgb(60, 27, 42, 53));
		e.Graphics.DrawRectangle(pen, 0, 0, base.Width - 1, base.Height - 1);
	}

	protected override void OnLoad(EventArgs e)
	{
		base.OnLoad(e);
		_ = BootEngineAsync();
	}

	private async Task BootEngineAsync()
	{
		AppendLog("[*] Checking licensing endpoint " + LicensingEndpoint
			+ " (TLS 1.2)...", Color.Gray);
		bool ok = await PingEndpointAsync();
		AppendLog(ok
			? "[+] Licensing endpoint reachable."
			: "[!] Endpoint unreachable - starting engine anyway; the "
				+ "licensing handshake will retry automatically.",
			ok ? Color.LimeGreen : Color.OrangeRed);
		StartEngine();
	}

	private async Task<bool> PingEndpointAsync()
	{
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			if (attempt > 1)
			{
				await Task.Delay(3000);
			}
			try
			{
				using (var client = new WebClient())
				{
					client.Headers.Add("User-Agent",
						"uid-bypass-launcher/1.0 (cold-start warmup)");
					client.Encoding = Encoding.UTF8;
					var body = await client.DownloadStringTaskAsync(
						LicensingEndpoint + "/healthz");
					if (body.IndexOf("ok", StringComparison.OrdinalIgnoreCase) >= 0
						|| body.IndexOf("healthy", StringComparison.OrdinalIgnoreCase) >= 0
						|| body.IndexOf("\"status\"", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						return true;
					}
				}
			}
			catch (Exception exc)
			{
				AppendLog("[!] Endpoint ping attempt " + attempt
					+ " failed: " + exc.Message, Color.DimGray);
			}
		}
		return false;
	}

	protected override void OnFormClosing(FormClosingEventArgs e)
	{
		_shuttingDown = true;
		StopEngine();
		base.OnFormClosing(e);
	}

	private void StartEngine()
	{
		StopEngine();
		_restartAttempts = 0;
		_restartScheduled = false;
		string text = Path.Combine(Path.GetTempPath(), "uid_bypass_engine");
		string text2 = Path.Combine(text, "UID_BYPASS.exe");
		try
		{
			Directory.CreateDirectory(text);
			ExtractEmbeddedResource("uid_bypass.Bypass.UID_BYPASS.exe", text2);
			ExtractEmbeddedResource("uid_bypass.Bypass.UIDBypassDll.dll", Path.Combine(text, "UIDBypassDll.dll"));
			File.WriteAllText(Path.Combine(text, "endpoint.txt"), LicensingEndpoint);
		}
		catch (Exception ex)
		{
			AppendLog("[!] Failed to extract engine files: " + ex.Message, Color.OrangeRed);
			SetEngineRunning(running: false);
			return;
		}
		if (!File.Exists(text2))
		{
			AppendLog("[!] UID_BYPASS.exe extraction failed.", Color.OrangeRed);
			SetEngineRunning(running: false);
			return;
		}
		try
		{
			EnsureConsole();
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = text2,
				WorkingDirectory = Path.GetDirectoryName(text2),
				UseShellExecute = false
			};
			startInfo.EnvironmentVariables["LIC_ENDPOINT"] = LicensingEndpoint;
			_engine = Process.Start(startInfo);
			_engine.EnableRaisingEvents = true;
			_engineStartedAt = DateTime.UtcNow;
			_engine.Exited += delegate
			{
				if (_shuttingDown)
				{
					return;
				}
				int exitCode = 0;
				try
				{
					exitCode = _engine.ExitCode;
				}
				catch
				{
				}
				double uptime = (DateTime.UtcNow - _engineStartedAt).TotalSeconds;
				BeginInvokeIfNeeded(delegate
				{
					AppendLog("[!] Engine exited (exit code " + exitCode
						+ ") after " + uptime.ToString("F1") + "s.", Color.OrangeRed);
					SetEngineRunning(running: false);
					if (uptime < 10.0)
					{
						ScheduleAutoRestart("startup failure (exit code "
							+ exitCode + ")");
					}
					else
					{
						AppendLog("[*] Engine ran normally and stopped. "
							+ "Press RESTART ENGINE to relaunch.", Color.Gray);
					}
				});
			};
			_lastScreen = string.Empty;
			_partialLine = string.Empty;
			_promptHandledThisScreen = false;
			_cancel = new CancellationTokenSource();
			Task.Run(delegate
			{
				ConsoleLoop(_cancel.Token);
			});
			SetEngineRunning(running: true);
			AppendLog("+------------------------------+", Color.FromArgb(0, 229, 255));
			AppendLog("[+] Bypass engine started. Use the buttons below.", Color.LimeGreen);
		}
		catch (Exception ex2)
		{
			AppendLog("[!] Failed to start engine: " + ex2.Message, Color.OrangeRed);
			SetEngineRunning(running: false);
		}
	}

	private void ScheduleAutoRestart(string reason)
	{
		if (_restartScheduled)
		{
			return;
		}
		_restartScheduled = true;
		if (_restartAttempts >= 3)
		{
			AppendLog("[!] Auto-restart exhausted after " + _restartAttempts
				+ " attempts (" + reason + "). Check " + _logPath
				+ " and your network, then press RESTART ENGINE.", Color.OrangeRed);
			return;
		}
		_restartAttempts++;
		int delay = 5 * _restartAttempts;
		AppendLog("[*] Auto-restart " + _restartAttempts
			+ "/3 in " + delay + "s (" + reason + ")...", Color.Gray);
		Task.Delay(TimeSpan.FromSeconds(delay)).ContinueWith(delegate
		{
			BeginInvokeIfNeeded(delegate
			{
				if (!_shuttingDown)
				{
					_restartScheduled = false;
					StartEngine();
				}
			});
		});
	}

	private void StopEngine()
	{
		if (_cancel != null)
		{
			_cancel.Cancel();
			_cancel = null;
		}
		Process engine = _engine;
		if (engine == null || engine.HasExited)
		{
			engine?.Dispose();
			_engine = null;
			return;
		}
		SendConsoleLine("0");
		if (engine.WaitForExit(4000))
		{
			engine.Dispose();
			_engine = null;
			return;
		}
		try
		{
			engine.Kill();
			engine.WaitForExit();
		}
		catch
		{
		}
		engine.Dispose();
		_engine = null;
	}

	private static void ExtractEmbeddedResource(string resourceName, string destinationPath)
	{
		using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
		if (stream == null)
		{
			throw new FileNotFoundException("Embedded resource not found: " + resourceName);
		}
		using FileStream destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write);
		stream.CopyTo(destination);
	}

	private void EnsureConsole()
	{
		if (GetConsoleWindow() != IntPtr.Zero)
		{
			FreeConsole();
		}
		AllocConsole();
		IntPtr consoleWindow = GetConsoleWindow();
		if (consoleWindow != IntPtr.Zero)
		{
			ShowWindow(consoleWindow, 0);
		}
		SECURITY_ATTRIBUTES sECURITY_ATTRIBUTES = default(SECURITY_ATTRIBUTES);
		sECURITY_ATTRIBUTES.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
		sECURITY_ATTRIBUTES.bInheritHandle = true;
		SECURITY_ATTRIBUTES structure = sECURITY_ATTRIBUTES;
		IntPtr intPtr = Marshal.AllocHGlobal(Marshal.SizeOf(structure));
		try
		{
			Marshal.StructureToPtr(structure, intPtr, fDeleteOld: false);
			_hIn = CreateFile("CONIN$", 3221225472u, 7u, intPtr, 3u, 0u, IntPtr.Zero);
			_hOut = CreateFile("CONOUT$", 3221225472u, 7u, intPtr, 3u, 0u, IntPtr.Zero);
		}
		finally
		{
			Marshal.FreeHGlobal(intPtr);
		}
		SetStdHandle(-10, _hIn);
		SetStdHandle(-11, _hOut);
		SetStdHandle(-12, _hOut);
		SetConsoleOutputCP(65001u);
		SetConsoleCP(65001u);
		SetConsoleScreenBufferSize(_hOut, new COORD(120, 2000));
		Console.Title = "UID Bypass Engine";
	}

	private void ConsoleLoop(CancellationToken token)
	{
		while (!token.IsCancellationRequested && _engine != null && !_engine.HasExited)
		{
			try
			{
				if (!GetConsoleScreenBufferInfo(_hOut, out var lpConsoleScreenBufferInfo))
				{
					Thread.Sleep(200);
					continue;
				}
				bool num = lpConsoleScreenBufferInfo.dwCursorPosition.X != _lastCursor.X || lpConsoleScreenBufferInfo.dwCursorPosition.Y != _lastCursor.Y;
				bool flag = lpConsoleScreenBufferInfo.dwSize.X != _lastSize.X || lpConsoleScreenBufferInfo.dwSize.Y != _lastSize.Y;
				if (!num && !flag)
				{
					Thread.Sleep(200);
					continue;
				}
				_lastCursor = lpConsoleScreenBufferInfo.dwCursorPosition;
				_lastSize = lpConsoleScreenBufferInfo.dwSize;
				int num2 = lpConsoleScreenBufferInfo.dwSize.X;
				int num3 = lpConsoleScreenBufferInfo.dwSize.Y;
				StringBuilder stringBuilder = new StringBuilder(num2 * num3 + 16);
				uint lpNumberOfCharsRead = 0u;
				if (!ReadConsoleOutputCharacter(_hOut, stringBuilder, (uint)(num2 * num3), new COORD(0, 0), out lpNumberOfCharsRead))
				{
					Thread.Sleep(200);
					continue;
				}
				string current = BuildText(stringBuilder.ToString(), num2);
				ProcessScreen(current);
			}
			catch
			{
				Thread.Sleep(200);
			}
			Thread.Sleep(120);
		}
	}

	private static string BuildText(string raw, int width)
	{
		raw = raw.TrimEnd(default(char));
		StringBuilder stringBuilder = new StringBuilder(raw.Length + raw.Length / width + 8);
		for (int i = 0; i < raw.Length; i += width)
		{
			int length = Math.Min(width, raw.Length - i);
			string text = raw.Substring(i, length).TrimEnd(' ');
			if (text.Length > 0)
			{
				stringBuilder.AppendLine(text);
			}
		}
		return stringBuilder.ToString();
	}

	private void ProcessScreen(string current)
	{
		if (current == _lastScreen)
		{
			return;
		}
		string lastScreen = _lastScreen;
		_lastScreen = current;
		int i = 0;
		for (int num = Math.Min(lastScreen.Length, current.Length); i < num && lastScreen[i] == current[i]; i++)
		{
		}
		if (current.Length < lastScreen.Length)
		{
			if (_partialLine.Length > 0)
			{
				AppendLog(_partialLine, Color.DimGray);
				_partialLine = string.Empty;
			}
			AppendLog("-------- console cleared --------", Color.DimGray);
			_promptHandledThisScreen = false;
		}
		string text = current.Substring(i);
		int num2 = text.LastIndexOf('\n');
		if (num2 >= 0)
		{
			string[] array = (_partialLine + text.Substring(0, num2 + 1)).Replace("\r\n", "\n").Split('\n');
			foreach (string text2 in array)
			{
				if (text2.Length != 0 && !IsBannerLine(text2) && !IsMenuOptionLine(text2) && !(text2 == _lastLoggedLine))
				{
					_lastLoggedLine = text2;
					AppendLog(text2, Color.FromArgb(198, 220, 238));
					HandleStatusLine(text2);
				}
			}
			_partialLine = string.Empty;
		}
		_partialLine += text.Substring(num2 + 1);
		ScanPrompts(current);
	}

	private static bool IsBannerLine(string line)
	{
		if (line.Length < 4)
		{
			return false;
		}
		int num = 0;
		for (int i = 0; i < line.Length; i++)
		{
			if (line[i] > '~')
			{
				num++;
			}
		}
		if (num >= 5)
		{
			return num * 2 >= line.Length;
		}
		return false;
	}

	private static bool IsMenuOptionLine(string line)
	{
		int num = 0;
		for (int i = 0; i < line.Length; i++)
		{
			if (line[i] == '[')
			{
				int j;
				for (j = i + 1; j < line.Length && char.IsDigit(line[j]); j++)
				{
				}
				if (j > i + 1 && j < line.Length && line[j] == ']')
				{
					num++;
					i = j;
				}
			}
		}
		return num >= 2;
	}

	private void HandleStatusLine(string line)
	{
if (line.IndexOf("Initialization Failed", StringComparison.OrdinalIgnoreCase) >= 0
			|| line.IndexOf("0x2F7", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			AppendLog("[!] UIDBypassDll initialization failed (0x2F7). "
				+ "The licensing handshake could not complete - see "
				+ _logPath + " for details.", Color.OrangeRed);
			ScheduleAutoRestart("UIDBypassDll initialization failed (0x2F7)");
			return;
		}
		int num = line.IndexOf("STATUS:", StringComparison.Ordinal);
		if (num >= 0)
		{
			int num2 = line.IndexOf("EMU:", num + 7, StringComparison.Ordinal);
			string text = ((num2 >= 0) ? line.Substring(num + 7, num2 - num - 7).Trim() : line.Substring(num + 7).Trim());
			UpdateEngineState(text);
		}
		int num3 = line.IndexOf("ADB:", StringComparison.Ordinal);
		if (num3 >= 0)
		{
			string text2 = line.Substring(num3 + 4).Trim();
			UpdateConnection(text2.IndexOf("ON", StringComparison.OrdinalIgnoreCase) >= 0);
		}
		int num4 = line.IndexOf("EMU:", StringComparison.Ordinal);
		if (num4 >= 0)
		{
			int num5 = line.IndexOf("ADB:", num4 + 4, StringComparison.Ordinal);
			string text3 = ((num5 >= 0) ? line.Substring(num4 + 4, num5 - num4 - 4).Trim() : line.Substring(num4 + 4).Trim());
			UpdateDetected((text3.Length == 0) ? "unknown" : text3, (text3.IndexOf("None", StringComparison.OrdinalIgnoreCase) >= 0) ? Color.FromArgb(255, 82, 82) : Color.FromArgb(0, 200, 83));
		}
	}

	private void UpdateEngineState(string state)
	{
		string label;
		Color color;
		if (state.IndexOf("Ready", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			label = "READY";
			color = CyberBackdrop.Success;
		}
		else if (state.IndexOf("Scan", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			label = "SCANNING";
			color = CyberBackdrop.Warning;
		}
		else if (state.IndexOf("Work", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			label = "WORKING";
			color = CyberBackdrop.AccentBlue;
		}
		else if (state.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			label = "ERROR";
			color = CyberBackdrop.Error;
		}
		else
		{
			label = "IDLE";
			color = CyberBackdrop.AccentCyan;
		}
		BeginInvokeIfNeeded(delegate
		{
			lblStatusVal.Text = label;
			lblStatusVal.ForeColor = color;
			lock (_logLock)
			{
				try
				{
					File.AppendAllText(_logPath, "[STATE] " + label + Environment.NewLine);
				}
				catch
				{
				}
			}
		});
	}

	private void UpdateConnection(bool online)
	{
		BeginInvokeIfNeeded(delegate
		{
			lblConnVal.Text = (online ? "ONLINE" : "OFFLINE");
			lblConnVal.ForeColor = (online ? CyberBackdrop.Success : CyberBackdrop.Error);
			lock (_logLock)
			{
				try
				{
					File.AppendAllText(_logPath, "[CONN] " + (online ? "ONLINE" : "OFFLINE") + Environment.NewLine);
				}
				catch
				{
				}
			}
		});
	}

	private void UpdateDetected(string target, Color color)
	{
		BeginInvokeIfNeeded(delegate
		{
			bool flag = target.Length == 0 || string.Equals(target, "unknown", StringComparison.OrdinalIgnoreCase) || target.IndexOf("None", StringComparison.OrdinalIgnoreCase) >= 0;
			Color color2 = (flag ? CyberBackdrop.Error : CyberBackdrop.Success);
			lblDetected.ForeColor = color2;
			lblDetected.Text = (flag ? "UNKNOWN" : "DETECTED");
			dotDetected.SetState(color2, pulse: false);
			if (target.IndexOf("BlueStacks", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				_autoEmuIndex = "1";
			}
			else if (target.IndexOf("MSI", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				_autoEmuIndex = "2";
			}
			else if (target.IndexOf("MEmu", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				_autoEmuIndex = "3";
			}
		});
	}

	private void ScanPrompts(string current)
	{
		if (_hIn == IntPtr.Zero)
		{
			return;
		}
		if (current.IndexOf("Enter new IPv4", StringComparison.Ordinal) >= 0 && !_promptHandledThisScreen)
		{
			_promptHandledThisScreen = true;
			SendConsoleLine(_ipAnswer);
			AppendLog(">>> IPv4: " + ((_ipAnswer.Length == 0) ? "(default)" : _ipAnswer), Color.FromArgb(0, 229, 255));
		}
		else if (current.IndexOf("> Choose:", StringComparison.Ordinal) >= 0 && !_promptHandledThisScreen)
		{
			_promptHandledThisScreen = true;
			SendConsoleLine(_emuIndex);
			AppendLog(">>> emulator: " + EmulatorLabel(), Color.FromArgb(0, 229, 255));
		}
		else if (current.IndexOf("> Choice:", StringComparison.Ordinal) >= 0)
		{
			string text = DequeueMenuCommand();
			if (text != null)
			{
				_promptHandledThisScreen = true;
				SendConsoleLine(text);
				AppendLog(">>> menu action: " + MenuLabel(text), Color.FromArgb(0, 229, 255));
			}
		}
	}

	private string EmulatorLabel()
	{
		return _emuIndex switch
		{
			"1" => "BlueStacks NXT", 
			"2" => "MSI App Player", 
			"3" => "MEmu Player", 
			_ => _emuIndex, 
		};
	}

	private string MenuLabel(string cmd)
	{
		return cmd switch
		{
			"1" => "Emulator Access", 
			"2" => "Connect Bypass", 
			"3" => "Disconnect", 
			"4" => "Install Cert", 
			"5" => "Remove Cert", 
			"0" => "Leave / Exit", 
			_ => cmd, 
		};
	}

	private string DequeueMenuCommand()
	{
		lock (_sync)
		{
			if (_menuQueue.Count == 0)
			{
				return null;
			}
			return _menuQueue.Dequeue();
		}
	}

	private void QueueMenuCommand(string cmd)
	{
		lock (_sync)
		{
			_menuQueue.Enqueue(cmd);
		}
		if (_lastScreen.Length > 0)
		{
			ScanPrompts(_lastScreen);
		}
	}

	private void SendConsoleLine(string line)
	{
		if (!(_hIn == IntPtr.Zero))
		{
			List<INPUT_RECORD> list = new List<INPUT_RECORD>(line.Length * 4 + 4);
			foreach (char c in line)
			{
				AddKey(list, c);
			}
			AddKey(list, '\r');
			uint lpNumberOfEventsWritten = 0u;
			WriteConsoleInput(_hIn, list.ToArray(), (uint)list.Count, out lpNumberOfEventsWritten);
		}
	}

	private static void AddKey(List<INPUT_RECORD> events, char c)
	{
		ushort virtualKeyCode = ((c == '\r') ? '\r' : c);
		events.Add(new INPUT_RECORD
		{
			EventType = 1,
			KeyDown = true,
			RepeatCount = 1,
			VirtualKeyCode = virtualKeyCode,
			UnicodeChar = c
		});
		events.Add(new INPUT_RECORD
		{
			EventType = 1,
			KeyDown = false,
			RepeatCount = 1,
			VirtualKeyCode = virtualKeyCode,
			UnicodeChar = c
		});
	}

	private void SetEngineRunning(bool running)
	{
		BeginInvokeIfNeeded(delegate
		{
			btnRestart.Visible = !running;
			btnGrant.Enabled = running;
			btnConnect.Enabled = running;
			btnDisconnect.Enabled = running;
			btnInstallCert.Enabled = running;
			btnRemoveCert.Enabled = running;
			cbEmulator.Enabled = running;
			txtIp.Enabled = running;
			if (!running)
			{
				lblStatusVal.Text = "ERROR";
				lblStatusVal.ForeColor = CyberBackdrop.Error;
				lblConnVal.Text = "OFFLINE";
				lblConnVal.ForeColor = CyberBackdrop.Error;
			}
			lblUidVal.Text = (running ? "READY" : "STANDBY");
			lblUidVal.ForeColor = (running ? CyberBackdrop.Success : CyberBackdrop.AccentCyan);
			lblEngineVal.Text = (running ? "READY" : "STANDBY");
			lblEngineVal.ForeColor = (running ? CyberBackdrop.Success : CyberBackdrop.AccentCyan);
		});
	}

	private void AppendLog(string text, Color color)
	{
		if (_shuttingDown)
		{
			return;
		}
		lock (_logLock)
		{
			try
			{
				File.AppendAllText(_logPath, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + Environment.NewLine);
			}
			catch
			{
			}
		}
		BeginInvokeIfNeeded(delegate
		{
			if (IsResultLine(text))
			{
				rtxtLog.Clear();
				UpdateLogEmptyState();
			}
			rtxtLog.SelectionStart = rtxtLog.TextLength;
			rtxtLog.SelectionLength = 0;
			rtxtLog.SelectionColor = color;
			rtxtLog.AppendText(text + Environment.NewLine);
			rtxtLog.SelectionStart = rtxtLog.TextLength;
			rtxtLog.ScrollToCaret();
			UpdateLogEmptyState();
		});
	}

	private static bool IsResultLine(string text)
	{
		if (!text.StartsWith("[!]", StringComparison.Ordinal))
		{
			return text.StartsWith("[+]", StringComparison.Ordinal);
		}
		return true;
	}

	private void UpdateLogEmptyState()
	{
		if (lblLogEmpty != null)
		{
			lblLogEmpty.Visible = rtxtLog.TextLength == 0;
		}
	}

	private void btnClearLog_Click(object sender, EventArgs e)
	{
		rtxtLog.Clear();
		UpdateLogEmptyState();
	}

	private void btnCopyLog_Click(object sender, EventArgs e)
	{
		if (rtxtLog.TextLength == 0)
		{
			return;
		}
		try
		{
			Clipboard.SetText(rtxtLog.Text);
			AppendLog("[*] Log copied to clipboard.", Color.Gray);
		}
		catch
		{
			AppendLog("[!] Failed to copy log to clipboard.", Color.OrangeRed);
		}
	}

	private void BeginInvokeIfNeeded(Action action)
	{
		if (base.IsDisposed || base.Disposing)
		{
			return;
		}
		if (base.InvokeRequired)
		{
			try
			{
				BeginInvoke(action);
				return;
			}
			catch
			{
				return;
			}
		}
		action();
	}

	private void btnGrant_Click(object sender, EventArgs e)
	{
		_emuIndex = ComputeEmulatorIndex();
		QueueMenuCommand("1");
		AppendLog("[*] Grant Emulator Access queued -> " + EmulatorLabel(), Color.Gray);
	}

	private void btnConnect_Click(object sender, EventArgs e)
	{
		string text = txtIp.Text.Trim();
		if (text.Length > 0 && !IPAddress.TryParse(text, out var _))
		{
			AppendLog("[!] Invalid IPv4 address. Leave blank for default.", Color.OrangeRed);
			return;
		}
		_ipAnswer = text;
		QueueMenuCommand("2");
		AppendLog("[*] Connect Bypass queued" + ((text.Length == 0) ? " (default IP)" : (" (" + text + ")")), Color.Gray);
	}

	private void btnDisconnect_Click(object sender, EventArgs e)
	{
		QueueMenuCommand("3");
		AppendLog("[*] Disconnect queued...", Color.Gray);
	}

	private void btnInstallCert_Click(object sender, EventArgs e)
	{
		QueueMenuCommand("4");
		AppendLog("[*] Install Cert queued...", Color.Gray);
	}

	private void btnRemoveCert_Click(object sender, EventArgs e)
	{
		QueueMenuCommand("5");
		AppendLog("[*] Remove Cert queued...", Color.Gray);
	}

	private void btnRestart_Click(object sender, EventArgs e)
	{
		_ = BootEngineAsync();
	}

	private string ComputeEmulatorIndex()
	{
		int selectedIndex = cbEmulator.SelectedIndex;
		if (selectedIndex <= 0)
		{
			return _autoEmuIndex ?? "1";
		}
		return selectedIndex.ToString();
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing && components != null)
		{
			components.Dispose();
		}
		base.Dispose(disposing);
	}

	private void InitializeComponent()
	{
		this.pnlTitle = new uid_bypass.VeroXChromePanel();
		this.lblBrand = new System.Windows.Forms.Label();
		this.lblBrandSub = new System.Windows.Forms.Label();
		this.lblSub = new System.Windows.Forms.Label();
		this.btnMin = new uid_bypass.VeroXButton();
		this.btnMax = new uid_bypass.VeroXButton();
		this.btnClose = new uid_bypass.VeroXButton();
		this.pnlEmulator = new uid_bypass.VeroXPanel();
		this.dotDetected = new uid_bypass.StatusDot();
		this.lblDetected = new System.Windows.Forms.Label();
		this.cbEmulator = new uid_bypass.VeroXComboBox();
		this.btnGrant = new uid_bypass.VeroXButton();
		this.lblHint = new System.Windows.Forms.Label();
		this.pnlBypass = new uid_bypass.VeroXPanel();
		this.txtIp = new uid_bypass.VeroXTextBox();
		this.btnConnect = new uid_bypass.VeroXButton();
		this.btnDisconnect = new uid_bypass.VeroXButton();
		this.btnInstallCert = new uid_bypass.VeroXButton();
		this.btnRemoveCert = new uid_bypass.VeroXButton();
		this.pnlLog = new uid_bypass.VeroXPanel();
		this.btnClearLog = new uid_bypass.VeroXButton();
		this.btnCopyLog = new uid_bypass.VeroXButton();
		this.pnlLogInner = new uid_bypass.VeroXLogInnerPanel();
		this.rtxtLog = new System.Windows.Forms.RichTextBox();
		this.lblLogEmpty = new System.Windows.Forms.Label();
		this.pnlStatus = new uid_bypass.VeroXChromePanel();
		this.lblStatusLbl = new System.Windows.Forms.Label();
		this.lblStatusVal = new System.Windows.Forms.Label();
		this.lblConnLbl = new System.Windows.Forms.Label();
		this.lblConnVal = new System.Windows.Forms.Label();
		this.lblUidLbl = new System.Windows.Forms.Label();
		this.lblUidVal = new System.Windows.Forms.Label();
		this.lblEngineLbl = new System.Windows.Forms.Label();
		this.lblEngineVal = new System.Windows.Forms.Label();
		this.btnRestart = new uid_bypass.VeroXButton();
		this.pnlTitle.SuspendLayout();
		this.pnlEmulator.SuspendLayout();
		this.pnlBypass.SuspendLayout();
		this.pnlLog.SuspendLayout();
		this.pnlLogInner.SuspendLayout();
		this.pnlStatus.SuspendLayout();
		base.SuspendLayout();
		this.pnlTitle.AccentBottom = true;
		this.pnlTitle.AccentTop = false;
		this.pnlTitle.BackColor = System.Drawing.Color.Transparent;
		this.pnlTitle.Controls.Add(this.lblBrand);
		this.pnlTitle.Controls.Add(this.lblBrandSub);
		this.pnlTitle.Controls.Add(this.lblSub);
		this.pnlTitle.Controls.Add(this.btnMin);
		this.pnlTitle.Controls.Add(this.btnMax);
		this.pnlTitle.Controls.Add(this.btnClose);
		this.pnlTitle.Dock = System.Windows.Forms.DockStyle.Top;
		this.pnlTitle.Location = new System.Drawing.Point(0, 0);
		this.pnlTitle.Name = "pnlTitle";
		this.pnlTitle.Size = new System.Drawing.Size(760, 64);
		this.pnlTitle.TabIndex = 0;
		this.pnlTitle.MouseDoubleClick += new System.Windows.Forms.MouseEventHandler(TitleBar_DoubleClick);
		this.pnlTitle.MouseDown += new System.Windows.Forms.MouseEventHandler(TitleBar_MouseDown);
		this.lblBrand.AutoSize = true;
		this.lblBrand.BackColor = System.Drawing.Color.Transparent;
		this.lblBrand.Font = new System.Drawing.Font("Segoe UI", 20f, System.Drawing.FontStyle.Bold);
		this.lblBrand.ForeColor = System.Drawing.Color.FromArgb(232, 247, 255);
		this.lblBrand.Location = new System.Drawing.Point(22, 6);
		this.lblBrand.Name = "lblBrand";
		this.lblBrand.Size = new System.Drawing.Size(104, 37);
		this.lblBrand.TabIndex = 1;
		this.lblBrand.Text = "VEROX";
		this.lblBrandSub.AutoSize = true;
		this.lblBrandSub.BackColor = System.Drawing.Color.Transparent;
		this.lblBrandSub.Font = new System.Drawing.Font("Segoe UI", 13f, System.Drawing.FontStyle.Bold);
		this.lblBrandSub.ForeColor = System.Drawing.Color.FromArgb(0, 217, 255);
		this.lblBrandSub.Location = new System.Drawing.Point(128, 18);
		this.lblBrandSub.Name = "lblBrandSub";
		this.lblBrandSub.Size = new System.Drawing.Size(116, 25);
		this.lblBrandSub.TabIndex = 2;
		this.lblBrandSub.Text = "UID ENGINE";
		this.lblSub.AutoSize = true;
		this.lblSub.BackColor = System.Drawing.Color.Transparent;
		this.lblSub.Font = new System.Drawing.Font("Segoe UI", 8f);
		this.lblSub.ForeColor = System.Drawing.Color.FromArgb(113, 136, 153);
		this.lblSub.Location = new System.Drawing.Point(22, 46);
		this.lblSub.Name = "lblSub";
		this.lblSub.Size = new System.Drawing.Size(168, 13);
		this.lblSub.TabIndex = 3;
		this.lblSub.Text = "UID BYPASS ENGINE  •  DESKTOP";
		this.btnMin.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
		this.btnMin.BackColor = System.Drawing.Color.Transparent;
		this.btnMin.BorderColor = System.Drawing.Color.FromArgb(32, 48, 62);
		this.btnMin.ColorBottom = System.Drawing.Color.FromArgb(15, 22, 31);
		this.btnMin.ColorTop = System.Drawing.Color.FromArgb(22, 31, 43);
		this.btnMin.Cursor = System.Windows.Forms.Cursors.Hand;
		this.btnMin.DangerHover = false;
		this.btnMin.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		this.btnMin.ForeColor = System.Drawing.Color.FromArgb(200, 220, 235);
		this.btnMin.GlowColor = System.Drawing.Color.FromArgb(0, 140, 255);
		this.btnMin.HoverBorderColor = System.Drawing.Color.FromArgb(0, 217, 255);
		this.btnMin.Location = new System.Drawing.Point(634, 15);
		this.btnMin.Name = "btnMin";
		this.btnMin.ShowGlow = true;
		this.btnMin.Size = new System.Drawing.Size(34, 34);
		this.btnMin.TabIndex = 5;
		this.btnMin.Text = "–";
		this.btnMin.UseVisualStyleBackColor = false;
		this.btnMin.Click += new System.EventHandler(btnMin_Click);
		this.btnMax.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
		this.btnMax.BackColor = System.Drawing.Color.Transparent;
		this.btnMax.BorderColor = System.Drawing.Color.FromArgb(32, 48, 62);
		this.btnMax.ColorBottom = System.Drawing.Color.FromArgb(15, 22, 31);
		this.btnMax.ColorTop = System.Drawing.Color.FromArgb(22, 31, 43);
		this.btnMax.Cursor = System.Windows.Forms.Cursors.Hand;
		this.btnMax.DangerHover = false;
		this.btnMax.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		this.btnMax.ForeColor = System.Drawing.Color.FromArgb(200, 220, 235);
		this.btnMax.GlowColor = System.Drawing.Color.FromArgb(0, 140, 255);
		this.btnMax.HoverBorderColor = System.Drawing.Color.FromArgb(0, 217, 255);
		this.btnMax.Location = new System.Drawing.Point(672, 15);
		this.btnMax.Name = "btnMax";
		this.btnMax.ShowGlow = true;
		this.btnMax.Size = new System.Drawing.Size(34, 34);
		this.btnMax.TabIndex = 6;
		this.btnMax.Text = "□";
		this.btnMax.UseVisualStyleBackColor = false;
		this.btnMax.Click += new System.EventHandler(btnMax_Click);
		this.btnClose.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
		this.btnClose.BackColor = System.Drawing.Color.Transparent;
		this.btnClose.BorderColor = System.Drawing.Color.FromArgb(50, 26, 38);
		this.btnClose.ColorBottom = System.Drawing.Color.FromArgb(15, 22, 31);
		this.btnClose.ColorTop = System.Drawing.Color.FromArgb(22, 31, 43);
		this.btnClose.Cursor = System.Windows.Forms.Cursors.Hand;
		this.btnClose.DangerHover = true;
		this.btnClose.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		this.btnClose.ForeColor = System.Drawing.Color.FromArgb(224, 228, 235);
		this.btnClose.GlowColor = System.Drawing.Color.FromArgb(255, 70, 92);
		this.btnClose.HoverBorderColor = System.Drawing.Color.FromArgb(255, 70, 92);
		this.btnClose.Location = new System.Drawing.Point(710, 15);
		this.btnClose.Name = "btnClose";
		this.btnClose.ShowGlow = true;
		this.btnClose.Size = new System.Drawing.Size(34, 34);
		this.btnClose.TabIndex = 7;
		this.btnClose.Text = "✕";
		this.btnClose.UseVisualStyleBackColor = false;
		this.btnClose.Click += new System.EventHandler(btnClose_Click);
		this.pnlEmulator.BackColor = System.Drawing.Color.Transparent;
		this.pnlEmulator.Controls.Add(this.dotDetected);
		this.pnlEmulator.Controls.Add(this.lblDetected);
		this.pnlEmulator.Controls.Add(this.cbEmulator);
		this.pnlEmulator.Controls.Add(this.btnGrant);
		this.pnlEmulator.Controls.Add(this.lblHint);
		this.pnlEmulator.Location = new System.Drawing.Point(16, 80);
		this.pnlEmulator.Name = "pnlEmulator";
		this.pnlEmulator.Size = new System.Drawing.Size(350, 208);
		this.pnlEmulator.TabIndex = 8;
		this.pnlEmulator.Title = "EMULATOR ACCESS";
		this.dotDetected.BackColor = System.Drawing.Color.Transparent;
		this.dotDetected.Location = new System.Drawing.Point(18, 52);
		this.dotDetected.Name = "dotDetected";
		this.dotDetected.Size = new System.Drawing.Size(12, 12);
		this.dotDetected.TabIndex = 9;
		this.dotDetected.Text = "dotDetected";
		this.lblDetected.AutoSize = true;
		this.lblDetected.BackColor = System.Drawing.Color.Transparent;
		this.lblDetected.Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold);
		this.lblDetected.ForeColor = System.Drawing.Color.FromArgb(255, 70, 92);
		this.lblDetected.Location = new System.Drawing.Point(38, 52);
		this.lblDetected.Name = "lblDetected";
		this.lblDetected.Size = new System.Drawing.Size(72, 15);
		this.lblDetected.TabIndex = 10;
		this.lblDetected.Text = "UNKNOWN";
		this.cbEmulator.BackColor = System.Drawing.Color.FromArgb(9, 13, 19);
		this.cbEmulator.DrawMode = System.Windows.Forms.DrawMode.OwnerDrawFixed;
		this.cbEmulator.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
		this.cbEmulator.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		this.cbEmulator.Font = new System.Drawing.Font("Segoe UI", 10f);
		this.cbEmulator.ForeColor = System.Drawing.Color.FromArgb(232, 247, 255);
		this.cbEmulator.ItemHeight = 26;
		this.cbEmulator.Items.AddRange(new object[4] { "Auto Detect", "BlueStacks NXT", "MSI App Player", "MEmu Player" });
		this.cbEmulator.Location = new System.Drawing.Point(18, 82);
		this.cbEmulator.Name = "cbEmulator";
		this.cbEmulator.Size = new System.Drawing.Size(314, 32);
		this.cbEmulator.TabIndex = 11;
		this.btnGrant.BackColor = System.Drawing.Color.Transparent;
		this.btnGrant.BorderColor = System.Drawing.Color.FromArgb(0, 96, 170);
		this.btnGrant.ColorBottom = System.Drawing.Color.FromArgb(0, 120, 210);
		this.btnGrant.ColorTop = System.Drawing.Color.FromArgb(0, 217, 255);
		this.btnGrant.Cursor = System.Windows.Forms.Cursors.Hand;
		this.btnGrant.DangerHover = false;
		this.btnGrant.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		this.btnGrant.Font = new System.Drawing.Font("Segoe UI", 9.75f, System.Drawing.FontStyle.Bold);
		this.btnGrant.ForeColor = System.Drawing.Color.White;
		this.btnGrant.GlowColor = System.Drawing.Color.FromArgb(0, 217, 255);
		this.btnGrant.HoverBorderColor = System.Drawing.Color.FromArgb(0, 217, 255);
		this.btnGrant.Location = new System.Drawing.Point(18, 122);
		this.btnGrant.Name = "btnGrant";
		this.btnGrant.ShowGlow = true;
		this.btnGrant.Size = new System.Drawing.Size(314, 40);
		this.btnGrant.TabIndex = 12;
		this.btnGrant.Text = "GRANT EMULATOR ACCESS";
		this.btnGrant.UseVisualStyleBackColor = false;
		this.btnGrant.Click += new System.EventHandler(btnGrant_Click);
		this.lblHint.AutoSize = true;
		this.lblHint.BackColor = System.Drawing.Color.Transparent;
		this.lblHint.Font = new System.Drawing.Font("Segoe UI", 8f);
		this.lblHint.ForeColor = System.Drawing.Color.FromArgb(113, 136, 153);
		this.lblHint.Location = new System.Drawing.Point(18, 172);
		this.lblHint.Name = "lblHint";
		this.lblHint.Size = new System.Drawing.Size(274, 13);
		this.lblHint.TabIndex = 21;
		this.lblHint.Text = "Select an emulator, then grant access to the engine.";
		this.pnlBypass.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
		this.pnlBypass.BackColor = System.Drawing.Color.Transparent;
		this.pnlBypass.Controls.Add(this.txtIp);
		this.pnlBypass.Controls.Add(this.btnConnect);
		this.pnlBypass.Controls.Add(this.btnDisconnect);
		this.pnlBypass.Controls.Add(this.btnInstallCert);
		this.pnlBypass.Controls.Add(this.btnRemoveCert);
		this.pnlBypass.Location = new System.Drawing.Point(378, 80);
		this.pnlBypass.Name = "pnlBypass";
		this.pnlBypass.Size = new System.Drawing.Size(366, 208);
		this.pnlBypass.TabIndex = 15;
		this.pnlBypass.Title = "BYPASS CONTROL";
		this.txtIp.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
		this.txtIp.BackColor = System.Drawing.Color.FromArgb(9, 13, 19);
		this.txtIp.BorderStyle = System.Windows.Forms.BorderStyle.None;
		this.txtIp.Font = new System.Drawing.Font("Segoe UI", 9.5f);
		this.txtIp.ForeColor = System.Drawing.Color.FromArgb(232, 247, 255);
		this.txtIp.Location = new System.Drawing.Point(18, 50);
		this.txtIp.Name = "txtIp";
		this.txtIp.PlaceholderText = "Optional IPv4 • leave blank for default";
		this.txtIp.Size = new System.Drawing.Size(330, 17);
		this.txtIp.TabIndex = 20;
		this.btnConnect.BackColor = System.Drawing.Color.Transparent;
		this.btnConnect.BorderColor = System.Drawing.Color.FromArgb(0, 96, 170);
		this.btnConnect.ColorBottom = System.Drawing.Color.FromArgb(0, 120, 210);
		this.btnConnect.ColorTop = System.Drawing.Color.FromArgb(0, 217, 255);
		this.btnConnect.Cursor = System.Windows.Forms.Cursors.Hand;
		this.btnConnect.DangerHover = false;
		this.btnConnect.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		this.btnConnect.Font = new System.Drawing.Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold);
		this.btnConnect.ForeColor = System.Drawing.Color.White;
		this.btnConnect.GlowColor = System.Drawing.Color.FromArgb(0, 217, 255);
		this.btnConnect.HoverBorderColor = System.Drawing.Color.FromArgb(0, 217, 255);
		this.btnConnect.Location = new System.Drawing.Point(18, 92);
		this.btnConnect.Name = "btnConnect";
		this.btnConnect.ShowGlow = true;
		this.btnConnect.Size = new System.Drawing.Size(159, 44);
		this.btnConnect.TabIndex = 16;
		this.btnConnect.Text = "CONNECT BYPASS";
		this.btnConnect.UseVisualStyleBackColor = false;
		this.btnConnect.Click += new System.EventHandler(btnConnect_Click);
		this.btnDisconnect.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
		this.btnDisconnect.BackColor = System.Drawing.Color.Transparent;
		this.btnDisconnect.BorderColor = System.Drawing.Color.FromArgb(32, 48, 62);
		this.btnDisconnect.ColorBottom = System.Drawing.Color.FromArgb(15, 22, 31);
		this.btnDisconnect.ColorTop = System.Drawing.Color.FromArgb(22, 31, 43);
		this.btnDisconnect.Cursor = System.Windows.Forms.Cursors.Hand;
		this.btnDisconnect.DangerHover = false;
		this.btnDisconnect.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		this.btnDisconnect.Font = new System.Drawing.Font("Segoe UI", 9.5f);
		this.btnDisconnect.ForeColor = System.Drawing.Color.FromArgb(220, 234, 245);
		this.btnDisconnect.GlowColor = System.Drawing.Color.FromArgb(0, 140, 255);
		this.btnDisconnect.HoverBorderColor = System.Drawing.Color.FromArgb(0, 217, 255);
		this.btnDisconnect.Location = new System.Drawing.Point(189, 92);
		this.btnDisconnect.Name = "btnDisconnect";
		this.btnDisconnect.ShowGlow = true;
		this.btnDisconnect.Size = new System.Drawing.Size(159, 44);
		this.btnDisconnect.TabIndex = 17;
		this.btnDisconnect.Text = "DISCONNECT";
		this.btnDisconnect.UseVisualStyleBackColor = false;
		this.btnDisconnect.Click += new System.EventHandler(btnDisconnect_Click);
		this.btnInstallCert.BackColor = System.Drawing.Color.Transparent;
		this.btnInstallCert.BorderColor = System.Drawing.Color.FromArgb(0, 96, 170);
		this.btnInstallCert.ColorBottom = System.Drawing.Color.FromArgb(0, 120, 210);
		this.btnInstallCert.ColorTop = System.Drawing.Color.FromArgb(0, 217, 255);
		this.btnInstallCert.Cursor = System.Windows.Forms.Cursors.Hand;
		this.btnInstallCert.DangerHover = false;
		this.btnInstallCert.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		this.btnInstallCert.Font = new System.Drawing.Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold);
		this.btnInstallCert.ForeColor = System.Drawing.Color.White;
		this.btnInstallCert.GlowColor = System.Drawing.Color.FromArgb(0, 217, 255);
		this.btnInstallCert.HoverBorderColor = System.Drawing.Color.FromArgb(0, 217, 255);
		this.btnInstallCert.Location = new System.Drawing.Point(18, 148);
		this.btnInstallCert.Name = "btnInstallCert";
		this.btnInstallCert.ShowGlow = true;
		this.btnInstallCert.Size = new System.Drawing.Size(159, 44);
		this.btnInstallCert.TabIndex = 18;
		this.btnInstallCert.Text = "INSTALL CERT";
		this.btnInstallCert.UseVisualStyleBackColor = false;
		this.btnInstallCert.Click += new System.EventHandler(btnInstallCert_Click);
		this.btnRemoveCert.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
		this.btnRemoveCert.BackColor = System.Drawing.Color.Transparent;
		this.btnRemoveCert.BorderColor = System.Drawing.Color.FromArgb(32, 48, 62);
		this.btnRemoveCert.ColorBottom = System.Drawing.Color.FromArgb(15, 22, 31);
		this.btnRemoveCert.ColorTop = System.Drawing.Color.FromArgb(22, 31, 43);
		this.btnRemoveCert.Cursor = System.Windows.Forms.Cursors.Hand;
		this.btnRemoveCert.DangerHover = false;
		this.btnRemoveCert.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		this.btnRemoveCert.Font = new System.Drawing.Font("Segoe UI", 9.5f);
		this.btnRemoveCert.ForeColor = System.Drawing.Color.FromArgb(220, 234, 245);
		this.btnRemoveCert.GlowColor = System.Drawing.Color.FromArgb(0, 140, 255);
		this.btnRemoveCert.HoverBorderColor = System.Drawing.Color.FromArgb(0, 217, 255);
		this.btnRemoveCert.Location = new System.Drawing.Point(189, 148);
		this.btnRemoveCert.Name = "btnRemoveCert";
		this.btnRemoveCert.ShowGlow = true;
		this.btnRemoveCert.Size = new System.Drawing.Size(159, 44);
		this.btnRemoveCert.TabIndex = 19;
		this.btnRemoveCert.Text = "REMOVE CERT";
		this.btnRemoveCert.UseVisualStyleBackColor = false;
		this.btnRemoveCert.Click += new System.EventHandler(btnRemoveCert_Click);
		this.pnlLog.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
		this.pnlLog.BackColor = System.Drawing.Color.Transparent;
		this.pnlLog.Controls.Add(this.btnClearLog);
		this.pnlLog.Controls.Add(this.btnCopyLog);
		this.pnlLog.Controls.Add(this.pnlLogInner);
		this.pnlLog.Location = new System.Drawing.Point(16, 300);
		this.pnlLog.Name = "pnlLog";
		this.pnlLog.Size = new System.Drawing.Size(728, 372);
		this.pnlLog.TabIndex = 20;
		this.pnlLog.Title = "ENGINE LOG";
		this.btnClearLog.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
		this.btnClearLog.BackColor = System.Drawing.Color.Transparent;
		this.btnClearLog.BorderColor = System.Drawing.Color.FromArgb(32, 48, 62);
		this.btnClearLog.ColorBottom = System.Drawing.Color.FromArgb(15, 22, 31);
		this.btnClearLog.ColorTop = System.Drawing.Color.FromArgb(22, 31, 43);
		this.btnClearLog.Cursor = System.Windows.Forms.Cursors.Hand;
		this.btnClearLog.DangerHover = false;
		this.btnClearLog.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		this.btnClearLog.Font = new System.Drawing.Font("Segoe UI", 8f);
		this.btnClearLog.ForeColor = System.Drawing.Color.FromArgb(180, 198, 212);
		this.btnClearLog.GlowColor = System.Drawing.Color.FromArgb(0, 140, 255);
		this.btnClearLog.HoverBorderColor = System.Drawing.Color.FromArgb(0, 217, 255);
		this.btnClearLog.Location = new System.Drawing.Point(592, 9);
		this.btnClearLog.Name = "btnClearLog";
		this.btnClearLog.ShowGlow = true;
		this.btnClearLog.Size = new System.Drawing.Size(56, 26);
		this.btnClearLog.TabIndex = 23;
		this.btnClearLog.Text = "CLEAR";
		this.btnClearLog.UseVisualStyleBackColor = false;
		this.btnClearLog.Click += new System.EventHandler(btnClearLog_Click);
		this.btnCopyLog.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
		this.btnCopyLog.BackColor = System.Drawing.Color.Transparent;
		this.btnCopyLog.BorderColor = System.Drawing.Color.FromArgb(32, 48, 62);
		this.btnCopyLog.ColorBottom = System.Drawing.Color.FromArgb(15, 22, 31);
		this.btnCopyLog.ColorTop = System.Drawing.Color.FromArgb(22, 31, 43);
		this.btnCopyLog.Cursor = System.Windows.Forms.Cursors.Hand;
		this.btnCopyLog.DangerHover = false;
		this.btnCopyLog.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		this.btnCopyLog.Font = new System.Drawing.Font("Segoe UI", 8f);
		this.btnCopyLog.ForeColor = System.Drawing.Color.FromArgb(180, 198, 212);
		this.btnCopyLog.GlowColor = System.Drawing.Color.FromArgb(0, 140, 255);
		this.btnCopyLog.HoverBorderColor = System.Drawing.Color.FromArgb(0, 217, 255);
		this.btnCopyLog.Location = new System.Drawing.Point(656, 9);
		this.btnCopyLog.Name = "btnCopyLog";
		this.btnCopyLog.ShowGlow = true;
		this.btnCopyLog.Size = new System.Drawing.Size(56, 26);
		this.btnCopyLog.TabIndex = 24;
		this.btnCopyLog.Text = "COPY";
		this.btnCopyLog.UseVisualStyleBackColor = false;
		this.btnCopyLog.Click += new System.EventHandler(btnCopyLog_Click);
		this.pnlLogInner.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
		this.pnlLogInner.BackColor = System.Drawing.Color.Transparent;
		this.pnlLogInner.Controls.Add(this.rtxtLog);
		this.pnlLogInner.Controls.Add(this.lblLogEmpty);
		this.pnlLogInner.Location = new System.Drawing.Point(16, 50);
		this.pnlLogInner.Name = "pnlLogInner";
		this.pnlLogInner.Size = new System.Drawing.Size(696, 306);
		this.pnlLogInner.TabIndex = 21;
		this.rtxtLog.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
		this.rtxtLog.BackColor = System.Drawing.Color.FromArgb(5, 8, 13);
		this.rtxtLog.BorderStyle = System.Windows.Forms.BorderStyle.None;
		this.rtxtLog.DetectUrls = false;
		this.rtxtLog.Font = new System.Drawing.Font("Consolas", 9.5f);
		this.rtxtLog.ForeColor = System.Drawing.Color.FromArgb(198, 220, 238);
		this.rtxtLog.HideSelection = false;
		this.rtxtLog.Location = new System.Drawing.Point(10, 10);
		this.rtxtLog.Name = "rtxtLog";
		this.rtxtLog.ReadOnly = true;
		this.rtxtLog.ScrollBars = System.Windows.Forms.RichTextBoxScrollBars.None;
		this.rtxtLog.Size = new System.Drawing.Size(676, 286);
		this.rtxtLog.TabIndex = 0;
		this.rtxtLog.Text = "";
		this.lblLogEmpty.BackColor = System.Drawing.Color.Transparent;
		this.lblLogEmpty.Dock = System.Windows.Forms.DockStyle.Fill;
		this.lblLogEmpty.Font = new System.Drawing.Font("Segoe UI", 9.5f);
		this.lblLogEmpty.ForeColor = System.Drawing.Color.FromArgb(80, 98, 116);
		this.lblLogEmpty.Location = new System.Drawing.Point(0, 0);
		this.lblLogEmpty.Name = "lblLogEmpty";
		this.lblLogEmpty.Size = new System.Drawing.Size(696, 306);
		this.lblLogEmpty.TabIndex = 1;
		this.lblLogEmpty.Text = "Waiting for engine activity...";
		this.lblLogEmpty.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
		this.pnlStatus.AccentBottom = false;
		this.pnlStatus.AccentTop = true;
		this.pnlStatus.BackColor = System.Drawing.Color.Transparent;
		this.pnlStatus.Controls.Add(this.lblStatusLbl);
		this.pnlStatus.Controls.Add(this.lblStatusVal);
		this.pnlStatus.Controls.Add(this.lblConnLbl);
		this.pnlStatus.Controls.Add(this.lblConnVal);
		this.pnlStatus.Controls.Add(this.lblUidLbl);
		this.pnlStatus.Controls.Add(this.lblUidVal);
		this.pnlStatus.Controls.Add(this.lblEngineLbl);
		this.pnlStatus.Controls.Add(this.lblEngineVal);
		this.pnlStatus.Controls.Add(this.btnRestart);
		this.pnlStatus.Dock = System.Windows.Forms.DockStyle.Bottom;
		this.pnlStatus.Location = new System.Drawing.Point(0, 688);
		this.pnlStatus.Name = "pnlStatus";
		this.pnlStatus.Size = new System.Drawing.Size(760, 64);
		this.pnlStatus.TabIndex = 22;
		this.lblStatusLbl.AutoSize = true;
		this.lblStatusLbl.BackColor = System.Drawing.Color.Transparent;
		this.lblStatusLbl.Font = new System.Drawing.Font("Segoe UI", 8f);
		this.lblStatusLbl.ForeColor = System.Drawing.Color.FromArgb(113, 136, 153);
		this.lblStatusLbl.Location = new System.Drawing.Point(22, 10);
		this.lblStatusLbl.Name = "lblStatusLbl";
		this.lblStatusLbl.Size = new System.Drawing.Size(42, 13);
		this.lblStatusLbl.TabIndex = 0;
		this.lblStatusLbl.Text = "STATUS";
		this.lblStatusVal.BackColor = System.Drawing.Color.Transparent;
		this.lblStatusVal.Font = new System.Drawing.Font("Segoe UI", 10f, System.Drawing.FontStyle.Bold);
		this.lblStatusVal.ForeColor = System.Drawing.Color.FromArgb(0, 217, 255);
		this.lblStatusVal.Location = new System.Drawing.Point(22, 28);
		this.lblStatusVal.Name = "lblStatusVal";
		this.lblStatusVal.Size = new System.Drawing.Size(84, 22);
		this.lblStatusVal.TabIndex = 1;
		this.lblStatusVal.Text = "IDLE";
		this.lblStatusVal.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
		this.lblConnLbl.AutoSize = true;
		this.lblConnLbl.BackColor = System.Drawing.Color.Transparent;
		this.lblConnLbl.Font = new System.Drawing.Font("Segoe UI", 8f);
		this.lblConnLbl.ForeColor = System.Drawing.Color.FromArgb(113, 136, 153);
		this.lblConnLbl.Location = new System.Drawing.Point(132, 10);
		this.lblConnLbl.Name = "lblConnLbl";
		this.lblConnLbl.Size = new System.Drawing.Size(77, 13);
		this.lblConnLbl.TabIndex = 2;
		this.lblConnLbl.Text = "CONNECTION";
		this.lblConnVal.BackColor = System.Drawing.Color.Transparent;
		this.lblConnVal.Font = new System.Drawing.Font("Segoe UI", 10f, System.Drawing.FontStyle.Bold);
		this.lblConnVal.ForeColor = System.Drawing.Color.FromArgb(255, 70, 92);
		this.lblConnVal.Location = new System.Drawing.Point(132, 28);
		this.lblConnVal.Name = "lblConnVal";
		this.lblConnVal.Size = new System.Drawing.Size(84, 22);
		this.lblConnVal.TabIndex = 3;
		this.lblConnVal.Text = "OFFLINE";
		this.lblConnVal.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
		this.lblUidLbl.AutoSize = true;
		this.lblUidLbl.BackColor = System.Drawing.Color.Transparent;
		this.lblUidLbl.Font = new System.Drawing.Font("Segoe UI", 8f);
		this.lblUidLbl.ForeColor = System.Drawing.Color.FromArgb(113, 136, 153);
		this.lblUidLbl.Location = new System.Drawing.Point(242, 10);
		this.lblUidLbl.Name = "lblUidLbl";
		this.lblUidLbl.Size = new System.Drawing.Size(64, 13);
		this.lblUidLbl.TabIndex = 4;
		this.lblUidLbl.Text = "VEROX UID";
		this.lblUidVal.BackColor = System.Drawing.Color.Transparent;
		this.lblUidVal.Font = new System.Drawing.Font("Segoe UI", 10f, System.Drawing.FontStyle.Bold);
		this.lblUidVal.ForeColor = System.Drawing.Color.FromArgb(0, 217, 255);
		this.lblUidVal.Location = new System.Drawing.Point(242, 28);
		this.lblUidVal.Name = "lblUidVal";
		this.lblUidVal.Size = new System.Drawing.Size(84, 22);
		this.lblUidVal.TabIndex = 5;
		this.lblUidVal.Text = "STANDBY";
		this.lblUidVal.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
		this.lblEngineLbl.AutoSize = true;
		this.lblEngineLbl.BackColor = System.Drawing.Color.Transparent;
		this.lblEngineLbl.Font = new System.Drawing.Font("Segoe UI", 8f);
		this.lblEngineLbl.ForeColor = System.Drawing.Color.FromArgb(113, 136, 153);
		this.lblEngineLbl.Location = new System.Drawing.Point(352, 10);
		this.lblEngineLbl.Name = "lblEngineLbl";
		this.lblEngineLbl.Size = new System.Drawing.Size(85, 13);
		this.lblEngineLbl.TabIndex = 6;
		this.lblEngineLbl.Text = "BYPASS ENGINE";
		this.lblEngineVal.BackColor = System.Drawing.Color.Transparent;
		this.lblEngineVal.Font = new System.Drawing.Font("Segoe UI", 10f, System.Drawing.FontStyle.Bold);
		this.lblEngineVal.ForeColor = System.Drawing.Color.FromArgb(0, 217, 255);
		this.lblEngineVal.Location = new System.Drawing.Point(352, 28);
		this.lblEngineVal.Name = "lblEngineVal";
		this.lblEngineVal.Size = new System.Drawing.Size(84, 22);
		this.lblEngineVal.TabIndex = 7;
		this.lblEngineVal.Text = "STANDBY";
		this.lblEngineVal.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
		this.btnRestart.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
		this.btnRestart.BackColor = System.Drawing.Color.Transparent;
		this.btnRestart.BorderColor = System.Drawing.Color.FromArgb(0, 96, 170);
		this.btnRestart.ColorBottom = System.Drawing.Color.FromArgb(0, 120, 210);
		this.btnRestart.ColorTop = System.Drawing.Color.FromArgb(0, 217, 255);
		this.btnRestart.Cursor = System.Windows.Forms.Cursors.Hand;
		this.btnRestart.DangerHover = false;
		this.btnRestart.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		this.btnRestart.Font = new System.Drawing.Font("Segoe UI", 10f, System.Drawing.FontStyle.Bold);
		this.btnRestart.ForeColor = System.Drawing.Color.White;
		this.btnRestart.GlowColor = System.Drawing.Color.FromArgb(0, 217, 255);
		this.btnRestart.HoverBorderColor = System.Drawing.Color.FromArgb(0, 217, 255);
		this.btnRestart.Location = new System.Drawing.Point(566, 14);
		this.btnRestart.Name = "btnRestart";
		this.btnRestart.ShowGlow = true;
		this.btnRestart.Size = new System.Drawing.Size(178, 36);
		this.btnRestart.TabIndex = 8;
		this.btnRestart.Text = "RESTART ENGINE";
		this.btnRestart.UseVisualStyleBackColor = false;
		this.btnRestart.Visible = false;
		this.btnRestart.Click += new System.EventHandler(btnRestart_Click);
		base.AutoScaleMode = System.Windows.Forms.AutoScaleMode.None;
		this.BackColor = System.Drawing.Color.FromArgb(7, 10, 15);
		base.ClientSize = new System.Drawing.Size(760, 752);
		base.Controls.Add(this.pnlEmulator);
		base.Controls.Add(this.pnlBypass);
		base.Controls.Add(this.pnlLog);
		base.Controls.Add(this.pnlStatus);
		base.Controls.Add(this.pnlTitle);
		this.Font = new System.Drawing.Font("Segoe UI", 10f);
		base.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
		base.Name = "Form1";
		base.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
		this.Text = "VEROX UID ENGINE";
		this.pnlTitle.ResumeLayout(false);
		this.pnlTitle.PerformLayout();
		this.pnlEmulator.ResumeLayout(false);
		this.pnlEmulator.PerformLayout();
		this.pnlBypass.ResumeLayout(false);
		this.pnlBypass.PerformLayout();
		this.pnlLog.ResumeLayout(false);
		this.pnlLogInner.ResumeLayout(false);
		this.pnlStatus.ResumeLayout(false);
		this.pnlStatus.PerformLayout();
		base.ResumeLayout(false);
	}
}
