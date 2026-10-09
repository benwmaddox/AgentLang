namespace AgentLang.SubagentTrials.IoMaintenance156.Fixture

module SelfTests =
    let private assertEqual testName expected actual =
        if actual <> expected then
            failwithf "%s: expected %A, got %A" testName expected actual

    let publishCopiesExactlyAndReturnsUnit () =
        let files =
            VirtualFiles(
                [ "source.txt", "source contents"
                  "destination.txt", "old destination"
                  "sentinel.txt", "leave this alone" ]
            )

        let result: unit = Configuration.publish(files, "source.txt", "destination.txt")

        assertEqual "publish result" () result
        assertEqual "copied contents" (Some "source contents") (files.Peek("destination.txt"))
        assertEqual "sentinel preserved" (Some "leave this alone") (files.Peek("sentinel.txt"))
        assertEqual "publish reads" 1 files.ReadCount
        assertEqual "publish writes" 1 files.WriteCount

    let publishPreservesEmptyContentsAndSentinel () =
        let files =
            VirtualFiles(
                [ "empty-source.txt", ""
                  "destination.txt", "replace this"
                  "sentinel.txt", "sentinel contents" ]
            )

        Configuration.publish(files, "empty-source.txt", "destination.txt")

        assertEqual "empty source copied" (Some "") (files.Peek("destination.txt"))
        assertEqual "empty publish sentinel preserved" (Some "sentinel contents") (files.Peek("sentinel.txt"))
        assertEqual "empty publish reads" 1 files.ReadCount
        assertEqual "empty publish writes" 1 files.WriteCount

    let publishPreservesUnicodeAndLineEndings () =
        let sourceContents = "Καλημέρα 🌍\r\nsecond line\nthird line\r"
        let files = VirtualFiles([ "unicode-source.txt", sourceContents; "sentinel.txt", "keep" ])

        Configuration.publish(files, "unicode-source.txt", "unicode-destination.txt")

        assertEqual "Unicode and line endings preserved" (Some sourceContents) (files.Peek("unicode-destination.txt"))
        assertEqual "Unicode sentinel preserved" (Some "keep") (files.Peek("sentinel.txt"))
        assertEqual "Unicode publish reads" 1 files.ReadCount
        assertEqual "Unicode publish writes" 1 files.WriteCount

    let refreshUsesPublisherWithoutCollateralChanges () =
        let files =
            VirtualFiles(
                [ "configuration.json", "{\"mode\":\"current\"}"
                  "refresh-copy.json", "stale"
                  "sentinel.json", "untouched" ]
            )

        Configuration.refresh(files, "configuration.json", "refresh-copy.json")

        assertEqual "refresh copied contents" (Some "{\"mode\":\"current\"}") (files.Peek("refresh-copy.json"))
        assertEqual "refresh sentinel preserved" (Some "untouched") (files.Peek("sentinel.json"))
        assertEqual "refresh reads" 1 files.ReadCount
        assertEqual "refresh writes" 1 files.WriteCount

    let run () =
        publishCopiesExactlyAndReturnsUnit ()
        publishPreservesEmptyContentsAndSentinel ()
        publishPreservesUnicodeAndLineEndings ()
        refreshUsesPublisherWithoutCollateralChanges ()
