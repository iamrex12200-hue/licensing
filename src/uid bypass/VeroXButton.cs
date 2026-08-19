using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace uid_bypass;

public class VeroXButton : Button
{
	public enum WindowGlyphKind
	{
		None,
		Minimize,
		Maximize,
		Restore,
		Close
	}

	private const float CornerRadius = 8f;

	private bool _hover;

	private bool _pressed;

	private float _hoverProgress;

	private readonly Timer _anim = new Timer
	{
		Interval = 16
	};

	public bool Circle { get; set; }

	public Color ColorTop { get; set; } = Color.FromArgb(0, 217, 255);


	public Color ColorBottom { get; set; } = Color.FromArgb(0, 120, 210);


	public Color BorderColor { get; set; } = Color.FromArgb(0, 90, 160);


	public Color HoverBorderColor { get; set; } = Color.FromArgb(0, 217, 255);


	public Color GlowColor { get; set; } = Color.FromArgb(0, 217, 255);


	public bool ShowGlow { get; set; } = true;


	public bool DangerHover { get; set; }

	public WindowGlyphKind Glyph { get; set; }

	public VeroXButton()
	{
		SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, value: true);
		base.FlatStyle = FlatStyle.Flat;
		base.FlatAppearance.BorderSize = 0;
		Cursor = Cursors.Hand;
		base.TabStop = true;
		ForeColor = Color.White;
		BackColor = Color.Transparent;
		_anim.Tick += delegate
		{
			Animate();
		};
	}

	private void Animate()
	{
		float num = (_hover ? 1f : 0f);
		_hoverProgress += (num - _hoverProgress) * 0.32f;
		if (Math.Abs(_hoverProgress - num) < 0.012f)
		{
			_hoverProgress = num;
			_anim.Stop();
		}
		Invalidate();
	}

	protected override void OnMouseEnter(EventArgs e)
	{
		_hover = true;
		_anim.Start();
		base.OnMouseEnter(e);
	}

	protected override void OnMouseLeave(EventArgs e)
	{
		_hover = false;
		_pressed = false;
		_anim.Start();
		base.OnMouseLeave(e);
	}

	protected override void OnMouseDown(MouseEventArgs mevent)
	{
		if (mevent.Button == MouseButtons.Left)
		{
			_pressed = true;
			Invalidate();
		}
		base.OnMouseDown(mevent);
	}

	protected override void OnMouseUp(MouseEventArgs mevent)
	{
		_pressed = false;
		Invalidate();
		base.OnMouseUp(mevent);
	}

	protected override void OnEnabledChanged(EventArgs e)
	{
		Invalidate();
		base.OnEnabledChanged(e);
	}

	protected override void OnPaint(PaintEventArgs pevent)
	{
		Graphics graphics = pevent.Graphics;
		graphics.SmoothingMode = SmoothingMode.AntiAlias;
		graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
		Rectangle clientRectangle = base.ClientRectangle;
		clientRectangle.Width--;
		clientRectangle.Height--;
		float hoverProgress = _hoverProgress;
		if (_pressed)
		{
			clientRectangle.Inflate(-2, -2);
		}
		Color color;
		Color color2;
		if (!base.Enabled)
		{
			color = Color.FromArgb(17, 21, 27);
			color2 = Color.FromArgb(12, 16, 21);
			goto IL_0110;
		}
		int num;
		Color color3;
		if (_hover)
		{
			num = (DangerHover ? 1 : 0);
			if (num != 0)
			{
				color3 = Color.FromArgb(255, 70, 92);
				goto IL_00ac;
			}
		}
		else
		{
			num = 0;
		}
		color3 = ColorTop;
		goto IL_00ac;
		IL_00ac:
		Color color4 = color3;
		Color obj = ((num != 0) ? Color.FromArgb(200, 42, 66) : ColorBottom);
		color = Lerp(color4, Lighten(color4, 0.16f), hoverProgress);
		color2 = Lerp(obj, Lighten(obj, 0.16f), hoverProgress);
		if (_pressed)
		{
			color = Darken(color, 0.7f);
			color2 = Darken(color2, 0.7f);
		}
		goto IL_0110;
		IL_0110:
		using (GraphicsPath path = PathFor(clientRectangle))
		{
			using (LinearGradientBrush brush = new LinearGradientBrush(clientRectangle, color, color2, 90f))
			{
				graphics.FillPath(brush, path);
			}
			Color color5 = ((!base.Enabled) ? Color.FromArgb(30, 38, 48) : ((!_hover && !_pressed) ? BorderColor : HoverBorderColor));
			using Pen pen = new Pen(color5, 1f);
			graphics.DrawPath(pen, path);
		}
		if (!Circle)
		{
			using Pen pen2 = new Pen(Color.FromArgb(24, 255, 255, 255), 1f);
			graphics.DrawLine(pen2, clientRectangle.X + 5, clientRectangle.Y + 2, clientRectangle.Right - 5, clientRectangle.Y + 2);
		}
		if (base.Enabled && ShowGlow && hoverProgress > 0.02f)
		{
			using Pen pen3 = new Pen(Color.FromArgb((int)(62f * hoverProgress), GlowColor), 2f);
			using GraphicsPath path2 = PathFor(new Rectangle(clientRectangle.X - 2, clientRectangle.Y - 2, clientRectangle.Width + 4, clientRectangle.Height + 4));
			graphics.DrawPath(pen3, path2);
		}
		Color color6 = (base.Enabled ? ForeColor : Color.FromArgb(88, 102, 116));
		if (Glyph != 0)
		{
			DrawGlyph(graphics, color6);
		}
		else
		{
			Rectangle bounds = clientRectangle;
			if (_pressed)
			{
				bounds.Offset(0, 1);
			}
			TextRenderer.DrawText(graphics, Text, Font, bounds, color6, TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
		}
		if (!Focused || !ShowFocusCues)
		{
			return;
		}
		using Pen pen4 = new Pen(Color.FromArgb(100, 255, 255, 255));
		using GraphicsPath path3 = PathFor(clientRectangle);
		graphics.DrawPath(pen4, path3);
	}

	private GraphicsPath PathFor(Rectangle rect)
	{
		if (Circle)
		{
			GraphicsPath graphicsPath = new GraphicsPath();
			graphicsPath.AddEllipse(rect);
			return graphicsPath;
		}
		return VeroXPanel.RoundedPath(rect, 8f);
	}

	private void DrawGlyph(Graphics g, Color color)
	{
		int num = base.Width / 2;
		int num2 = base.Height / 2;
		int num3 = 6;
		int num4 = (_pressed ? 1 : 0);
		using Pen pen = new Pen(color, 2f);
		pen.StartCap = LineCap.Square;
		pen.EndCap = LineCap.Square;
		switch (Glyph)
		{
		case WindowGlyphKind.Minimize:
			g.DrawLine(pen, num - num3, num2 + num4, num + num3, num2 + num4);
			break;
		case WindowGlyphKind.Maximize:
			g.DrawRectangle(pen, num - num3, num2 - num3 + num4, 2 * num3, 2 * num3);
			break;
		case WindowGlyphKind.Restore:
			g.DrawRectangle(pen, num - num3 + 3, num2 - num3 + num4, 2 * num3 - 3, 2 * num3 - 3);
			g.DrawRectangle(pen, num - num3, num2 - num3 + num4 + 3, 2 * num3 - 3, 2 * num3 - 3);
			break;
		case WindowGlyphKind.Close:
			g.DrawLine(pen, num - num3, num2 - num3 + num4, num + num3, num2 + num3 + num4);
			g.DrawLine(pen, num + num3, num2 - num3 + num4, num - num3, num2 + num3 + num4);
			break;
		}
	}

	private static Color Lerp(Color a, Color b, float t)
	{
		return Color.FromArgb(a.A, (int)((float)(int)a.R + (float)(b.R - a.R) * t), (int)((float)(int)a.G + (float)(b.G - a.G) * t), (int)((float)(int)a.B + (float)(b.B - a.B) * t));
	}

	private static Color Lighten(Color c, float f)
	{
		return Color.FromArgb(c.A, (int)Math.Min(255f, (float)(int)c.R + (float)(255 - c.R) * f), (int)Math.Min(255f, (float)(int)c.G + (float)(255 - c.G) * f), (int)Math.Min(255f, (float)(int)c.B + (float)(255 - c.B) * f));
	}

	private static Color Darken(Color c, float f)
	{
		return Color.FromArgb(c.A, (int)((float)(int)c.R * f), (int)((float)(int)c.G * f), (int)((float)(int)c.B * f));
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_anim.Dispose();
		}
		base.Dispose(disposing);
	}
}
