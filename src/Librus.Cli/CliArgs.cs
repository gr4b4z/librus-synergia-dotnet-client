namespace Librus.Cli;

/// <summary>Tiny, dependency-free argument parser: <c>verb [sub] --flag value --switch</c>.</summary>
internal sealed class CliArgs
{
    private static readonly HashSet<string> Switches = ["--unread", "--once", "--dry-run", "--verbose", "--raw", "--help", "-h", "--version"];

    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);

    public CliArgs(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith('-'))
            {
                var name = arg;
                string? value = null;
                var eq = arg.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    name = arg[..eq];
                    value = arg[(eq + 1)..];
                }
                else if (!Switches.Contains(arg))
                {
                    if (i + 1 >= args.Count)
                    {
                        throw new LibrusException(LibrusErrorCodes.InvalidArgument, $"Option {arg} requires a value.");
                    }

                    value = args[++i];
                }

                if (!_options.TryGetValue(name, out var list))
                {
                    _options[name] = list = [];
                }

                list.Add(value ?? "true");
            }
            else
            {
                Positional.Add(arg);
            }
        }
    }

    public List<string> Positional { get; } = [];

    public string? Get(string name) => _options.TryGetValue(name, out var v) ? v[^1] : null;

    public IReadOnlyList<string> GetAll(string name) => _options.TryGetValue(name, out var v)
        ? v.SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList()
        : [];

    public bool Has(string name) => _options.ContainsKey(name);

    public DateOnly? GetDate(string name)
    {
        var value = Get(name);
        if (value is null)
        {
            return null;
        }

        return DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            ? d
            : throw new LibrusException(LibrusErrorCodes.InvalidArgument, $"{name} must be a date in YYYY-MM-DD format.");
    }

    public int? GetInt(string name)
    {
        var value = Get(name);
        return value is null ? null : int.TryParse(value, out var n) ? n : throw new LibrusException(LibrusErrorCodes.InvalidArgument, $"{name} must be an integer.");
    }
}
