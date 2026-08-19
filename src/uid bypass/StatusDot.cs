using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace uid_bypass;

public class StatusDot : Control
{
	private Color _color = Color.FromArgb(113, 136, 153);

	private readonly Timer _timer = new Timer
	{
		Interval = 450
	};

	private bool _phase;

	public StatusDot()
	{
		SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, value: true);
		base.Size = new Size(12, 12);
		_timer.Tick += delegate
		{
			_phase = !_phase;
			Invalidate();
		};
	}

	public void SetState(Color color, bool pulse)
	{
		_color = color;
		_phase = false;
		_timer.Enabled = pulse;
		Invalidate();
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		Graphics graphics = e.Graphics;
		graphics.SmoothingMode = SmoothingMode.AntiAlias;
		Rectangle rect = new Rectangle(2, 2, base.Width - 4, base.Height - 4);
		Color color = (_phase ? Color.FromArgb(110, _color.R, _color.G, _color.B) : _color);
		using (Pen pen = new Pen(Color.FromArgb(_phase ? 90 : 45, _color.R, _color.G, _color.B), 1.5f))
		{
			graphics.DrawEllipse(pen, rect);
		}
		using (SolidBrush brush = new SolidBrush(color))
		{
			graphics.FillEllipse(brush, rect);
		}
		using Pen pen2 = new Pen(Color.FromArgb(60, 255, 255, 255), 1f);
		graphics.DrawArc(pen2, new Rectangle(rect.X - 1, rect.Y - 1, rect.Width - 2, rect.Height - 2), 200f, 140f);
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_timer.Dispose();
		}
		base.Dispose(disposing);
	}
}
