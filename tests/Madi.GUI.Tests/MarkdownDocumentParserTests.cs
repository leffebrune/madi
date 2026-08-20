using Madi.GUI.Presentation;

namespace Madi.GUI.Tests;

public sealed class MarkdownDocumentParserTests
{
    [Fact]
    public void ParsesCommonChatMarkdownWithoutExecutingHtml()
    {
        var blocks = MarkdownDocumentParser.Parse(
            """
            # 제목

            **강조**와 [링크](https://example.com)

            - 첫째
            - 둘째

            ```csharp
            Console.WriteLine("안녕");
            ```

            | 이름 | 값 |
            | --- | --- |
            | Madi | 1 |

            <script>alert('x')</script>
            """);

        Assert.Contains(blocks, block => block is { Kind: MarkdownBlockKind.Heading, Text: "제목" });
        Assert.Contains(blocks, block => block.Kind == MarkdownBlockKind.ListItem);
        Assert.Contains(blocks, block => block is { Kind: MarkdownBlockKind.Code, Language: "csharp" });
        Assert.Contains(blocks, block => block.Kind == MarkdownBlockKind.Table);
        Assert.Contains(blocks, block => block.Text.Contains("<script>", StringComparison.Ordinal));
    }
}
