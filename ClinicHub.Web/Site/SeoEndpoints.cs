using System.Text;
using System.Xml.Linq;

namespace ClinicHub.Web.Site;

/// <summary>robots.txt и sitemap.xml (PLAN, задача 4.2).</summary>
public static class SeoEndpoints
{
    /// <summary>Страницы с собственным кодом. Страницы из блоков берутся из БД (опубликованные).</summary>
    private static readonly string[] CodePaths = ["/zapis", "/politika", "/pravila"];

    public static IEndpointRouteBuilder MapSeoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/robots.txt", async (HttpRequest req, SiteStore store) =>
        {
            var baseUrl = SiteUrls.BaseUrl(await store.GetClinicAsync(), req);
            var sb = new StringBuilder()
                .AppendLine("User-agent: *")
                .AppendLine("Disallow: /zapis?")
                .AppendLine($"Sitemap: {baseUrl}/sitemap.xml");
            return Results.Text(sb.ToString(), "text/plain", Encoding.UTF8);
        });

        app.MapGet("/sitemap.xml", async (HttpRequest req, SiteStore store) =>
        {
            var baseUrl = SiteUrls.BaseUrl(await store.GetClinicAsync(), req);
            var paths = (await store.GetPublishedPathsAsync()).Concat(CodePaths);
            XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(ns + "urlset",
                    paths.Select(p => new XElement(ns + "url", new XElement(ns + "loc", baseUrl + p)))));
            return Results.Text(doc.Declaration + "\n" + doc.Root, "application/xml", Encoding.UTF8);
        });

        return app;
    }
}
