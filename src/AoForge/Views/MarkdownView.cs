using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace AoForge.Views;

// Shows release notes written in GitHub Markdown: headings, bullet and numbered lists, quotes,
// code blocks, **bold**, *italic*, `code` and links. Single line breaks are kept, as GitHub shows
// them in release notes. Anything else is shown as plain text.
public sealed class MarkdownView : StackPanel
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView),
        new PropertyMetadata(null, (d, _) => ((MarkdownView)d).Build()));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    static readonly Regex Heading = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$");
    static readonly Regex ListItem = new(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$");
    static readonly Regex Rule = new(@"^\s*([-*_])(\s*\1){2,}\s*$");
    static readonly Regex Inline = new(
        @"(?<code>`[^`]+`)|!\[(?<alt>[^\]]*)\]\([^)]*\)|\[(?<text>[^\]]+)\]\((?<url>[^)\s]+)[^)]*\)|(?<bold>\*\*[^*]+\*\*|__[^_]+__)|(?<italic>\*[^*\s][^*]*\*)|(?<link>https?://[^\s<>()]+[^\s<>().,;:!?'""])");

    static readonly Brush Body = new SolidColorBrush(Color.FromRgb(0xC9, 0xCE, 0xD6));
    static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x9A, 0xA2, 0xAF));
    static readonly Brush CodeBack = new SolidColorBrush(Color.FromRgb(0x27, 0x2B, 0x35));
    static readonly Brush LinkBrush = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
    static readonly FontFamily Mono = new("Consolas");

    void Build()
    {
        Children.Clear();
        string text = (Markdown ?? "").Replace("\r\n", "\n");
        var para = new List<string>();
        var code = new StringBuilder();
        bool inCode = false;

        void FlushPara()
        {
            if (para.Count > 0)
                Add(Text(string.Join("\n", para), Body), 0, 6);
            para.Clear();
        }

        foreach (string raw in text.Split('\n')) {
            string line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```")) {
                if (inCode) {
                    var box = new TextBlock {
                        Text = code.ToString().TrimEnd('\n'), FontFamily = Mono, FontSize = 12, Foreground = Body,
                        TextWrapping = TextWrapping.Wrap,
                    };
                    Children.Add(new Border { Background = CodeBack, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 6), Child = box });
                    code.Clear();
                } else {
                    FlushPara();
                }
                inCode = !inCode;
                continue;
            }
            if (inCode) {
                code.Append(raw).Append('\n');
                continue;
            }
            if (line.Length == 0) {
                FlushPara();
                continue;
            }
            Match m;
            if ((m = Heading.Match(line)).Success) {
                FlushPara();
                var h = Text(m.Groups[2].Value, Brushes.White);
                h.FontWeight = FontWeights.SemiBold;
                h.FontSize = m.Groups[1].Length <= 2 ? 14 : 13;
                Add(h, 0, 4, Children.Count > 0 ? 6 : 0);
            } else if (Rule.IsMatch(line)) {
                FlushPara();
                Children.Add(new Border { Height = 1, Background = CodeBack, Margin = new Thickness(0, 4, 0, 8) });
            } else if ((m = ListItem.Match(line)).Success) {
                FlushPara();
                int level = m.Groups[1].Value.Replace("\t", "  ").Length / 2;
                string marker = char.IsDigit(m.Groups[2].Value[0]) ? m.Groups[2].Value : level > 0 ? "◦" : "•";
                var grid = new Grid { Margin = new Thickness(level * 16, 0, 0, 3) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.Children.Add(new TextBlock { Text = marker, Foreground = Muted, FontSize = 12 });
                var item = Text(m.Groups[3].Value, Body);
                Grid.SetColumn(item, 1);
                grid.Children.Add(item);
                Children.Add(grid);
            } else if (line.StartsWith(">")) {
                FlushPara();
                var q = Text(line.TrimStart('>', ' '), Muted);
                Children.Add(new Border {
                    BorderBrush = CodeBack, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0),
                    Margin = new Thickness(0, 0, 0, 6), Child = q,
                });
            } else if (Children.Count > 0 && para.Count == 0 && raw.StartsWith("  ") && Children[Children.Count - 1] is Grid prev
                       && prev.Children.Count == 2 && prev.Children[1] is TextBlock prevText) {
                // An indented line right under a list item continues it.
                prevText.Inlines.Add(new LineBreak());
                AddInlines(prevText.Inlines, line.Trim(), Body);
            } else {
                para.Add(line);
            }
        }
        if (inCode)
            para.Add(code.ToString());
        FlushPara();
        if (Children.Count > 0 && Children[Children.Count - 1] is FrameworkElement last)
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
    }

    void Add(FrameworkElement e, double left, double bottom, double top = 0)
    {
        e.Margin = new Thickness(left, top, 0, bottom);
        Children.Add(e);
    }

    static TextBlock Text(string markdown, Brush brush)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = brush, FontSize = 12.5, LineHeight = 18 };
        string[] lines = markdown.Split('\n');
        for (int i = 0; i < lines.Length; i++) {
            if (i > 0)
                tb.Inlines.Add(new LineBreak());
            AddInlines(tb.Inlines, lines[i], brush);
        }
        return tb;
    }

    static void AddInlines(InlineCollection inlines, string text, Brush brush)
    {
        int at = 0;
        foreach (Match m in Inline.Matches(text)) {
            if (m.Index > at)
                inlines.Add(new Run(Unescape(text.Substring(at, m.Index - at))));
            if (m.Groups["code"].Success) {
                inlines.Add(new Run(m.Value.Trim('`')) { FontFamily = Mono, Background = CodeBack });
            } else if (m.Groups["alt"].Success) {
                if (m.Groups["alt"].Length > 0)
                    inlines.Add(new Run(m.Groups["alt"].Value) { Foreground = Muted });
            } else if (m.Groups["url"].Success) {
                inlines.Add(Link(Unescape(m.Groups["text"].Value), m.Groups["url"].Value));
            } else if (m.Groups["bold"].Success) {
                var bold = new Bold { Foreground = Brushes.White };
                AddInlines(bold.Inlines, m.Value.Substring(2, m.Value.Length - 4), brush);
                inlines.Add(bold);
            } else if (m.Groups["italic"].Success) {
                var italic = new Italic();
                AddInlines(italic.Inlines, m.Value.Substring(1, m.Value.Length - 2), brush);
                inlines.Add(italic);
            } else {
                inlines.Add(Link(m.Value, m.Value));
            }
            at = m.Index + m.Length;
        }
        if (at < text.Length)
            inlines.Add(new Run(Unescape(text.Substring(at))));
    }

    static string Unescape(string s) => Regex.Replace(s, @"\\([\\`*_{}\[\]()#+\-.!<>])", "$1");

    static Inline Link(string text, string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new Run(text);
        var link = new Hyperlink(new Run(text)) { Foreground = LinkBrush, ToolTip = url };
        link.Click += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return link;
    }
}
