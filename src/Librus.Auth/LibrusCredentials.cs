using System.Text.Json;
using System.Text.Json.Serialization;

namespace Librus;

/// <summary>Konto LIBRUS (portal.librus.pl) credentials. <see cref="ToString"/> never prints the password.</summary>
public sealed record LibrusCredentials(string Email, string Password)
{
    public const string EmailEnv = "LIBRUS_PORTAL_EMAIL";
    public const string PasswordEnv = "LIBRUS_PORTAL_PASSWORD";
    public const string SecretsFileEnv = "LIBRUS_SECRETS_FILE";

    public override string ToString() => $"LibrusCredentials {{ Email = {Email}, Password = *** }}";

    /// <summary>Default secrets file: <c>~/.config/librus-net/secrets.json</c>.</summary>
    public static string DefaultSecretsPath => Path.Combine(LibrusPaths.ConfigDirectory, "secrets.json");

    /// <summary>
    /// Resolves credentials from environment variables first, then from the private secrets file
    /// (<c>LIBRUS_SECRETS_FILE</c> or <see cref="DefaultSecretsPath"/>).
    /// </summary>
    public static LibrusCredentials Resolve(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var email = getEnv(EmailEnv);
        var password = getEnv(PasswordEnv);
        if (!string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(password))
        {
            return new LibrusCredentials(email, password);
        }

        var path = getEnv(SecretsFileEnv);
        if (string.IsNullOrEmpty(path))
        {
            path = DefaultSecretsPath;
        }

        if (File.Exists(path))
        {
            return LoadFromFile(path);
        }

        throw new LibrusException(
            LibrusErrorCodes.ConfigurationError,
            $"Missing credentials. Set {EmailEnv} and {PasswordEnv}, or create {path} (mode 600) with {{\"email\":\"...\",\"password\":\"...\"}}.");
    }

    public static LibrusCredentials LoadFromFile(string path)
    {
        WarnIfPermissive(path);
        SecretsFile? secrets;
        try
        {
            using var stream = File.OpenRead(path);
            secrets = JsonSerializer.Deserialize(stream, SecretsJsonContext.Default.SecretsFile);
        }
        catch (JsonException)
        {
            // Do not include the parser message: it may echo part of the file content.
            throw new LibrusException(LibrusErrorCodes.ConfigurationError, $"Secrets file {path} is not valid JSON.");
        }

        if (secrets is null || string.IsNullOrEmpty(secrets.Email) || string.IsNullOrEmpty(secrets.Password))
        {
            throw new LibrusException(LibrusErrorCodes.ConfigurationError, $"Secrets file {path} must contain non-empty \"email\" and \"password\".");
        }

        return new LibrusCredentials(secrets.Email, secrets.Password);
    }

    private static void WarnIfPermissive(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = File.GetUnixFileMode(path);
        const UnixFileMode groupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & groupOrOther) != 0)
        {
            Console.Error.WriteLine($"warning: {path} is readable by group/others; run `chmod 600 {path}`.");
        }
    }
}

internal sealed class SecretsFile
{
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; set; }
}

/// <summary>
/// Optional non-Librus secrets (notifier tokens) resolved from the environment first, then from
/// the same private secrets file (e.g. <c>"telegramBotToken"</c>).
/// </summary>
public static class LibrusSecrets
{
    public static string? Get(string envName, string fileKey, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var value = getEnv(envName);
        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }

        var path = getEnv(LibrusCredentials.SecretsFileEnv);
        if (string.IsNullOrEmpty(path))
        {
            path = LibrusCredentials.DefaultSecretsPath;
        }

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var secrets = JsonSerializer.Deserialize(stream, SecretsJsonContext.Default.SecretsFile);
            return secrets?.Other is not null && secrets.Other.TryGetValue(fileKey, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

[JsonSerializable(typeof(SecretsFile))]
internal sealed partial class SecretsJsonContext : JsonSerializerContext;

/// <summary>Well-known filesystem locations (<c>~/.config/librus-net</c>, overridable with <c>LIBRUS_CONFIG_DIR</c>).</summary>
public static class LibrusPaths
{
    public static string ConfigDirectory
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("LIBRUS_CONFIG_DIR");
            if (!string.IsNullOrEmpty(overrideDir))
            {
                return overrideDir;
            }

            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var baseDir = !string.IsNullOrEmpty(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(baseDir, "librus-net");
        }
    }

    public static string StateDirectory => Path.Combine(ConfigDirectory, "state");
}
