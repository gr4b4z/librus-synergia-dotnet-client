using System.Text.Json;
using Librus.Api;
using Librus.Poller;
using Librus.Poller.Notifiers;
using Librus.Poller.Sources;
using Librus.Poller.State;

namespace Librus.Tests;

public class PollerTests
{
    private static readonly LibrusCredentials Creds = new("parent@example.com", "pw");

    [Fact]
    public void First_observation_is_a_silent_baseline()
    {
        var state = new CategoryState();
        Assert.Empty(SnapshotDiff.Apply(state, [Item("1"), Item("2")]));
        Assert.True(state.Initialized);

        var fresh = SnapshotDiff.Apply(state, [Item("1"), Item("2"), Item("3")]);
        Assert.Equal(["3"], fresh.Select(f => f.Id));
        Assert.Empty(SnapshotDiff.Apply(state, [Item("3")]));
    }

    [Fact]
    public void Seen_ids_are_capped()
    {
        var state = new CategoryState { Initialized = true };
        SnapshotDiff.Apply(state, Enumerable.Range(0, ChildState.MaxSeenPerCategory + 10).Select(i => Item(i.ToString())).ToList());
        Assert.Equal(ChildState.MaxSeenPerCategory, state.Seen.Count);
        Assert.Equal("10", state.Seen[0]);
    }

    [Fact]
    public async Task State_store_round_trips_atomically_with_private_mode()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var store = new FileStateStore(dir);
        var state = await store.LoadAsync(11);
        state.Categories["grades"] = new CategoryState { Initialized = true, Seen = ["a"] };
        state.Outbox.Add(new PendingNotification { Category = "grades", ItemId = "b", Title = "t" });
        await store.SaveAsync(state);

        var loaded = await store.LoadAsync(11);
        Assert.Equal(["a"], loaded.Categories["grades"].Seen);
        Assert.Equal("b", Assert.Single(loaded.Outbox).ItemId);
        Assert.Equal(["11.json"], Directory.GetFiles(dir).Select(Path.GetFileName));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.PathFor(11)));
        }
    }

    [Fact]
    public async Task Corrupt_state_starts_a_new_baseline()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "5.json"), "{not json");
        var state = await new FileStateStore(dir).LoadAsync(5);
        Assert.Empty(state.Categories);
        Assert.Single(Directory.GetFiles(dir, "5.json.corrupt-*"));
    }

    [Fact]
    public void ChangeRegister_entries_are_parsed_loosely()
    {
        using var doc = JsonDocument.Parse("""{"ChangeRegister":[{"Id":5,"Type":"Grade"},{"Id":"7","Resource":{"Url":"https://api.librus.pl/3.0/Notes/3"}}],"Url":"x"}""");
        var entries = ChangeRegisterSource.ParseEntries(doc.RootElement);
        Assert.Equal([5L, 7L], entries.Select(e => e.Id));
        Assert.Equal("Grade", entries[0].Kind);
        Assert.Equal("3", entries[1].Kind);

        using var bad = JsonDocument.Parse("""{"Status":"Error"}""");
        Assert.Equal(LibrusErrorCodes.ResponseValidationFailed,
            Assert.Throws<LibrusException>(() => ChangeRegisterSource.ParseEntries(bad.RootElement)).Code);
    }

    [Fact]
    public async Task Poller_checkpoints_before_notifying_and_redelivers_after_failure()
    {
        var gradesJson = """{"Resources":{},"Url":"u","Grades":[{"Id":1,"Grade":"5","Date":"2026-10-01","Subject":{"Id":9,"Url":"s"},"Category":{"Id":2,"Url":"c"}}]}""";
        var fake = new FakeLibrus().WithPortal();
        fake.On("GET", "api.librus.pl/3.0/Grades", (_, _) => FakeLibrus.Json(gradesJson));
        fake.OnJson("GET", "api.librus.pl/3.0/Subjects", """{"Resources":{},"Url":"u","Subjects":[{"Id":9,"Name":"Matematyka"}]}""");
        fake.OnJson("GET", "api.librus.pl/3.0/Grades/Categories", """{"Resources":{},"Url":"u","Categories":[{"Id":2,"Name":"Sprawdzian"}]}""");
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());
        var dir = Directory.CreateTempSubdirectory().FullName;
        var store = new FileStateStore(dir);
        var notifier = new FlakyNotifier();
        var options = new PollerOptions { Children = ["11"] };
        var poller = new PollerService(session, new CollectionDiffSource([Categories.Grades]), store, notifier, options);

        Assert.Equal(0, await poller.PollOnceAsync()); // baseline
        gradesJson = gradesJson.Replace("]}", """,{"Id":2,"Grade":"4","Date":"2026-10-02","Subject":{"Id":9,"Url":"s"},"Category":{"Id":2,"Url":"c"}}]}""", StringComparison.Ordinal);

        notifier.FailNext = true;
        Assert.Equal(0, await poller.PollOnceAsync());
        var pending = Assert.Single((await store.LoadAsync(11)).Outbox);
        Assert.Equal("Ania: ocena 4 — Matematyka", pending.Title);
        Assert.Equal("Sprawdzian · 2026-10-02", pending.Body);

        Assert.Equal(1, await poller.PollOnceAsync()); // redelivered, not re-detected
        Assert.Equal(["2"], notifier.Delivered.Select(n => n.ItemId));
        Assert.Empty((await store.LoadAsync(11)).Outbox);
        Assert.Equal(0, await poller.PollOnceAsync());
    }

    [Fact]
    public async Task Auto_strategy_falls_back_to_diff_when_changeregister_is_forbidden()
    {
        var fake = new FakeLibrus().WithPortal();
        fake.OnJson("GET", "api.librus.pl/3.0/ChangeRegister", "{}", System.Net.HttpStatusCode.Forbidden);
        fake.OnJson("GET", "api.librus.pl/3.0/Notes", """{"Resources":{},"Url":"u","Notes":[{"Id":1,"Text":"Brak zadania"}]}""");
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());
        var source = new AutoChangeSource(new ChangeRegisterSource(), new CollectionDiffSource([Categories.Notes]));
        var client = await session.ForChildAsync("11");
        var state = new ChildState { ChildId = 11 };
        await source.DetectAsync(client, state);
        await source.DetectAsync(client, state);
        Assert.True(state.Categories[Categories.Notes].Initialized);
        Assert.Equal(1, fake.Requests.Count(r => r.Path == "api.librus.pl/3.0/ChangeRegister"));
    }

    [Fact]
    public async Task ChangeRegister_cursor_only_emits_higher_ids()
    {
        var feed = """{"ChangeRegister":[{"Id":10,"Type":"Grade"}]}""";
        var fake = new FakeLibrus().WithPortal();
        fake.On("GET", "api.librus.pl/3.0/ChangeRegister", (_, _) => FakeLibrus.Json(feed));
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());
        var client = await session.ForChildAsync("11");
        var state = new ChildState { ChildId = 11 };
        var source = new ChangeRegisterSource();
        Assert.Empty(await source.DetectAsync(client, state));
        feed = """{"ChangeRegister":[{"Id":10,"Type":"Grade"},{"Id":12,"Type":"Note"}]}""";
        var fresh = await source.DetectAsync(client, state);
        Assert.Equal("12", Assert.Single(fresh).ItemId);
        Assert.Equal(12, state.ChangeRegisterCursor);
    }

    [Fact]
    public void Interval_is_jittered_between_bounds()
    {
        var poller = new PollerService(null!, new CollectionDiffSource(), new FileStateStore("/tmp"), new ConsoleNotifier(TextWriter.Null));
        Assert.Equal(TimeSpan.FromMinutes(12), poller.NextInterval(0));
        Assert.Equal(TimeSpan.FromMinutes(15), poller.NextInterval(1));
        Assert.InRange(poller.NextInterval(0.5), TimeSpan.FromMinutes(13), TimeSpan.FromMinutes(14));
    }

    private static ObservedItem Item(string id) => new("grades", id, "t");

    private sealed class FlakyNotifier : INotifier
    {
        public bool FailNext { get; set; }

        public List<PendingNotification> Delivered { get; } = [];

        public string Name => "flaky";

        public Task SendAsync(PendingNotification notification, CancellationToken ct = default)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new LibrusException(LibrusErrorCodes.NetworkError, "down");
            }

            Delivered.Add(notification);
            return Task.CompletedTask;
        }
    }
}
