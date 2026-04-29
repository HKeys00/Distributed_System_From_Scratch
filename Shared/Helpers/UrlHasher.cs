using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace Shared.Helpers
{
    public static class Helpers
    {
        #region Methods

        /// <summary>
        /// Parses a URL, sorts its query parameters alphabetically,
        /// lowercases everything, and returns a SHA256 hex hash.
        /// </summary>
        public static string HashUrl(this string url)
        {
            var normalized = Normalize(url);
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        public static string Normalize(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                throw new ArgumentException($"Invalid URL: {url}", nameof(url));

            // Parse query string and sort keys alphabetically
            var query = HttpUtility.ParseQueryString(uri.Query);
            var sortedPairs = query.AllKeys
                .Where(k => k is not null)
                .OrderBy(k => k, StringComparer.Ordinal)
                .SelectMany(k => (query.GetValues(k) ?? Array.Empty<string>())
                    .OrderBy(v => v, StringComparer.Ordinal)
                    .Select(v => $"{Uri.EscapeDataString(k!)}={Uri.EscapeDataString(v ?? "")}"));

            var sortedQuery = string.Join("&", sortedPairs);

            // Rebuild URL with sorted query
            var builder = new UriBuilder(uri) { Query = sortedQuery };

            // Lowercase the entire normalized URL
            return builder.Uri.ToString().ToLowerInvariant();
        }

        #endregion
    }
}