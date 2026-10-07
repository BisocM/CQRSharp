using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CQRSharp.Analyzers;

/// <summary>
///     The line ending a code fix writes: the file's own. The formatter's default newline is the platform's (CRLF on
///     Windows, LF elsewhere), so a fix that leaves a line break to the formatter writes CRLF into an LF file on Windows,
///     and LF into a CRLF file elsewhere.
/// </summary>
internal static class LineEndings
{
    /// <summary>The first line ending in the file, or LF in a file of one line.</summary>
    public static SyntaxTrivia Of(SyntaxNode root)
    {
        foreach (var trivia in root.DescendantTrivia())
            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                return trivia;

        return SyntaxFactory.LineFeed;
    }

    /// <summary>The whitespace that ends <paramref name="leading" />: the indentation of the line its token starts.</summary>
    public static SyntaxTriviaList Indentation(SyntaxTriviaList leading)
        => leading.Count > 0 && leading[leading.Count - 1].IsKind(SyntaxKind.WhitespaceTrivia)
            ? SyntaxFactory.TriviaList(leading[leading.Count - 1])
            : SyntaxFactory.TriviaList();
}
