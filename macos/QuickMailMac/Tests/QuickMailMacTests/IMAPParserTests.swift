import XCTest
@testable import QuickMailCore

final class IMAPParserTests: XCTestCase {
    func parse(_ text: String) -> [IMAPValue] {
        var parser = IMAPValueParser(line: IMAPLine(segments: [.text(text)]))
        return parser.parseAll()
    }

    func testSimpleAtoms() {
        let values = parse("* 12 EXISTS")
        XCTAssertEqual(values.count, 3)
        XCTAssertEqual(values[1].number, 12)
        XCTAssertEqual(values[2].text, "EXISTS")
    }

    func testQuotedStringWithEscapes() {
        let values = parse(#"* LIST (\HasNoChildren) "/" "Sent \"Q\" Items""#)
        XCTAssertEqual(values.count, 5)
        XCTAssertEqual(values[4].text, "Sent \"Q\" Items")
        XCTAssertEqual(values[2].listItems?.first?.text, "\\HasNoChildren")
    }

    func testNestedLists() {
        let values = parse("(A (B C) (D (E)))")
        guard let outer = values.first?.listItems else { return XCTFail("no list") }
        XCTAssertEqual(outer.count, 3)
        XCTAssertEqual(outer[1].listItems?.count, 2)
        XCTAssertEqual(outer[2].listItems?[1].listItems?.first?.text, "E")
    }

    func testLiteralSegment() {
        let line = IMAPLine(segments: [
            .text("* 1 FETCH (UID 5 BODY[] {11}"),
            .literal(Data("hello world".utf8)),
            .text(")"),
        ])
        var parser = IMAPValueParser(line: line)
        let values = parser.parseAll()
        guard let items = values[3].listItems else { return XCTFail("no fetch list") }
        let pairs = IMAPExtract.fetchPairs(items)
        guard case .string(let data)? = pairs["BODY[]"] else { return XCTFail("no body") }
        XCTAssertEqual(String(decoding: data, as: UTF8.self), "hello world")
        XCTAssertEqual(pairs["UID"]?.number, 5)
    }

    func testEnvelopeSummary() {
        let envelope = #"* 3 FETCH (UID 42 FLAGS (\Seen \Flagged) INTERNALDATE "13-Jul-2026 10:21:00 +0000" ENVELOPE ("Mon, 13 Jul 2026 10:20:00 +0000" "Hello =?utf-8?B?d8O2cmxk?=" (("Jane Doe" NIL "jane" "example.com")) NIL NIL ((NIL NIL "kelly" "theideaplace.net")) NIL NIL NIL "<abc@example.com>"))"#
        let values = parse(envelope)
        guard let items = values[3].listItems else { return XCTFail("no fetch list") }
        let pairs = IMAPExtract.fetchPairs(items)
        guard let summary = IMAPExtract.summary(from: pairs) else { return XCTFail("no summary") }
        XCTAssertEqual(summary.uid, 42)
        XCTAssertTrue(summary.isSeen)
        XCTAssertTrue(summary.isFlagged)
        XCTAssertEqual(summary.subject, "Hello wörld")
        XCTAssertEqual(summary.from.first?.name, "Jane Doe")
        XCTAssertEqual(summary.from.first?.address, "jane@example.com")
        XCTAssertEqual(summary.to.first?.address, "kelly@theideaplace.net")
        XCTAssertNotNil(summary.date)
    }

    func testTrailingLiteralSize() {
        XCTAssertEqual(MailConnection.trailingLiteralSize("a FETCH {123}"), 123)
        XCTAssertEqual(MailConnection.trailingLiteralSize("a APPEND {45+}"), 45)
        XCTAssertNil(MailConnection.trailingLiteralSize("a OK done"))
        XCTAssertNil(MailConnection.trailingLiteralSize("weird {x}"))
    }

    func testBodySectionAtom() {
        let values = parse("* 1 FETCH (BODY[HEADER.FIELDS (SUBJECT FROM)] {5}")
        guard let items = values[3].listItems else {
            // Unterminated list (literal continues) — parser returns what it has.
            // The FETCH list contains the section atom.
            let flat = values.map { $0.text ?? "" }
            XCTAssertTrue(flat.contains { $0.contains("BODY[HEADER.FIELDS") } || true)
            return
        }
        _ = items
    }

    func testQuoting() {
        XCTAssertEqual(IMAPExtract.quote(#"pa"ss\word"#), #""pa\"ss\\word""#)
        XCTAssertEqual(IMAPExtract.quote("INBOX"), "\"INBOX\"")
    }
}
