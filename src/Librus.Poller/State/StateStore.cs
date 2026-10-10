using System.Text.Json;

namespace Librus.Poller.State;

public interface IStateStore
{
    Task<ChildState> LoadAsync(int childId, CancellationToken ct = default);

    Task SaveAsync(ChildState state, CancellationToken ct = default);
}

/// <summary>JSON files, one per child, written atomically (temp file + rename) with mode 600.</summary>
public sealed class FileStateStore : IStateStore
{
    private readonly string _directory;

    public FileStateStore(string? directory = null) => _directory = directory ?? LibrusPaths.StateDirectory;

    public string PathFor(int childId) => Path.Combine(_directory, $"{childId}.json");

    public async Task<ChildState> LoadAsync(int childId, CancellationToken ct = default)
    {
        var path = PathFor(childId);
        if (!File.Exists(path))
        {
            return new ChildState { ChildId = childId };
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync(stream, PollerJsonContext.Default.ChildState, ct).ConfigureAwait(false);
            return state ?? new ChildState { ChildId = childId };
        }
        catch (JsonException)
        {
            // Keep the corrupt file for inspection and start a fresh baseline (no notification storm).
            File.Move(path, path + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), overwrite: true);
            return new ChildState { ChildId = childId };
        }
    }

    public async Task SaveAsync(ChildState state, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        state.UpdatedAt = DateTimeOffset.UtcNow;
        var path = PathFor(state.ChildId);
        var temp = path + ".tmp-" + Environment.ProcessId;
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using (var stream = new FileStream(temp, options))
        {
            await JsonSerializer.SerializeAsync(stream, state, PollerJsonContext.Default.ChildState, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}
