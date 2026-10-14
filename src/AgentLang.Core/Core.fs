namespace AgentLang

open System

/// A source location always points into the exact source text parsed by the host.
type SourceSpan =
    { File: string
      Line: int
      Column: int
      Length: int }

type LangType =
    | TInt
    | TFloat
    | TBool
    | TString
    | TUnit
    | TList of LangType
    | TOption of LangType
    | TResult of LangType * LangType
    | TNamed of string
    | TVar of string

type Value =
    | IntValue of int64
    | FloatValue of double
    | BoolValue of bool
    | StringValue of string
    | UnitValue
    // Container element types are stored with the value so empty/inactive cases
    // never need to infer a type from their payload.
    | ListValue of LangType * Value list
    | OptionValue of LangType * Value option
    | ResultValue of LangType * LangType * Result<Value, Value>
    | RecordValue of string * Map<string, Value>
    | EnumValue of string * string
    | NamedValue of string * Value

type Literal =
    | LInt of int64
    | LFloat of double
    | LBool of bool
    | LString of string
    | LUnit

/// One part of an `html"…"` template: literal markup, or a hole whose value is
/// escaped (`String`) or inserted as-is (`Html`).
[<RequireQualifiedAccess>]
type HtmlTemplatePart =
    | Text of string
    | StringHole
    | HtmlHole
    /// A List<Html>: every item is inserted as-is, in order.
    | HtmlListHole

type ContainerConstructor =
    | ListEmpty
    | ListSingleton
    /// [a, b, c]: consumes the top `count` values (all of the one type argument)
    /// in order and builds one list, copying each element once.
    | ListOf of count: int
    | OptionNone
    | OptionSome
    | ResultOk
    | ResultError
    /// html"…": consumes one value per hole and pushes the type argument (the
    /// program's Html type). Built only by the template lowering.
    | HtmlTemplate of HtmlTemplatePart list

type Expr =
    | Push of Literal * SourceSpan
    | Call of string * SourceSpan
    | ConstructContainer of ContainerConstructor * LangType list * SourceSpan
    | MapList of string * SourceSpan
    | FilterList of string * SourceSpan
    | EachList of string * SourceSpan
    | FoldList of string * SourceSpan
    | MapListWithContext of string * SourceSpan
    | Let of string * SourceSpan
    | Load of string * SourceSpan
    | If of Expr list * Expr list * SourceSpan
    /// Internal lexical block used by explicit authoring frontends. It keeps
    /// the resulting stack while restoring the entry local environment.
    | Scope of Expr list * SourceSpan
    /// compact { ... }: evaluates the block; the compiler may relocate its single
    /// result to the block's arena mark and reclaim the block's temporaries.
    | Compact of Expr list * SourceSpan
    | MatchOption of string * Expr list * Expr list * SourceSpan
    | MatchResult of string * string * Expr list * Expr list * SourceSpan
    | MatchEnum of (string * Expr list) list * SourceSpan

type TestExpectation =
    | ExpectedValue of Literal
    | ExpectedRuntimeError of string
    | ExpectedExpression of Expr list
    /// Step-list checks: `expect <Bool>` calls are inline in the test body; the body must leave an empty stack.
    | ExpectedChecks

module TestExpectation =
    /// Error assertions name a stable diagnostic code, not message text.
    let isValidRuntimeErrorCode (code: string) =
        not (String.IsNullOrEmpty code)
        && code[0] >= 'A' && code[0] <= 'Z'
        && (code |> Seq.forall (fun character ->
            (character >= 'A' && character <= 'Z')
            || (character >= '0' && character <= '9')
            || character = '_'))

/// Exact counts for provider effects invoked while the test's user target is active.
/// The span points to the authored `effects { ... }` annotation.
type EffectCountAssertion =
    { Counts: Map<string, int>
      Span: SourceSpan }

module EffectCountAssertion =
    /// Provider categories observed by the host, including the typed database
    /// bridge. Domain outcomes remain language values; these counts measure only
    /// whether the host boundary was crossed.
    let observableEffects = set [ "clock.read"; "console.write"; "db.read"; "db.write"; "fs.read"; "fs.write"; "random.read" ]

    /// The interpreter's per-body step limit bounds how many provider calls one
    /// authored test can make.
    let maximumCount = 10_000

type WordMaturity = ProjectWord | LibraryWord

type RecordField =
    { Name: string
      Type: LangType }

type RecordDefinition =
    { Name: string
      Fields: RecordField list
      Validator: string option
      SourceText: string
      Span: SourceSpan }

type ScalarTypeDefinition =
    { Name: string
      BaseType: LangType
      Validator: string option
      SourceText: string
      Span: SourceSpan }

type EnumDefinition =
    { Name: string
      Cases: string list
      SourceText: string
      Span: SourceSpan }

type WordDefinition =
    { Name: string
      Inputs: LangType list
      Outputs: LangType list
      Effects: Set<string>
      Maturity: WordMaturity
      Revision: int
      Documentation: string
      Body: Expr list
      SourceText: string
      Span: SourceSpan }

type TestDefinition =
    { Name: string
      Word: string
      Body: Expr list
      Expected: TestExpectation
      EffectAssertion: EffectCountAssertion option
      SourceText: string
      Span: SourceSpan }

type ExampleDefinition =
    { Name: string
      Word: string
      Body: Expr list
      Expected: Literal
      SourceText: string
      Span: SourceSpan }

type ParsedSource =
    { Records: RecordDefinition list
      Scalars: ScalarTypeDefinition list
      Enums: EnumDefinition list
      Words: WordDefinition list
      Tests: TestDefinition list
      Examples: ExampleDefinition list }

type WordStatus = Primitive | Candidate | Temporary | Persistent

/// Stable identity for a closed, Engine-registered host operation. It is
/// separate from PrimitiveId: application operations are nominal, per-Engine
/// contracts and are not part of the standard primitive ABI.
[<Struct>]
type HostOperationId = HostOperationId of string

/// Exact source-level contract for one registered host operation.
type HostOperationDefinition =
    { Id: HostOperationId
      WordName: string
      Inputs: LangType list
      Outputs: LangType list
      Effects: Set<string>
      Documentation: string }

/// Stable host-boundary failures. Provider details must not be interpolated
/// into language diagnostics; ordinary domain failures belong in a declared
/// operation result value instead.
[<RequireQualifiedAccess>]
type HostOperationFailure =
    | MissingHandler
    | InvocationFailed
    | ProviderContractMismatch
    | TestProviderUnavailable

/// A host-owned operation table. Production-provider lifetime belongs to its
/// creator; test providers are Engine-created and disposed per attached test.
type HostOperationProvider =
    { OperationIds: Set<HostOperationId>
      Invoke: HostOperationId -> Value list -> Result<Value list, HostOperationFailure>
      Dispose: unit -> unit }

/// Optional closed manifest and its separate production/test provider paths.
type HostOperationEnvironment =
    { Manifest: Map<HostOperationId, HostOperationDefinition>
      ProductionProvider: HostOperationProvider
      CreateTestProvider: unit -> Result<HostOperationProvider, HostOperationFailure> }

type Builtin =
    | BuiltinOp of string
    | HostOperation of HostOperationId
    | RecordConstructor of string
    | RecordAccessor of string * string
    | ScalarConstructor of string
    | ScalarAccessor of string
    | EnumCaseConstructor of string * string

type WordEntry =
    { Definition: WordDefinition
      Builtin: Builtin option
      Status: WordStatus
      Maturity: WordMaturity
      Revision: int }

type RecordEntry =
    { Definition: RecordDefinition
      Status: WordStatus }

type ScalarEntry =
    { Definition: ScalarTypeDefinition
      Status: WordStatus }

type EnumEntry =
    { Definition: EnumDefinition
      Status: WordStatus }

type Diagnostic =
    { Code: string
      Message: string
      Word: string option
      Span: SourceSpan option
      Expected: string list
      Actual: string list }

exception LanguageException of Diagnostic

module Types =
    let rec format = function
        | TInt -> "Int"
        | TFloat -> "Float"
        | TBool -> "Bool"
        | TString -> "String"
        | TUnit -> "Unit"
        | TList item -> $"List<{format item}>"
        | TOption item -> $"Option<{format item}>"
        | TResult(ok, error) -> $"Result<{format ok}, {format error}>"
        | TNamed name -> name
        | TVar name -> name

    let rec formatValue = function
        | IntValue value -> string value
        | FloatValue value -> FloatText.format value
        | BoolValue value -> if value then "true" else "false"
        | StringValue value -> System.Text.Json.JsonSerializer.Serialize(value)
        | UnitValue -> "unit"
        | ListValue(_, values) -> values |> List.map formatValue |> String.concat ", " |> sprintf "[%s]"
        | OptionValue(_, None) -> "none"
        | OptionValue(_, Some value) -> $"some {formatValue value}"
        | ResultValue(_, _, Ok value) -> $"ok {formatValue value}"
        | ResultValue(_, _, Error error) -> $"error {formatValue error}"
        | RecordValue(name, fields) ->
            fields
            |> Map.toList
            |> List.map (fun (field, value) -> $"{field} = {formatValue value}")
            |> String.concat ", "
            |> sprintf "%s { %s }" name
        | EnumValue(typeName, caseName) -> $"{typeName}::{caseName}"
        | NamedValue(name, value) -> $"{name}({formatValue value})"

    let rec literalValue = function
        | LInt value -> IntValue value
        | LFloat value -> FloatValue value
        | LBool value -> BoolValue value
        | LString value -> StringValue value
        | LUnit -> UnitValue

    let rec ofValue = function
        | IntValue _ -> TInt
        | FloatValue _ -> TFloat
        | BoolValue _ -> TBool
        | StringValue _ -> TString
        | UnitValue -> TUnit
        | ListValue(itemType, _) -> TList itemType
        | OptionValue(itemType, _) -> TOption itemType
        | ResultValue(okType, errorType, _) -> TResult(okType, errorType)
        | RecordValue(name, _) -> TNamed name
        | EnumValue(name, _) -> TNamed name
        | NamedValue(name, _) -> TNamed name

module Diagnostics =
    let raiseError code message word span expected actual =
        raise (LanguageException
            { Code = code
              Message = message
              Word = word
              Span = span
              Expected = expected
              Actual = actual })

    let render diagnostic =
        let span =
            diagnostic.Span
            |> Option.map (fun value -> $" at {value.File}:{value.Line}:{value.Column}")
            |> Option.defaultValue ""
        let owner = diagnostic.Word |> Option.map (sprintf " in %s") |> Option.defaultValue ""
        $"{diagnostic.Code}{owner}{span}: {diagnostic.Message}"
