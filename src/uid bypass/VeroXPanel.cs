using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace uid_bypass;

public class VeroXPanel : Panel
{
	private struct Particle
	{
		public float X;

		public float Y;

		public float VX;

		public float VY;

		public float Phase;

		public float Fade;

		public int Size;
	}

	private const float CornerRadius = 10f;

	private string _title = string.Empty;

	private readonly Font _titleFont = new Font("Segoe UI", 10f, FontStyle.Bold);

	private const int ParticleCount = 16;

	private static readonly Random _rand = new Random();

	private readonly Particle[] _particles = new Particle[16];

	private readonly Timer _particleTimer = new Timer
	{
		Interval = 24
	};

	private bool _particlesReady;

	public string Title
	{
		get
		{
			return _title;
		}
		set
		{
			_title = value;
			Invalidate();
		}
	}

	public VeroXPanel()
	{
		SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, value: true);
		BackColor = Color.Transparent;
		if (!base.DesignMode)
		{
			_particleTimer.Tick += ParticleTick;
			_particleTimer.Start();
		}
	}

	protected override void OnPaintBackground(PaintEventArgs e)
	{
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		Graphics graphics = e.Graphics;
		graphics.SmoothingMode = SmoothingMode.AntiAlias;
		graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
		Rectangle bounds = new Rectangle(0, 0, base.Width, base.Height);
		DrawShadow(graphics, bounds);
		DrawCardBody(graphics, bounds);
		DrawParticles(graphics);
		if (!string.IsNullOrEmpty(_title))
		{
			DrawTitle(graphics);
		}
	}

	private void DrawShadow(Graphics g, Rectangle bounds)
	{
		using GraphicsPath path = RoundedPath(new Rectangle(bounds.X, bounds.Y + 4, bounds.Width, bounds.Height - 4), 10f);
		using SolidBrush brush = new SolidBrush(Color.FromArgb(22, 0, 0, 0));
		g.FillPath(brush, path);
	}

	private void DrawCardBody(Graphics g, Rectangle bounds)
	{
		using (GraphicsPath path = RoundedPath(bounds, 10f))
		{
			using (LinearGradientBrush brush = new LinearGradientBrush(bounds, Color.FromArgb(240, 13, 19, 27), Color.FromArgb(230, 9, 13, 19), 90f))
			{
				g.FillPath(brush, path);
			}
			using Pen pen = new Pen(CyberBackdrop.PanelBorder, 1f);
			g.DrawPath(pen, path);
		}
		using Pen pen2 = new Pen(Color.FromArgb(14, 255, 255, 255), 1f);
		g.DrawLine(pen2, bounds.X + 12, bounds.Y + 1, bounds.Right - 12, bounds.Y + 1);
	}

	private void InitParticles()
	{
		if (!_particlesReady && base.Width >= 10 && base.Height >= 10)
		{
			for (int i = 0; i < 16; i++)
			{
				_particles[i].X = (float)(_rand.NextDouble() * (double)base.Width);
				_particles[i].Y = (float)(_rand.NextDouble() * (double)base.Height);
				_particles[i].VX = (float)(_rand.NextDouble() * 0.3 - 0.15);
				_particles[i].VY = (float)(0.0 - (0.06 + _rand.NextDouble() * 0.22));
				_particles[i].Size = ((_rand.Next(2) == 0) ? 1 : 2);
				_particles[i].Phase = (float)(_rand.NextDouble() * Math.PI * 2.0);
				_particles[i].Fade = (float)(0.02 + _rand.NextDouble() * 0.03);
			}
			_particlesReady = true;
		}
	}

	private void ParticleTick(object sender, EventArgs e)
	{
		if (!base.Visible)
		{
			return;
		}
		InitParticles();
		if (!_particlesReady)
		{
			return;
		}
		double num = (double)Environment.TickCount * 0.001;
		for (int i = 0; i < 16; i++)
		{
			Particle particle = _particles[i];
			particle.X += particle.VX + (float)Math.Sin(num * 0.8 + (double)i) * 0.04f;
			particle.Y += particle.VY;
			particle.Phase += particle.Fade;
			if (particle.Y < -4f)
			{
				particle.Y = base.Height + 4;
				particle.X = (float)(_rand.NextDouble() * (double)base.Width);
			}
			if (particle.X < -4f)
			{
				particle.X = base.Width + 4;
			}
			if (particle.X > (float)(base.Width + 4))
			{
				particle.X = -4f;
			}
			_particles[i] = particle;
		}
		Invalidate();
	}

	private void DrawParticles(Graphics g)
	{
		InitParticles();
		if (!_particlesReady)
		{
			return;
		}
		using GraphicsPath clip = RoundedPath(new Rectangle(0, 0, base.Width, base.Height), 10f);
		using SolidBrush solidBrush = new SolidBrush(CyberBackdrop.Error);
		GraphicsState gstate = g.Save();
		g.SetClip(clip);
		for (int i = 0; i < 16; i++)
		{
			Particle particle = _particles[i];
			float num = (float)(Math.Sin(particle.Phase) * 0.5 + 0.5);
			solidBrush.Color = Color.FromArgb((int)(26f + 116f * num), CyberBackdrop.Error);
			g.FillEllipse(solidBrush, particle.X, particle.Y, particle.Size, particle.Size);
		}
		g.Restore(gstate);
	}

	private void DrawTitle(Graphics g)
	{
		using (SolidBrush brush = new SolidBrush(CyberBackdrop.AccentCyan))
		{
			g.FillRectangle(brush, 14, 10, 3, 15);
		}
		TextRenderer.DrawText(g, _title, _titleFont, new Rectangle(24, 7, base.Width - 70, 22), Color.FromArgb(216, 237, 250), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
		using Pen pen = new Pen(Color.FromArgb(26, 27, 42, 53), 1f);
		g.DrawLine(pen, 16, 44, base.Width - 16, 44);
	}

	internal static GraphicsPath RoundedPath(Rectangle bounds, float radius)
	{
		GraphicsPath graphicsPath = new GraphicsPath();
		float num = Math.Min(radius, (float)Math.Min(bounds.Width, bounds.Height) / 2f);
		graphicsPath.AddArc(bounds.X, bounds.Y, num, num, 180f, 90f);
		graphicsPath.AddArc((float)bounds.Right - num, bounds.Y, num, num, 270f, 90f);
		graphicsPath.AddArc((float)bounds.Right - num, (float)bounds.Bottom - num, num, num, 0f, 90f);
		graphicsPath.AddArc(bounds.X, (float)bounds.Bottom - num, num, num, 90f, 90f);
		graphicsPath.CloseFigure();
		return graphicsPath;
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_particleTimer.Stop();
			_particleTimer.Dispose();
			_titleFont.Dispose();
		}
		base.Dispose(disposing);
	}
}
