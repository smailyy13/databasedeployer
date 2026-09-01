using SchemaDiff.Core.Extraction;

namespace SchemaDiff.Tests;

public class CompareProgressTests
{
    // --- CompareProgress: sayaçlar ve monotonik aşama ---

    [Fact]
    public void Counts_queries_and_build_items()
    {
        var p = new CompareProgress();
        p.AddQueries(10);
        p.QueryCompleted();
        p.QueryCompleted();
        p.AddBuildItems(4);
        p.BuildItemCompleted();

        var s = p.Current;
        Assert.Equal(10, s.QueriesTotal);
        Assert.Equal(2, s.QueriesDone);
        Assert.Equal(4, s.BuildTotal);
        Assert.Equal(1, s.BuildDone);
    }

    [Fact]
    public void Phase_is_monotonic_never_goes_backward()
    {
        var p = new CompareProgress();
        p.EnterPhase(ComparePhase.Building);
        p.EnterPhase(ComparePhase.Extracting); // geri gitmemeli
        Assert.Equal(ComparePhase.Building, p.Current.Phase);

        p.EnterPhase(ComparePhase.Done);
        p.EnterPhase(ComparePhase.Comparing); // yine geri gitmemeli
        Assert.Equal(ComparePhase.Done, p.Current.Phase);
    }

    [Fact]
    public void Callback_fires_on_change()
    {
        var hits = 0;
        var p = new CompareProgress(_ => Interlocked.Increment(ref hits));
        p.AddQueries(1);
        p.QueryCompleted();
        p.EnterPhase(ComparePhase.Extracting);
        Assert.True(hits >= 3);
    }

    [Fact]
    public void AddQueries_ignores_nonpositive()
    {
        var p = new CompareProgress();
        p.AddQueries(0);
        p.AddQueries(-5);
        Assert.Equal(0, p.Current.QueriesTotal);
    }

    // --- ProgressMath: yüzde bantları ---

    private static ProgressSnapshot Snap(ComparePhase phase, int qDone = 0, int qTotal = 0, int bDone = 0, int bTotal = 0)
        => new(phase, qDone, qTotal, bDone, bTotal);

    [Fact]
    public void Percent_connecting_is_small_nonzero()
    {
        Assert.Equal(2, ProgressMath.Percent(Snap(ComparePhase.Connecting)));
    }

    [Fact]
    public void Percent_extracting_scales_with_query_completion()
    {
        Assert.Equal(ProgressMath.ExtractStart, ProgressMath.Percent(Snap(ComparePhase.Extracting, 0, 100)));
        var mid = ProgressMath.Percent(Snap(ComparePhase.Extracting, 50, 100));
        Assert.InRange(mid, ProgressMath.ExtractStart + 1, ProgressMath.ExtractEnd - 1);
        Assert.Equal(ProgressMath.ExtractEnd, ProgressMath.Percent(Snap(ComparePhase.Extracting, 100, 100)));
    }

    [Fact]
    public void Percent_extracting_with_no_queries_is_band_start()
    {
        Assert.Equal(ProgressMath.ExtractStart, ProgressMath.Percent(Snap(ComparePhase.Extracting, 0, 0)));
    }

    [Fact]
    public void Percent_building_scales_within_its_band()
    {
        Assert.Equal(ProgressMath.ExtractEnd, ProgressMath.Percent(Snap(ComparePhase.Building, bDone: 0, bTotal: 50)));
        Assert.Equal(ProgressMath.BuildEnd, ProgressMath.Percent(Snap(ComparePhase.Building, bDone: 50, bTotal: 50)));
    }

    [Fact]
    public void Percent_building_with_no_modules_jumps_to_band_end()
    {
        Assert.Equal(ProgressMath.BuildEnd, ProgressMath.Percent(Snap(ComparePhase.Building, bDone: 0, bTotal: 0)));
    }

    [Fact]
    public void Percent_done_is_hundred()
    {
        Assert.Equal(100, ProgressMath.Percent(Snap(ComparePhase.Done)));
    }

    [Fact]
    public void Percent_is_monotonic_across_phases()
    {
        var connecting = ProgressMath.Percent(Snap(ComparePhase.Connecting));
        var extractEnd = ProgressMath.Percent(Snap(ComparePhase.Extracting, 100, 100));
        var buildEnd = ProgressMath.Percent(Snap(ComparePhase.Building, bDone: 10, bTotal: 10));
        var comparing = ProgressMath.Percent(Snap(ComparePhase.Comparing));
        var done = ProgressMath.Percent(Snap(ComparePhase.Done));
        Assert.True(connecting < extractEnd && extractEnd <= buildEnd && buildEnd < comparing && comparing < done);
    }

    // --- ProgressMath: ETA ---

    [Fact]
    public void Eta_hidden_when_percent_too_low()
    {
        Assert.Null(ProgressMath.EtaSeconds(5, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Eta_hidden_before_one_second_elapsed()
    {
        Assert.Null(ProgressMath.EtaSeconds(50, TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public void Eta_hidden_at_completion()
    {
        Assert.Null(ProgressMath.EtaSeconds(100, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Eta_extrapolates_linearly()
    {
        // %25'te 2 sn geçtiyse kalan ≈ 2 * 75/25 = 6 sn.
        Assert.Equal(6, ProgressMath.EtaSeconds(25, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Eta_rounds_up()
    {
        // %40'ta 1 sn geçtiyse kalan = 1 * 60/40 = 1.5 → yukarı yuvarla 2.
        Assert.Equal(2, ProgressMath.EtaSeconds(40, TimeSpan.FromSeconds(1)));
    }
}
