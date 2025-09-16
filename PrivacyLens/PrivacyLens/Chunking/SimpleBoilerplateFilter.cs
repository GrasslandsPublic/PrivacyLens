using System.Text.RegularExpressions;

namespace PrivacyLens.Chunking
{
    /// <summary>
    /// Basic boilerplate remover (script/style/nav/header/footer/cookie banners).
    /// Keep simple and fast; extend with additional patterns as needed.
    /// </summary>
    public sealed class SimpleBoilerplateFilter : IBoilerplateFilter
    {
        public string StripChrome(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;

            // Drop script/style/noscript
            var s = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<noscript[\s\S]*?</noscript>", "", RegexOptions.IgnoreCase);

            // Remove obvious layout regions
            s = Regex.Replace(s, @"<(header|footer|nav|aside)[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);

            // Cookie/consent/newsletter banners
            s = Regex.Replace(
                s,
                @"<(div|section)[^>]*(cookie|consent|banner|subscribe)[^>]*>[\s\S]*?</\1>",
                "",
                RegexOptions.IgnoreCase
            );

            return s;
        }
    }
}

