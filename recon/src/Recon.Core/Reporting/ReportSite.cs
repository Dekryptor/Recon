using System.Reflection;
using System.Text;
using Recon.Compare;

namespace Recon.Reporting;

/// <summary>
/// The viewer: one HTML file with no external requests, so a report can be opened from a file share,
/// an email or a checked-in directory and still work. The same page runs against the tool's own
/// server, in which case the documents are fetched instead of embedded.
/// </summary>
public static class ReportSite
{
    public const string IndexFileName = "index.html";

    private const string ResourceName = "Recon.Reporting.viewer.html";

    private const string BootstrapMarker = "/*__BOOTSTRAP__*/";

    public static string Template { get; } = LoadTemplate();

    /// <summary>The self-contained page: the documents are inside it, so nothing else is needed.</summary>
    public static string StaticHtml(ComparisonDocument comparison, ProgressReport progress)
    {
        var bootstrap = new StringBuilder();
        bootstrap.Append("window.__RECON__ = { comparison: ")
            .Append(Reports.Serialize(comparison))
            .Append(", progress: ")
            .Append(Reports.Serialize(progress))
            .Append(" };");
        return Inject(bootstrap.ToString());
    }

    /// <summary>The same page against a running server: it fetches the documents from these paths.</summary>
    public static string ServerHtml()
    {
        var bootstrap = new StringBuilder();
        bootstrap.Append("window.__RECON_API__ = { comparison: \"api/comparison\", progress: \"api/progress\", aligned: \"api/aligned?id=\" };");
        return Inject(bootstrap.ToString());
    }

    private static string Inject(string bootstrap)
    {
        if (!Template.Contains(BootstrapMarker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"the viewer template has lost its {BootstrapMarker} marker");
        }

        // A "</" inside the JSON would end the script element early, whatever else it means.
        return Template.Replace(BootstrapMarker, bootstrap.Replace("</", "<\\/", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    private static string LoadTemplate()
    {
        using var stream = typeof(ReportSite).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"the viewer template ({ResourceName}) is missing from this build");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
