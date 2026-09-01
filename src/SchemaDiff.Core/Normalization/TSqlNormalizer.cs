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
    bool NormalizeIdentifierQuoting = true)
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
        foreach (var token in tokens)
        {
            switch (token.TokenType)
            {
                case TSqlTokenType.EndOfFile:
                    break;

                case TSqlTokenType.WhiteSpace:
                    if (options.IgnoreWhitespace)
                    {
                        if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                    }
                    else sb.Append(token.Text);
                    break;

                case TSqlTokenType.SingleLineComment:
                case TSqlTokenType.MultilineComment:
                    if (!options.IgnoreComments) sb.Append(token.Text);
                    // Yorumu attıktan sonra iki token'ın yapışmaması için ayırıcı bırak.
                    else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                    break;

                case TSqlTokenType.Semicolon:
                    if (!options.IgnoreSemicolons)
                    {
                        TrimPendingSpace(sb, options.IgnoreWhitespace);
                        sb.Append(token.Text);
                    }
                    break;

                // Noktalı virgül/virgülden ÖNCE ayırıcı boşluk anlamsızdır. Boşluk yok sayılırken
                // (özellikle bir yorum atıldıktan sonra kalan) boşluğu at ki "[X] ;" ile "[X];"
                // ya da "a , b" ile "a, b" eşit sayılsın — yoksa yorum/boşluk yok saymak işe yaramaz.
                case TSqlTokenType.Comma:
                    TrimPendingSpace(sb, options.IgnoreWhitespace);
                    sb.Append(token.Text);
                    break;

                case TSqlTokenType.Identifier:
                    sb.Append(options.NormalizeIdentifierQuoting ? Bracket(token.Text) : token.Text);
                    break;

                case TSqlTokenType.QuotedIdentifier:
                    sb.Append(options.NormalizeIdentifierQuoting ? Bracket(Unquote(token.Text)) : token.Text);
                    break;

                default:
                    sb.Append(options.IgnoreKeywordCasing ? UpperIfKeyword(token) : token.Text);
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

    /// <summary>Boşluk yok sayılırken kuyrukta kalan tek ayırıcı boşluğu (varsa) atar.</summary>
    private static void TrimPendingSpace(StringBuilder sb, bool ignoreWhitespace)
    {
        if (ignoreWhitespace && sb.Length > 0 && sb[^1] == ' ') sb.Length--;
    }

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
