using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Madi.GUI;

public sealed class MarkdownView : UserControl
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownView),
        new PropertyMetadata(string.Empty, OnMarkdownChanged));

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();
    private readonly StackPanel _root = new() { Spacing = 8 };

    public MarkdownView()
    {
        Content = _root;
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static void OnMarkdownChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((MarkdownView)sender).Render(args.NewValue as string ?? string.Empty);
    }

    private void Render(string markdown)
    {
        _root.Children.Clear();
        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        AppendBlocks(document, _root, listDepth: 0);
    }

    private static void AppendBlocks(ContainerBlock container, Panel target, int listDepth)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    target.Children.Add(CreateTextBlock(
                        heading.Inline,
                        heading.Level switch { 1 => 26, 2 => 22, 3 => 19, _ => 16 },
                        FontWeights.SemiBold));
                    break;
                case ParagraphBlock paragraph:
                    var paragraphView = CreateTextBlock(paragraph.Inline, 15, FontWeights.Normal);
                    if (listDepth > 0)
                    {
                        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                        row.Children.Add(new TextBlock { Text = "•", FontSize = 15 });
                        row.Children.Add(paragraphView);
                        target.Children.Add(row);
                    }
                    else
                    {
                        target.Children.Add(paragraphView);
                    }
                    break;
                case FencedCodeBlock fenced:
                    target.Children.Add(CreateCodeBlock(fenced.Lines.ToString(), fenced.Info));
                    break;
                case CodeBlock code:
                    target.Children.Add(CreateCodeBlock(code.Lines.ToString(), null));
                    break;
                case QuoteBlock quote:
                    var quotePanel = new StackPanel { Spacing = 6 };
                    AppendBlocks(quote, quotePanel, listDepth);
                    target.Children.Add(new Border
                    {
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                        Padding = new Thickness(12, 4, 4, 4),
                        Child = quotePanel,
                    });
                    break;
                case ListBlock list:
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        AppendBlocks(item, target, listDepth + 1);
                    }
                    break;
                case Table table:
                    target.Children.Add(CreateTable(table));
                    break;
                case ThematicBreakBlock:
                    target.Children.Add(new Border
                    {
                        Height = 1,
                        Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                        Margin = new Thickness(0, 4, 0, 4),
                    });
                    break;
                case HtmlBlock:
                    target.Children.Add(new TextBlock
                    {
                        Text = "HTML 콘텐츠는 표시하지 않습니다.",
                        FontStyle = Windows.UI.Text.FontStyle.Italic,
                        Opacity = 0.7,
                    });
                    break;
                case ContainerBlock nested:
                    AppendBlocks(nested, target, listDepth);
                    break;
            }
        }
    }

    private static TextBlock CreateTextBlock(
        ContainerInline? inline,
        double fontSize,
        Windows.UI.Text.FontWeight weight)
    {
        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontSize = fontSize,
            FontWeight = weight,
        };
        if (inline is not null)
        {
            AppendInlines(inline.FirstChild, textBlock.Inlines, bold: false, italic: false);
        }
        return textBlock;
    }

    private static void AppendInlines(
        Markdig.Syntax.Inlines.Inline? inline,
        InlineCollection target,
        bool bold,
        bool italic)
    {
        while (inline is not null)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(CreateRun(literal.Content.ToString(), bold, italic));
                    break;
                case CodeInline code:
                    target.Add(new Run
                    {
                        Text = code.Content,
                        FontFamily = new FontFamily("Cascadia Mono"),
                        FontWeight = FontWeights.SemiBold,
                    });
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case EmphasisInline emphasis:
                    AppendInlines(
                        emphasis.FirstChild,
                        target,
                        bold || emphasis.DelimiterCount >= 2,
                        italic || emphasis.DelimiterCount == 1);
                    break;
                case LinkInline link when link.IsImage:
                    target.Add(new Run
                    {
                        Text = $"[이미지: {GetInlineText(link)}]",
                        FontStyle = Windows.UI.Text.FontStyle.Italic,
                    });
                    break;
                case LinkInline link:
                    var hyperlink = new Hyperlink();
                    AppendInlines(link.FirstChild, hyperlink.Inlines, bold, italic);
                    var url = link.Url;
                    hyperlink.Click += async (_, _) =>
                    {
                        if (TryGetSafeUri(url, out var uri))
                        {
                            await Launcher.LaunchUriAsync(uri);
                        }
                    };
                    target.Add(hyperlink);
                    break;
                case HtmlInline:
                    target.Add(new Run { Text = "[HTML 생략]" });
                    break;
                case ContainerInline nested:
                    AppendInlines(nested.FirstChild, target, bold, italic);
                    break;
            }
            inline = inline.NextSibling;
        }
    }

    private static Run CreateRun(string text, bool bold, bool italic) => new()
    {
        Text = text,
        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
    };

    private static Border CreateCodeBlock(string code, string? language)
    {
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(language) ? "코드" : language.Trim(),
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var copy = new Button { Content = "복사", Padding = new Thickness(8, 2, 8, 2) };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(code);
            Clipboard.SetContent(package);
        };
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);
        panel.Children.Add(header);
        var text = new TextBlock
        {
            Text = code,
            FontFamily = new FontFamily("Cascadia Mono"),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var scroller = new ScrollViewer
        {
            Content = text,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
        };
        Grid.SetRow(scroller, 1);
        panel.Children.Add(scroller);
        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Child = panel,
        };
    }

    private static FrameworkElement CreateTable(Table table)
    {
        var grid = new Grid();
        var rows = table.OfType<TableRow>().ToArray();
        var columnCount = rows.Select(static row => row.Count).DefaultIfEmpty(0).Max();
        for (var column = 0; column < columnCount; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
        }
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var columnIndex = 0;
            foreach (var cell in rows[rowIndex].OfType<TableCell>())
            {
                var text = string.Join(
                    Environment.NewLine,
                    cell.OfType<ParagraphBlock>().Select(paragraph => GetInlineText(paragraph.Inline)));
                var border = new Border
                {
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 5, 8, 5),
                    Child = new TextBlock
                    {
                        Text = text,
                        TextWrapping = TextWrapping.Wrap,
                        FontWeight = rows[rowIndex].IsHeader ? FontWeights.SemiBold : FontWeights.Normal,
                    },
                };
                Grid.SetRow(border, rowIndex);
                Grid.SetColumn(border, columnIndex++);
                grid.Children.Add(border);
            }
        }
        return new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
        };
    }

    private static string GetInlineText(ContainerInline? inline)
    {
        if (inline is null)
        {
            return string.Empty;
        }
        var parts = new List<string>();
        var current = inline.FirstChild;
        while (current is not null)
        {
            if (current is LiteralInline literal)
            {
                parts.Add(literal.Content.ToString());
            }
            else if (current is CodeInline code)
            {
                parts.Add(code.Content);
            }
            else if (current is ContainerInline container)
            {
                parts.Add(GetInlineText(container));
            }
            current = current.NextSibling;
        }
        return string.Concat(parts);
    }

    private static bool TryGetSafeUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            && candidate.Scheme is "https" or "http" or "mailto")
        {
            uri = candidate;
            return true;
        }
        uri = null!;
        return false;
    }
}
