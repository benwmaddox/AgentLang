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
        equal { Frontend = SourceFrontend.Stack; Version = 1 } loaded.Manifest.Value.Revisions.Head.SourceFormat "frozen v1 defaults to Stack/1"
        equal [] loaded.Manifest.Value.Revisions.Head.CallBindings "frozen v1 defaults to no call bindings"
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
                    SourceFormat = { Frontend = SourceFrontend.Stack; Version = 1 }
                    CallBindings = []
                    Maturity = ProjectWord
                    Actor = "storage-test"
                    TaskId = Some "fixture"
                    TimestampUtc = DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)
                    Deprecated = false } ] }
        manifest, [ project; definition; test; example ], projectText

    let private flowFormat : SourceFormat = { Frontend = SourceFrontend.Flow; Version = 1 }
    let private stackFormat : SourceFormat = { Frontend = SourceFrontend.Stack; Version = 1 }

    let private typeSource name definition sourceFormat validatorTarget : TypeSource =
        { Name = name
          Definition = definition
          SourceFormat = sourceFormat
          ValidatorTarget = validatorTarget }

    let private callBinding
        (sourceReference: SourceRef)
        (caseName: string option)
        (bodyRole: StoredCallBodyRole)
        (path: FlowAstPathSegment list)
        (form: StoredCallForm)
        (requestedName: string)
        (target: StoredCallTarget)
        : StoredCallBinding =
        { Source = sourceReference
          CaseName = caseName
          BodyRole = bodyRole
          Path = FlowAstPath.FlowAstPath path
          Form = form
          RequestedName = requestedName
          Target = target }

    let private flowV2Fixture suffix =
        let manifest, sources, projectText = fixture suffix
        let definition = List.item 1 sources
        let test = List.item 2 sources
        let example = List.item 3 sources
        let wordName = $"sample.{suffix}"
        let callBindings =
            [ callBinding definition.Reference None StoredCallBodyRole.Definition
                  [ FlowAstPathSegment.BlockStatement 0
                    FlowAstPathSegment.EvaluateExpression
                    FlowAstPathSegment.CallArgument 0 ]
                  StoredCallForm.Direct wordName (StoredCallTarget.UserWord "word-target-stable-1")
              callBinding test.Reference (Some "returns-42") StoredCallBodyRole.Actual
                  [ FlowAstPathSegment.BlockStatement 0
                    FlowAstPathSegment.EvaluateExpression
                    FlowAstPathSegment.RootCallArgument 0 ]
                  StoredCallForm.AbsoluteRoot wordName (StoredCallTarget.Primitive "int.add")
              callBinding test.Reference (Some "returns-42") StoredCallBodyRole.ExpectedExpression
                  [ FlowAstPathSegment.BlockStatement 0
                    FlowAstPathSegment.EvaluateExpression
                    FlowAstPathSegment.CallArgument 0 ]
                  (StoredCallForm.DotStage "option.some") wordName (StoredCallTarget.GeneratedWord "generated:option.some")
              callBinding example.Reference (Some "basic") StoredCallBodyRole.Actual
                  [ FlowAstPathSegment.BlockStatement 0
                    FlowAstPathSegment.EvaluateExpression
                    FlowAstPathSegment.DotArgument 0 ]
                  (StoredCallForm.StaticCallback("map", FlowWordReferenceQualification.NamespaceQualified))
                  wordName (StoredCallTarget.UserWord "word-target-stable-2") ]
        let revision =
            { manifest.Revisions.Head with
                SourceFormat = flowFormat
                CallBindings = callBindings }
        { manifest with FormatVersion = 2; Revisions = [ revision ] }, sources, projectText

    let private flowV3Fixture suffix =
        let manifest, sources, projectText = flowV2Fixture suffix
        let record = source StorageObjectKind.TypeDefinition $"type {suffix}.Record = record"
        let flowRecord = source StorageObjectKind.TypeDefinition $"type {suffix}.FlowRecord = flow"
        let userValidatedScalar = source StorageObjectKind.TypeDefinition $"type {suffix}.Email = scalar"
        let primitiveValidatedScalar = source StorageObjectKind.TypeDefinition $"type {suffix}.Flag = scalar"
        let generatedValidatedScalar = source StorageObjectKind.TypeDefinition $"type {suffix}.Wrapped = scalar"
        let types =
            [ typeSource $"{suffix}.Record" record.Reference stackFormat None
              typeSource $"{suffix}.FlowRecord" flowRecord.Reference flowFormat None
              typeSource $"{suffix}.Email" userValidatedScalar.Reference flowFormat (Some(StoredCallTarget.UserWord "word-stable-1"))
              typeSource $"{suffix}.Flag" primitiveValidatedScalar.Reference flowFormat (Some(StoredCallTarget.Primitive "string.contains"))
              typeSource $"{suffix}.Wrapped" generatedValidatedScalar.Reference flowFormat (Some(StoredCallTarget.GeneratedWord "generated:scalar.wrap")) ]
        { manifest with FormatVersion = 3; Types = types },
        sources @ [ record; flowRecord; userValidatedScalar; primitiveValidatedScalar; generatedValidatedScalar ],
        projectText

    let private appendFlowRevision (baseManifest: ProjectManifest) suffix =
        let flowManifest, sources, projectText = flowV2Fixture suffix
        let maxRevision = baseManifest.Revisions |> List.map (fun item -> item.Revision) |> List.max
        let newRevision = { flowManifest.Revisions.Head with Revision = maxRevision + 1 }
        let nextManifest =
            { baseManifest with
                FormatVersion = 2
                ProjectSource = flowManifest.ProjectSource
                Words =
                    baseManifest.Words
                    |> List.map (fun head ->
                        if head.WordId = newRevision.WordId then
                            { head with CurrentName = newRevision.Name; CurrentRevision = newRevision.Revision }
                        else head)
                Revisions = baseManifest.Revisions @ [ newRevision ] }
        nextManifest, sources, projectText, newRevision

    let private storageRoot project = Path.Combine(project, ".agentlang", "store")

    let private installedRawManifest project (rawText: string) =
        let storeRoot = storageRoot project
        let manifestHash = digest rawText
        writeBytes
            (Path.Combine(storeRoot, "manifests", manifestHash + ".json"))
            (UTF8Encoding(false).GetBytes rawText)
        let currentPath = Path.Combine(storeRoot, "CURRENT")
        let current = JsonNode.Parse(File.ReadAllText currentPath).AsObject()
        let generation = current["generation"].GetValue<int64>()
        current["generation"] <- JsonValue.Create(generation + 1L)
        current["kind"] <- JsonValue.Create("manifest")
        current["manifestHash"] <- JsonValue.Create(manifestHash)
        File.WriteAllText(currentPath, current.ToJsonString(), UTF8Encoding(false))
        manifestHash

    let private cloneValidFlowManifest project =
        let store = Storage.create project
        let manifest, sources, text = flowV2Fixture "raw"
        Storage.commit store 0L manifest sources text |> ok "write raw-manifest v2 base" |> ignore
        let loaded = Storage.load store |> ok "load raw-manifest v2 base"
        let raw =
            File.ReadAllText(
                Path.Combine(storageRoot project, "manifests", loaded.ManifestHash.Value + ".json"))
        store, JsonNode.Parse(raw).AsObject()

    let private cloneValidFlowV3Manifest project =
        let store = Storage.create project
        let manifest, sources, text = flowV3Fixture "raw-v3"
        Storage.commit store 0L manifest sources text |> ok "write raw-manifest v3 base" |> ignore
        let loaded = Storage.load store |> ok "load raw-manifest v3 base"
        let raw =
            File.ReadAllText(
                Path.Combine(storageRoot project, "manifests", loaded.ManifestHash.Value + ".json"))
        store, JsonNode.Parse(raw).AsObject()

    let private firstRevisionObject (manifest: JsonObject) =
        let revisions = manifest["revisions"].AsArray()
        (revisions.[0]).AsObject()

    let private firstCallBindingObject (manifest: JsonObject) =
        let revision = firstRevisionObject manifest
        let bindings = revision["callBindings"].AsArray()
        (bindings.[0]).AsObject()

    let private firstTypeSourceObject (manifest: JsonObject) =
        let types = manifest["types"].AsArray()
        (types.[0]).AsObject()

    let private commitFixture store generation suffix =
        let manifest, sources, projectText = fixture suffix
        Storage.commit store generation manifest sources projectText |> ok "commit fixture"

    let private testManifestV1WriterRefusesMeaningfulV2Fields root =
        let baseManifest, baseSources, projectText = fixture "v1-writer"
        let stackTypeObject = source StorageObjectKind.TypeDefinition "type v1-writer.Record = record"
        let manifest =
            { baseManifest with
                Types = [ typeSource "v1-writer.Record" stackTypeObject.Reference stackFormat None ] }
        let sources = stackTypeObject :: baseSources
        let flowSourceFormat = { Frontend = SourceFrontend.Flow; Version = 1 }
        let changedFormat =
            { manifest with
                Revisions = [ { manifest.Revisions.Head with SourceFormat = flowSourceFormat } ] }
        let changedVersion =
            { manifest with
                Revisions = [ { manifest.Revisions.Head with SourceFormat = { Frontend = SourceFrontend.Stack; Version = 2 } } ] }
        let definitionBinding =
            callBinding manifest.Revisions.Head.Definition None StoredCallBodyRole.Definition
                [ FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.EvaluateExpression ]
                StoredCallForm.Direct manifest.Revisions.Head.Name (StoredCallTarget.UserWord "retained-word-id")
        let changedBindings =
            { manifest with
                Revisions = [ { manifest.Revisions.Head with CallBindings = [ definitionBinding ] } ] }
        let changedTypeFrontend =
            { manifest with
                Types = [ { manifest.Types.Head with SourceFormat = flowFormat } ] }
        let changedTypeTarget =
            { manifest with
                Types = [ { manifest.Types.Head with ValidatorTarget = Some(StoredCallTarget.UserWord "validator-word-id") } ] }
        let changedTypeVersion =
            { manifest with
                Types = [ { manifest.Types.Head with SourceFormat = { Frontend = SourceFrontend.Stack; Version = 2 } } ] }
        for name, invalid, expectedCode in
            [ "frontend", changedFormat, "STORAGE_INVALID_MANIFEST"
              "source-version", changedVersion, "STORAGE_UNSUPPORTED_VERSION"
              "bindings", changedBindings, "STORAGE_INVALID_MANIFEST"
              "type-frontend", changedTypeFrontend, "STORAGE_INVALID_MANIFEST"
              "type-validator-target", changedTypeTarget, "STORAGE_INVALID_MANIFEST"
              "type-source-version", changedTypeVersion, "STORAGE_UNSUPPORTED_VERSION" ] do
            let project = Path.Combine(root, "v1-writer-" + name)
            let store = Storage.create project
            Storage.commit store 0L invalid sources projectText |> error expectedCode |> ignore
            let after = Storage.load store |> ok "load after refused v1 metadata"
            equal EmptyAuthority after.Authority $"v1 {name} refusal leaves authority empty"
            equal 0L after.Generation $"v1 {name} refusal leaves generation unchanged"
            check (not (File.Exists(Path.Combine(storageRoot project, "CURRENT")))) $"v1 {name} refusal does not create CURRENT"

        let project = Path.Combine(root, "v1-parser-fields")
        let store = Storage.create project
        Storage.commit store 0L manifest sources projectText |> ok "write v1 parser-field base" |> ignore
        let loaded = Storage.load store |> ok "load v1 parser-field base"
        let loadedType = loaded.Manifest.Value.Types.Head
        equal stackFormat loadedType.SourceFormat "v1 type source defaults to Stack/1"
        equal None loadedType.ValidatorTarget "v1 type source defaults to no validator target"
        let rawPath = Path.Combine(storageRoot project, "manifests", loaded.ManifestHash.Value + ".json")
        let raw = JsonNode.Parse(File.ReadAllText rawPath).AsObject()
        let rawType = firstTypeSourceObject raw
        check (not (rawType.ContainsKey "sourceFormat")) "v1 type source encoding omits sourceFormat"
        check (not (rawType.ContainsKey "validatorTarget")) "v1 type source encoding omits validatorTarget"
        let revision = firstRevisionObject raw
        let flowNode = JsonObject()
        flowNode["frontend"] <- JsonValue.Create("flow")
        flowNode["version"] <- JsonValue.Create(1)
        revision["sourceFormat"] <- flowNode
        let rawText = raw.ToJsonString()
        installedRawManifest project rawText |> ignore
        Storage.load store |> error "STORAGE_INVALID_MANIFEST" |> ignore

    let private testManifestV2RoundTripAndCanonicalBindings root =
        let baseManifest, baseSources, projectText = flowV2Fixture "roundtrip"
        let stackTypeObject = source StorageObjectKind.TypeDefinition "type roundtrip.Record = record"
        let manifest =
            { baseManifest with
                Types = [ typeSource "roundtrip.Record" stackTypeObject.Reference stackFormat None ] }
        let sources = stackTypeObject :: baseSources
        let firstProject = Path.Combine(root, "v2-first")
        let firstStore = Storage.create firstProject
        let committed = Storage.commit firstStore 0L manifest sources projectText |> ok "commit v2 source-format manifest"
        let loaded = Storage.load firstStore |> ok "load v2 source-format manifest"
        equal committed.ManifestHash loaded.ManifestHash "v2 reload keeps manifest hash"
        let loadedManifest = loaded.Manifest.Value
        equal 2 loadedManifest.FormatVersion "manifest schema version 2 round trips"
        equal [ typeSource "roundtrip.Record" stackTypeObject.Reference stackFormat None ] loadedManifest.Types "v2 type sources default to Stack/1 without validator targets"
        let expectedRevision = manifest.Revisions.Head
        let actualRevision = loadedManifest.Revisions.Head
        equal flowFormat actualRevision.SourceFormat "v2 Flow/1 frontend metadata round trips"
        equal (Set.ofList expectedRevision.CallBindings) (Set.ofList actualRevision.CallBindings) "v2 stored binding fields round trip"
        equal 4 actualRevision.CallBindings.Length "definition, actual, expected, and example call sites persist"
        check (actualRevision.CallBindings |> List.exists (fun item -> item.BodyRole = StoredCallBodyRole.ExpectedExpression)) "expected-expression call binding survives separately"
        check (actualRevision.CallBindings |> List.exists (fun item -> item.CaseName = Some "basic" && item.BodyRole = StoredCallBodyRole.Actual)) "example call binding retains its case identity"
        check (actualRevision.CallBindings |> List.exists (fun item -> item.Form = StoredCallForm.AbsoluteRoot)) "absolute-root call form round trips"
        check (actualRevision.CallBindings |> List.exists (fun item -> item.Target = StoredCallTarget.Primitive "int.add")) "stable primitive target identity round trips"
        let rawManifestText = File.ReadAllText(Path.Combine(storageRoot firstProject, "manifests", loaded.ManifestHash.Value + ".json"))
        let rawManifest = JsonNode.Parse(rawManifestText).AsObject()
        let rawType = firstTypeSourceObject rawManifest
        check (not (rawType.ContainsKey "sourceFormat")) "v2 type source encoding omits sourceFormat"
        check (not (rawType.ContainsKey "validatorTarget")) "v2 type source encoding omits validatorTarget"
        let rawRevision = firstRevisionObject rawManifest
        let rawBindings = rawRevision["callBindings"].AsArray()
        for bindingNode in rawBindings do
            let bindingObject = bindingNode.AsObject()
            let targetObject = bindingObject["target"].AsObject()
            check (not (bindingObject.ContainsKey "targetRevision")) "stored call binding omits mutable target revision"
            check (not (targetObject.ContainsKey "revision")) "stored target identity has no revision number"

        let currentPath = Path.Combine(storageRoot firstProject, "CURRENT")
        let current = JsonNode.Parse(File.ReadAllText currentPath).AsObject()
        equal 1 (current["formatVersion"].GetValue<int>()) "CURRENT pointer envelope stays at version 1"
        Storage.saveSnapshot firstStore loaded.Generation "manifest-v2" Map.empty None |> ok "snapshot v2 manifest under v1 snapshot envelope"
        let snapshotPath = Path.Combine(storageRoot firstProject, "snapshots", "manifest-v2.json")
        let snapshotJson = JsonNode.Parse(File.ReadAllText snapshotPath).AsObject()
        equal 1 (snapshotJson["formatVersion"].GetValue<int>()) "named snapshot envelope stays at version 1"
        let snapshot = Storage.readSnapshot firstStore "manifest-v2" |> ok "read v2 manifest snapshot"
        equal 2 snapshot.Manifest.FormatVersion "v1 snapshot envelope can reference v2 manifest"
        equal loaded.ManifestHash.Value snapshot.ManifestHash "snapshot retains exact v2 manifest identity"

        let reversedManifest =
            { manifest with
                Revisions = [ { manifest.Revisions.Head with CallBindings = List.rev manifest.Revisions.Head.CallBindings } ] }
        let reversedStore = Storage.create (Path.Combine(root, "v2-reversed"))
        let reversed = Storage.commit reversedStore 0L reversedManifest sources projectText |> ok "commit reversed input call-binding order"
        equal committed.ManifestHash reversed.ManifestHash "v2 canonical serialization ignores input binding order"

    let private testManifestV3TypeSourceRoundTripAndValidation root =
        let manifest, sources, projectText = flowV3Fixture "v3-roundtrip"
        let firstProject = Path.Combine(root, "v3-first")
        let firstStore = Storage.create firstProject
        let committed = Storage.commit firstStore 0L manifest sources projectText |> ok "commit v3 type-source manifest"
        let loaded = Storage.load firstStore |> ok "load v3 type-source manifest"
        equal committed.ManifestHash loaded.ManifestHash "v3 reload keeps the exact manifest hash"
        let loadedManifest = loaded.Manifest.Value
        equal 3 loadedManifest.FormatVersion "manifest schema version 3 round trips"
        equal (manifest.Types |> List.sortBy _.Name) loadedManifest.Types "v3 source formats and validator identities round trip"
        equal flowFormat loadedManifest.Revisions.Head.SourceFormat "v3 word revision keeps the v2 Flow/1 metadata"
        equal (Set.ofList manifest.Revisions.Head.CallBindings) (Set.ofList loadedManifest.Revisions.Head.CallBindings) "v3 word call-binding encoding remains v2-compatible"
        for item in loadedManifest.Types do
            equal (Storage.readSource firstStore item.Definition |> ok "read v3 type source") (List.find (fun candidate -> candidate.Reference = item.Definition) sources).Content $"v3 type source {item.Name} remains hash-addressed"

        let manifestPath = Path.Combine(storageRoot firstProject, "manifests", loaded.ManifestHash.Value + ".json")
        let rawText = File.ReadAllText manifestPath
        equal loaded.ManifestHash.Value (digest rawText) "v3 manifest bytes match the content-addressed filename"
        let rawManifest = JsonNode.Parse(rawText).AsObject()
        let rawTypes = rawManifest["types"].AsArray() |> Seq.cast<JsonNode> |> Seq.map (fun node -> node.AsObject()) |> Seq.toList
        let typeNode name = rawTypes |> List.find (fun item -> item["name"].GetValue<string>() = name)
        let v3Revision = firstRevisionObject rawManifest
        let expectedWordRevisionProperties =
            [ "actor"; "callBindings"; "definition"; "deprecated"; "examples"; "maturity"; "name"; "revision"
              "sourceFormat"; "taskId"; "tests"; "timestampUtc"; "wordId" ]
        equal expectedWordRevisionProperties (v3Revision |> Seq.map (fun pair -> pair.Key) |> Seq.sort |> Seq.toList) "v3 word revision wire fields remain identical to v2"
        let recordNode = typeNode "v3-roundtrip.Record"
        check (recordNode.ContainsKey "sourceFormat") "v3 type source includes required sourceFormat"
        check (recordNode.ContainsKey "validatorTarget") "v3 unvalidated type includes explicit nullable validatorTarget"
        let recordFrontend = recordNode["sourceFormat"]["frontend"]
        equal "stack" (recordFrontend.GetValue<string>()) "v3 Stack type source frontend is explicit"
        check (isNull recordNode["validatorTarget"]) "v3 record has no validator identity"
        let flowRecordNode = typeNode "v3-roundtrip.FlowRecord"
        let flowRecordFrontend = flowRecordNode["sourceFormat"]["frontend"]
        equal "flow" (flowRecordFrontend.GetValue<string>()) "v3 Flow type source frontend is explicit"
        check (isNull flowRecordNode["validatorTarget"]) "v3 unvalidated Flow type preserves a null validator"
        for name, kind, identity in
            [ "v3-roundtrip.Email", "userWord", "word-stable-1"
              "v3-roundtrip.Flag", "primitive", "string.contains"
              "v3-roundtrip.Wrapped", "generatedWord", "generated:scalar.wrap" ] do
            let ownerTypeNode = typeNode name
            let targetNode = ownerTypeNode["validatorTarget"]
            let target = targetNode.AsObject()
            let targetKind = target["kind"]
            let targetIdentity = target["identity"]
            equal kind (targetKind.GetValue<string>()) $"v3 {name} validator target kind"
            equal identity (targetIdentity.GetValue<string>()) $"v3 {name} stable validator target identity"
            check (not (target.ContainsKey "revision")) $"v3 {name} validator target does not persist a mutable revision"

        let current = JsonNode.Parse(File.ReadAllText(Path.Combine(storageRoot firstProject, "CURRENT"))).AsObject()
        equal 1 (current["formatVersion"].GetValue<int>()) "CURRENT pointer envelope remains version 1 for a v3 manifest"
        Storage.saveSnapshot firstStore loaded.Generation "manifest-v3" Map.empty None |> ok "snapshot v3 manifest under v1 snapshot envelope"
        let snapshotPath = Path.Combine(storageRoot firstProject, "snapshots", "manifest-v3.json")
        let snapshotJson = JsonNode.Parse(File.ReadAllText snapshotPath).AsObject()
        equal 1 (snapshotJson["formatVersion"].GetValue<int>()) "named snapshot envelope remains version 1 for a v3 manifest"
        let snapshot = Storage.readSnapshot firstStore "manifest-v3" |> ok "read v3 manifest snapshot"
        equal 3 snapshot.Manifest.FormatVersion "v1 snapshot envelope can reference v3 manifest"
        equal loaded.ManifestHash.Value snapshot.ManifestHash "v3 snapshot retains the exact manifest identity"

        let reversedManifest = { manifest with Types = List.rev manifest.Types }
        let reversedStore = Storage.create (Path.Combine(root, "v3-reversed-types"))
        let reversed = Storage.commit reversedStore 0L reversedManifest sources projectText |> ok "commit reversed v3 type input order"
        equal committed.ManifestHash reversed.ManifestHash "v3 canonical serialization ignores input type order"
        let reversedRaw = File.ReadAllBytes(Path.Combine(storageRoot (Path.Combine(root, "v3-reversed-types")), "manifests", reversed.ManifestHash.Value + ".json"))
        check (bytesEqual (File.ReadAllBytes manifestPath) reversedRaw) "v3 canonical serialization re-emits byte-identical manifest bytes"

        let typeToCorrupt = loadedManifest.Types |> List.find (fun item -> item.Name = "v3-roundtrip.Record")
        let corruptTypePath = Path.Combine(storageRoot firstProject, "objects", typeToCorrupt.Definition.Hash + ".agent")
        File.WriteAllText(corruptTypePath, "tampered type source", UTF8Encoding(false))
        Storage.load firstStore |> error "STORAGE_HASH_MISMATCH" |> ignore

        let expectRawFailure name expectedCode mutate =
            let project = Path.Combine(root, "invalid-v3-type-" + name)
            let store, raw = cloneValidFlowV3Manifest project
            mutate (firstTypeSourceObject raw)
            installedRawManifest project (raw.ToJsonString()) |> ignore
            Storage.load store |> error expectedCode |> ignore

        let expectRawRevisionFailure name expectedCode mutate =
            let project = Path.Combine(root, "invalid-v3-word-revision-" + name)
            let store, raw = cloneValidFlowV3Manifest project
            mutate (firstRevisionObject raw)
            installedRawManifest project (raw.ToJsonString()) |> ignore
            Storage.load store |> error expectedCode |> ignore

        expectRawFailure "missing-source-format" "STORAGE_INVALID_JSON" (fun item ->
            item.Remove("sourceFormat") |> ignore)
        expectRawFailure "missing-validator-target" "STORAGE_INVALID_JSON" (fun item ->
            item.Remove("validatorTarget") |> ignore)
        expectRawFailure "missing-frontend" "STORAGE_INVALID_JSON" (fun item ->
            let sourceFormat = item["sourceFormat"].AsObject()
            sourceFormat.Remove("frontend") |> ignore)
        expectRawFailure "missing-source-version" "STORAGE_INVALID_JSON" (fun item ->
            let sourceFormat = item["sourceFormat"].AsObject()
            sourceFormat.Remove("version") |> ignore)
        expectRawFailure "unknown-frontend" "STORAGE_UNSUPPORTED_FRONTEND" (fun item ->
            item["sourceFormat"]["frontend"] <- JsonValue.Create("future"))
        expectRawFailure "unsupported-source-version" "STORAGE_UNSUPPORTED_VERSION" (fun item ->
            item["sourceFormat"]["version"] <- JsonValue.Create(77))
        expectRawFailure "unknown-target-kind" "STORAGE_INVALID_MANIFEST" (fun item ->
            item["validatorTarget"]["kind"] <- JsonValue.Create("dynamic"))
        expectRawFailure "missing-userword-head" "STORAGE_INVALID_MANIFEST" (fun item ->
            item["validatorTarget"]["identity"] <- JsonValue.Create("word-not-in-manifest"))
        expectRawFailure "target-revision-field" "STORAGE_INVALID_MANIFEST" (fun item ->
            item["validatorTarget"]["revision"] <- JsonValue.Create(1))
        expectRawFailure "nonobject-target" "STORAGE_INVALID_JSON" (fun item ->
            item["validatorTarget"] <- JsonValue.Create("word-stable-1"))
        expectRawFailure "missing-target-kind" "STORAGE_INVALID_JSON" (fun item ->
            let target = item["validatorTarget"].AsObject()
            target.Remove("kind") |> ignore)
        expectRawFailure "missing-target-identity" "STORAGE_INVALID_JSON" (fun item ->
            let target = item["validatorTarget"].AsObject()
            target.Remove("identity") |> ignore)
        expectRawFailure "empty-target-identity" "STORAGE_INVALID_MANIFEST" (fun item ->
            item["validatorTarget"]["identity"] <- JsonValue.Create(""))
        expectRawFailure "overlong-target-identity" "STORAGE_INVALID_MANIFEST" (fun item ->
            item["validatorTarget"]["identity"] <- JsonValue.Create(String.replicate 129 "x"))
        expectRawRevisionFailure "missing-source-format" "STORAGE_INVALID_JSON" (fun revision ->
            revision.Remove("sourceFormat") |> ignore)
        expectRawRevisionFailure "missing-call-bindings" "STORAGE_INVALID_JSON" (fun revision ->
            revision.Remove("callBindings") |> ignore)

        let v2Manifest, v2Sources, v2Text = flowV2Fixture "v2-type-guard"
        let flowTypeObject = source StorageObjectKind.TypeDefinition "type v2-type-guard.FlowType = record"
        let validV2WithType =
            { v2Manifest with
                Types = [ typeSource "v2-type-guard.FlowType" flowTypeObject.Reference flowFormat None ] }
        let v2WithSources = flowTypeObject :: v2Sources
        Storage.commit (Storage.create (Path.Combine(root, "v2-flow-type-refused"))) 0L validV2WithType v2WithSources v2Text
        |> error "STORAGE_INVALID_MANIFEST"
        |> ignore
        let validatorTypeObject = source StorageObjectKind.TypeDefinition "type v2-type-guard.Validated = scalar"
        let validV2WithValidator =
            { v2Manifest with
                Types = [ typeSource "v2-type-guard.Validated" validatorTypeObject.Reference stackFormat (Some(StoredCallTarget.UserWord "validator-word-id")) ] }
        Storage.commit (Storage.create (Path.Combine(root, "v2-validator-type-refused"))) 0L validV2WithValidator (validatorTypeObject :: v2Sources) v2Text
        |> error "STORAGE_INVALID_MANIFEST"
        |> ignore

        let userValidatedType = manifest.Types |> List.find (fun item -> item.ValidatorTarget.IsSome)
        let invalidTypeMetadata =
            [ "empty-validator-id", { userValidatedType with ValidatorTarget = Some(StoredCallTarget.UserWord "") }
              "overlong-validator-id", { userValidatedType with ValidatorTarget = Some(StoredCallTarget.UserWord(String.replicate 129 "x")) } ]
        for name, invalidType in invalidTypeMetadata do
            let invalidManifest =
                { manifest with
                    Types = manifest.Types |> List.map (fun item -> if item.Name = invalidType.Name then invalidType else item) }
            let invalidStore = Storage.create (Path.Combine(root, "v3-invalid-writer-" + name))
            Storage.commit invalidStore 0L invalidManifest sources projectText |> error "STORAGE_INVALID_MANIFEST" |> ignore
            let unchanged = Storage.load invalidStore |> ok "load after invalid v3 type-target writer refusal"
            equal EmptyAuthority unchanged.Authority $"invalid {name} leaves v3 authority empty"
            equal 0L unchanged.Generation $"invalid {name} leaves v3 generation unchanged"

    let private testV1HistoryMigrationAndSnapshotRestore root =
        let project = Path.Combine(root, "history-migration")
        let store = Storage.create project
        let initial = commitFixture store 0L "before-flow"
        let initialLoaded = Storage.load store |> ok "load v1 before Flow migration"
        let oldManifest = initialLoaded.Manifest.Value
        let oldRevision = oldManifest.Revisions.Head
        equal 1 oldManifest.FormatVersion "history begins in manifest v1"
        equal { Frontend = SourceFrontend.Stack; Version = 1 } oldRevision.SourceFormat "historical revision starts Stack/1"
        equal [] oldRevision.CallBindings "historical v1 revision starts without call bindings"
        let oldContent = Storage.readRevision store initial.ManifestHash.Value oldRevision.WordId oldRevision.Revision |> ok "read v1 revision before migration"
        let oldReferenceContents =
            [ oldRevision.Definition ] @ oldRevision.Tests @ oldRevision.Examples
            |> List.map (fun reference -> reference, Storage.readSource store reference |> ok "read historical source before migration")

        let migratedManifest, newSources, newProjectText, newRevision = appendFlowRevision oldManifest "after-flow"
        let migrated =
            Storage.commit store initialLoaded.Generation migratedManifest newSources newProjectText
            |> ok "commit Flow revision 2 under the same stable word ID"
        let loaded = Storage.load store |> ok "load v1-to-v2 history migration"
        let manifest = loaded.Manifest.Value
        equal 2 manifest.FormatVersion "migration publishes manifest v2"
        equal oldManifest.Words.Head.WordId manifest.Words.Head.WordId "migration preserves stable word identity"
        equal oldRevision manifest.Revisions.Head "migration preserves the complete v1 revision record"
        equal flowFormat manifest.Revisions.Tail.Head.SourceFormat "new revision records Flow/1 source format"
        let actualNewRevision = manifest.Revisions.Tail.Head
        equal
            { newRevision with CallBindings = [] }
            { actualNewRevision with CallBindings = [] }
            "new Flow revision metadata remains intact apart from canonical binding order"
        equal newRevision.CallBindings.Length actualNewRevision.CallBindings.Length "new Flow revision preserves binding count"
        equal (Set.ofList newRevision.CallBindings) (Set.ofList actualNewRevision.CallBindings) "new Flow revision retains every call-binding field"
        equal 2 manifest.Revisions.Length "both immutable revisions remain in history"
        equal oldRevision oldContent.Revision "pre-migration revision read has expected identity"
        let migratedOldContent = Storage.readRevision store migrated.ManifestHash.Value oldRevision.WordId oldRevision.Revision |> ok "read v1 history after migration"
        equal oldContent.DefinitionSource migratedOldContent.DefinitionSource "old definition bytes survive manifest upgrade"
        equal oldContent.TestSources migratedOldContent.TestSources "old test bytes survive manifest upgrade"
        equal oldContent.ExampleSources migratedOldContent.ExampleSources "old example bytes survive manifest upgrade"
        for reference, expectedText in oldReferenceContents do
            equal expectedText (Storage.readSource store reference |> ok "read retained v1 source after migration") $"historical object {reference.Hash} remains byte-exact"
        equal oldRevision.Definition migratedOldContent.Revision.Definition "old definition reference remains unchanged"
        equal oldRevision.Tests migratedOldContent.Revision.Tests "old test references remain unchanged"
        equal oldRevision.Examples migratedOldContent.Revision.Examples "old example references remain unchanged"

        let snapshotProject = Path.Combine(root, "snapshot-v1-to-v2")
        let snapshotStore = Storage.create snapshotProject
        let v1Commit = commitFixture snapshotStore 0L "snapshot-source"
        let v1Loaded = Storage.load snapshotStore |> ok "load v1 before snapshot migration"
        Storage.saveSnapshot snapshotStore v1Loaded.Generation "v1-history" (Map.ofList [ "state", "before" ]) None
        |> ok "save v1 snapshot before v2 publication"
        let v1Snapshot = Storage.readSnapshot snapshotStore "v1-history" |> ok "read v1 snapshot before migration"
        let v1SnapshotPath = Path.Combine(storageRoot snapshotProject, "snapshots", "v1-history.json")
        let v1SnapshotBytes = File.ReadAllBytes v1SnapshotPath
        let nextManifest, nextSources, nextProjectText, _ = appendFlowRevision v1Loaded.Manifest.Value "snapshot-flow"
        let v2Commit = Storage.commit snapshotStore v1Loaded.Generation nextManifest nextSources nextProjectText |> ok "publish v2 after v1 snapshot"
        let currentV2 = Storage.load snapshotStore |> ok "load v2 before restoring v1 snapshot"
        equal 2 currentV2.Manifest.Value.FormatVersion "snapshot store currently points to v2"
        check (bytesEqual v1SnapshotBytes (File.ReadAllBytes v1SnapshotPath)) "v1 snapshot bytes remain unchanged after v2 publication"
        let restored = Storage.restoreSnapshot snapshotStore v2Commit.Generation v1Snapshot |> ok "restore v1 snapshot while current manifest is v2"
        equal v1Commit.ManifestHash restored.ManifestHash "restoring v1 snapshot selects its original v1 manifest"
        let restoredLoad = Storage.load snapshotStore |> ok "load restored v1 manifest after v2 snapshot history"
        equal 1 restoredLoad.Manifest.Value.FormatVersion "v1 snapshot restores a v1 manifest"
        equal v1Commit.ManifestHash restoredLoad.ManifestHash "restored v1 manifest identity is exact"
        let restoredPointer = JsonNode.Parse(File.ReadAllText(Path.Combine(storageRoot snapshotProject, "CURRENT"))).AsObject()
        equal 1 (restoredPointer["formatVersion"].GetValue<int>()) "CURRENT remains a v1 envelope through v1-to-v2 restore"
        let stillNamed = Storage.readSnapshot snapshotStore "v1-history" |> ok "read v1 snapshot after restore"
        equal v1Snapshot.ManifestHash stillNamed.ManifestHash "named v1 snapshot continues to reference its v1 manifest"

    let private testManifestV2ValidationAndLimits root =
        let expectRawFailure name expectedCode mutate =
            let project = Path.Combine(root, "invalid-v2-" + name)
            let store, raw = cloneValidFlowManifest project
            mutate raw
            installedRawManifest project (raw.ToJsonString()) |> ignore
            Storage.load store |> error expectedCode |> ignore

        expectRawFailure "missing-source-format" "STORAGE_INVALID_JSON" (fun raw ->
            let revision = firstRevisionObject raw
            revision.Remove("sourceFormat") |> ignore)
        expectRawFailure "missing-call-bindings" "STORAGE_INVALID_JSON" (fun raw ->
            let revision = firstRevisionObject raw
            revision.Remove("callBindings") |> ignore)
        expectRawFailure "missing-frontend" "STORAGE_INVALID_JSON" (fun raw ->
            let revision = firstRevisionObject raw
            let sourceFormat = revision["sourceFormat"].AsObject()
            sourceFormat.Remove("frontend") |> ignore)
        expectRawFailure "unknown-frontend" "STORAGE_UNSUPPORTED_FRONTEND" (fun raw ->
            let revision = firstRevisionObject raw
            let sourceFormat = revision["sourceFormat"].AsObject()
            sourceFormat["frontend"] <- JsonValue.Create("future"))
        expectRawFailure "missing-source-version" "STORAGE_INVALID_JSON" (fun raw ->
            let revision = firstRevisionObject raw
            let sourceFormat = revision["sourceFormat"].AsObject()
            sourceFormat.Remove("version") |> ignore)
        expectRawFailure "unsupported-source-version" "STORAGE_UNSUPPORTED_VERSION" (fun raw ->
            let revision = firstRevisionObject raw
            let sourceFormat = revision["sourceFormat"].AsObject()
            sourceFormat["version"] <- JsonValue.Create(77))
        expectRawFailure "unknown-body-role" "STORAGE_INVALID_MANIFEST" (fun raw ->
            let binding = firstCallBindingObject raw
            binding["bodyRole"] <- JsonValue.Create("initialization"))
        expectRawFailure "unknown-path-segment" "STORAGE_INVALID_MANIFEST" (fun raw ->
            let binding = firstCallBindingObject raw
            let path = binding["path"].AsArray()
            let firstSegment = (path.[0]).AsObject()
            firstSegment["segment"] <- JsonValue.Create("mystery"))
        expectRawFailure "missing-path" "STORAGE_INVALID_JSON" (fun raw ->
            let binding = firstCallBindingObject raw
            binding.Remove("path") |> ignore)
        expectRawFailure "unknown-call-form" "STORAGE_INVALID_MANIFEST" (fun raw ->
            let binding = firstCallBindingObject raw
            let form = binding["form"].AsObject()
            form["kind"] <- JsonValue.Create("macro"))
        expectRawFailure "missing-target" "STORAGE_INVALID_JSON" (fun raw ->
            let binding = firstCallBindingObject raw
            binding.Remove("target") |> ignore)
        expectRawFailure "missing-binding-source" "STORAGE_INVALID_JSON" (fun raw ->
            let binding = firstCallBindingObject raw
            binding.Remove("source") |> ignore)
        expectRawFailure "unknown-target-kind" "STORAGE_INVALID_MANIFEST" (fun raw ->
            let binding = firstCallBindingObject raw
            let target = binding["target"].AsObject()
            target["kind"] <- JsonValue.Create("dynamic"))
        expectRawFailure "foreign-source-reference" "STORAGE_INVALID_MANIFEST" (fun raw ->
            let binding = firstCallBindingObject raw
            let sourceReference = binding["source"].AsObject()
            sourceReference["hash"] <- JsonValue.Create(String.replicate 64 "f"))
        expectRawFailure "duplicate-site-key" "STORAGE_INVALID_MANIFEST" (fun raw ->
            let revision = firstRevisionObject raw
            let bindings = revision["callBindings"].AsArray()
            bindings.Add((bindings.[0]).DeepClone()))
        expectRawFailure "negative-path-index" "STORAGE_INVALID_MANIFEST" (fun raw ->
            let binding = firstCallBindingObject raw
            let path = binding["path"].AsArray()
            let firstSegment = (path.[0]).AsObject()
            firstSegment["index"] <- JsonValue.Create(-1))
        expectRawFailure "oversized-path-index" "STORAGE_LIMIT_EXCEEDED" (fun raw ->
            let binding = firstCallBindingObject raw
            let path = binding["path"].AsArray()
            let firstSegment = (path.[0]).AsObject()
            firstSegment["index"] <- JsonValue.Create(100001))
        expectRawFailure "excessive-path-depth" "STORAGE_LIMIT_EXCEEDED" (fun raw ->
            let path = JsonArray()
            for _ in 1 .. 129 do
                let segment = JsonObject()
                segment["segment"] <- JsonValue.Create("dotReceiver")
                path.Add segment
            let binding = firstCallBindingObject raw
            binding["path"] <- path)

        let invalidVersionProject = Path.Combine(root, "missing-v2-version-fields")
        let unsupportedManifestNode = JsonObject()
        unsupportedManifestNode["formatVersion"] <- JsonValue.Create(999)
        let incompleteRevision = JsonObject()
        incompleteRevision["wordId"] <- JsonValue.Create("word-incomplete-v999")
        incompleteRevision["revision"] <- JsonValue.Create(1)
        let unsupportedRevisions = JsonArray()
        unsupportedRevisions.Add incompleteRevision
        unsupportedManifestNode["revisions"] <- unsupportedRevisions
        let rawUnsupported = unsupportedManifestNode.ToJsonString()
        let hash = digest rawUnsupported
        let unsupportedStoreRoot = storageRoot invalidVersionProject
        Directory.CreateDirectory(Path.Combine(unsupportedStoreRoot, "manifests")) |> ignore
        File.WriteAllText(Path.Combine(unsupportedStoreRoot, "manifests", hash + ".json"), rawUnsupported, UTF8Encoding(false))
        let pointer = JsonObject()
        pointer["formatVersion"] <- JsonValue.Create(1)
        pointer["generation"] <- JsonValue.Create(1L)
        pointer["kind"] <- JsonValue.Create("manifest")
        pointer["manifestHash"] <- JsonValue.Create(hash)
        File.WriteAllText(Path.Combine(unsupportedStoreRoot, "CURRENT"), pointer.ToJsonString(), UTF8Encoding(false))
        Storage.load (Storage.create invalidVersionProject) |> error "STORAGE_UNSUPPORTED_VERSION" |> ignore

        let tooManyBindings =
            [ "count", 20001, 1
              "aggregate-path-segments", 16667, 6 ]
        for label, count, depth in tooManyBindings do
            let project = Path.Combine(root, "binding-limit-" + label)
            let store, raw = cloneValidFlowManifest project
            let revision = firstRevisionObject raw
            let template = firstCallBindingObject raw
            let bindings = JsonArray()
            for index in 0 .. count - 1 do
                let binding = (template.DeepClone()).AsObject()
                let path = JsonArray()
                let block = JsonObject()
                block["segment"] <- JsonValue.Create("blockStatement")
                block["index"] <- JsonValue.Create(index)
                path.Add block
                for _ in 2 .. depth do
                    let segment = JsonObject()
                    segment["segment"] <- JsonValue.Create("dotReceiver")
                    path.Add segment
                binding["path"] <- path
                bindings.Add binding
            revision["callBindings"] <- bindings
            installedRawManifest project (raw.ToJsonString()) |> ignore
            Storage.load store |> error "STORAGE_LIMIT_EXCEEDED" |> ignore

        let duplicateWriterProject = Path.Combine(root, "duplicate-writer-v2")
        let duplicateWriterStore = Storage.create duplicateWriterProject
        let validV2, validSources, validText = flowV2Fixture "duplicate-writer"
        let firstBinding = validV2.Revisions.Head.CallBindings.Head
        let testActualBinding =
            validV2.Revisions.Head.CallBindings
            |> List.find (fun binding -> binding.BodyRole = StoredCallBodyRole.Actual && binding.CaseName = Some "returns-42")
        let exampleBinding =
            validV2.Revisions.Head.CallBindings
            |> List.find (fun binding -> binding.CaseName = Some "basic")
        let rejectBinding (name: string) (binding: StoredCallBinding) =
            let project = Path.Combine(root, "invalid-binding-writer-" + name)
            let store = Storage.create project
            let revision = { validV2.Revisions.Head with CallBindings = [ binding ] }
            let manifest = { validV2 with Revisions = [ revision ] }
            Storage.commit store 0L manifest validSources validText |> error "STORAGE_INVALID_MANIFEST" |> ignore
            let unchanged = Storage.load store |> ok "load after invalid binding writer refusal"
            equal EmptyAuthority unchanged.Authority $"invalid {name} binding leaves authority empty"
            equal 0L unchanged.Generation $"invalid {name} binding leaves generation unchanged"

        let exampleExpectedRole = { exampleBinding with BodyRole = StoredCallBodyRole.ExpectedExpression }
        let actualWithoutCase = { testActualBinding with CaseName = None }
        let definitionWithTestKind = { firstBinding with Source = { firstBinding.Source with Kind = StorageObjectKind.TestDefinition } }
        let overlongCase = { testActualBinding with CaseName = Some(String.replicate 257 "c") }
        let overlongRequestedName = { firstBinding with RequestedName = String.replicate 257 "n" }
        let overlongTargetId = { firstBinding with Target = StoredCallTarget.UserWord(String.replicate 129 "t") }
        let overlongDotStage = { firstBinding with Form = StoredCallForm.DotStage(String.replicate 129 "s") }
        let unsupportedStaticCallbackStage =
            { exampleBinding with
                Form = StoredCallForm.StaticCallback(String.replicate 129 "m", FlowWordReferenceQualification.NamespaceQualified) }
        rejectBinding "example-expected-role" exampleExpectedRole
        rejectBinding "actual-case-omitted" actualWithoutCase
        rejectBinding "wrong-source-kind" definitionWithTestKind
        rejectBinding "long-case-name" overlongCase
        rejectBinding "long-requested-name" overlongRequestedName
        rejectBinding "long-target-id" overlongTargetId
        rejectBinding "long-dot-stage" overlongDotStage
        rejectBinding "unsupported-static-callback-stage" unsupportedStaticCallbackStage

        let duplicateManifest =
            { validV2 with
                Revisions = [ { validV2.Revisions.Head with CallBindings = validV2.Revisions.Head.CallBindings @ [ firstBinding ] } ] }
        Storage.commit duplicateWriterStore 0L duplicateManifest validSources validText |> error "STORAGE_INVALID_MANIFEST" |> ignore
        let unchanged = Storage.load duplicateWriterStore |> ok "load after duplicate writer rejection"
        equal EmptyAuthority unchanged.Authority "typed v2 rejection leaves authority unchanged"
        equal 0L unchanged.Generation "typed v2 rejection leaves generation unchanged"

        let invalidIndexProject = Path.Combine(root, "invalid-index-writer-v2")
        let invalidIndexStore = Storage.create invalidIndexProject
        let invalidBinding = { firstBinding with Path = FlowAstPath.FlowAstPath [ FlowAstPathSegment.BlockStatement -1 ] }
        let invalidRevision = { validV2.Revisions.Head with CallBindings = [ invalidBinding ] }
        let invalidIndexManifest = { validV2 with Revisions = [ invalidRevision ] }
        Storage.commit invalidIndexStore 0L invalidIndexManifest validSources validText |> error "STORAGE_INVALID_MANIFEST" |> ignore
        equal EmptyAuthority ((Storage.load invalidIndexStore |> ok "load after path limit rejection").Authority) "path limit rejection leaves authority unchanged"

        let stackV2Project = Path.Combine(root, "stack-v2-bindings")
        let stackV2Store = Storage.create stackV2Project
        let stackManifest, stackSources, stackText = fixture "stack-v2-bindings"
        let stackBinding =
            callBinding stackManifest.Revisions.Head.Definition None StoredCallBodyRole.Definition
                [ FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.EvaluateExpression ]
                StoredCallForm.Direct stackManifest.Revisions.Head.Name (StoredCallTarget.UserWord "stack-word-id")
        let stackRevision = { stackManifest.Revisions.Head with CallBindings = [ stackBinding ] }
        let stackV2Manifest = { stackManifest with FormatVersion = 2; Revisions = [ stackRevision ] }
        Storage.commit stackV2Store 0L stackV2Manifest stackSources stackText |> error "STORAGE_INVALID_MANIFEST" |> ignore
        let unchangedStackV2 = Storage.load stackV2Store |> ok "load after Stack v2 binding refusal"
        equal EmptyAuthority unchangedStackV2.Authority "Stack v2 refuses Flow bindings without changing authority"
        equal 0L unchangedStackV2.Generation "Stack v2 binding refusal leaves generation unchanged"

    let private testRuntimePublishesV2ForExplicitStackFrontend root =
        let stackProject = Path.Combine(root, "runtime-stack-v2")
        let engine = Runtime.Engine(stackProject, Set.empty, "2030-01-02T03:04:05Z")
        let defineArgs = JsonObject()
        let runtimeSource =
            String.concat "\n"
                [ "word runtime.sample : Int -> Int"
                  "    effects none"
                  "    1 add"
                  "end"
                  ""
                  "test runtime.sample/basic"
                  "    41 runtime.sample"
                  "    expect 42"
                  "end"
                  "" ]
        defineArgs["source"] <- JsonValue.Create(runtimeSource)
        defineArgs["frontend"] <- JsonValue.Create("stack")
        let defined = engine.Dispatch("define", defineArgs)
        check (defined["ok"].GetValue<bool>()) $"Runtime accepts a typed Stack candidate and attached test: {defined.ToJsonString()}"
        let commitArgs = JsonObject()
        commitArgs["word"] <- JsonValue.Create("runtime.sample")
        let committed = engine.Dispatch("commit", commitArgs)
        check (committed["ok"].GetValue<bool>()) $"Runtime commits the Stack candidate with passing test: {committed.ToJsonString()}"
        let loaded = Storage.load (Storage.create stackProject) |> ok "load Runtime's new Stack publication"
        equal 2 loaded.Manifest.Value.FormatVersion "new Runtime writes use manifest v2"
        let runtimeRevision = loaded.Manifest.Value.Revisions.Head
        equal { Frontend = SourceFrontend.Stack; Version = 1 } runtimeRevision.SourceFormat "Runtime records the explicitly selected Stack/1 source format"
        equal [] runtimeRevision.CallBindings "Runtime Stack publication carries no authored call bindings"

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
            testManifestV1WriterRefusesMeaningfulV2Fields root
            testManifestV2RoundTripAndCanonicalBindings root
            testManifestV3TypeSourceRoundTripAndValidation root
            testV1HistoryMigrationAndSnapshotRestore root
            testManifestV2ValidationAndLimits root
            testRuntimePublishesV2ForExplicitStackFrontend root
            testCommitReloadRevisionAndStableHistory root
            testStaleGenerationWriterLockAndLimits root
            testFailureBoundaries root
            testLegacyMigrationAndEmptyRestore root
            testNamedSnapshotsAndReadOnlyDiscovery root
            testTamperingUnsupportedVersionAndNoFallback root
            testTaskLogValidation root
            testReparsePointRefusal root
            printfn $"Storage tests passed: 15 groups, {assertions} assertions."
            0
        finally
            if Directory.Exists root then Directory.Delete(root, true)
