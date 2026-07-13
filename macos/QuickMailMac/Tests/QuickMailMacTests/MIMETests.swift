import XCTest
@testable import QuickMailCore

final class MIMETests: XCTestCase {
    func testRFC2047Decode() {
        XCTAssertEqual(RFC2047.decodeHeaderValue("=?utf-8?B?SGVsbG8gd8O2cmxk?="), "Hello wörld")
        XCTAssertEqual(RFC2047.decodeHeaderValue("=?iso-8859-1?Q?caf=E9?="), "café")
        XCTAssertEqual(RFC2047.decodeHeaderValue("plain subject"), "plain subject")
        XCTAssertEqual(RFC2047.decodeHeaderValue("=?utf-8?Q?a_b?="), "a b")
        // Adjacent encoded-words: whitespace between them is dropped.
        XCTAssertEqual(
            RFC2047.decodeHeaderValue("=?utf-8?B?SGVs?= =?utf-8?B?bG8=?="),
            "Hello")
        // Mixed literal + encoded.
        XCTAssertEqual(
            RFC2047.decodeHeaderValue("Re: =?utf-8?Q?caf=C3=A9?= meeting"),
            "Re: café meeting")
        // Lowercase encoding letters (Python's email module emits these).
        XCTAssertEqual(
            RFC2047.decodeHeaderValue("=?utf-8?q?B=C3=BCro_Caf=C3=A9?="),
            "Büro Café")
        XCTAssertEqual(
            RFC2047.decodeHeaderValue("=?utf-8?b?dMOrc3Q=?="),
            "tëst")
    }

    func testRFC2047EncodeRoundTrip() {
        let original = "Übung macht den Meister ✓"
        let encoded = RFC2047.encodeHeaderValue(original)
        XCTAssertTrue(encoded.hasPrefix("=?utf-8?B?"))
        XCTAssertEqual(RFC2047.decodeHeaderValue(encoded), original)
        XCTAssertEqual(RFC2047.encodeHeaderValue("plain"), "plain")
    }

    func testAddressParser() {
        let addrs = AddressParser.parse(#""Doe, Jane" <jane@example.com>, bob@example.com, Team <team@x.org>"#)
        XCTAssertEqual(addrs.count, 3)
        XCTAssertEqual(addrs[0].name, "Doe, Jane")
        XCTAssertEqual(addrs[0].address, "jane@example.com")
        XCTAssertEqual(addrs[1].address, "bob@example.com")
        XCTAssertEqual(addrs[2].name, "Team")
    }

    func testSimplePlainTextMessage() {
        let raw = Data("""
        From: jane@example.com\r
        To: kelly@theideaplace.net\r
        Subject: =?utf-8?Q?caf=C3=A9?=\r
        Date: Mon, 13 Jul 2026 10:20:00 +0000\r
        Content-Type: text/plain; charset=utf-8\r
        \r
        Hello there.\r
        Second line.\r
        """.utf8)
        let msg = MIMEParser.parseMessage(raw, uid: 7)
        XCTAssertEqual(msg.subject, "café")
        XCTAssertEqual(msg.from.first?.address, "jane@example.com")
        XCTAssertEqual(msg.textBody?.contains("Second line."), true)
        XCTAssertNil(msg.htmlBody)
        XCTAssertNotNil(msg.date)
    }

    func testMultipartAlternativeWithAttachment() {
        let body = "PGI+Qm9sZDwvYj4=" // "<b>Bold</b>"
        let raw = Data("""
        From: a@b.c\r
        Subject: multi\r
        Content-Type: multipart/mixed; boundary="OUTER"\r
        \r
        --OUTER\r
        Content-Type: multipart/alternative; boundary="INNER"\r
        \r
        --INNER\r
        Content-Type: text/plain; charset=utf-8\r
        Content-Transfer-Encoding: quoted-printable\r
        \r
        Caf=C3=A9 body with =\r
        soft break.\r
        --INNER\r
        Content-Type: text/html; charset=utf-8\r
        Content-Transfer-Encoding: base64\r
        \r
        \(body)\r
        --INNER--\r
        --OUTER\r
        Content-Type: application/pdf; name="report.pdf"\r
        Content-Disposition: attachment; filename="report.pdf"\r
        Content-Transfer-Encoding: base64\r
        \r
        JVBERi0=\r
        --OUTER--\r
        """.utf8)
        let msg = MIMEParser.parseMessage(raw, uid: 1)
        XCTAssertEqual(msg.textBody, "Café body with soft break.")
        XCTAssertEqual(msg.htmlBody, "<b>Bold</b>")
        XCTAssertEqual(msg.attachments.count, 1)
        XCTAssertEqual(msg.attachments.first?.filename, "report.pdf")
        XCTAssertEqual(msg.attachments.first?.data, Data("%PDF-".utf8))
    }

    func testOutgoingMessageSerialization() {
        let out = OutgoingMessage(
            from: MailAddress(name: "Kelly", address: "kelly@theideaplace.net"),
            to: [MailAddress(address: "jane@example.com")],
            subject: "Tëst",
            textBody: "Body line one.\n.dot line\n",
            inReplyTo: "<orig@example.com>",
            references: ["<orig@example.com>"]
        )
        let data = out.rfc5322Data()
        let text = String(decoding: data, as: UTF8.self)
        XCTAssertTrue(text.contains("From: Kelly <kelly@theideaplace.net>"))
        XCTAssertTrue(text.contains("Subject: =?utf-8?B?"))
        XCTAssertTrue(text.contains("In-Reply-To: <orig@example.com>"))
        // Round-trip through the parser.
        let parsed = MIMEParser.parseMessage(data, uid: 0)
        XCTAssertEqual(parsed.subject, "Tëst")
        XCTAssertEqual(parsed.textBody, "Body line one.\n.dot line\n")
    }

    func testDotStuffing() {
        let stuffed = SMTPClient.dotStuff(Data("a\r\n.b\r\n..c\r\n".utf8))
        XCTAssertEqual(String(decoding: stuffed, as: UTF8.self), "a\r\n..b\r\n...c\r\n")
    }

    func testHTMLRendererSanitizes() {
        let page = HTMLRenderer.page(forHTML:
            #"<p onclick="evil()">hi</p><script>alert(1)</script><a href="javascript:x">l</a>"#)
        XCTAssertFalse(page.contains("<script"))
        XCTAssertFalse(page.contains("onclick"))
        XCTAssertFalse(page.contains("javascript:"))
        XCTAssertTrue(page.contains("Content-Security-Policy"))
    }

    func testHTMLRendererLinkifiesPlainText() {
        let page = HTMLRenderer.page(forPlainText: "See https://example.com/x. Also <tags> escaped & such.")
        XCTAssertTrue(page.contains(#"<a href="https://example.com/x">"#))
        XCTAssertTrue(page.contains("&lt;tags&gt;"))
        XCTAssertTrue(page.contains("&amp;"))
    }
}
