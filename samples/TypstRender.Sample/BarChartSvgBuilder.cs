using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace TypstRender.Sample;

public sealed class BarChartSvgBuilder
{
    public string Build(JsonArray series)
    {
        const double width = 600;
        const double height = 240;
        const double left = 10;
        const double bottom = 28;
        const double top = 16;

        var points = series
            .Select(p => new ChartPoint(
                p?["label"]?.GetValue<string>() ?? "",
                p?["value"]?.GetValue<double>() ?? 0))
            .ToList();

        var svg = new StringBuilder()
            .AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {Format(width)} {Format(height)}\">");

        if (points.Count == 0)
        {
            return svg.AppendLine("</svg>").ToString();
        }

        var max = Math.Max(points.Max(p => p.Value), 1);
        var slot = (width - 2 * left) / points.Count;
        var barWidth = slot * 0.6;

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var barHeight = (height - bottom - top) * (point.Value / max);
            var x = left + i * slot + (slot - barWidth) / 2;
            var y = height - bottom - barHeight;
            var center = x + barWidth / 2;

            svg.AppendLine($"  <rect x=\"{Format(x)}\" y=\"{Format(y)}\" width=\"{Format(barWidth)}\" height=\"{Format(barHeight)}\" rx=\"3\" fill=\"#1f3a93\"/>")
                .AppendLine($"  <text x=\"{Format(center)}\" y=\"{Format(y - 5)}\" font-size=\"11\" fill=\"#666666\" text-anchor=\"middle\" font-family=\"sans-serif\">{Format(point.Value / 1000)}k</text>")
                .AppendLine($"  <text x=\"{Format(center)}\" y=\"{Format(height - 10)}\" font-size=\"12\" fill=\"#333333\" text-anchor=\"middle\" font-family=\"sans-serif\">{Escape(point.Label)}</text>");
        }

        return svg.AppendLine("</svg>").ToString();
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Escape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private sealed record ChartPoint(string Label, double Value);
}
