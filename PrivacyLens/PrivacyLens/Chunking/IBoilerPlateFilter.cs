namespace PrivacyLens.Chunking
{
    public interface IBoilerplateFilter
    {
        /// <summary>
        /// Remove common page chrome: header/footer/nav, banners, etc.
        /// </summary>
        string StripChrome(string html);
    }
}

