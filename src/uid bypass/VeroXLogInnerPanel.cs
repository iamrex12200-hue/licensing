using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace uid_bypass;

public class VeroXLogInnerPanel : Panel
{
	public VeroXLogInnerPanel()
	{
		SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, value: true);
		BackColor = Color.Transparent;
	}

	protected override void OnPaintBackground(PaintEventArgs e)
	{
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		Graphics graphics = e.Graphics;
		graphics.SmoothingMode = SmoothingMode.AntiAlias;
		graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
		Rectangle rect = new Rectangle(0, 0, base.Width, base.Height);
		using (SolidBrush brush = new SolidBrush(Color.FromArgb(236, 5, 8, 13)))
		{
			graphics.FillRectangle(brush, rect);
		}
		using (Pen pen = new Pen(Color.FromArgb(10, 255, 255, 255), 1f))
		{
			graphics.DrawLine(pen, rect.X + 1, rect.Y + 1, rect.Right - 1, rect.Y + 1);
		}
		using (Pen pen2 = new Pen(Color.FromArgb(24, 38, 49), 1f))
		{
			using GraphicsPath path = VeroXPanel.RoundedPath(new Rectangle(0, 0, base.Width - 1, base.Height - 1), 8f);
			graphics.DrawPath(pen2, path);
		}
		using Pen pen3 = new Pen(Color.FromArgb(18, 0, 217, 255), 1f);
		graphics.DrawLine(pen3, rect.X + 4, rect.Y, rect.X + 48, rect.Y);
	}
}
