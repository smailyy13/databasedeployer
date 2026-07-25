using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.SqlServer.Dac.Compare;
using SchemaDiff.Core;

// Head-to-head: SSDT / VS Code "Schema Compare" ile aynı işi yapan DacFx
// SchemaComparison API'si vs. bu repodaki hash tabanlı motor.
// Aynı iki veritabanı, aynı makine, aynı anda başka yük yok.

Console.OutputEncoding = Encoding.UTF8;

if (args.Length < 2)
{
    Console.WriteLine("""
        Kullanım:
          SchemaDiff.Bench "<source-conn>" "<target-conn>" [tekrar]

        Örnek:
          SchemaDiff.Bench "Server=localhost;Database=Big1;Integrated Security=true;TrustServerCertificate=true" ^
                           "Server=localhost;Database=Big2;Integrated Security=true;TrustServerCertificate=true" 3
        """);
    return 2;
}

var sourceConnection = args[0];
var targetConnection = args[1];
var runs = args.Length > 2 && int.TryParse(args[2], out var parsed) ? parsed : 3;

Console.WriteLine($"Tekrar sayısı: {runs}  (ilk koşu ısınma sayılır ve ortalamaya girmez)\n");

var fastTimings = new List<TimeSpan>();
var dacFxTimings = new List<TimeSpan>();
var fastCount = 0;
var dacFxCount = 0;

for (var run = 0; run <= runs; run++)
{
    var label = run == 0 ? "ısınma" : $"koşu {run}";

    var swFast = Stopwatch.StartNew();
    var fastResult = await SchemaDiffService.CompareAsync(sourceConnection, targetConnection);
    swFast.Stop();
    fastCount = fastResult.Differences.Count;
    if (run > 0) fastTimings.Add(swFast.Elapsed);

    var swDac = Stopwatch.StartNew();
    var comparison = new SchemaComparison(
        new SchemaCompareDatabaseEndpoint(sourceConnection),
        new SchemaCompareDatabaseEndpoint(targetConnection));
    var dacResult = comparison.Compare();
    swDac.Stop();
    dacFxCount = dacResult.Differences.Count();
    if (run > 0) dacFxTimings.Add(swDac.Elapsed);

    Console.WriteLine(
        $"  {label,-8}  SchemaDiff {Fmt(swFast.Elapsed)}   DacFx {Fmt(swDac.Elapsed)}   " +
        $"→ {Ratio(swDac.Elapsed, swFast.Elapsed)}x");
}

var fastAverage = Average(fastTimings);
var dacFxAverage = Average(dacFxTimings);

Console.WriteLine();
Console.WriteLine("=".PadRight(64, '='));
Console.WriteLine($"  SchemaDiff ortalama : {Fmt(fastAverage)}   ({fastCount} fark)");
Console.WriteLine($"  DacFx      ortalama : {Fmt(dacFxAverage)}   ({dacFxCount} fark)");
Console.WriteLine($"  HIZ KAZANCI         : {Ratio(dacFxAverage, fastAverage)}x");
Console.WriteLine("=".PadRight(64, '='));
Console.WriteLine();
Console.WriteLine("""
    Not: fark sayıları birebir eşit olmak zorunda değil — DacFx izinler, extended
    property'ler ve tip tanımları gibi bu PoC'nin henüz kapsamadığı obje sınıflarını
    da karşılaştırır. Anlamlı kıyas, kapsam eşitlendikten sonra yapılmalıdır.
    """);

return 0;

static string Fmt(TimeSpan value) =>
    $"{value.TotalMilliseconds.ToString("N0", CultureInfo.InvariantCulture),9} ms";

static TimeSpan Average(List<TimeSpan> values) =>
    values.Count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks((long)values.Average(v => v.Ticks));

static string Ratio(TimeSpan slow, TimeSpan fast) =>
    fast.Ticks == 0 ? "?" : (slow.TotalMilliseconds / fast.TotalMilliseconds).ToString("N1", CultureInfo.InvariantCulture);
