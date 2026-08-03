using System.Collections.Concurrent;
using System.Diagnostics;
using SchemaDiff.Core;
using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Connections;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Normalization;

namespace SchemaDiff.Web;

public sealed class CompareSession
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required string Id { get; init; }
    public required string SourceLabel { get; init; }
    public required string TargetLabel { get; init; }

    public CompareResult? Comparison { get; private set; }
    public CompareResultDto? Dto { get; private set; }
    public string? Error { get; private set; }
    public bool Finished { get; private set; }

    public Task Changed
    {
        get { lock (_gate) return _signal.Task; }
    }

    public void Complete(CompareResult comparison, CompareResultDto dto)
    {
        lock (_gate)
        {
            Comparison = comparison;
            Dto = dto;
            Finished = true;
            Signal();
        }
    }

    public void Fail(string error)
    {
        lock (_gate)
        {
            Error = error;
            Finished = true;
            Signal();
        }
    }

    private void Signal()
    {
        var previous = _signal;
        _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }
}

public sealed class CompareService
{
    // Tarayıcıya gönderilecek değişiklik sayısı üst sınırı. Aşılırsa arayüzde
    // açıkça söylenir — sessizce kesmek "hepsi bu kadar" izlenimi verir.
    private const int MaxChanges = 5000;

    private readonly ConcurrentDictionary<string, CompareSession> _sessions = new();

    public CompareSession? Get(string id) => _sessions.GetValueOrDefault(id);

    public CompareSession Start(SqlConnectionInfo source, SqlConnectionInfo target, CompareOptionsDto options)
    {
        var session = new CompareSession
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            SourceLabel = source.Label,
            TargetLabel = target.Label,
        };
        _sessions[session.Id] = session;

        var snapshotOptions = new SnapshotOptions
        {
            Normalization = new NormalizationOptions(
                IgnoreWhitespace: options.IgnoreWhitespace,
                IgnoreComments: options.IgnoreComments,
                IgnoreSemicolons: options.IgnoreSemicolons,
                IgnoreKeywordCasing: options.IgnoreKeywordCasing),
            IgnoreSystemNamedConstraints = options.IgnoreSystemNamedConstraints,
            IgnoreIndexPhysicalOptions = options.IgnoreIndexPhysical,
            IgnoreColumnOrder = options.IgnoreColumnOrder,
            IgnoreCollation = options.IgnoreCollation,
            IgnoreIdentitySeed = options.IgnoreIdentitySeed,
            IgnoreExtendedProperties = options.IgnoreExtendedProperties,
            IgnorePermissions = options.IgnorePermissions,
            CaseSensitiveNames = options.CaseSensitiveNames,
            KeepDisplayScripts = true,   // alt panelde tam kodu bağlamıyla göstermek için
        };

        // Arka planda koş; istemci akışa bağlanmasa bile tamamlanır.
        _ = Task.Run(async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var gate = new ExtractionGate(Math.Max(1, options.MaxQueries));
                var comparison = await SchemaDiffService.CompareAsync(
                    source.ToConnectionString(), target.ToConnectionString(), snapshotOptions, gate);

                session.Complete(comparison, Map(session, comparison, stopwatch.Elapsed));
            }
            catch (Exception ex)
            {
                session.Fail(ex.Message.Split('\n')[0].Trim());
            }
        });

        return session;
    }

    public ObjectDetailDto? Detail(CompareSession session, string schema, string name, string kind)
    {
        if (session.Comparison is not { } comparison) return null;
        if (!Enum.TryParse<ObjectKind>(kind.Replace(" ", ""), ignoreCase: true, out var objectKind))
            objectKind = KindFromLabel(kind);

        var key = new ObjectKey(schema, name, objectKind);
        comparison.Source.Objects.TryGetValue(key, out var source);
        comparison.Target.Objects.TryGetValue(key, out var target);
        if (source is null && target is null) return null;

        return new ObjectDetailDto(
            schema, name, kind,
            source?.Canonical, target?.Canonical,
            source?.DisplayScript, target?.DisplayScript);
    }

    private static ObjectKind KindFromLabel(string label) => label switch
    {
        "Scalar Function" => ObjectKind.ScalarFunction,
        "Inline Function" => ObjectKind.InlineTableFunction,
        "Table Function" => ObjectKind.TableFunction,
        _ => ObjectKind.Unknown,
    };

    /// <summary>Arayüzdeki görünen tür adını ("Table", "Scalar Function", "Role" …) ObjectKind'e çevirir.</summary>
    public static ObjectKind ResolveKind(string label)
    {
        if (Enum.TryParse<ObjectKind>(label.Replace(" ", ""), ignoreCase: true, out var kind)
            && kind != ObjectKind.Unknown)
            return kind;
        return KindFromLabel(label);
    }

    private static CompareResultDto Map(CompareSession session, CompareResult comparison, TimeSpan duration)
    {
        var changes = ChangeCatalog.Build(comparison);

        var changeDtos = changes
            .Take(MaxChanges)
            .Select(c => new ObjectChangeDto(
                c.Action.ToString(), c.ObjectType, c.Schema, c.Name,
                [.. c.Children.Select(ch => new ChildChangeDto(
                    ch.Action.ToString(), ch.Category, ch.ItemType, ch.Name, ch.QualifiedName, ch.Detail))],
                c.Risk.ToString(), c.TargetRowCount, c.WillBlock, c.ConditionalOnly, c.Indeterminate, c.Note))
            .ToArray();

        var risks = DeploymentRiskAnalyzer.Analyze(comparison)
            .Select(r => new TableRiskDto(
                r.Table.Schema, r.Table.Name, r.Risk.ToString(), r.WillBlock, r.ConditionalOnly, r.TargetRowCount,
                [.. r.Findings.OrderByDescending(f => f.Risk)
                    .Select(f => new RiskFindingDto(f.Column, f.Risk.ToString(), f.Description, f.Conditional))]))
            .ToArray();

        var triggers = comparison.Target.Objects.Values
            .Where(o => o.Key.Kind == ObjectKind.Trigger)
            .OrderBy(o => o.Key.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Key.Name, StringComparer.OrdinalIgnoreCase)
            .Select(o => new TriggerDto(
                o.Key.Schema, o.Key.Name, o.IsDisabled == true,
                comparison.Source.Objects.TryGetValue(o.Key, out var s) ? s.IsDisabled : null))
            .ToArray();

        var renames = RenameDetector.Detect(comparison)
            .Select(r => new RenameDto(r.Removed.ToString(), r.Added.ToString(), r.Basis))
            .ToArray();

        var warnings = comparison.Source.Report.Warnings
            .Select(w => $"{comparison.Source.Database}: {w}")
            .Concat(comparison.Target.Report.Warnings.Select(w => $"{comparison.Target.Database}: {w}"))
            .ToArray();

        return new CompareResultDto(
            session.Id, session.SourceLabel, session.TargetLabel,
            comparison.Source.Count, comparison.Target.Count, comparison.EqualCount,
            changes.Count(c => c.Action == ChangeAction.Add),
            changes.Count(c => c.Action == ChangeAction.Change),
            changes.Count(c => c.Action == ChangeAction.Delete),
            comparison.IndeterminateCount,
            duration.TotalMilliseconds,
            warnings,
            changeDtos,
            changes.Count > MaxChanges,
            risks,
            triggers,
            renames);
    }
}
