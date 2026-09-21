using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace NimBus.WebApp.Services.IntegrationIntelligence;

/// <summary>
/// Replaces only administrator-owned values/arrays while preserving the host's
/// other configuration providers and reload behavior. Lower array tails must not survive.
/// </summary>
internal sealed class IntelligenceSettingsConfigurationSource(
    IConfiguration original, IDictionary<string, string?> values, string[] replacedArrays) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new Provider(original, values, replacedArrays);

    private sealed class Provider : ConfigurationProvider, IDisposable
    {
        private readonly IConfiguration _original;
        private readonly string[] _replacedArrays;
        private readonly IDisposable _reload;

        public Provider(IConfiguration original, IDictionary<string, string?> values, string[] replacedArrays)
        {
            _original = original;
            _replacedArrays = replacedArrays;
            Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
            _reload = ChangeToken.OnChange(original.GetReloadToken, OnReload);
        }

        public override bool TryGet(string key, out string? value)
        {
            if (Data.TryGetValue(key, out value)) return true;
            if (Replaced(key)) return false;
            value = _original[key];
            return value is not null;
        }

        public override IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
        {
            var children = parentPath is null ? _original.GetChildren() : _original.GetSection(parentPath).GetChildren();
            return base.GetChildKeys(earlierKeys.Concat(children.Where(child => !Replaced(child.Path)).Select(child => child.Key)), parentPath);
        }

        private bool Replaced(string key) => _replacedArrays.Any(path => key.Equals(path, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith(path + ":", StringComparison.OrdinalIgnoreCase));

        public void Dispose() => _reload.Dispose();
    }
}
