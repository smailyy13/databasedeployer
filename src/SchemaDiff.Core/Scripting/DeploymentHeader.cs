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
        // Yalnızca hedef veritabanını seç — yanlış DB'ye (ör. master) çalıştırmayı önler.
        // Açıklama/başlık bloğu yok (production için sade script).
        if (runOnDatabase is null) return string.Empty;
        return UseDatabase(runOnDatabase).TrimEnd() + Environment.NewLine + Environment.NewLine;
    }

    /// <summary>Bir bölümün çalışacağı DB'yi seçen <c>USE [db]; GO</c> bloğu (bracket-safe).</summary>
    public static string UseDatabase(string database)
    {
        var safe = database.Replace("]", "]]");
        return $"USE [{safe}];{Environment.NewLine}GO{Environment.NewLine}";
    }
}
