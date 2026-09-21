using Microsoft.AspNetCore.Html;

namespace WorkUp.Infrastructure;

/// <summary>
/// Inline SVG icons (24px grid, 2px stroke). Always paired with a visible text label; decorative, hidden from
/// screen readers. Never render icon-font ligature names as text. Add new icons here.
/// </summary>
public static class Icons
{
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["sun"] = "<circle cx=\"12\" cy=\"12\" r=\"4\"/><path d=\"M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4\"/>",
        ["calendar"] = "<rect x=\"3\" y=\"5\" width=\"18\" height=\"16\" rx=\"2\"/><path d=\"M16 3v4M8 3v4M3 10h18\"/>",
        ["grid"] = "<rect x=\"3\" y=\"3\" width=\"7\" height=\"7\"/><rect x=\"14\" y=\"3\" width=\"7\" height=\"7\"/><rect x=\"3\" y=\"14\" width=\"7\" height=\"7\"/><rect x=\"14\" y=\"14\" width=\"7\" height=\"7\"/>",
        ["repeat"] = "<path d=\"M17 2l4 4-4 4\"/><path d=\"M3 11v-1a4 4 0 0 1 4-4h14\"/><path d=\"M7 22l-4-4 4-4\"/><path d=\"M21 13v1a4 4 0 0 1-4 4H3\"/>",
        ["users"] = "<circle cx=\"9\" cy=\"8\" r=\"4\"/><path d=\"M2 21a7 7 0 0 1 14 0\"/><path d=\"M16 4a4 4 0 0 1 0 8M22 21a7 7 0 0 0-4-6.3\"/>",
        ["user"] = "<circle cx=\"12\" cy=\"8\" r=\"4\"/><path d=\"M4 21a8 8 0 0 1 16 0\"/>",
        ["coin"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 6v12M15 9.5c0-1.4-1.3-2.5-3-2.5s-3 1-3 2.3c0 3 6 1.6 6 4.6 0 1.3-1.3 2.3-3 2.3s-3-1.1-3-2.5\"/>",
        ["receipt"] = "<path d=\"M5 3h14v18l-3-2-2 2-2-2-2 2-2-2-3 2z\"/><path d=\"M9 8h6M9 12h6\"/>",
        ["box"] = "<path d=\"M3 7l9-4 9 4v10l-9 4-9-4z\"/><path d=\"M3 7l9 4 9-4M12 11v10\"/>",
        ["chart"] = "<path d=\"M4 20V10M10 20V4M16 20v-7M22 20H2\"/>",
        ["sliders"] = "<path d=\"M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12M20 18h0\"/><circle cx=\"16\" cy=\"6\" r=\"2\"/><circle cx=\"10\" cy=\"12\" r=\"2\"/><circle cx=\"18\" cy=\"18\" r=\"2\"/>",
        ["flask"] = "<path d=\"M9 3h6M10 3v6l-5 9a2 2 0 0 0 1.7 3h10.6a2 2 0 0 0 1.7-3l-5-9V3\"/><path d=\"M7.5 15h9\"/>",
        ["menu"] = "<path d=\"M4 6h16M4 12h16M4 18h16\"/>",
        ["more"] = "<circle cx=\"5\" cy=\"12\" r=\"1.5\" fill=\"currentColor\"/><circle cx=\"12\" cy=\"12\" r=\"1.5\" fill=\"currentColor\"/><circle cx=\"19\" cy=\"12\" r=\"1.5\" fill=\"currentColor\"/>",
        ["clock"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 7v5l3 2\"/>",
        ["check"] = "<path d=\"M5 12l5 5L20 7\"/>",
        ["check-double"] = "<path d=\"M2 12l5 5L17 7\"/><path d=\"M12 16l1 1L23 7\"/>",
        ["cross"] = "<path d=\"M6 6l12 12M18 6L6 18\"/>",
        ["plus"] = "<path d=\"M12 5v14M5 12h14\"/>",
        ["alert"] = "<path d=\"M12 3l10 18H2z\"/><path d=\"M12 10v5M12 18h0\"/>",
        ["info"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 11v6M12 7.5h0\"/>",
        ["pause"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M10 9v6M14 9v6\"/>",
        ["logout"] = "<path d=\"M15 4h4a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1h-4\"/><path d=\"M10 17l-5-5 5-5M5 12h11\"/>",
        ["external"] = "<path d=\"M14 4h6v6M20 4l-9 9\"/><path d=\"M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5\"/>",
        ["arrow-left"] = "<path d=\"M19 12H5M11 6l-6 6 6 6\"/>",
        ["arrow-right"] = "<path d=\"M5 12h14M13 6l6 6-6 6\"/>",
        ["link"] = "<path d=\"M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1\"/><path d=\"M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1\"/>",
        ["copy"] = "<rect x=\"9\" y=\"9\" width=\"11\" height=\"11\" rx=\"1\"/><path d=\"M5 15V5a1 1 0 0 1 1-1h10\"/>",
        ["lock"] = "<rect x=\"5\" y=\"11\" width=\"14\" height=\"10\" rx=\"1\"/><path d=\"M8 11V7a4 4 0 0 1 8 0v4\"/>",
        ["edit"] = "<path d=\"M12 20h9\"/><path d=\"M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4Z\"/>",
    };

    public static IHtmlContent Svg(string name, string cssClass = "icon")
    {
        if (!Paths.TryGetValue(name, out var inner))
            throw new ArgumentException($"Unknown icon '{name}'. Add it to Icons.Paths.", nameof(name));

        return new HtmlString(
            $"<svg class=\"{cssClass}\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" " +
            $"stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\" focusable=\"false\">{inner}</svg>");
    }
}
