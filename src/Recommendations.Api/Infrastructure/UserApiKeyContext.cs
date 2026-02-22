namespace Recommendations.Api.Infrastructure;

/// <summary>
/// AsyncLocal-based per-request store for user-provided API key + model overrides.
/// Set by <see cref="Pipeline.RecommendationOrchestrator"/> at the start of each request.
/// AsyncLocal is safely scoped per async execution context, so concurrent requests
/// each have their own isolated values with no cross-contamination.
/// </summary>
public static class UserApiKeyContext
{
    private static readonly AsyncLocal<IReadOnlyDictionary<string, string>?> _keys = new();

    /// <summary>Sets the user-provided overrides for the current async execution context.</summary>
    public static void Set(IReadOnlyDictionary<string, string>? keys) => _keys.Value = keys;

    /// <summary>
    /// Returns the user-provided key override if present and non-empty;
    /// otherwise returns <paramref name="configuredKey"/>.
    /// </summary>
    public static string GetEffectiveKey(string providerKey, string configuredKey)
    {
        var dict = _keys.Value;
        if (dict is not null && dict.TryGetValue(providerKey, out var userKey) && !string.IsNullOrWhiteSpace(userKey))
            return userKey;
        return configuredKey;
    }

    /// <summary>
    /// Returns the user-provided model override if present and non-empty;
    /// otherwise returns <paramref name="configuredModel"/>.
    /// <paramref name="modelKey"/> convention: "&lt;ProviderKey&gt;Model" e.g. "OpenRouterModel".
    /// </summary>
    public static string GetEffectiveModel(string modelKey, string configuredModel)
    {
        var dict = _keys.Value;
        if (dict is not null && dict.TryGetValue(modelKey, out var userModel) && !string.IsNullOrWhiteSpace(userModel))
            return userModel;
        return configuredModel;
    }

    /// <summary>
    /// Returns true when either the user-provided override or the configured key is non-empty.
    /// </summary>
    public static bool HasEffectiveKey(string providerKey, string configuredKey) =>
        !string.IsNullOrWhiteSpace(GetEffectiveKey(providerKey, configuredKey));

    /// <summary>
    /// Returns true when the user explicitly provided a key for <paramref name="providerKey"/>
    /// in the current request (i.e. not the server-configured fallback).
    /// Used to let user-supplied keys bypass the <c>Enabled</c> flag in appsettings.
    /// </summary>
    public static bool HasUserProvidedKey(string providerKey)
    {
        var dict = _keys.Value;
        return dict is not null
            && dict.TryGetValue(providerKey, out var key)
            && !string.IsNullOrWhiteSpace(key);
    }
}
