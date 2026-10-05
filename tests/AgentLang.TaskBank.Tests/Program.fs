open System
open System.Collections.Generic
open System.IO
open System.Text.Json.Nodes

let mutable assertions = 0

let check name passed =
    assertions <- assertions + 1
    if not passed then
        failwith $"FAIL: {name}"

let readJson path =
    File.ReadAllText(path)
    |> JsonNode.Parse
    |> fun node -> node.AsObject()

let stringValue (obj: JsonObject) (key: string) =
    obj[key].GetValue<string>()

let boolValue (obj: JsonObject) (key: string) =
    obj[key].GetValue<bool>()

let tryStringValue (node: JsonNode) =
    if isNull node || node.GetValueKind() <> System.Text.Json.JsonValueKind.String then
        None
    else
        Some(node.GetValue<string>())

let tryBoolValue (node: JsonNode) =
    if isNull node || node.GetValueKind() <> System.Text.Json.JsonValueKind.True && node.GetValueKind() <> System.Text.Json.JsonValueKind.False then
        None
    else
        Some(node.GetValue<bool>())

let tryObject (node: JsonNode) =
    if isNull node || node.GetValueKind() <> System.Text.Json.JsonValueKind.Object then
        None
    else
        Some(node.AsObject())

let exactKeys (node: JsonNode) keys =
    let actual =
        node.AsObject()
        |> Seq.map (fun pair -> pair.Key)
        |> Set.ofSeq
    actual = Set.ofList keys

let arrayItems (node: JsonNode) =
    node.AsArray()
    |> Seq.map (fun item -> item)
    |> Seq.toArray

let stringItems (node: JsonNode) =
    arrayItems node
    |> Array.map (fun item -> item.GetValue<string>())

let sha256LooksValid (node: JsonNode) =
    tryStringValue node
    |> Option.exists (fun value -> value.Length = 64 && (value |> Seq.forall Uri.IsHexDigit))

let concreteEvidence (node: JsonNode) =
    match tryObject node with
    | None -> false
    | Some evidence ->
        tryStringValue evidence["status"] = Some "verified"
        && (tryStringValue evidence["evidenceRef"]
            |> Option.exists (String.IsNullOrWhiteSpace >> not))
        && sha256LooksValid evidence["sha256"]

let canPromote (task: JsonObject) =
    let wantsPromotion =
        tryBoolValue task["executable"] = Some true
        || tryStringValue task["status"] = Some "verified"

    if not wantsPromotion then
        tryBoolValue task["executable"] = Some false
        && tryStringValue task["status"] = Some "planned"
    else
        let requiredAdapters = [ "agentlang"; "conventional"; "independentOracle" ]
        let adapterEvidencePresent =
            match tryObject task["adapterEvidence"] with
            | None -> false
            | Some adapters ->
                requiredAdapters
                |> List.forall (fun name -> concreteEvidence adapters[name])

        let snapshotEvidencePresent =
            match tryObject task["snapshotPins"] with
            | None -> false
            | Some snapshots ->
                [ "flat"; "growing"; "conventional" ]
                |> List.forall (fun name ->
                    match tryObject snapshots[name] with
                    | None -> false
                    | Some snapshot ->
                        tryStringValue snapshot["status"] = Some "verified"
                        && sha256LooksValid snapshot["sha256"])

        tryStringValue task["status"] = Some "verified"
        && tryBoolValue task["executable"] = Some true
        && concreteEvidence task["acceptanceEvidence"]
        && adapterEvidencePresent
        && snapshotEvidencePresent

let prerequisitesAcyclic (tasks: JsonArray) =
    let dependencies = Dictionary<string, string list>(StringComparer.Ordinal)
    let mutable valid = true

    for node in tasks do
        let task = node.AsObject()
        dependencies[stringValue task "id"] <-
            stringItems task["prerequisites"] |> Array.toList

    let marks = Dictionary<string, int>(StringComparer.Ordinal)

    let rec visit id =
        match marks.TryGetValue(id) with
        | true, 1 -> valid <- false
        | true, 2 -> ()
        | _ ->
            if not (dependencies.ContainsKey(id)) then
                valid <- false
            else
                marks[id] <- 1
                for prerequisite in dependencies[id] do
                    visit prerequisite
                marks[id] <- 2

    for id in dependencies.Keys do
        visit id

    valid

let findRepoRoot () =
    let marker =
        Path.Combine("experiments", "AgentLang.Benchmarks", "task-bank", "manifest.json")

    let rec ascend (directory: DirectoryInfo) =
        if isNull directory then
            failwith "Could not locate repository root containing the task-bank manifest."
        elif File.Exists(Path.Combine(directory.FullName, marker)) then
            directory.FullName
        else
            ascend directory.Parent

    ascend (DirectoryInfo(AppContext.BaseDirectory))

let root = findRepoRoot ()
let bankRoot = Path.Combine(root, "experiments", "AgentLang.Benchmarks", "task-bank")
let manifest = readJson (Path.Combine(bankRoot, "manifest.json"))
let tasks = manifest["tasks"].AsArray()
let taskNodes = arrayItems manifest["tasks"]
let categories = [ "simple", 20; "medium", 20; "debugging", 10; "refactoring", 10 ]

check "manifest schema version" (manifest["schemaVersion"].GetValue<int>() = 1)
check "manifest status remains planned" (stringValue manifest "status" = "planned")
check "manifest is not executable" (not (boolValue manifest "executable"))
check "manifest task count is exactly 60" (taskNodes.Length = 60)
check "manifest category counts match required bank" (
    categories
    |> List.forall (fun (category, expected) ->
        (manifest["categoryCounts"][category]).GetValue<int>() = expected
        && (taskNodes
            |> Array.filter (fun task -> stringValue (task.AsObject()) "category" = category)
            |> Array.length)
           = expected))

let taskById = Dictionary<string, JsonObject>(StringComparer.Ordinal)
for node in taskNodes do
    let task = node.AsObject()
    let id = stringValue task "id"
    check $"unique task id {id}" (not (taskById.ContainsKey(id)))
    taskById[id] <- task

check "all prerequisites resolve and form a DAG" (prerequisitesAcyclic tasks)

let cyclicManifest = manifest.DeepClone().AsObject()
let cyclicTasks = cyclicManifest["tasks"].AsArray()
let firstTask = cyclicTasks[0].AsObject()
let selfId = stringValue firstTask "id"
let selfDependency = JsonValue.Create(selfId) :> JsonNode
firstTask["prerequisites"] <- JsonArray([| selfDependency |])
check "validator rejects a prerequisite cycle" (not (prerequisitesAcyclic cyclicTasks))

let unknownDependencyManifest = manifest.DeepClone().AsObject()
let unknownTasks = unknownDependencyManifest["tasks"].AsArray()
let unknownTask = unknownTasks[0].AsObject()
let missingDependency = JsonValue.Create("MISSING") :> JsonNode
unknownTask["prerequisites"] <- JsonArray([| missingDependency |])
check "validator rejects an unknown prerequisite" (not (prerequisitesAcyclic unknownTasks))

let mutable vectorCount = 0
let mutable minimumCases = Int32.MaxValue
let mutable maximumCases = 0
let hiddenOnlyPropertyNames =
    Set.ofList [ "acceptance"; "acceptanceFile"; "oracle"; "cases"; "expected"; "mutationId"; "snapshotHash"; "acceptanceEvidence" ]

for KeyValue(id, task) in taskById do
    check $"{id} remains planned" (stringValue task "status" = "planned")
    check $"{id} is not executable" (not (boolValue task "executable"))
    check $"{id} has approved retention track" (
        [ "flat-and-growing"; "isolated-adversarial"; "isolated-refactor" ]
        |> List.contains (stringValue task "retentionTrack"))
    check $"{id} declares files in fixed public/hidden locations" (
        stringValue task "publicFile" = $"public/{id}.json"
        && stringValue task "acceptanceFile" = $"acceptance/{id}.json")

    let pins = task["snapshotPins"].AsObject()
    check $"{id} has all three comparison snapshot pins" (
        [ "flat"; "growing"; "conventional" ]
        |> List.forall pins.ContainsKey)

    for mode in [ "flat"; "growing"; "conventional" ] do
        let pin = pins[mode].AsObject()
        check $"{id}/{mode} snapshot is explicitly pending" (
            stringValue pin "status" = "pending" && isNull pin["sha256"])
        check $"{id}/{mode} snapshot ID is concrete" (
            not (String.IsNullOrWhiteSpace(stringValue pin "id")))

    let adapterEvidence = task["adapterEvidence"].AsObject()
    check $"{id} adapter evidence remains pending" (
        [ "agentlang"; "conventional"; "independentOracle" ]
        |> List.forall (fun name -> isNull adapterEvidence[name]))
    check $"{id} acceptance evidence remains pending" (
        let evidence = task["acceptanceEvidence"].AsObject()
        stringValue evidence "status" = "pending"
        && isNull evidence["evidenceRef"]
        && isNull evidence["sha256"])

    let publicPath = Path.Combine(bankRoot, "public", $"{id}.json")
    let publicDoc = readJson publicPath
    check $"{id} public payload contains only approved top-level fields" (
        exactKeys publicDoc [ "schemaVersion"; "id"; "category"; "goal"; "initialContext"; "requiredPublicContract" ])
    check $"{id} public identity/category match manifest" (
        stringValue publicDoc "id" = id
        && stringValue publicDoc "category" = stringValue task "category")
    check $"{id} public contract matches manifest" (
        JsonNode.DeepEquals(publicDoc["requiredPublicContract"], task["requiredPublicContract"]))
    check $"{id} public payload has no hidden-only fields" (
        publicDoc
        |> Seq.forall (fun property -> not (hiddenOnlyPropertyNames.Contains(property.Key))))

    let contract = task["requiredPublicContract"].AsObject()
    check $"{id} contract contains a symbol and signature" (
        not (String.IsNullOrWhiteSpace(stringValue contract "symbol"))
        && not (String.IsNullOrWhiteSpace(stringValue contract "signature")))

    let acceptancePath = Path.Combine(bankRoot, "acceptance", $"{id}.json")
    let acceptance = readJson acceptancePath
    check $"{id} hidden fixture is explicitly pending review and execution" (
        stringValue acceptance "taskId" = id
        && stringValue acceptance "vectorStatus" = "proposed-pending-host-review"
        && stringValue acceptance "executionStatus" = "pending")
    check $"{id} hidden adapters are all pending" (
        let adapters = acceptance["adapters"].AsObject()
        [ "agentlang"; "conventional"; "referenceCrossCheck" ]
        |> List.forall (fun name -> stringValue adapters name = "pending"))

    let cases = arrayItems acceptance["cases"]
    vectorCount <- vectorCount + cases.Length
    minimumCases <- min minimumCases cases.Length
    maximumCases <- max maximumCases cases.Length
    check $"{id} has at least three hidden cases" (cases.Length >= 3)

    let caseIds = HashSet<string>(StringComparer.Ordinal)
    for caseNode in cases do
        let testCase = caseNode.AsObject()
        let caseId = stringValue testCase "id"
        check $"{id}/{caseId} has a unique case id" (caseIds.Add(caseId))
        check $"{id}/{caseId} input is a structured JSON object" (
            not (isNull testCase["input"])
            && testCase["input"].GetValueKind() = System.Text.Json.JsonValueKind.Object)

        let expected = testCase["expected"].AsObject()
        check $"{id}/{caseId} expected projection has fixed fields" (
            exactKeys expected [ "result"; "errorCode"; "stateProjection" ])
        let hasResult = not (isNull expected["result"])
        let hasError = not (isNull expected["errorCode"])
        check $"{id}/{caseId} asserts a result or a structured error" (hasResult <> hasError)
        if hasError then
            let errorCode = stringValue expected "errorCode"
            check $"{id}/{caseId} error code is stable machine text" (
                errorCode.Length > 0
                && (errorCode |> Seq.forall (fun character -> Char.IsUpper(character) || Char.IsDigit(character) || character = '_')))

    if stringValue task "category" = "debugging" then
        check $"{id} mutation recipe stays in hidden fixture" (
            acceptance.ContainsKey("fixtureSetup")
            && stringValue (acceptance["fixtureSetup"].AsObject()) "status" = "pending-host-fixture"
            && not (File.ReadAllText(publicPath).Contains("mutationId", StringComparison.Ordinal)))

    if stringValue task "category" = "refactoring" then
        check $"{id} refactor structural witness forbids substring proof" (
            let witness = acceptance["structuralWitness"].AsObject()
            stringValue witness "kind" = "symbol-call-graph"
            && witness["substringEvidenceAllowed"].GetValue<bool>() = false
            && stringValue witness "conventionalStatus" = "pending-callgraph-adapter"
            && stringValue witness "agentlangStatus" = "pending-runtime-adapter")

check "all 60 acceptance files contain exactly 180 proposed vectors" (vectorCount = 180)
check "case density remains bounded and consistent" (minimumCases >= 3 && maximumCases <= 5)

let sample = taskById["S01"].DeepClone().AsObject()
check "planned task is not blocked by promotion gate" (canPromote sample)
sample["status"] <- JsonValue.Create("verified")
sample["executable"] <- JsonValue.Create(true)
check "promotion is rejected without concrete evidence" (not (canPromote sample))

let completeEvidence name =
    JsonObject(
        [ KeyValuePair("status", JsonValue.Create("verified") :> JsonNode)
          KeyValuePair("evidenceRef", JsonValue.Create($"evidence/{name}.json") :> JsonNode)
          KeyValuePair("sha256", JsonValue.Create(String.replicate 64 "a") :> JsonNode) ])

sample["acceptanceEvidence"] <- completeEvidence "acceptance"
let completeAdapters = JsonObject()
for adapter in [ "agentlang"; "conventional"; "independentOracle" ] do
    completeAdapters[adapter] <- completeEvidence adapter
sample["adapterEvidence"] <- completeAdapters
let completePins = sample["snapshotPins"].AsObject()
for mode in [ "flat"; "growing"; "conventional" ] do
    let pin = completePins[mode].AsObject()
    pin["status"] <- JsonValue.Create("verified")
    pin["sha256"] <- JsonValue.Create(String.replicate 64 "b")
check "promotion gate accepts a complete evidence-shaped task" (canPromote sample)

let missingAdapter = sample.DeepClone().AsObject()
missingAdapter["adapterEvidence"]["conventional"] <- null
check "promotion gate rejects a missing conventional adapter report" (not (canPromote missingAdapter))

let missingAcceptance = sample.DeepClone().AsObject()
missingAcceptance["acceptanceEvidence"]["sha256"] <- null
check "promotion gate rejects a missing acceptance hash" (not (canPromote missingAcceptance))

let missingAcceptanceReference = sample.DeepClone().AsObject()
missingAcceptanceReference["acceptanceEvidence"]["evidenceRef"] <- null
check "promotion gate rejects null acceptance evidence without crashing" (not (canPromote missingAcceptanceReference))

let missingSnapshot = sample.DeepClone().AsObject()
let missingSnapshotPins = missingSnapshot["snapshotPins"].AsObject()
missingSnapshotPins["growing"]["sha256"] <- null
check "promotion gate rejects a missing snapshot hash" (not (canPromote missingSnapshot))

let malformedAdapters = sample.DeepClone().AsObject()
malformedAdapters["adapterEvidence"] <- JsonValue.Create("no reports")
check "promotion gate rejects malformed adapter evidence without crashing" (not (canPromote malformedAdapters))

printfn "Task-bank validator passed: %d assertions, 60 tasks, 180 proposed vectors (minimum %d / maximum %d cases per task)." assertions minimumCases maximumCases
