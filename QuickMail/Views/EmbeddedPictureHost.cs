using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Views;

/// <summary>
/// Serves the pictures sent inside the message a WebView2 is showing (#729). The sanitized body
/// refers to each as <c>https://quickmail-images.invalid/&lt;key&gt;/&lt;Content-ID&gt;</c>; this
/// answers those requests itself from the message's own parts, so nothing leaves the computer. A
/// <c>.invalid</c> host never resolves, so a request this does not answer fails rather than
/// reaching the network, and the document's CSP allows images from this one origin only.
///
/// The page is not held back for the pictures: it renders at once, and each picture request
/// waits (a deferral) until the message's pictures have been fetched. Reading is never
/// interrupted by a second navigation when they arrive.
///
/// One per WebView2. Each new message gets a new key, so a request still in flight for the
/// previous message is answered "not found" rather than with the wrong message's picture.
/// </summary>
internal sealed class EmbeddedPictureHost
{
    public const string Origin = "https://quickmail-images.invalid";

    private readonly CoreWebView2Environment _environment;
    private string? _key;
    private Func<Task<IReadOnlyDictionary<string, AttachmentModel>>>? _load;
    private Task<IReadOnlyDictionary<string, AttachmentModel>>? _pictures;

    private EmbeddedPictureHost(CoreWebView2Environment environment) => _environment = environment;

    /// <summary>Starts answering picture requests for <paramref name="core"/>.</summary>
    public static EmbeddedPictureHost Attach(CoreWebView2 core, CoreWebView2Environment environment)
    {
        var host = new EmbeddedPictureHost(environment);
        core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.Image);
        core.WebResourceRequested += host.OnWebResourceRequested;
        return host;
    }

    /// <summary>
    /// The picture address for the next message rendered, with its pictures fetched by
    /// <paramref name="load"/> on the first request; or null — and nothing served — when pictures
    /// are off, the message is shown as plain text, or its HTML shows no picture of its own.
    /// </summary>
    public string? BeginMessage(bool enabled, MailMessageDetail detail,
        Func<Task<IReadOnlyDictionary<string, AttachmentModel>>> load)
    {
        _pictures = null;
        if (!enabled || !Helpers.InlineImages.HasReferences(detail.HtmlBody))
        {
            _key = null;
            _load = null;
            return null;
        }
        _key = Guid.NewGuid().ToString("N");
        _load = load;
        return $"{Origin}/{_key}/";
    }

    private async void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        CoreWebView2Deferral? deferral = null;
        try
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
                || !string.Equals(uri.GetLeftPart(UriPartial.Authority), Origin, StringComparison.OrdinalIgnoreCase))
                return; // not ours: the filter should not have sent it

            var segments = uri.AbsolutePath.Trim('/').Split('/', 2);
            var key = _key;
            if (segments.Length != 2 || key is null || segments[0] != key || _load is null)
            {
                e.Response = NotFound();
                return;
            }

            deferral = e.GetDeferral();
            _pictures ??= _load();
            var pictures = await _pictures;
            var contentId = Uri.UnescapeDataString(segments[1]);
            // The message may have changed while the pictures were fetched.
            if (key == _key && pictures.TryGetValue(contentId, out var picture)
                && picture.Content is { } bytes && EmbeddedPictureLoader.IsDisplayable(picture.ContentType))
            {
                e.Response = _environment.CreateWebResourceResponse(
                    new MemoryStream(bytes, writable: false), 200, "OK",
                    $"Content-Type: {picture.ContentType.Split(';')[0].Trim()}\r\n" +
                    "X-Content-Type-Options: nosniff\r\nCache-Control: no-store");
            }
            else
            {
                e.Response = NotFound();
            }
        }
        catch (Exception ex)
        {
            LogService.Log("EmbeddedPictureHost", ex);
            try { e.Response = NotFound(); } catch { /* the request is gone */ }
        }
        finally
        {
            // The WebView2 may have closed while the pictures were fetched; an async void handler
            // must not let that escape to the dispatcher.
            try { deferral?.Complete(); } catch (Exception ex) { LogService.Debug($"EmbeddedPictureHost: {ex.Message}"); }
        }
    }

    private CoreWebView2WebResourceResponse NotFound() =>
        _environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");
}
