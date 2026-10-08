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

type ContainerConstructor =
    | ListEmpty
    | ListSingleton
    | OptionNone
    | OptionSome
    | ResultOk
    | ResultError

type Expr =
    | Push of Literal * SourceSpan
    | Call of string * SourceSpan
    | ConstructContainer of ContainerConstructor * LangType list * SourceSpan
    | MapList of string * SourceSpan
    | FilterList of string * SourceSpan
    | EachList of string * SourceSpan
    | FoldList of string * SourceSpan
    | Let of string * SourceSpan
    | Load of string * SourceSpan
    | If of Expr list * Expr list * SourceSpan
    /// Internal lexical block used by explicit authoring frontends. It keeps
    /// the resulting stack while restoring the entry local environment.
    | Scope of Expr list * SourceSpan
    | MatchOption of string * Expr list * Expr list * SourceSpan
    | MatchResult of string * string * Expr list * Expr list * SourceSpan
    | MatchEnum of (string * Expr list) list * SourceSpan

type TestExpectation =
    | ExpectedValue of Literal
    | ExpectedRuntimeError of string
    | ExpectedExpression of Expr list

module TestExpectation =
    /// Error assertions name a stable diagnostic code, not message text.
    let isValidRuntimeErrorCode (code: string) =
        not (String.IsNullOrEmpty code)
        && code[0] >= 'A' && code[0] <= 'Z'
        && (code |> Seq.forall (fun character ->
            (character >= 'A' && character <= 'Z')
            || (character >= '0' && character <= '9')
            || character = '_'))

type WordMaturity = ProjectWord | LibraryWord

type RecordField =
    { Name: string
      Type: LangType }

type RecordDefinition =
    { Name: string
      Fields: RecordField list
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

type Builtin =
    | BuiltinOp of string
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
        | FloatValue value -> value.ToString("G", Globalization.CultureInfo.InvariantCulture)
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
