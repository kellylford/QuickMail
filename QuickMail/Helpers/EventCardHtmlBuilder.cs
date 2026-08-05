using System;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Helpers;

/// <summary>
/// Builds the accessible HTML event card shown above a meeting invitation's body.
///
/// This lives here, not on <c>MainViewModel</c>, because the card belongs to every surface that
/// renders a message — reading pane, tab, and the standalone <c>MessageWindow</c>. It was originally
/// a MainViewModel method injected only by MainWindow, so Window mode silently dropped the card and
/// with it the meeting's date and time: a Zoom-style invite carries the "when" ONLY in the ICS part,
/// never in the body, so Window mode had no way to show the user when the meeting was.
/// </summary>
public static class EventCardHtmlBuilder
{
    /// <summary>
    /// Builds the card for <paramref name="invite"/>, or an empty string when there is no invite.
    /// Colors come from the resolved theme as hex strings (IThemeService never exposes UI types);
    /// the fallbacks match the Parchment light palette, for tests that run without a theme service.
    /// </summary>
    public static string Build(IcsModel? invite, IThemeService? themeService)
    {
        if (invite == null) return string.Empty;

        var theme = themeService?.ResolvedTheme;
        string Color(string token, string fallback) => theme?.ColorOf(token) ?? fallback;
        var cardBorder = Color("border", "#D8D4CC");
        var cardBg     = Color("surfaceBackground", "#F5F3EF");
        var cardText   = Color("textPrimary", "#1F2328");

        var sb = new System.Text.StringBuilder();
        sb.Append($"<div style=\"border:1px solid {cardBorder};border-radius:6px;padding:12px;margin:0 0 16px 0;background:{cardBg};color:{cardText};font-family:Segoe UI,Arial,sans-serif;font-size:13px;line-height:1.45;\" role=\"region\" aria-label=\"");
        sb.Append(System.Net.WebUtility.HtmlEncode(invite.DisplaySummary));
        sb.Append("\">");
        sb.Append("<div style=\"font-weight:bold;font-size:15px;margin-bottom:8px;\">Event Invitation</div>");

        // Cancellation notice — shown instead of the accept/decline buttons when
        // the organizer sent METHOD:CANCEL.
        var isCancel = string.Equals(invite.Method, "CANCEL", StringComparison.OrdinalIgnoreCase);
        if (isCancel)
        {
            sb.Append($"<div style=\"font-weight:bold;color:{Color("error", "#B3261E")};margin-bottom:8px;\">This event has been cancelled by the organizer.</div>");
        }

        if (!string.IsNullOrWhiteSpace(invite.Summary))
        {
            sb.Append("<div style=\"margin-bottom:4px;\"><strong>Event:</strong> ");
            sb.Append(System.Net.WebUtility.HtmlEncode(invite.Summary));
            sb.Append("</div>");
        }

        if (!string.IsNullOrWhiteSpace(invite.OrganizerName))
        {
            sb.Append("<div style=\"margin-bottom:4px;\"><strong>Organizer:</strong> ");
            sb.Append(System.Net.WebUtility.HtmlEncode(invite.OrganizerName));
            sb.Append("</div>");
        }
        else if (!string.IsNullOrWhiteSpace(invite.Organizer))
        {
            sb.Append("<div style=\"margin-bottom:4px;\"><strong>Organizer:</strong> ");
            sb.Append(System.Net.WebUtility.HtmlEncode(invite.Organizer));
            sb.Append("</div>");
        }

        if (invite.StartTime.HasValue)
        {
            sb.Append("<div style=\"margin-bottom:4px;\"><strong>When:</strong> ");
            sb.Append(System.Net.WebUtility.HtmlEncode(invite.StartTime.Value.ToLocalTime().ToString("f")));
            if (invite.EndTime.HasValue)
            {
                sb.Append(" – ");
                sb.Append(System.Net.WebUtility.HtmlEncode(invite.EndTime.Value.ToLocalTime().ToString("t")));
            }
            sb.Append("</div>");
        }

        if (!string.IsNullOrWhiteSpace(invite.Location))
        {
            sb.Append("<div style=\"margin-bottom:8px;\"><strong>Location:</strong> ");
            sb.Append(System.Net.WebUtility.HtmlEncode(invite.Location));
            sb.Append("</div>");
        }

        if (!string.IsNullOrWhiteSpace(invite.Description))
        {
            sb.Append("<div style=\"margin-bottom:8px;white-space:pre-wrap;\">");
            sb.Append(System.Net.WebUtility.HtmlEncode(invite.Description));
            sb.Append("</div>");
        }

        // Buttons: Accept, Tentative, Decline — hidden for cancellations. Each uses
        // its status color's pale background tint with the dark status text partner
        // and a 1px status border, readable in light and dark themes alike; the
        // verb text (not color) carries the meaning.
        if (!isCancel)
        {
            void AppendButton(string href, string ariaLabel, string label, string fg, string bg, bool last = false)
            {
                sb.Append($"<a href=\"{href}\" role=\"button\" aria-label=\"{ariaLabel}\" ");
                sb.Append($"style=\"display:inline-block;padding:6px 14px;{(last ? "" : "margin-right:8px;")}margin-bottom:4px;");
                sb.Append($"background:{bg};color:{fg};border:1px solid {fg};border-radius:4px;text-decoration:none;font-weight:600;\">{label}</a>");
            }

            sb.Append("<div style=\"margin-top:8px;\">");
            AppendButton("quickmail:ics-accept", "Accept invitation", "Accept",
                Color("success", "#2E6B3E"), Color("successBackground", "#E9F3EC"));
            AppendButton("quickmail:ics-tentative", "Tentatively accept invitation", "Tentative",
                Color("warning", "#8A5A00"), Color("warningBackground", "#FBF3E2"));
            AppendButton("quickmail:ics-decline", "Decline invitation", "Decline",
                Color("error", "#B3261E"), Color("errorBackground", "#FBEAE9"), last: true);
            sb.Append("</div>");

            // Live status region for RSVP feedback (issue #329), updated in place via
            // ExecuteScriptAsync so the result is announced from inside the document — a host-window
            // notification is dropped while focus is in the WebView2. Empty until the user responds.
            sb.Append("<div id=\"qm-invite-status\" aria-live=\"assertive\" aria-atomic=\"true\" " +
                      "style=\"margin-top:8px;font-weight:600;\"></div>");
        }

        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>
    /// The JS that writes <paramref name="text"/> into the open card's <c>aria-live</c> status
    /// region. Shared by every window that hosts a card, so the RSVP result is announced from
    /// inside the document the screen reader is already reading — a host-window notification is
    /// dropped while focus is in the WebView2 (issue #329). JsonSerializer yields a safe, quoted
    /// JS string literal; textContent prevents HTML injection.
    /// </summary>
    public static string StatusScript(string text) =>
        "(function(){var s=document.getElementById('qm-invite-status');" +
        "if(s){s.textContent=" + System.Text.Json.JsonSerializer.Serialize(text) + ";}})();";
}
