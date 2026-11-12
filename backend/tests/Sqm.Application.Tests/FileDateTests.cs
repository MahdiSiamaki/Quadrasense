using Sqm.Application.DataImport;

namespace Sqm.Application.Tests;

/// <summary>
/// Where a file's date comes from - its own name, its archive's, or a person - and when none of
/// them is believed.
/// </summary>
public sealed class FileDateTests
{
    private static readonly DateOnly Today = new(2026, 11, 11);

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    [Fact]
    public void A_name_with_one_date_settles_it_and_the_archive_is_not_asked()
    {
        var decision = FileDate.Decide(
            "daily_subs_device_sim_info_2026-10-17.csv", "export_2026-10-18.zip", onlyFileInArchive: true, Today);

        Assert.True(decision.IsSettled);
        Assert.Equal(D(2026, 10, 17), decision.Date);
        Assert.Equal(FileDateSource.Name, decision.Source);
    }

    [Fact]
    public void The_only_file_of_an_archive_takes_the_archives_date_when_its_own_name_has_none()
    {
        // GSMA's delivery, as it arrives.
        var decision = FileDate.Decide(
            "DeviceDatabase.csv", "DeviceDatabaseTAC09Nov2026.rar", onlyFileInArchive: true, Today);

        Assert.True(decision.IsSettled);
        Assert.Equal(D(2026, 11, 9), decision.Date);
        Assert.Equal(FileDateSource.Archive, decision.Source);
    }

    [Fact]
    public void An_archive_of_several_files_dates_none_of_them()
    {
        // Its name is about the set: which of the files is "the 9th" it does not say.
        var decision = FileDate.Decide(
            "DeviceDatabase.csv", "DeviceDatabaseTAC09Nov2026.rar", onlyFileInArchive: false, Today);

        Assert.Equal(FileDateState.Missing, decision.State);
        Assert.Null(decision.Date);
        Assert.Empty(decision.Candidates);
    }

    [Theory]
    [InlineData("subs_03-04-2026.csv", "week_2026-04-03.zip", 2026, 4, 3)]
    [InlineData("subs_03-04-2026.csv", "week_2026-03-04.zip", 2026, 3, 4)]
    [InlineData("subs_2026-10-17_exported_2026-10-18.csv", "subs_17Oct2026.rar", 2026, 10, 17)]
    public void The_archives_date_settles_a_name_that_could_mean_it(
        string name, string archive, int year, int month, int day)
    {
        var decision = FileDate.Decide(name, archive, onlyFileInArchive: true, Today);

        Assert.True(decision.IsSettled);
        Assert.Equal(D(year, month, day), decision.Date);
        Assert.Equal(FileDateSource.Archive, decision.Source);
    }

    [Fact]
    public void An_archives_date_the_name_cannot_mean_settles_nothing()
    {
        var decision = FileDate.Decide(
            "subs_03-04-2026.csv", "sent_2026-04-20.zip", onlyFileInArchive: true, Today);

        Assert.Equal(FileDateState.Ambiguous, decision.State);
        Assert.Equal([D(2026, 3, 4), D(2026, 4, 3)], decision.Candidates);
    }

    [Theory]
    [InlineData("subs_03-04-2026.csv", null, FileDateState.Ambiguous, "2026-03-04,2026-04-03")]
    [InlineData("subs_2026-10-17_exported_2026-10-18.csv", null, FileDateState.Several, "2026-10-17,2026-10-18")]
    [InlineData("DeviceDatabase.csv", null, FileDateState.Missing, "")]
    [InlineData("", null, FileDateState.Missing, "")]
    // A name with nothing to offer leaves what the archive's name offers.
    [InlineData("DeviceDatabase.csv", "TAC_03-04-2026.rar", FileDateState.Ambiguous, "2026-03-04,2026-04-03")]
    public void A_name_that_does_not_settle_its_date_says_what_can_be_chosen(
        string name, string? archive, FileDateState state, string candidates)
    {
        var decision = FileDate.Decide(name, archive, onlyFileInArchive: archive is not null, Today);

        Assert.Equal(state, decision.State);
        Assert.False(decision.IsSettled);
        Assert.Null(decision.Date);
        Assert.Null(decision.Source);
        Assert.Equal(candidates, string.Join(',', decision.Candidates.Select(FileDate.Text)));
    }

    [Theory]
    // Tomorrow is allowed: the server keeps UTC and the files are named in Tehran.
    [InlineData("subs_2026-11-11.csv", true)]
    [InlineData("subs_2026-11-12.csv", true)]
    [InlineData("subs_2026-11-13.csv", false)]
    [InlineData("subs_2062-10-17.csv", false)]
    public void A_date_that_has_not_happened_is_not_taken_from_a_name(string name, bool taken)
    {
        var decision = FileDate.Decide(name, null, onlyFileInArchive: false, Today);

        Assert.Equal(taken, decision.IsSettled);
        if (!taken)
        {
            Assert.Equal(FileDateState.Future, decision.State);
            Assert.Null(decision.Date);
            Assert.Empty(decision.Candidates);
        }
    }

    [Fact]
    public void A_date_that_has_not_happened_is_not_taken_from_an_archive_either()
    {
        var decision = FileDate.Decide("DeviceDatabase.csv", "TAC09Nov2027.rar", onlyFileInArchive: true, Today);

        Assert.Equal(FileDateState.Future, decision.State);
    }

    [Fact]
    public void Of_two_readings_only_the_one_that_has_happened_is_offered()
    {
        // 12-11-2026 is 12 November (tomorrow) or 11 December (not yet): one choice is left, and it
        // is still a person's to make - the name was written a way that can be read twice.
        var decision = FileDate.Decide("subs_12-11-2026.csv", null, onlyFileInArchive: false, Today);

        Assert.Equal(FileDateState.Ambiguous, decision.State);
        Assert.Equal([D(2026, 11, 12)], decision.Candidates);
    }

    [Fact]
    public void A_person_may_choose_among_what_the_name_can_mean_and_nothing_else()
    {
        var names = FileDate.Decide("subs_03-04-2026.csv", null, onlyFileInArchive: false, Today);

        var chosen = FileDate.Accept(names, D(2026, 4, 3), Today, out var problem);
        Assert.NotNull(chosen);
        Assert.Null(problem);
        Assert.True(chosen.IsSettled);
        Assert.Equal(D(2026, 4, 3), chosen.Date);
        Assert.Equal(FileDateSource.Person, chosen.Source);

        Assert.Null(FileDate.Accept(names, D(2026, 4, 4), Today, out problem));
        Assert.Equal("The name can mean 2026-03-04 or 2026-04-03, not 2026-04-04.", problem);
    }

    [Fact]
    public void A_person_may_give_any_day_that_has_happened_to_a_name_with_no_date()
    {
        var names = FileDate.Decide("DeviceDatabase.csv", null, onlyFileInArchive: false, Today);

        var given = FileDate.Accept(names, D(2026, 11, 9), Today, out var problem);
        Assert.NotNull(given);
        Assert.Null(problem);
        Assert.Equal(D(2026, 11, 9), given.Date);
        Assert.Equal(FileDateSource.Person, given.Source);

        Assert.NotNull(FileDate.Accept(names, D(2026, 11, 12), Today, out _));
        Assert.Null(FileDate.Accept(names, D(2026, 11, 13), Today, out problem));
        Assert.Equal("2026-11-13 has not happened yet.", problem);
    }

    [Fact]
    public void A_person_does_not_overrule_a_name_that_says_its_date_plainly()
    {
        var names = FileDate.Decide("subs_2026-10-17.csv", null, onlyFileInArchive: false, Today);

        // Saying the same is no disagreement, and the name is still who said so.
        var same = FileDate.Accept(names, D(2026, 10, 17), Today, out var problem);
        Assert.NotNull(same);
        Assert.Null(problem);
        Assert.Equal(FileDateSource.Name, same.Source);

        Assert.Null(FileDate.Accept(names, D(2026, 10, 18), Today, out problem));
        Assert.StartsWith("The name already says 2026-10-17.", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("subs_2026-10-17.csv", "subs_2026-10-17.csv is dated 2026-10-17.")]
    [InlineData("subs_03-04-2026.csv", "The date in subs_03-04-2026.csv can be read two ways: 2026-03-04 or 2026-04-03. It waits for someone to say which.")]
    [InlineData("DeviceDatabase.csv", "There is no date in the name DeviceDatabase.csv. It waits for someone to give it.")]
    [InlineData("subs_2062-10-17.csv", "The date in subs_2062-10-17.csv has not happened yet, so it is not taken as the file's date. It waits for someone to give it.")]
    public void Why_a_file_waits_is_said_in_words(string name, string explanation)
    {
        Assert.Equal(explanation, FileDate.Decide(name, null, onlyFileInArchive: false, Today).Explain(name));
    }

    [Theory]
    [InlineData("SQM", true)]
    [InlineData("sqm", true)]
    [InlineData("TAC", false)]
    [InlineData("tac", false)]
    public void Only_a_daily_files_job_carries_the_date(string source, bool carries)
    {
        // A snapshot's job with a business date would wait behind every earlier dataset that failed.
        Assert.Equal(carries, ImportSources.JobCarriesTheDate(source));
    }
}
