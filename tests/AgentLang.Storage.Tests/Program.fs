namespace AgentLang.Storage.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open AgentLang

module Program =
    let mutable private assertions = 0

    let private check condition message =
        assertions <- assertions + 1
        if not condition then failwith message

    let private equal expected actual message =
        assertions <- assertions + 1
        if expected <> actual then failwith $"{message}: expected {expected}, got {actual}"

    let private ok (label: string) (result: Result<'T, StorageError>) : 'T =
        match result with
        | Ok value -> value
        | Error error -> failwith $"{label}: {error.Code}: {error.Message}"

    let private error (expectedCode: string) (result: Result<'T, StorageError>) : StorageError =
        match result with
        | Ok _ -> failwith $"expected {expectedCode}, got success"
        | Error actual ->
            equal expectedCode actual.Code "storage error code"
            actual

    let private newRoot name =
        let path = Path.Combine(Path.GetTempPath(), $"agentlang-storage-{name}-{Guid.NewGuid():N}")
        Directory.CreateDirectory path |> ignore
        path

    let private source kind text = Storage.sourceObject kind text

    let private digest (text: string) =
        UTF8Encoding(false).GetBytes text
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private digestBytes (bytes: byte array) =
        bytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private goldenV1Fixture name =
        File.ReadAllBytes(Path.Combine(__SOURCE_DIRECTORY__, "fixtures", "v1-golden", name))

    let private checkGoldenV1Hash name expected =
        let bytes = goldenV1Fixture name
        equal expected (digestBytes bytes) $"frozen v1 {name} hash"
        bytes

    let private bytesEqual (left: byte array) (right: byte array) =
        left.Length = right.Length && left.AsSpan().SequenceEqual(right.AsSpan())

    let private writeBytes (path: string) (bytes: byte array) =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllBytes(path, bytes)

    let private testFrozenV1Compatibility root =
        // These hashes and files were frozen from the v1 on-disk contract. Keep
        // the expected manifest and pointer literal; do not create them through
        // Storage.commit or its serializer in this compatibility test.
        let currentHash = "236daa1c04fa56447e51331d79fdd568ca289a1df7a19bf5aaff9a4fb274be3e"
        let manifestHash = "4c6d03175ba00897f616f5e292fec13662aa377969c4c9415ff20afd4996fb12"
        let projectHash = "e6b4cbf8141a8451ce44bbb1a9c65bd38bd83b830a7728d3ded525bbe3fa4e57"
        let definitionHash = "d5fccf0da01f2b10f3ab009d88ebfee93193263f3f525f0c09484dc379d8b31f"
        let testHash = "8460ea4537c9508a107090437311053d9cd9938dc7d550790a30d19fc62a51e4"
        let exampleHash = "a488717128dea5bec22b990072976ea2f5d676982eff0d58a4bf85901288844b"

        let currentBytes = checkGoldenV1Hash "CURRENT" currentHash
        let manifestBytes = checkGoldenV1Hash "manifest.json" manifestHash
        let projectBytes = checkGoldenV1Hash "project.agent" projectHash
        let definitionBytes = checkGoldenV1Hash "definition.agent" definitionHash
        let testBytes = checkGoldenV1Hash "test.agent" testHash
        let exampleBytes = checkGoldenV1Hash "example.agent" exampleHash
        let decode (bytes: byte array) = UTF8Encoding(false, true).GetString bytes
        let projectText = decode projectBytes

        let projectPath = Path.Combine(root, "golden-v1")
        let storeRoot = Path.Combine(projectPath, ".agentlang", "store")
        let objectsRoot = Path.Combine(storeRoot, "objects")
        let manifestRoot = Path.Combine(storeRoot, "manifests")
        writeBytes (Path.Combine(storeRoot, "CURRENT")) currentBytes
        writeBytes (Path.Combine(manifestRoot, manifestHash + ".json")) manifestBytes
        writeBytes (Path.Combine(objectsRoot, projectHash + ".agent")) projectBytes
        writeBytes (Path.Combine(objectsRoot, definitionHash + ".agent")) definitionBytes
        writeBytes (Path.Combine(objectsRoot, testHash + ".agent")) testBytes
        writeBytes (Path.Combine(objectsRoot, exampleHash + ".agent")) exampleBytes
        writeBytes (Path.Combine(projectPath, "dictionary.agent")) projectBytes

        let store = Storage.create projectPath
        let loaded = Storage.load store |> ok "load the frozen v1 fixture"
        equal 1L loaded.Generation "frozen v1 CURRENT generation"
        equal (ManifestAuthority manifestHash) loaded.Authority "frozen v1 authority"
        equal (Some manifestHash) loaded.ManifestHash "frozen v1 manifest identity"
        equal (Some projectText) loaded.ProjectSource "frozen v1 project source bytes"
        check (loaded.Manifest.IsSome) "frozen v1 manifest is parsed"
        equal 1 loaded.Manifest.Value.FormatVersion "frozen v1 schema version"
        equal projectHash loaded.Manifest.Value.ProjectSource.Hash "frozen v1 project object hash"
        equal definitionHash loaded.Manifest.Value.Revisions.Head.Definition.Hash "frozen v1 definition object hash"
        equal testHash loaded.Manifest.Value.Revisions.Head.Tests.Head.Hash "frozen v1 test object hash"
        equal exampleHash loaded.Manifest.Value.Revisions.Head.Examples.Head.Hash "frozen v1 example object hash"
        check (bytesEqual currentBytes (File.ReadAllBytes(Path.Combine(storeRoot, "CURRENT")))) "load leaves frozen v1 CURRENT bytes unchanged"

        let revision = Storage.readRevision store manifestHash "word-golden-v1" 1 |> ok "read frozen v1 revision"
        equal "fixture.stable" revision.Revision.Name "frozen v1 revision identity"
        equal (decode definitionBytes) revision.DefinitionSource "frozen v1 definition bytes"
        equal [ decode testBytes ] revision.TestSources "frozen v1 attached test bytes"
        equal [ decode exampleBytes ] revision.ExampleSources "frozen v1 attached example bytes"

        let captured = Storage.capture store |> ok "capture frozen v1 authority"
        equal (ManifestAuthority manifestHash) captured.Authority "captured frozen v1 authority"
        equal (Some projectText) captured.ExportText "captured frozen v1 export bytes"
        let providerFiles = Map.ofList [ "state/value", "golden" ]
        Storage.saveSnapshot store loaded.Generation "golden-v1" providerFiles (Some "2026-01-02T03:04:05Z")
        |> ok "save named snapshot of frozen v1"
        let named = Storage.readSnapshot store "golden-v1" |> ok "read named snapshot of frozen v1"
        equal manifestHash named.ManifestHash "named v1 snapshot retains manifest identity"
        equal 1 named.Manifest.FormatVersion "named v1 snapshot retains schema version"
        equal providerFiles named.VirtualFiles "named v1 snapshot provider bytes"
        equal (Some "2026-01-02T03:04:05Z") named.ClockValue "named v1 snapshot clock"

        let intermediateText = "temporary intervening export\n"
        let intermediateProject = Storage.sourceObject StorageObjectKind.ProjectSource intermediateText
        let intermediateManifest = { loaded.Manifest.Value with ProjectSource = intermediateProject.Reference }
        let commitIntermediate generation =
            Storage.commit store generation intermediateManifest [ intermediateProject ] intermediateText
            |> ok "commit intervening manifest before v1 restore"

        let firstIntervening = commitIntermediate loaded.Generation
        let namedRestore = Storage.restoreSnapshot store firstIntervening.Generation named |> ok "restore named frozen v1 snapshot"
        equal manifestHash namedRestore.ManifestHash.Value "named restore returns original v1 manifest identity"
        let secondIntervening = commitIntermediate namedRestore.Generation
        let taskRestore = Storage.restore store secondIntervening.Generation captured |> ok "restore captured frozen v1 authority"
        equal manifestHash taskRestore.ManifestHash.Value "task restore returns original v1 manifest identity"

        let final = Storage.load store |> ok "reload original v1 authority after both restore paths"
        equal manifestHash final.ManifestHash.Value "restored v1 manifest hash"
        equal 5L final.Generation "both restore paths advance storage generation"
        equal (Some projectText) final.ProjectSource "restored v1 project bytes"
        let finalRevision = Storage.readRevision store manifestHash "word-golden-v1" 1 |> ok "read restored frozen v1 revision"
        equal (decode definitionBytes) finalRevision.DefinitionSource "restored v1 definition bytes"
        equal [ decode testBytes ] finalRevision.TestSources "restored v1 test bytes"
        equal [ decode exampleBytes ] finalRevision.ExampleSources "restored v1 example bytes"

        let persistedManifest = File.ReadAllBytes(Path.Combine(manifestRoot, manifestHash + ".json"))
        check (bytesEqual manifestBytes persistedManifest) "restore leaves original v1 manifest bytes unchanged"
        for hash, expected in [ projectHash, projectBytes; definitionHash, definitionBytes; testHash, testBytes; exampleHash, exampleBytes ] do
            let persisted = File.ReadAllBytes(Path.Combine(objectsRoot, hash + ".agent"))
            check (bytesEqual expected persisted) $"restore leaves v1 object {hash} bytes unchanged"
        equal projectText (File.ReadAllText(Path.Combine(projectPath, "dictionary.agent"))) "restored v1 project export bytes"

    let private fixture suffix =
        let projectText = $"project fixture {suffix}\n"
        let definitionText = $"word sample.{suffix}\n    42\nend\n"
        let testText = $"test sample.{suffix}/returns-42\n    sample.{suffix}\n    expect 42\nend\n"
        let exampleText = $"example sample.{suffix}/basic\n    sample.{suffix}\n    => 42\nend\n"
        let project = source StorageObjectKind.ProjectSource projectText
        let definition = source StorageObjectKind.WordDefinition definitionText
        let test = source StorageObjectKind.TestDefinition testText
        let example = source StorageObjectKind.ExampleDefinition exampleText
        let manifest =
            { FormatVersion = 1
              ProjectSource = project.Reference
              Types = []
              Words =
                [ { WordId = "word-stable-1"
                    CurrentName = $"sample.{suffix}"
                    CurrentRevision = 1
                    Deprecated = false } ]
              Revisions =
                [ { WordId = "word-stable-1"
                    Name = $"sample.{suffix}"
                    Revision = 1
                    Definition = definition.Reference
                    Tests = [ test.Reference ]
                    Examples = [ example.Reference ]
                    Maturity = ProjectWord
                    Actor = "storage-test"
                    TaskId = Some "fixture"
                    TimestampUtc = DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)
                    Deprecated = false } ] }
        manifest, [ project; definition; test; example ], projectText

    let private commitFixture store generation suffix =
        let manifest, sources, projectText = fixture suffix
        Storage.commit store generation manifest sources projectText |> ok "commit fixture"

    let private testCommitReloadRevisionAndStableHistory root =
        let store = Storage.create (Path.Combine(root, "commit"))
        let before = Storage.load store |> ok "initial load"
        equal 0L before.Generation "new project begins at generation zero"
        equal EmptyAuthority before.Authority "new project has empty authority"

        let committed = commitFixture store 0L "first"
        equal 1L committed.Generation "commit increments generation"
        check committed.ManifestHash.IsSome "commit returns manifest hash"
        check committed.ExportWarning.IsNone "successful export has no warning"

        let loaded = Storage.load store |> ok "reload committed project"
        equal 1L loaded.Generation "reload sees committed generation"
        equal committed.ManifestHash loaded.ManifestHash "reload sees exact manifest hash"
        let manifest = loaded.Manifest |> Option.defaultWith (fun () -> failwith "manifest missing after reload")
        equal "word-stable-1" manifest.Words.Head.WordId "stable word ID survives reload"
        let history = Storage.readRevision store committed.ManifestHash.Value "word-stable-1" 1 |> ok "read attached history"
        equal "sample.first" history.Revision.Name "revision includes its stored name"
        equal "word sample.first\n    42\nend\n" history.DefinitionSource "definition source round trips exactly"
        equal 1 history.TestSources.Length "test source is retained"
        equal 1 history.ExampleSources.Length "example source is retained"

        let snapshot = Storage.capture store |> ok "capture committed state"
        let renamedProjectText = "project fixture rename\n"
        let renamedDefinition = source StorageObjectKind.WordDefinition "word sample.renamed\n    42\nend\n"
        let renamedTest = source StorageObjectKind.TestDefinition "test sample.renamed/returns-42\n    sample.renamed\n    expect 42\nend\n"
        let renamedExample = source StorageObjectKind.ExampleDefinition "example sample.renamed/basic\n    sample.renamed\n    => 42\nend\n"
        let renamedProject = source StorageObjectKind.ProjectSource renamedProjectText
        let oldRevision = loaded.Manifest.Value.Revisions.Head
        let renamedRevision =
            { oldRevision with
                Name = "sample.renamed"
                Revision = 2
                Definition = renamedDefinition.Reference
                Tests = [ renamedTest.Reference ]
                Examples = [ renamedExample.Reference ]
                TaskId = Some "rename"
                TimestampUtc = oldRevision.TimestampUtc.AddMinutes 1.0 }
        let renamedManifest =
            { loaded.Manifest.Value with
                ProjectSource = renamedProject.Reference
                Words = [ { loaded.Manifest.Value.Words.Head with CurrentName = "sample.renamed"; CurrentRevision = 2 } ]
                Revisions = loaded.Manifest.Value.Revisions @ [ renamedRevision ] }
        let next =
            Storage.commit store 1L renamedManifest [ renamedProject; renamedDefinition; renamedTest; renamedExample ] renamedProjectText
            |> ok "commit rename revision"
        let renamed = Storage.load store |> ok "load rename revision"
        let renamedManifest = renamed.Manifest.Value
        equal "word-stable-1" renamedManifest.Words.Head.WordId "rename retains stable identity"
        equal 2 renamedManifest.Words.Head.CurrentRevision "rename advances current revision"

        let archived =
            { renamedManifest with
                Words = [] }
        let archivedText = renamedProjectText
        let archivedCommitted =
            Storage.commit store next.Generation archived [] archivedText
            |> ok "archive word head while retaining immutable history"
        let archivedLoaded = Storage.load store |> ok "load archived history"
        equal 0 archivedLoaded.Manifest.Value.Words.Length "archived word has no active head"
        let archivedRevision = Storage.readRevision store archivedCommitted.ManifestHash.Value "word-stable-1" 1 |> ok "read archived historical revision"
        equal "sample.first" archivedRevision.Revision.Name "archived revision remains addressable"

        let restored = Storage.restore store archivedCommitted.Generation snapshot |> ok "restore exact captured manifest"
        equal 4L restored.Generation "restore advances generation without rewinding counter"
        equal committed.ManifestHash restored.ManifestHash "restore reinstates exact manifest identity"
        let final = Storage.load store |> ok "load restored authority"
        equal "word-stable-1" final.Manifest.Value.Words.Head.WordId "restore recovers stable word head"
        equal "sample.first" final.Manifest.Value.Words.Head.CurrentName "restore recovers original name"

    let private testStaleGenerationWriterLockAndLimits root =
        let project = Path.Combine(root, "stale")
        let store = Storage.create project
        let first = commitFixture store 0L "initial"
        let manifest, sources, text = fixture "stale"
        Storage.commit store 0L manifest sources text |> error "STORAGE_STALE_GENERATION" |> ignore
        equal first.Generation ((Storage.load store |> ok "load after stale commit").Generation) "stale commit does not advance pointer"
        Storage.saveSnapshot store first.Generation "../escape" Map.empty None |> error "STORAGE_INVALID_SNAPSHOT_NAME" |> ignore

        let lockPath = Path.Combine(project, ".agentlang", "store", "WRITE.lock")
        use held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
        Storage.load store |> error "STORAGE_WRITER_LOCKED" |> ignore

        let invalidRoot = Path.Combine(root, "invalid-source")
        let invalidStore = Storage.create invalidRoot
        let manifest, sources, text = fixture "invalid-source"
        let invalidProvided = sources @ [ { sources.Head with Content = sources.Head.Content + "tampered" } ]
        Storage.commit invalidStore 0L manifest invalidProvided text |> error "STORAGE_HASH_MISMATCH" |> ignore
        equal EmptyAuthority ((Storage.load invalidStore |> ok "load after invalid supplied source").Authority) "invalid source is rejected before authority changes"

    let private testFailureBoundaries root =
        let beforePath = Path.Combine(root, "before-pointer")
        let beforeStore =
            Storage.createWithFailureInjector beforePath (fun point ->
                if point = StorageFailurePoint.BeforePointerReplacement then failwith "injected before pointer")
        let manifest, sources, text = fixture "before"
        Storage.commit beforeStore 0L manifest sources text |> error "STORAGE_INJECTED_FAILURE" |> ignore
        let unchanged = Storage.load (Storage.create beforePath) |> ok "load after pre-pointer failure"
        equal 0L unchanged.Generation "pre-pointer failure preserves generation"
        equal EmptyAuthority unchanged.Authority "pre-pointer failure preserves prior authority"
        check (not (File.Exists(Path.Combine(beforePath, ".agentlang", "store", "CURRENT")))) "pre-pointer failure leaves CURRENT absent"

        let afterPath = Path.Combine(root, "after-pointer")
        let afterStore =
            Storage.createWithFailureInjector afterPath (fun point ->
                if point = StorageFailurePoint.AfterPointerReplacement then failwith "injected after pointer")
        let completed =
            let manifest, sources, text = fixture "after"
            Storage.commit afterStore 0L manifest sources text |> ok "post-pointer injected failure remains committed"
        equal 1L completed.Generation "post-pointer failure returns committed generation"
        equal (Some "STORAGE_INJECTED_FAILURE") (completed.ExportWarning |> Option.map (fun warning -> warning.Code)) "post-pointer failure is surfaced as warning"
        equal 1L ((Storage.load (Storage.create afterPath) |> ok "load after pointer warning").Generation) "post-pointer failure is authoritative"

        let exportPath = Path.Combine(root, "export-warning")
        let exportStore =
            Storage.createWithFailureInjector exportPath (fun point ->
                if point = StorageFailurePoint.BeforeExportRefresh then failwith "injected export failure")
        let exportCommitted =
            let manifest, sources, text = fixture "export"
            Storage.commit exportStore 0L manifest sources text |> ok "export failure does not roll back committed manifest"
        equal 1L exportCommitted.Generation "export failure still advances generation"
        equal (Some "STORAGE_INJECTED_FAILURE") (exportCommitted.ExportWarning |> Option.map (fun warning -> warning.Code)) "export failure has explicit warning"

    let private testLegacyMigrationAndEmptyRestore root =
        let legacyRoot = Path.Combine(root, "legacy")
        Directory.CreateDirectory legacyRoot |> ignore
        let exportPath = Path.Combine(legacyRoot, "dictionary.agent")
        let legacyText = "legacy words\n"
        File.WriteAllText(exportPath, legacyText, UTF8Encoding(false))
        let store = Storage.create legacyRoot
        let imported = Storage.load store |> ok "read legacy source"
        equal 0L imported.Generation "legacy import does not synthesize a generation"
        check (match imported.Authority with LegacyAuthority _ -> true | _ -> false) "legacy file is recognized as legacy authority"
        equal legacyText imported.ProjectSource.Value "legacy source content remains exact"
        let current = Path.Combine(legacyRoot, ".agentlang", "store", "CURRENT")
        check (not (File.Exists current)) "legacy load does not write CURRENT"
        let captured = Storage.capture store |> ok "capture pre-migration legacy state"

        commitFixture store 0L "migrated" |> ignore
        let restored = Storage.restore store 1L captured |> ok "restore pre-migration legacy authority"
        equal 2L restored.Generation "legacy restoration advances generation"
        equal (Some legacyText) (File.ReadAllText exportPath |> Some) "legacy export is restored"
        let afterRestore = Storage.load store |> ok "load restored legacy pointer"
        check (match afterRestore.Authority with LegacyAuthority _ -> true | _ -> false) "legacy pointer remains authoritative after migration rollback"

        let emptyRoot = Path.Combine(root, "empty")
        let emptyStore = Storage.create emptyRoot
        let empty = Storage.capture emptyStore |> ok "capture empty project"
        commitFixture emptyStore 0L "discard-me" |> ignore
        let cleared = Storage.restore emptyStore 1L empty |> ok "restore empty authority"
        equal 2L cleared.Generation "empty restore advances generation"
        let emptyLoaded = Storage.load emptyStore |> ok "load restored empty authority"
        equal EmptyAuthority emptyLoaded.Authority "empty authority is restored through pointer tombstone"
        equal None emptyLoaded.ProjectSource "empty authority has no project source"
        check (not (File.Exists(Path.Combine(emptyRoot, "dictionary.agent")))) "empty restore removes derived export"

    let private testNamedSnapshotsAndReadOnlyDiscovery root =
        let project = Path.Combine(root, "named-snapshot")
        let store = Storage.create project
        let baseCommit = commitFixture store 0L "snapshot-base"
        let files = Map.ofList [ "input/customer.json", "{\"id\":\"c-1\"}"; "results/out.txt", "ok" ]
        Storage.saveSnapshot store baseCommit.Generation "baseline-01" files (Some "2031-05-06T07:08:09Z")
        |> ok "save provider snapshot"
        Storage.saveSnapshot store baseCommit.Generation "invalid-vfs" (Map.ofList [ "../escape", "blocked" ]) None
        |> error "STORAGE_INVALID_VIRTUAL_PATH"
        |> ignore
        let next = commitFixture store baseCommit.Generation "snapshot-next"
        let beforeRead = Storage.load store |> ok "current before snapshot read"
        let named = Storage.readSnapshot store "baseline-01" |> ok "read named snapshot"
        equal files named.VirtualFiles "named snapshot provider files round trip"
        equal (Some "2031-05-06T07:08:09Z") named.ClockValue "named snapshot fixed clock round trips"
        equal beforeRead.ManifestHash ((Storage.load store |> ok "read must not activate snapshot").ManifestHash) "readSnapshot does not activate a snapshot"
        let restored = Storage.restoreSnapshot store next.Generation named |> ok "activate named snapshot"
        equal baseCommit.ManifestHash restored.ManifestHash "named snapshot restores its exact manifest"
        let activated = Storage.readSnapshot store "baseline-01" |> ok "snapshot remains readable after activation"
        equal named.VirtualFiles activated.VirtualFiles "provider state remains immutable and readable"

        let snapshotPath = Path.Combine(project, ".agentlang", "store", "snapshots", "baseline-01.json")
        let snapshotFile = JsonNode.Parse(File.ReadAllText snapshotPath)
        let virtualFilesNode = snapshotFile["virtualFiles"].AsObject()
        let virtualFilesHash = virtualFilesNode["hash"].GetValue<string>()
        let providerObject = Path.Combine(project, ".agentlang", "store", "objects", virtualFilesHash + ".json")
        File.WriteAllText(providerObject, "tampered provider state", UTF8Encoding(false))
        Storage.readSnapshot store "baseline-01" |> error "STORAGE_HASH_MISMATCH" |> ignore

    let private testTamperingUnsupportedVersionAndNoFallback root =
        let project = Path.Combine(root, "tamper")
        let store = Storage.create project
        let committed = commitFixture store 0L "tamper"
        let manifest = (Storage.load store |> ok "load before object tamper").Manifest.Value
        let definitionRef = manifest.Revisions.Head.Definition
        let objectPath = Path.Combine(project, ".agentlang", "store", "objects", definitionRef.Hash + ".agent")
        File.WriteAllText(objectPath, "tampered source", UTF8Encoding(false))
        Storage.readSource store definitionRef |> error "STORAGE_HASH_MISMATCH" |> ignore
        Storage.load store |> error "STORAGE_HASH_MISMATCH" |> ignore

        let corruptRoot = Path.Combine(root, "unsupported")
        let corruptStore = Storage.create corruptRoot
        commitFixture corruptStore 0L "unsupported" |> ignore
        let currentPath = Path.Combine(corruptRoot, ".agentlang", "store", "CURRENT")
        let invalidPointer = JsonObject()
        invalidPointer["formatVersion"] <- JsonValue.Create(999)
        invalidPointer["generation"] <- JsonValue.Create(2)
        invalidPointer["kind"] <- JsonValue.Create("empty")
        File.WriteAllText(currentPath, invalidPointer.ToJsonString(), UTF8Encoding(false))
        File.WriteAllText(Path.Combine(corruptRoot, "dictionary.agent"), "fallback must not occur", UTF8Encoding(false))
        Storage.load corruptStore |> error "STORAGE_UNSUPPORTED_VERSION" |> ignore

        let invalidHash = { Kind = StorageObjectKind.WordDefinition; Hash = "../escape" }
        Storage.readSource corruptStore invalidHash |> error "STORAGE_INVALID_HASH" |> ignore

        let missingRoot = Path.Combine(root, "missing-object")
        let missingStore = Storage.create missingRoot
        commitFixture missingStore 0L "missing" |> ignore
        let missingManifest = (Storage.load missingStore |> ok "load before missing-object test").Manifest.Value
        let missingDefinition = missingManifest.Revisions.Head.Definition
        File.Delete(Path.Combine(missingRoot, ".agentlang", "store", "objects", missingDefinition.Hash + ".agent"))
        Storage.load missingStore |> error "STORAGE_OBJECT_MISSING" |> ignore

        let schemaRoot = Path.Combine(root, "unsupported-manifest")
        let schemaStore = Storage.create schemaRoot
        commitFixture schemaStore 0L "schema" |> ignore
        let projectSource = (Storage.load schemaStore |> ok "load before unsupported-manifest test").Manifest.Value.ProjectSource
        let invalidManifest = JsonObject()
        invalidManifest["formatVersion"] <- JsonValue.Create(999)
        let projectReference = JsonObject()
        projectReference["kind"] <- JsonValue.Create("project-source")
        projectReference["hash"] <- JsonValue.Create(projectSource.Hash)
        invalidManifest["projectSource"] <- projectReference
        invalidManifest["types"] <- JsonArray()
        invalidManifest["words"] <- JsonArray()
        invalidManifest["revisions"] <- JsonArray()
        let invalidManifestText = invalidManifest.ToJsonString()
        let invalidManifestHash = digest invalidManifestText
        let manifestPath = Path.Combine(schemaRoot, ".agentlang", "store", "manifests", invalidManifestHash + ".json")
        File.WriteAllText(manifestPath, invalidManifestText, UTF8Encoding(false))
        let pointer = JsonObject()
        pointer["formatVersion"] <- JsonValue.Create(1)
        pointer["generation"] <- JsonValue.Create(2)
        pointer["kind"] <- JsonValue.Create("manifest")
        pointer["manifestHash"] <- JsonValue.Create(invalidManifestHash)
        File.WriteAllText(Path.Combine(schemaRoot, ".agentlang", "store", "CURRENT"), pointer.ToJsonString(), UTF8Encoding(false))
        Storage.load schemaStore |> error "STORAGE_UNSUPPORTED_VERSION" |> ignore

    let private testTaskLogValidation root =
        let store = Storage.create (Path.Combine(root, "task-log"))
        Storage.saveTaskLog store "task-104" "{\"task\":\"task-104\",\"testsRun\":2}" |> ok "save structured task log"
        let log = Path.Combine(root, "task-log", "history", "task-task-104.json")
        check (File.Exists log) "task log is written beneath project history"
        equal "{\"task\":\"task-104\",\"testsRun\":2}" (File.ReadAllText log) "task log content is exact"
        Storage.saveTaskLog store "../escape" "{}" |> error "STORAGE_INVALID_TASK_ID" |> ignore
        Storage.saveTaskLog store "task-105" "not-json" |> error "STORAGE_INVALID_TASK_LOG" |> ignore

    let private testReparsePointRefusal root =
        let target = Path.Combine(root, "reparse-target")
        let link = Path.Combine(root, "reparse-link")
        Directory.CreateDirectory target |> ignore
        try
            Directory.CreateSymbolicLink(link, target) |> ignore
            Storage.load (Storage.create link) |> error "STORAGE_REPARSE_POINT" |> ignore
        with
        | :? UnauthorizedAccessException -> printfn "Reparse-point test skipped: symlink creation is not permitted by this host."
        | :? PlatformNotSupportedException -> printfn "Reparse-point test skipped: directory symlinks are unavailable on this host."

    [<EntryPoint>]
    let main _ =
        let root = newRoot "suite"
        try
            testFrozenV1Compatibility root
            testCommitReloadRevisionAndStableHistory root
            testStaleGenerationWriterLockAndLimits root
            testFailureBoundaries root
            testLegacyMigrationAndEmptyRestore root
            testNamedSnapshotsAndReadOnlyDiscovery root
            testTamperingUnsupportedVersionAndNoFallback root
            testTaskLogValidation root
            testReparsePointRefusal root
            printfn $"Storage tests passed: 9 groups, {assertions} assertions."
            0
        finally
            if Directory.Exists root then Directory.Delete(root, true)
