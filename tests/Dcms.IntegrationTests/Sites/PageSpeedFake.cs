using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Dcms.IntegrationTests.Sites;

/// <summary>
/// Google PageSpeed Insights, stood in (#649): answers every run with <see cref="Response"/> for the
/// URL asked and remembers what was asked. The response follows PSI's documented shape
/// (<c>lighthouseResult</c> with categories, audits and their details; <c>loadingExperience</c>),
/// hand-built: keyless PSI calls get no quota, so none could be recorded here.
/// </summary>
public sealed class PageSpeedFake : HttpMessageHandler
{
    public ConcurrentQueue<(string Url, string Strategy, string? Key)> Calls { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
        var url = query["url"] ?? "";
        Calls.Enqueue((url, query["strategy"] ?? "", query["key"]));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Response(url), Encoding.UTF8, "application/json"),
        });
    }

    public static string Response(string url) => $$"""
        {
          "id": "{{url}}",
          "loadingExperience": {
            "id": "{{url}}",
            "metrics": {
              "LARGEST_CONTENTFUL_PAINT_MS": { "percentile": 2100, "category": "FAST" },
              "CUMULATIVE_LAYOUT_SHIFT_SCORE": { "percentile": 5, "category": "FAST" }
            },
            "overall_category": "FAST"
          },
          "lighthouseResult": {
            "requestedUrl": "{{url}}",
            "finalUrl": "{{url}}",
            "finalDisplayedUrl": "{{url}}",
            "lighthouseVersion": "12.6.0",
            "runWarnings": [],
            "categories": {
              "performance": { "id": "performance", "title": "Performance", "score": 0.62, "auditRefs": [
                { "id": "first-contentful-paint", "weight": 10, "group": "metrics" },
                { "id": "largest-contentful-paint", "weight": 25, "group": "metrics" },
                { "id": "total-blocking-time", "weight": 30, "group": "metrics" },
                { "id": "cumulative-layout-shift", "weight": 25, "group": "metrics" },
                { "id": "speed-index", "weight": 10, "group": "metrics" },
                { "id": "uses-optimized-images", "weight": 0 },
                { "id": "render-blocking-resources", "weight": 0 },
                { "id": "server-response-time", "weight": 0 } ] },
              "accessibility": { "id": "accessibility", "title": "Accessibility", "score": 0.88, "auditRefs": [
                { "id": "image-alt", "weight": 10 }, { "id": "color-contrast", "weight": 7 } ] },
              "best-practices": { "id": "best-practices", "title": "Best Practices", "score": 1, "auditRefs": [
                { "id": "is-on-https", "weight": 5 } ] },
              "seo": { "id": "seo", "title": "SEO", "score": 0.9, "auditRefs": [
                { "id": "meta-description", "weight": 1 }, { "id": "is-crawlable", "weight": 1 } ] }
            },
            "audits": {
              "first-contentful-paint": { "id": "first-contentful-paint", "title": "First Contentful Paint", "score": 0.7, "scoreDisplayMode": "numeric", "displayValue": "2.1 s" },
              "largest-contentful-paint": { "id": "largest-contentful-paint", "title": "Largest Contentful Paint", "score": 0.4, "scoreDisplayMode": "numeric", "displayValue": "4.8 s" },
              "total-blocking-time": { "id": "total-blocking-time", "title": "Total Blocking Time", "score": 0.9, "scoreDisplayMode": "numeric", "displayValue": "120 ms" },
              "cumulative-layout-shift": { "id": "cumulative-layout-shift", "title": "Cumulative Layout Shift", "score": 1, "scoreDisplayMode": "numeric", "displayValue": "0.01" },
              "speed-index": { "id": "speed-index", "title": "Speed Index", "score": 0.8, "scoreDisplayMode": "numeric", "displayValue": "3.2 s" },
              "render-blocking-resources": { "id": "render-blocking-resources", "title": "Eliminate render-blocking resources",
                "description": "Resources are blocking the first paint of your page. [Learn more](https://developer.chrome.com/docs/lighthouse/performance/render-blocking-resources/).",
                "score": 0.3, "scoreDisplayMode": "metricSavings", "displayValue": "Potential savings of 750 ms",
                "details": { "type": "opportunity", "overallSavingsMs": 750, "overallSavingsBytes": 0, "headings": [],
                  "items": [ { "url": "https://example.test/assets/index.css", "totalBytes": 4800, "wastedMs": 750 } ] } },
              "uses-optimized-images": { "id": "uses-optimized-images", "title": "Efficiently encode images",
                "description": "Optimized images load faster and consume less cellular data.",
                "score": 0.5, "scoreDisplayMode": "metricSavings", "displayValue": "Potential savings of 120 KiB",
                "details": { "type": "opportunity", "overallSavingsMs": 300, "overallSavingsBytes": 122880, "headings": [],
                  "items": [ { "url": "https://example.test/hero.jpg", "totalBytes": 300000, "wastedBytes": 122880 } ] } },
              "server-response-time": { "id": "server-response-time", "title": "Initial server response time was short", "score": 1, "scoreDisplayMode": "metricSavings" },
              "image-alt": { "id": "image-alt", "title": "Image elements do not have [alt] attributes",
                "description": "Informative elements should aim for short, descriptive alternate text.",
                "score": 0, "scoreDisplayMode": "binary",
                "details": { "type": "table", "headings": [], "items": [
                  { "node": { "selector": "main > img", "snippet": "<img src=\"hero.jpg\">", "nodeLabel": "main > img" } } ] } },
              "color-contrast": { "id": "color-contrast", "title": "Background and foreground colors have a sufficient contrast ratio", "score": null, "scoreDisplayMode": "notApplicable" },
              "is-on-https": { "id": "is-on-https", "title": "Uses HTTPS", "score": 1, "scoreDisplayMode": "binary" },
              "meta-description": { "id": "meta-description", "title": "Document does not have a meta description",
                "description": "Meta descriptions may be included in search results to concisely summarize page content.",
                "score": 0, "scoreDisplayMode": "binary", "details": { "type": "table", "headings": [], "items": [] } },
              "is-crawlable": { "id": "is-crawlable", "title": "Page isn’t blocked from indexing", "score": 1, "scoreDisplayMode": "binary" }
            }
          }
        }
        """;
}
