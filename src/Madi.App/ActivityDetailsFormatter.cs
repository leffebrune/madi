namespace Madi.App;

public static class ActivityDetailsFormatter
{
    public const int ResultLineLimit = 10;

    private const string TruncationMarker = "… (이후 내용 생략)";

    public static string FormatTool(string arguments, string? result) =>
        $"### 인자\n```json\n{arguments}\n```\n\n### 결과\n```text\n{TruncateResult(result)}\n```";

    public static string FormatSubagent(string prompt, string? result) =>
        $"### 프롬프트\n{prompt}\n\n### 결과\n{TruncateResult(result)}";

    public static string TruncateResult(string? result, int lineLimit = ResultLineLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lineLimit, 1);

        if (result is null)
        {
            return "결과 없음";
        }

        var line = 1;
        for (var index = 0; index < result.Length; index++)
        {
            var character = result[index];
            if (character is not ('\r' or '\n'))
            {
                continue;
            }

            var nextIndex = character == '\r'
                && index + 1 < result.Length
                && result[index + 1] == '\n'
                    ? index + 2
                    : index + 1;
            if (nextIndex >= result.Length)
            {
                return result;
            }

            if (line == lineLimit)
            {
                return $"{result[..index]}\n{TruncationMarker}";
            }

            line++;
            index = nextIndex - 1;
        }

        return result;
    }
}
