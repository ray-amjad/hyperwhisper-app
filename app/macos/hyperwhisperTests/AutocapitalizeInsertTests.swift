//
//  AutocapitalizeInsertTests.swift
//  hyperwhisperTests
//

import Testing
@testable import HyperWhisper

struct AutocapitalizeInsertTests {
    @Test func lowercasesRegularWordMidSentence() {
        #expect(AutocapitalizeInsert.apply("Then we go", context: .midSentence) == "then we go")
        #expect(AutocapitalizeInsert.apply("It is", context: .midSentence) == "it is")
        #expect(AutocapitalizeInsert.apply("Internet access", context: .midSentence) == "internet access")
    }

    @Test func preservesAcronymMidSentence() {
        #expect(AutocapitalizeInsert.apply("API is up", context: .midSentence) == "API is up")
    }

    @Test func passesThroughAtSentenceStart() {
        #expect(AutocapitalizeInsert.apply("Then we go", context: .startOfSentence) == "Then we go")
    }

    @Test func preservesStandalonePronounMidSentence() {
        #expect(AutocapitalizeInsert.apply("I think we should", context: .midSentence) == "I think we should")
    }

    @Test func preservesPronounContractionsMidSentence() {
        #expect(AutocapitalizeInsert.apply("I'm done", context: .midSentence) == "I'm done")
        #expect(AutocapitalizeInsert.apply("I'll go", context: .midSentence) == "I'll go")
        #expect(AutocapitalizeInsert.apply("I've seen it", context: .midSentence) == "I've seen it")
        #expect(AutocapitalizeInsert.apply("I'd like that", context: .midSentence) == "I'd like that")
    }

    @Test func preservesPronounWithCurlyApostrophe() {
        #expect(AutocapitalizeInsert.apply("I\u{2019}m here", context: .midSentence) == "I\u{2019}m here")
    }

    @Test func preservesOneWordPronounFragmentWithTerminator() {
        #expect(AutocapitalizeInsert.apply("I.", context: .midSentence) == "I.")
    }

    @Test func leavesLowercasePronounUntouched() {
        #expect(AutocapitalizeInsert.apply("i think", context: .midSentence) == "i think")
    }
}
