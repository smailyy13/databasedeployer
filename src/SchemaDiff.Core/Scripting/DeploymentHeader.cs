using System.Text;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

/// <summary>
/// Birleşik dağıtım script'inin en üstündeki TEK master başlık: kaynak/hedef, üretim
/// zamanı ve bölümlerin ÇALIŞMA SIRASI. Bölüm başlıkları bu metadata'yı tekrar etmez;
/// böylece script başında tek, düzenli bir özet olur.
/// </summary>
public static class DeploymentHeader
{
    /// <param name="runOnDatabase">
    /// Bu script'in üzerinde çalışacağı veritabanı → başa <c>USE [db]</c> yazılır.
    /// null verilirse USE yazılmaz (ör. ileri+ters karışık script; her bölüm kendi USE'unu koyar).
    /// </param>
    public static string Master(CompareResult result, string? generatedAt, bool allowDataLoss,
        string? runOnDatabase = null)
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine("/* ==========================================================================");
        sb.AppendLine("   SchemaDiff — DAĞITIM SCRIPT'İ");
        sb.AppendLine();
        sb.AppendLine($"   Kaynak (source) : {result.Source.Server} / {result.Source.Database}");
        sb.AppendLine($"   Hedef  (target) : {result.Target.Server} / {result.Target.Database}");
        if (generatedAt is not null) sb.AppendLine($"   Üretim          : {generatedAt}");
        sb.AppendLine();
        sb.AppendLine("   ÇALIŞMA SIRASI (yukarıdan aşağıya; her bölüm kendi transaction'ında):");
        sb.AppendLine("     1) Tipler / sequence / synonym / partition  — tablo & modüller bunlara bağlı");
        sb.AppendLine("     2) Tablolar   — CREATE, kolon ADD/ALTER/DROP, index & constraint");
        sb.AppendLine("     3) Modüller   — CREATE SCHEMA, view, fonksiyon, prosedür, trigger");
        sb.AppendLine("     4) Roller     — CREATE/ALTER/DROP ROLE ve üyelik");
        sb.AppendLine("     5) Extended property + izinler (GRANT/DENY)");
        sb.AppendLine();
        sb.AppendLine(allowDataLoss
            ? "   !! VERİ KAYBI ONAYLANDI — kolon/tablo silme ve tip daraltma script'e DAHİL."
            : "   Veri kaybı riski taşıyan adımlar script'e ALINMADI (sonda ayrıca raporlanır).");
        sb.AppendLine("   ========================================================================== */");
        sb.AppendLine();
        // Hedef veritabanını açıkça seç — yanlış DB'ye (ör. master) çalıştırmayı önler.
        if (runOnDatabase is not null)
        {
            sb.AppendLine(UseDatabase(runOnDatabase).TrimEnd());
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Bir bölümün çalışacağı DB'yi seçen <c>USE [db]; GO</c> bloğu (bracket-safe).</summary>
    public static string UseDatabase(string database)
    {
        var safe = database.Replace("]", "]]");
        return $"USE [{safe}];{Environment.NewLine}GO{Environment.NewLine}";
    }
}
