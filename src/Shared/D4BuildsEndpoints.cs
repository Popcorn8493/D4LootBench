namespace D4LootBench.Shared;

/// <summary>
/// d4builds.gg endpoints shared by the Core gear importer and the Paragon board importer.
/// Compiled into BOTH assemblies as a linked source file (Paragon does not reference Core), so
/// the Firestore project id and public web API key live in exactly one place.
/// </summary>
internal static class D4BuildsEndpoints
{
    /// <summary>
    /// Firestore REST endpoint for a build document; {0} is the build's uuid. Project id and API
    /// key are the site's public web-app config, embedded in every page it serves.
    /// </summary>
    internal const string BuildDocumentApiFormat =
        "https://firestore.googleapis.com/v1/projects/d4builds-a3254/databases/(default)/documents/builds/{0}"
        + "?key=AIzaSyDiFjyn-CH9a80pzfcwMd_AH-zSstNmjDc";

    /// <summary>
    /// Gatsby page-data endpoint for a curated build's pretty-slug page; {0} is the slug. The
    /// page is prerendered and its <c>result.pageContext.seoId</c> is the build document's uuid.
    /// </summary>
    internal const string PageDataApiFormat = "https://d4builds.gg/page-data/builds/{0}/page-data.json";
}
