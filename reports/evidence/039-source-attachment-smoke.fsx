open AgentLang
open System
open System.IO
open System.Security.Cryptography

let mutable checks = 0
let require label condition =
    checks <- checks + 1
    if not condition then failwith label

let context: FlowLowering.Context =
    { CompilerContext =
        { Words = Compiler.primitives
          Records = Map.empty
          Scalars = Map.empty
          WordIds = Compiler.primitives |> Map.map (fun name _ -> WordId("primitive-" + name)) }
      ParameterNames = Map.empty
      SourceOrigins = Map.empty }
let owner = WordId "external-smoke-increment"
let wordText = "word smoke.increment(value: Int) -> Int {\n    effects none\n    return add(value, 1)\n}"
let wordSource: FlowLowering.FlowSourceDocument =
    { OwnerName = "smoke.increment"; OwnerId = owner; OwnerRevision = 1
      Reference = (Storage.sourceObject StorageObjectKind.WordDefinition wordText).Reference
      SourceFile = "smoke-word.flow"; Content = wordText }
let testText = "test smoke.increment/basic {\n    smoke::increment(4)\n    => value add(2, 3)\n}"
let exampleText = "example smoke.increment/basic {\n    smoke::increment(9)\n    => 10\n}"
let attachment kind storageKind content: FlowLowering.FlowAttachmentSourceDocument =
    { OwnerName = "smoke.increment"; OwnerId = owner; OwnerRevision = 1
      Kind = kind; CaseName = "basic"
      Reference = (Storage.sourceObject storageKind content).Reference
      SourceFile = "smoke-cases.flow"; Content = content }
let testSource = attachment FlowLowering.FlowAttachmentKind.Test StorageObjectKind.TestDefinition testText
let exampleSource = attachment FlowLowering.FlowAttachmentKind.Example StorageObjectKind.ExampleDefinition exampleText
let emptyWords: FlowLowering.FlowSourceInventory = { ExpectedFlowOwnerIds = Set.empty; Sources = [] }
let emptyCases: FlowLowering.FlowAttachmentInventory = { ExpectedSources = Map.empty; Sources = [] }
let initial =
    FlowLowering.compileBatchFlowProjectSources context emptyWords
        [ { RevisionIntent = FlowLowering.FlowWordRevisionIntent.Add(owner, 1); Source = wordSource } ]
        emptyCases [ FlowLowering.FlowAttachmentChange.Add testSource; FlowLowering.FlowAttachmentChange.Add exampleSource ]
require "two typed cases survive initial assembly" (initial.Attachments.Length = 2)
require "test and example with same name have distinct keys" (initial.AttachmentBindings |> List.map (fun row -> row.Attachment) |> Set.ofList |> Set.count = 2)
require "all three authored calls are captured" (initial.AttachmentBindings.Length = 3)
require "actual and expected roles are distinct" (initial.AttachmentBindings |> List.map (fun row -> row.BodyRole) |> Set.ofList |> Set.count = 2)
let host: IrInterpreterHost =
    { PreflightEffects = fun _ _ _ -> ()
      ChargeInstruction = fun _ _ -> ()
      RecordBranchOutcome = fun _ _ _ -> ()
      RecordUse = ignore
      InvokeEffect = fun value -> failwithf "Unexpected effect: %A" value
      WordDefinitionSpan = fun _ -> None
      PrimitiveDefinitionSpan = fun _ -> None }
let execute cases =
    for case in cases do
        match case with
        | FlowLowering.FlowCompiledAttachment.Test(source, compiled) ->
            require "test actual value is independently correct" (IrInterpreter.executeBody host "external-actual" compiled.Body = [ IntValue 5L ])
            require "pure expectation is independently correct" (IrInterpreter.executeBody host "external-expected" compiled.ExpectationBody.Value = [ IntValue 5L ])
            require "test retains exact bytes" (compiled.Lowered.SourceText = source.Content)
        | FlowLowering.FlowCompiledAttachment.Example(source, compiled) ->
            require "example value is independently correct" (IrInterpreter.executeBody host "external-example" compiled.Body = [ IntValue 10L ])
            require "example retains exact bytes" (compiled.Lowered.SourceText = source.Content)
execute initial.Attachments
let key (document: FlowLowering.FlowAttachmentSourceDocument): FlowLowering.FlowAttachmentKey =
    { OwnerId = document.OwnerId; Kind = document.Kind; CaseName = document.CaseName }
let cases: FlowLowering.FlowAttachmentInventory =
    { ExpectedSources = [ testSource; exampleSource ] |> List.map (fun document -> key document, document.Reference) |> Map.ofList
      Sources = [ testSource; exampleSource ] }
let words: FlowLowering.FlowSourceInventory = { ExpectedFlowOwnerIds = Set.singleton owner; Sources = [ wordSource ] }
let updated =
    FlowLowering.compileBatchFlowProjectSources initial.WordCompilation.Context words
        [ { RevisionIntent = FlowLowering.FlowWordRevisionIntent.Replace(owner, 1, 2); Source = { wordSource with OwnerRevision = 2 } } ]
        cases []
require "owner update retains both cases" (updated.Attachments.Length = 2)
require "retained case source references stay fixed" (updated.AttachmentBindings |> List.map (fun row -> row.Source) |> Set.ofList = Set.ofList [ testSource.Reference; exampleSource.Reference ])
require "retained owner metadata advances" (updated.AttachmentBindings |> List.forall (fun row -> row.OwnerRevision = 2))
let ownerCalls = updated.AttachmentBindings |> List.filter (fun row -> row.Site.Target = FlowLowering.FlowCallTargetIdentity.UserWord owner)
require "both retained actual bodies select the stable word" (ownerCalls.Length = 2)
require "retained calls select the new exact revision" (ownerCalls |> List.forall (fun row -> row.Site.TargetRevision = Some 2))
execute updated.Attachments
let assemblyPath = typeof<FlowLowering.Context>.Assembly.Location
printfn "Core: %s" assemblyPath
printfn "Core SHA256: %s" (File.ReadAllBytes assemblyPath |> SHA256.HashData |> Convert.ToHexString)
printfn "Source-backed attachment smoke passed %d checks." checks
