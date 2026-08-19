using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace uid_bypass;

public class VeroXComboBox : ComboBox
{
	private const int WM_PAINT = 15;

	private const int WM_NCPAINT = 133;

	private const int WM_PRINT = 791;

	private Color _border = CyberBackdrop.PanelBorder;

	private bool _hover;

	[DllImport("user32.dll")]
	private static extern IntPtr GetWindowDC(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

	public VeroXComboBox()
	{
		base.DrawMode = DrawMode.OwnerDrawFixed;
		base.DropDownStyle = ComboBoxStyle.DropDownList;
		base.FlatStyle = FlatStyle.Flat;
		BackColor = CyberBackdrop.InputBg;
		ForeColor = CyberBackdrop.TextColor;
		Font = new Font("Segoe UI", 10f);
		base.ItemHeight = 26;
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

	protected override void OnDrawItem(DrawItemEventArgs e)
	{
		if (e.Index < 0)
		{
			e.DrawBackground();
			return;
		}
		string itemText = GetItemText(base.Items[e.Index]);
		Graphics graphics = e.Graphics;
		graphics.SmoothingMode = SmoothingMode.AntiAlias;
		Rectangle bounds = e.Bounds;
		if ((e.State & DrawItemState.ComboBoxEdit) != 0)
		{
			using (SolidBrush brush = new SolidBrush(base.Enabled ? CyberBackdrop.InputBg : Color.FromArgb(12, 16, 21)))
			{
				graphics.FillRectangle(brush, 0, 0, base.Width, base.Height);
			}
			TextRenderer.DrawText(bounds: new Rectangle(bounds.X + 12, bounds.Y, bounds.Width - 46, bounds.Height), dc: graphics, text: itemText, font: Font, foreColor: CyberBackdrop.TextColor, flags: TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			return;
		}
		bool flag = (e.State & DrawItemState.Selected) != 0;
		bool flag2 = (e.State & DrawItemState.HotLight) != 0;
		using (SolidBrush brush2 = new SolidBrush(flag ? Color.FromArgb(16, 42, 56) : (flag2 ? Color.FromArgb(15, 25, 35) : CyberBackdrop.InputBg)))
		{
			graphics.FillRectangle(brush2, bounds);
		}
		if (flag)
		{
			using SolidBrush brush3 = new SolidBrush(CyberBackdrop.AccentCyan);
			graphics.FillRectangle(brush3, bounds.X, bounds.Y + 6, 2, bounds.Height - 12);
		}
		Color foreColor = ((flag || flag2) ? CyberBackdrop.TextColor : CyberBackdrop.MutedText);
		TextRenderer.DrawText(bounds: new Rectangle(bounds.X + 14, bounds.Y, bounds.Width - 20, bounds.Height), dc: graphics, text: itemText, font: Font, foreColor: foreColor, flags: TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
	}

	protected override void WndProc(ref Message m)
	{
		if (m.Msg == 15 || m.Msg == 133)
		{
			base.WndProc(ref m);
			DrawCustomChrome();
		}
		else if (m.Msg == 791)
		{
			base.WndProc(ref m);
			IntPtr lParam = m.LParam;
			if (lParam != IntPtr.Zero)
			{
				using (Graphics graphics = Graphics.FromHdc(lParam))
				{
					graphics.SmoothingMode = SmoothingMode.AntiAlias;
					DrawChrome(graphics);
				}
			}
		}
		else
		{
			base.WndProc(ref m);
		}
	}

	private void DrawCustomChrome()
	{
		IntPtr windowDC = GetWindowDC(base.Handle);
		if (windowDC == IntPtr.Zero)
		{
			return;
		}
		try
		{
			using Graphics graphics = Graphics.FromHdc(windowDC);
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			DrawChrome(graphics);
		}
		finally
		{
			ReleaseDC(base.Handle, windowDC);
		}
	}

	private void DrawChrome(Graphics g)
	{
		int num = base.Width;
		int num2 = base.Height;
		using (SolidBrush brush = new SolidBrush(base.Enabled ? CyberBackdrop.InputBg : Color.FromArgb(12, 16, 21)))
		{
			g.FillRectangle(brush, 0, 0, num, 3);
			g.FillRectangle(brush, 0, num2 - 3, num, 3);
			g.FillRectangle(brush, 0, 0, 3, num2);
			g.FillRectangle(brush, num - 3, 0, 3, num2);
			g.FillRectangle(brush, num - 26, 0, 26, num2);
		}
		Color color = ((!base.Enabled) ? Color.FromArgb(70, 84, 96) : ((_hover || Focused) ? CyberBackdrop.AccentCyan : CyberBackdrop.MutedText));
		DrawChevron(g, num - 14, num2 / 2, color);
		using (Pen pen = new Pen(_border, 1f))
		{
			DrawRoundedBorder(g, pen, new Rectangle(0, 0, num - 1, num2 - 1), 8f);
		}
		if (Focused && base.Enabled)
		{
			using (Pen pen2 = new Pen(Color.FromArgb(130, CyberBackdrop.AccentCyan), 2f))
			{
				DrawRoundedBorder(g, pen2, new Rectangle(1, 1, num - 3, num2 - 3), 8f);
			}
		}
	}

	private void DrawChevron(Graphics g, int cx, int cy, Color color)
	{
		using Pen pen = new Pen(color, 1.6f);
		pen.StartCap = LineCap.Round;
		pen.EndCap = LineCap.Round;
		g.DrawLines(pen, new Point[3]
		{
			new Point(cx - 5, cy - 3),
			new Point(cx, cy + 2),
			new Point(cx + 5, cy - 3)
		});
	}

	private static void DrawRoundedBorder(Graphics g, Pen pen, Rectangle b, float radius)
	{
		using GraphicsPath path = VeroXPanel.RoundedPath(b, radius);
		g.DrawPath(pen, path);
	}
}
