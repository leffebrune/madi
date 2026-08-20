using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Madi.GUI.Presentation;

public enum MarkdownBlockKind
{
    Paragraph,
    Heading,
    Code,
    Quote,
    ListItem,
    Table,
    Rule,
    BlockedHtml,
}

public sealed record MarkdownBlockModel(
    MarkdownBlockKind Kind,
    string Text,
    int Level = 0,
    string? Language = null,
    IReadOnlyList<IReadOnlyList<string>>? Rows = null);

public static class MarkdownDocumentParser
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static IReadOnlyList<MarkdownBlockModel> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var document = Markdown.Parse(markdown, Pipeline);
        var result = new List<MarkdownBlockModel>();
        AppendBlocks(document, result, 0);
        return result;
    }

    private static void AppendBlocks(
        ContainerBlock container,
        List<MarkdownBlockModel> result,
        int listDepth)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    result.Add(new MarkdownBlockModel(
                        MarkdownBlockKind.Heading,
                        GetInlineText(heading.Inline),
                        heading.Level));
                    break;
                case ParagraphBlock paragraph:
                    result.Add(new MarkdownBlockModel(
                        listDepth > 0 ? MarkdownBlockKind.ListItem : MarkdownBlockKind.Paragraph,
                        GetInlineText(paragraph.Inline),
                        listDepth));
                    break;
                case FencedCodeBlock fenced:
                    result.Add(new MarkdownBlockModel(
                        MarkdownBlockKind.Code,
                        fenced.Lines.ToString(),
                        Language: fenced.Info));
                    break;
                case CodeBlock code:
                    result.Add(new MarkdownBlockModel(
                        MarkdownBlockKind.Code,
                        code.Lines.ToString()));
                    break;
                case QuoteBlock quote:
                    var quoteItems = new List<MarkdownBlockModel>();
                    AppendBlocks(quote, quoteItems, listDepth);
                    result.Add(new MarkdownBlockModel(
                        MarkdownBlockKind.Quote,
                        string.Join(Environment.NewLine, quoteItems.Select(static item => item.Text))));
                    break;
                case ListBlock list:
                    foreach (var child in list.OfType<ListItemBlock>())
                    {
                        AppendBlocks(child, result, listDepth + 1);
                    }
                    break;
                case Table table:
                    result.Add(new MarkdownBlockModel(
                        MarkdownBlockKind.Table,
                        string.Empty,
                        Rows: ParseTable(table)));
                    break;
                case ThematicBreakBlock:
                    result.Add(new MarkdownBlockModel(MarkdownBlockKind.Rule, string.Empty));
                    break;
                case HtmlBlock:
                    result.Add(new MarkdownBlockModel(
                        MarkdownBlockKind.BlockedHtml,
                        "HTML 콘텐츠는 표시하지 않습니다."));
                    break;
                case ContainerBlock nested:
                    AppendBlocks(nested, result, listDepth);
                    break;
            }
        }
    }

    private static IReadOnlyList<IReadOnlyList<string>> ParseTable(Table table)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var row in table.OfType<TableRow>())
        {
            rows.Add(row.OfType<TableCell>()
                .Select(cell => string.Join(
                    Environment.NewLine,
                    cell.OfType<ParagraphBlock>().Select(paragraph => GetInlineText(paragraph.Inline))))
                .ToArray());
        }
        return rows;
    }

    private static string GetInlineText(ContainerInline? inline)
    {
        if (inline is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        AppendInline(inline.FirstChild, parts);
        return string.Concat(parts);
    }

    private static void AppendInline(Inline? inline, List<string> parts)
    {
        while (inline is not null)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    parts.Add(literal.Content.ToString());
                    break;
                case CodeInline code:
                    parts.Add(code.Content);
                    break;
                case LineBreakInline:
                    parts.Add(Environment.NewLine);
                    break;
                case LinkInline link when link.IsImage:
                    parts.Add($"[이미지: {GetChildrenText(link)}]");
                    break;
                case ContainerInline container:
                    AppendInline(container.FirstChild, parts);
                    break;
            }
            inline = inline.NextSibling;
        }
    }

    private static string GetChildrenText(ContainerInline inline)
    {
        var parts = new List<string>();
        AppendInline(inline.FirstChild, parts);
        return string.Concat(parts);
    }
}
