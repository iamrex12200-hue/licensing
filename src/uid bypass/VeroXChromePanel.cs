using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace uid_bypass;

public class VeroXChromePanel : Panel
{
	private static readonly Color FillTop = Color.FromArgb(13, 19, 27);

	private static readonly Color FillBottom = Color.FromArgb(9, 13, 19);

	public bool AccentTop { get; set; }

	public bool AccentBottom { get; set; }

	public VeroXChromePanel()
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
		using (LinearGradientBrush brush = new LinearGradientBrush(rect, FillTop, FillBottom, 90f))
		{
			graphics.FillRectangle(brush, rect);
		}
		if (AccentBottom)
		{
			using (Pen pen = new Pen(CyberBackdrop.PanelBorder, 1f))
			{
				graphics.DrawLine(pen, 0, rect.Bottom - 1, rect.Right, rect.Bottom - 1);
			}
			using LinearGradientBrush brush2 = new LinearGradientBrush(new Rectangle(0, rect.Bottom - 4, rect.Width, 2), Color.FromArgb(160, 0, 217, 255), Color.FromArgb(0, 0, 217, 255), 0f);
			graphics.FillRectangle(brush2, 0, rect.Bottom - 3, rect.Width, 2);
		}
		if (AccentTop)
		{
			using (Pen pen2 = new Pen(CyberBackdrop.PanelBorder, 1f))
			{
				graphics.DrawLine(pen2, 0, 0, rect.Right, 0);
			}
			using SolidBrush brush3 = new SolidBrush(Color.FromArgb(34, 0, 217, 255));
			graphics.FillRectangle(brush3, 0, 1, rect.Width, 1);
		}
	}
}
