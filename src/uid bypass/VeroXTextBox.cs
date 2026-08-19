using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace uid_bypass;

public class VeroXTextBox : TextBox
{
	private const int WM_NCPAINT = 133;

	private Color _border = CyberBackdrop.PanelBorder;

	private bool _hover;

	private string _placeholderText = string.Empty;

	private IntPtr _cuePtr = IntPtr.Zero;

	public string PlaceholderText
	{
		get
		{
			return _placeholderText;
		}
		set
		{
			_placeholderText = value ?? string.Empty;
			if (base.IsHandleCreated)
			{
				ApplyCue();
			}
		}
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern IntPtr GetWindowDC(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

	public VeroXTextBox()
	{
		base.BorderStyle = BorderStyle.None;
		BackColor = CyberBackdrop.InputBg;
		ForeColor = CyberBackdrop.TextColor;
		Font = new Font("Segoe UI", 10f);
		PlaceholderText = "Optional IPv4 • leave blank for default";
	}

	protected override void OnHandleCreated(EventArgs e)
	{
		base.OnHandleCreated(e);
		ApplyCue();
	}

	protected override void OnHandleDestroyed(EventArgs e)
	{
		base.OnHandleDestroyed(e);
		if (_cuePtr != IntPtr.Zero)
		{
			Marshal.FreeHGlobal(_cuePtr);
			_cuePtr = IntPtr.Zero;
		}
	}

	private void ApplyCue()
	{
		if (_cuePtr != IntPtr.Zero)
		{
			Marshal.FreeHGlobal(_cuePtr);
			_cuePtr = IntPtr.Zero;
		}
		if (_placeholderText.Length == 0)
		{
			SendMessage(base.Handle, 5377, IntPtr.Zero, IntPtr.Zero);
			return;
		}
		_cuePtr = Marshal.StringToHGlobalUni(_placeholderText);
		SendMessage(base.Handle, 5377, new IntPtr(1), _cuePtr);
	}

	protected override void OnMouseEnter(EventArgs e)
	{
		_hover = true;
		UpdateBorder();
		Invalidate();
		base.OnMouseEnter(e);
	}

	protected override void OnMouseLeave(EventArgs e)
	{
		_hover = false;
		UpdateBorder();
		Invalidate();
		base.OnMouseLeave(e);
	}

	protected override void OnGotFocus(EventArgs e)
	{
		UpdateBorder();
		Invalidate();
		base.OnGotFocus(e);
	}

	protected override void OnLostFocus(EventArgs e)
	{
		UpdateBorder();
		Invalidate();
		base.OnLostFocus(e);
	}

	protected override void OnEnabledChanged(EventArgs e)
	{
		UpdateBorder();
		Invalidate();
		base.OnEnabledChanged(e);
	}

	private void UpdateBorder()
	{
		if (!base.Enabled)
		{
			_border = Color.FromArgb(28, 36, 46);
		}
		else if (Focused)
		{
			_border = CyberBackdrop.AccentCyan;
		}
		else if (_hover)
		{
			_border = CyberBackdrop.BorderHover;
		}
		else
		{
			_border = CyberBackdrop.PanelBorder;
		}
	}

	protected override void WndProc(ref Message m)
	{
		base.WndProc(ref m);
		if (m.Msg != 133)
		{
			return;
		}
		IntPtr windowDC = GetWindowDC(base.Handle);
		if (!(windowDC != IntPtr.Zero))
		{
			return;
		}
		try
		{
			using Graphics graphics = Graphics.FromHdc(windowDC);
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			DrawRoundedBorder(graphics, _border, 1f, 8f);
			if (Focused && base.Enabled)
			{
				DrawRoundedBorder(graphics, Color.FromArgb(120, CyberBackdrop.AccentCyan), 2f, 8f);
			}
		}
		finally
		{
			ReleaseDC(base.Handle, windowDC);
		}
	}

	private void DrawRoundedBorder(Graphics g, Color color, float width, float radius)
	{
		Rectangle bounds = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
		using Pen pen = new Pen(color, width);
		pen.DashStyle = DashStyle.Solid;
		using GraphicsPath path = VeroXPanel.RoundedPath(bounds, radius);
		g.DrawPath(pen, path);
	}
}
