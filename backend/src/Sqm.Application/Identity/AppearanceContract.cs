using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;

namespace Sqm.Application.Identity;

/// <summary>Reads and writes each person's appearance settings.</summary>
public interface IAppearanceStore
{
    /// <summary>What this person last saved, or <see langword="null"/> if they never have.</summary>
    Task<IReadOnlyDictionary<string, string>?> GetAsync(long userId, CancellationToken ct);

    /// <summary>Replaces this person's settings.</summary>
    Task SaveAsync(long userId, IReadOnlyDictionary<string, string> settings, CancellationToken ct);
}

/// <summary>The settings a person can change, and the values each may take.</summary>
/// <remarks>
/// One file for both sides, as the dashboard's widget contract is:
/// <c>frontend/src/lib/appearance-contract.json</c>, embedded here. The frontend builds its controls
/// from it and this validates against it, so a value cannot exist on one side only, and a new
/// setting is a change to that file. ADR-023.
/// </remarks>
public static class AppearanceContract
{
    private const string ResourceName = "Sqm.Application.Identity.appearance-contract.json";

    private static readonly JsonSerializerOptions ReadOptions =
        new() { PropertyNameCaseInsensitive = true };

    private static readonly FrozenDictionary<string, Setting> Settings = Load();

    /// <summary>
    /// Why these values cannot be saved, or <see langword="null"/> when they can.
    /// </summary>
    /// <remarks>
    /// A setting left out is fine, and takes its default. One that does not exist is not: the page
    /// only sends what the contract offered it, so anything else came from somewhere else, and
    /// this is a row that gets stored.
    /// </remarks>
    public static string? Problem(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        foreach (var (key, value) in values)
        {
            if (!Settings.TryGetValue(key, out var setting))
            {
                return $"There is no setting called {Shorten(key)}.";
            }

            if (!setting.Values.Contains(value))
            {
                return $"{Shorten(value)} is not a value of {key}.";
            }
        }

        return null;
    }

    /// <summary>Every setting, with the default for any left out.</summary>
    public static IReadOnlyDictionary<string, string> Complete(
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return Settings.ToDictionary(
            setting => setting.Key,
            setting => values.GetValueOrDefault(setting.Key, setting.Value.Default),
            StringComparer.Ordinal);
    }

    private static string Shorten(string? text) =>
        text is null ? "(nothing)" : text.Length <= 40 ? text : text[..40];

    private static FrozenDictionary<string, Setting> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{ResourceName} is not embedded.");

        var settings = JsonSerializer.Deserialize<Dictionary<string, Setting>>(stream, ReadOptions)
            ?? throw new InvalidOperationException($"{ResourceName} is empty.");

        return settings.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private sealed record Setting(string Default, IReadOnlyList<string> Values);
}
