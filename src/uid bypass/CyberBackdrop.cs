using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace uid_bypass;

internal static class CyberBackdrop
{
	public static readonly Color BaseColor = Color.FromArgb(7, 10, 15);

	public static readonly Color PanelFill = Color.FromArgb(11, 16, 23);

	public static readonly Color PanelFillAlt = Color.FromArgb(9, 14, 21);

	public static readonly Color InputBg = Color.FromArgb(9, 13, 19);

	public static readonly Color PanelBorder = Color.FromArgb(27, 42, 53);

	public static readonly Color BorderHover = Color.FromArgb(44, 72, 88);

	public static readonly Color AccentCyan = Color.FromArgb(0, 217, 255);

	public static readonly Color AccentBlue = Color.FromArgb(0, 140, 255);

	public static readonly Color TextColor = Color.FromArgb(232, 247, 255);

	public static readonly Color MutedText = Color.FromArgb(138, 158, 174);

	public static readonly Color Success = Color.FromArgb(25, 230, 162);

	public static readonly Color Warning = Color.FromArgb(255, 200, 87);

	public static readonly Color Error = Color.FromArgb(255, 70, 92);

	private static Bitmap _noiseTile;

	private static readonly object _noiseLock = new object();

	private static Bitmap NoiseTile
	{
		get
		{
			lock (_noiseLock)
			{
				if (_noiseTile == null)
				{
					_noiseTile = BuildNoiseTile();
				}
				return _noiseTile;
			}
		}
	}

	private static Bitmap BuildNoiseTile()
	{
		Bitmap bitmap = new Bitmap(96, 96, PixelFormat.Format32bppArgb);
		Random random = new Random(1337);
		using Graphics graphics = Graphics.FromImage(bitmap);
		graphics.Clear(Color.Transparent);
		for (int i = 0; i < 240; i++)
		{
			int num = random.Next(0, 13);
			if (num != 0)
			{
				int x = random.Next(96);
				int y = random.Next(96);
				using SolidBrush brush = new SolidBrush((random.Next(0, 2) == 0) ? Color.FromArgb(num, 255, 255, 255) : Color.FromArgb(num, 0, 217, 255));
				graphics.FillRectangle(brush, x, y, 1, 1);
			}
		}
		return bitmap;
	}

	public static void PaintScene(Graphics g, Rectangle bounds, int width, int height)
	{
		using (SolidBrush brush = new SolidBrush(Color.FromArgb(245, 7, 10, 15)))
		{
			g.FillRectangle(brush, bounds);
		}
		DrawSubtleGrid(g, width, height);
		DrawNoise(g, bounds);
		DrawAmbientGlows(g, width, height);
	}

	private static void DrawSubtleGrid(Graphics g, int width, int height)
	{
		using Pen pen = new Pen(Color.FromArgb(9, 0, 217, 255), 1f);
		for (int i = 0; i < width; i += 24)
		{
			g.DrawLine(pen, i, 0, i, height);
		}
		for (int j = 0; j < height; j += 24)
		{
			g.DrawLine(pen, 0, j, width, j);
		}
	}

	private static void DrawNoise(Graphics g, Rectangle bounds)
	{
		using TextureBrush brush = new TextureBrush(NoiseTile, WrapMode.Tile);
		g.FillRectangle(brush, bounds);
	}

	private static void DrawAmbientGlows(Graphics g, int width, int height)
	{
		DrawRadialGlow(g, new Rectangle(-120, -110, 380, 380), Color.FromArgb(16, 0, 120, 200));
		DrawRadialGlow(g, new Rectangle(width / 2 - 150, height / 2 - 130, 300, 300), Color.FromArgb(9, 0, 70, 140));
	}

	private static void DrawRadialGlow(Graphics g, Rectangle ellipse, Color center)
	{
		using GraphicsPath graphicsPath = new GraphicsPath();
		graphicsPath.AddEllipse(ellipse);
		using PathGradientBrush pathGradientBrush = new PathGradientBrush(graphicsPath);
		pathGradientBrush.CenterColor = center;
		pathGradientBrush.SurroundColors = new Color[1] { Color.FromArgb(0, 0, 0, 0) };
		g.FillPath(pathGradientBrush, graphicsPath);
	}

	public static Point GetFormOffset(Control control)
	{
		Control control2 = control;
		int num = 0;
		int num2 = 0;
		while (control2 != null && !(control2 is Form))
		{
			num += control2.Left;
			num2 += control2.Top;
			control2 = control2.Parent;
		}
		return new Point(num, num2);
	}
}
