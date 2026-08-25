using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
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

    private readonly TextBlock _document = new()
    {
        FontSize = 15,
        IsTextSelectionEnabled = true,
        TextWrapping = TextWrapping.Wrap,
    };

    public MarkdownView()
    {
        Content = _document;
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
        _document.Inlines.Clear();
        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        var state = new RenderState();
        AppendBlocks(document, _document.Inlines, state, listDepth: 0);
    }

    private static void AppendBlocks(
        ContainerBlock container,
        InlineCollection target,
        RenderState state,
        int listDepth)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    StartBlock(target, state, isListItem: false);
                    AppendInlines(
                        heading.Inline?.FirstChild,
                        target,
                        bold: true,
                        italic: false,
                        fontSize: heading.Level switch { 1 => 26, 2 => 22, 3 => 19, _ => 16 });
                    break;

                case ParagraphBlock paragraph:
                    var isListItem = listDepth > 0;
                    StartBlock(target, state, isListItem);
                    if (isListItem)
                    {
                        target.Add(new Run
                        {
                            Text = $"{new string('\u00A0', (listDepth - 1) * 4)}• ",
                            FontSize = 15,
                        });
                    }
                    AppendInlines(
                        paragraph.Inline?.FirstChild,
                        target,
                        bold: false,
                        italic: false,
                        fontSize: 15);
                    break;

                case FencedCodeBlock fenced:
                    AppendCodeBlock(target, state, fenced.Lines.ToString());
                    break;

                case CodeBlock code:
                    AppendCodeBlock(target, state, code.Lines.ToString());
                    break;

                case QuoteBlock quote:
                    AppendBlocks(quote, target, state, listDepth);
                    break;

                case ListBlock list:
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        AppendBlocks(item, target, state, listDepth + 1);
                    }
                    break;

                case Table table:
                    AppendTable(target, state, table);
                    break;

                case ThematicBreakBlock:
                    StartBlock(target, state, isListItem: false);
                    target.Add(new Run { Text = "────────", FontSize = 15 });
                    break;

                case HtmlBlock:
                    StartBlock(target, state, isListItem: false);
                    target.Add(new Run
                    {
                        Text = "HTML 콘텐츠는 표시하지 않습니다.",
                        FontStyle = Windows.UI.Text.FontStyle.Italic,
                        FontSize = 15,
                    });
                    break;

                case ContainerBlock nested:
                    AppendBlocks(nested, target, state, listDepth);
                    break;
            }
        }
    }

    private static void StartBlock(
        InlineCollection target,
        RenderState state,
        bool isListItem)
    {
        if (state.HasContent)
        {
            target.Add(new LineBreak());
            if (!state.PreviousWasListItem || !isListItem)
            {
                target.Add(new LineBreak());
            }
        }

        state.HasContent = true;
        state.PreviousWasListItem = isListItem;
    }

    private static void AppendCodeBlock(
        InlineCollection target,
        RenderState state,
        string code)
    {
        StartBlock(target, state, isListItem: false);
        target.Add(new Run
        {
            Text = code.TrimEnd('\r', '\n'),
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 14,
        });
    }

    private static void AppendTable(
        InlineCollection target,
        RenderState state,
        Table table)
    {
        StartBlock(target, state, isListItem: false);
        var rows = table.OfType<TableRow>().ToArray();
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            if (rowIndex > 0)
            {
                target.Add(new LineBreak());
            }

            var cells = rows[rowIndex]
                .OfType<TableCell>()
                .Select(cell => string.Join(
                    " ",
                    cell.OfType<ParagraphBlock>()
                        .Select(paragraph => GetInlineText(paragraph.Inline))));
            target.Add(new Run
            {
                Text = string.Join(" | ", cells),
                FontFamily = new FontFamily("Cascadia Mono"),
                FontWeight = rows[rowIndex].IsHeader ? FontWeights.SemiBold : FontWeights.Normal,
                FontSize = 14,
            });
        }
    }

    private static void AppendInlines(
        Markdig.Syntax.Inlines.Inline? inline,
        InlineCollection target,
        bool bold,
        bool italic,
        double fontSize)
    {
        while (inline is not null)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(CreateRun(literal.Content.ToString(), bold, italic, fontSize));
                    break;

                case CodeInline code:
                    target.Add(new Run
                    {
                        Text = code.Content,
                        FontFamily = new FontFamily("Cascadia Mono"),
                        FontSize = fontSize,
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
                        italic || emphasis.DelimiterCount == 1,
                        fontSize);
                    break;

                case LinkInline link when link.IsImage:
                    target.Add(new Run
                    {
                        Text = $"[이미지: {GetInlineText(link)}]",
                        FontStyle = Windows.UI.Text.FontStyle.Italic,
                        FontSize = fontSize,
                    });
                    break;

                case LinkInline link:
                    var hyperlink = new Hyperlink();
                    AppendInlines(link.FirstChild, hyperlink.Inlines, bold, italic, fontSize);
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
                    target.Add(CreateRun("[HTML 생략]", bold, italic, fontSize));
                    break;

                case ContainerInline nested:
                    AppendInlines(nested.FirstChild, target, bold, italic, fontSize);
                    break;
            }

            inline = inline.NextSibling;
        }
    }

    private static Run CreateRun(
        string text,
        bool bold,
        bool italic,
        double fontSize) => new()
    {
        Text = text,
        FontSize = fontSize,
        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
    };

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

    private sealed class RenderState
    {
        public bool HasContent { get; set; }
        public bool PreviousWasListItem { get; set; }
    }
}
