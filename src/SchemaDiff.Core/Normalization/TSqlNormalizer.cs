using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SchemaDiff.Core.Normalization;

public sealed record NormalizationOptions(
    bool IgnoreWhitespace = true,
    bool IgnoreComments = true,
    bool IgnoreSemicolons = false,
    bool IgnoreKeywordCasing = false,
    // Tanımlayıcı alıntı biçimini birleştir: [a], "a" ve a aynı sayılır (harf büyüklüğü KORUNUR).
    // Semantik olarak doğrudur — parantez kimliği değiştirmez. Varsayılan açık.
    bool NormalizeIdentifierQuoting = true,
    // Tanımlayıcı harf büyüklüğünü küçük harfe indir: harfe DUYARSIZ karşılaştırmada
    // (caseSensitiveNames kapalı) [Foo] ile [foo] aynı objeyi/kolonu gösterir; gövdede de
    // aynı sayılmalı. Yalnız TANIMLAYICILARA uygulanır — string literal'lere DEĞİL.
    bool FoldIdentifierCase = false)
{
    public static readonly NormalizationOptions Default = new();
}

/// <summary>
/// T-SQL gövdelerini kanonik forma indirger. Regex ile DEĞİL, ScriptDom'un token
/// akışıyla çalışır — aksi hâlde string literal içindeki '--' ya da '/*' yorum
/// sanılıp gövde bozulur.
/// </summary>
public static class TSqlNormalizer
{
    /// <param name="quotedIdentifiers">Objenin sys.sql_modules.uses_quoted_identifier değeri.</param>
    public static string Normalize(string? sql, bool quotedIdentifiers = true, NormalizationOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(sql)) return string.Empty;
        options ??= NormalizationOptions.Default;

        var parser = new TSql160Parser(quotedIdentifiers);
        IList<ParseError> errors;
        IList<TSqlParserToken> tokens;
        using (var reader = new StringReader(sql))
        {
            tokens = parser.GetTokenStream(reader, out errors);
        }

        // Tokenize edilemeyen gövdeyi bozmaktansa kaba normalizasyona düş.
        if (errors.Count > 0 || tokens.Count == 0) return FallbackNormalize(sql);

        var sb = new StringBuilder(sql.Length);

        // Boşluk yok sayılırken ayırıcı boşluk YALNIZCA iki "kelime" karakterini (harf/rakam/_/
        // @/#/$) ayırmak için gerekir — aksi hâlde "SELECT" ile "1" birleşip "SELECT1" olurdu.
        // Noktalama/operatör çevresindeki boşluk (", ) ( = >= + ." vb.) anlamsızdır ve atılır;
        // böylece "a, b"≡"a,b", "a >= 1"≡"a>=1", "dbo . T"≡"dbo.T", "( a )"≡"(a)" olur.
        var pendingSpace = false;
        void Emit(string piece)
        {
            if (piece.Length == 0) return;
            if (pendingSpace && sb.Length > 0 && IsWordChar(sb[^1]) && IsWordChar(piece[0]))
                sb.Append(' ');
            pendingSpace = false;
            sb.Append(piece);
        }

        foreach (var token in tokens)
        {
            switch (token.TokenType)
            {
                case TSqlTokenType.EndOfFile:
                    break;

                case TSqlTokenType.WhiteSpace:
                    if (options.IgnoreWhitespace) pendingSpace = true;   // gerekirse ayırıcıya döner
                    else { sb.Append(token.Text); pendingSpace = false; }
                    break;

                case TSqlTokenType.SingleLineComment:
                case TSqlTokenType.MultilineComment:
                    // Yorum atılınca yerinde bir ayırıcı ihtimali kalır (boşlukla aynı kural).
                    if (options.IgnoreComments) pendingSpace = true;
                    else Emit(token.Text);
                    break;

                case TSqlTokenType.Semicolon:
                    if (!options.IgnoreSemicolons) Emit(token.Text);
                    // yok sayılıyorsa hiç yazma; çevredeki boşluk zaten pendingSpace olur
                    break;

                case TSqlTokenType.Identifier:
                {
                    var text = options.FoldIdentifierCase ? token.Text.ToLowerInvariant() : token.Text;
                    Emit(options.NormalizeIdentifierQuoting ? Bracket(text) : text);
                    break;
                }

                case TSqlTokenType.QuotedIdentifier:
                {
                    var inner = Unquote(token.Text);
                    if (options.FoldIdentifierCase) inner = inner.ToLowerInvariant();
                    Emit(options.NormalizeIdentifierQuoting
                        ? Bracket(inner)
                        : (options.FoldIdentifierCase ? token.Text.ToLowerInvariant() : token.Text));
                    break;
                }

                default:   // anahtar kelimeler, operatörler, virgül, literal'ler…
                    Emit(options.IgnoreKeywordCasing ? UpperIfKeyword(token) : token.Text);
                    break;
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Değer taşıyan token'lar (tanımlayıcı, literal, değişken) ASLA değiştirilmez —
    /// 'select' ile 'SELECT' aynı şeydir ama 'Ali' ile 'ALI' değildir.
    /// </summary>
    private static string UpperIfKeyword(TSqlParserToken token)
    {
        if (token.Text is null || token.Text.Length == 0) return token.Text ?? string.Empty;

        var carriesValue = token.TokenType is
            TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier or
            TSqlTokenType.AsciiStringLiteral or TSqlTokenType.UnicodeStringLiteral or
            TSqlTokenType.Integer or TSqlTokenType.Numeric or TSqlTokenType.Real or
            TSqlTokenType.Money or TSqlTokenType.HexLiteral or
            TSqlTokenType.Variable or TSqlTokenType.Label;

        if (carriesValue) return token.Text;

        foreach (var ch in token.Text)
            if (!char.IsAsciiLetter(ch) && ch != '_') return token.Text;

        return token.Text.ToUpperInvariant();
    }

    /// <summary>
    /// "Kelime" karakteri: yan yana gelince tek token'a kaynayabilecek karakterler
    /// (harf, rakam, alt çizgi, değişken/temp/para öneki). Yalnızca iki kelime karakteri
    /// arasında ayırıcı boşluk gerekir; noktalama/operatör çevresinde boşluk anlamsızdır.
    /// </summary>
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    /// <summary>Tanımlayıcıyı kanonik [ad] biçimine getirir; içteki ] kaçırılır. Harf büyüklüğü korunur.</summary>
    private static string Bracket(string inner) => $"[{inner.Replace("]", "]]")}]";

    /// <summary>[a] ya da "a" alıntısını iç değere indirger, kaçış çözer.</summary>
    private static string Unquote(string text)
    {
        if (text.Length >= 2 && text[0] == '[' && text[^1] == ']')
            return text[1..^1].Replace("]]", "]");
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            return text[1..^1].Replace("\"\"", "\"");
        return text;
    }

    /// <summary>Parse edilemeyen metin için: satır sonlarını ve boşlukları sadeleştir.</summary>
    private static string FallbackNormalize(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var lastWasSpace = false;
        foreach (var ch in sql)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }
        return sb.ToString().Trim();
    }
}
