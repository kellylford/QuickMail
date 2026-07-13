import SwiftUI
import WebKit
import QuickMailCore
import UniformTypeIdentifiers

struct ReadingPaneView: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        if let detail = state.currentDetail {
            VStack(alignment: .leading, spacing: 0) {
                MessageHeaderView(detail: detail)
                Divider()
                if !detail.attachments.isEmpty {
                    AttachmentBar(attachments: detail.attachments)
                    Divider()
                }
                SandboxedWebView(
                    html: pageHTML(for: detail),
                    focusToken: state.bodyFocusToken,
                    onEscape: { state.returnFocusToList() }
                )
                .accessibilityLabel("Message body")
            }
        } else if state.selectedMessageUID != nil {
            ProgressView("Loading message…")
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            Text("No message selected")
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private func pageHTML(for detail: MessageDetail) -> String {
        if let html = detail.htmlBody {
            return HTMLRenderer.page(forHTML: html)
        }
        return HTMLRenderer.page(forPlainText: detail.textBody ?? "(empty message)")
    }
}

struct MessageHeaderView: View {
    let detail: MessageDetail

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(detail.subject.isEmpty ? "(no subject)" : detail.subject)
                .font(.title3.weight(.semibold))
                .textSelection(.enabled)
            addressLine("From", detail.from)
            addressLine("To", detail.to)
            if !detail.cc.isEmpty { addressLine("Cc", detail.cc) }
            if let date = detail.date {
                Text(DateFormatter.localizedString(from: date, dateStyle: .full, timeStyle: .short))
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(10)
    }

    @ViewBuilder
    private func addressLine(_ label: String, _ addresses: [MailAddress]) -> some View {
        if !addresses.isEmpty {
            Text("\(label): \(addresses.map(\.description).joined(separator: ", "))")
                .font(.callout)
                .textSelection(.enabled)
                .lineLimit(2)
        }
    }
}

struct AttachmentBar: View {
    let attachments: [AttachmentInfo]

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack {
                ForEach(attachments) { attachment in
                    Button {
                        save(attachment)
                    } label: {
                        Label("\(attachment.filename) (\(byteText(attachment.data.count)))",
                              systemImage: "paperclip")
                    }
                    .accessibilityLabel("Save attachment \(attachment.filename), \(byteText(attachment.data.count))")
                }
            }
            .padding(8)
        }
        .accessibilityLabel("Attachments")
    }

    private func byteText(_ count: Int) -> String {
        ByteCountFormatter.string(fromByteCount: Int64(count), countStyle: .file)
    }

    private func save(_ attachment: AttachmentInfo) {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = attachment.filename
        if panel.runModal() == .OK, let url = panel.url {
            try? attachment.data.write(to: url)
        }
    }
}

/// WKWebView host with the same posture as the Windows reading pane:
/// JavaScript off, all network subresource loads blocked, link activations
/// opened in the default browser instead of navigating the pane.
struct SandboxedWebView: NSViewRepresentable {
    let html: String
    var focusToken: Int = 0
    var onEscape: () -> Void = {}

    static let blockAllNetworkRules = """
    [{"trigger": {"url-filter": ".*"}, "action": {"type": "block"}}]
    """

    func makeNSView(context: Context) -> WKWebView {
        let config = WKWebViewConfiguration()
        config.defaultWebpagePreferences.allowsContentJavaScript = false
        config.websiteDataStore = .nonPersistent()
        let webView = EscapableWebView(frame: .zero, configuration: config)
        webView.onEscape = { [weak coordinator = context.coordinator] in
            coordinator?.onEscape()
        }
        webView.navigationDelegate = context.coordinator
        webView.setValue(false, forKey: "drawsBackground")
        WKContentRuleListStore.default().compileContentRuleList(
            forIdentifier: "quickmail-block-network",
            encodedContentRuleList: Self.blockAllNetworkRules
        ) { ruleList, _ in
            if let ruleList {
                webView.configuration.userContentController.add(ruleList)
            }
        }
        return webView
    }

    func updateNSView(_ webView: WKWebView, context: Context) {
        context.coordinator.onEscape = onEscape
        if context.coordinator.lastHTML != html {
            context.coordinator.lastHTML = html
            webView.loadHTMLString(html, baseURL: nil)
        }
        if focusToken != context.coordinator.lastFocusToken {
            context.coordinator.lastFocusToken = focusToken
            if webView.isLoading {
                context.coordinator.wantsFocusAfterLoad = true
            } else {
                webView.window?.makeFirstResponder(webView)
            }
        }
    }

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    final class Coordinator: NSObject, WKNavigationDelegate {
        var lastHTML: String?
        var lastFocusToken = 0
        var wantsFocusAfterLoad = false
        var onEscape: () -> Void = {}

        func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
            if wantsFocusAfterLoad {
                wantsFocusAfterLoad = false
                webView.window?.makeFirstResponder(webView)
            }
        }

        func webView(
            _ webView: WKWebView,
            decidePolicyFor navigationAction: WKNavigationAction,
            decisionHandler: @escaping (WKNavigationActionPolicy) -> Void
        ) {
            // The initial loadHTMLString comes through as .other with a nil/about URL.
            if navigationAction.navigationType == .linkActivated,
               let url = navigationAction.request.url {
                NSWorkspace.shared.open(url)
                decisionHandler(.cancel)
                return
            }
            if let scheme = navigationAction.request.url?.scheme?.lowercased(),
               !["about", "data"].contains(scheme) {
                decisionHandler(.cancel)
                return
            }
            decisionHandler(.allow)
        }
    }
}

/// WKWebView that hands Escape back to the app (return focus to the list)
/// instead of swallowing it.
final class EscapableWebView: WKWebView {
    var onEscape: () -> Void = {}

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { // Escape
            onEscape()
            return
        }
        super.keyDown(with: event)
    }
}
