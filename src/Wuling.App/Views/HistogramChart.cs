using System;
using Avalonia;
using Avalonia.Media;
using Wuling.Plugins.Python;

namespace Wuling.App.Views;

/// <summary>
/// 直方图绘制控件(Python 色彩直方图模式的宿主侧渲染件;04 §3 声明式 histogram 元素)。
/// RGB 三通道半透明叠加, 对数纵轴压缩(避免高光尖峰压扁其余区间)。
/// </summary>
public sealed class HistogramChart : Avalonia.Controls.Control
{
    public static readonly AvaloniaProperty<PythonHistogramData?> HistogramProperty =
        AvaloniaProperty.Register<HistogramChart, PythonHistogramData?>(nameof(Histogram));

    public PythonHistogramData? Histogram
    {
        get => (PythonHistogramData?)GetValue(HistogramProperty);
        set => SetValue(HistogramProperty, value);
    }

    static HistogramChart()
    {
        HistogramProperty.Changed.AddClassHandler<HistogramChart>((c, _) => c.InvalidateVisual());
    }

    public override void Render(DrawingContext context)
    {
        var data = Histogram;
        if (data is null || data.Red.Length == 0)
        {
            return;
        }

        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        // 背景
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12)), bounds);
        // 网格(1/4 分割线)
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1);
        for (var i = 1; i < 4; i++)
        {
            var x = bounds.Width * i / 4;
            context.DrawLine(gridPen, new Point(x, 0), new Point(x, bounds.Height));
            var y = bounds.Height * i / 4;
            context.DrawLine(gridPen, new Point(0, y), new Point(bounds.Width, y));
        }

        DrawChannel(context, data.Red, Color.FromArgb(0x88, 0xE5, 0x4B, 0x4B), bounds);
        DrawChannel(context, data.Green, Color.FromArgb(0x88, 0x4B, 0xC9, 0x63), bounds);
        DrawChannel(context, data.Blue, Color.FromArgb(0x88, 0x4B, 0x8B, 0xE5), bounds);
    }

    private static void DrawChannel(DrawingContext context, int[] bins, Color color, Rect bounds)
    {
        if (bins.Length == 0)
        {
            return;
        }

        // 对数纵轴: 峰值不被单点尖峰压扁
        var maxLog = 0d;
        var logs = new double[bins.Length];
        for (var i = 0; i < bins.Length; i++)
        {
            logs[i] = Math.Log10(1 + bins[i]);
            if (logs[i] > maxLog)
            {
                maxLog = logs[i];
            }
        }

        if (maxLog <= 0)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var baseY = bounds.Height;
            ctx.BeginFigure(new Point(0, baseY), isFilled: true);
            var n = bins.Length;
            for (var i = 0; i < n; i++)
            {
                var x = bounds.Width * i / (n - 1.0);
                var y = baseY - (logs[i] / maxLog) * bounds.Height * 0.95;
                ctx.LineTo(new Point(x, y));
            }

            ctx.LineTo(new Point(bounds.Width, baseY));
            ctx.EndFigure(true);
        }

        var brush = new SolidColorBrush(color);
        context.DrawGeometry(brush, null, geometry);
    }
}
