open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open AgentLang

type ExtractedDeclaration =
    { kind: string
      owner: string
      name: string
      source: string }

let main () =
    let requestPath = Environment.GetEnvironmentVariable("AGENTLANG_FLOW_SOURCE_REQUEST")
    if String.IsNullOrWhiteSpace requestPath then
        failwith "Set AGENTLANG_FLOW_SOURCE_REQUEST."

    use requestJson = JsonDocument.Parse(File.ReadAllText requestPath)
    let paths =
        requestJson.RootElement.GetProperty("paths").EnumerateArray()
        |> Seq.map (fun item -> item.GetString())
        |> Seq.toArray
    let requestedOwners =
        requestJson.RootElement.GetProperty("owners").EnumerateArray()
        |> Seq.map (fun item -> item.GetString())
        |> Seq.toArray
    let owners = Set.ofArray requestedOwners
    let declarations =
        [ for pathIndex, path in paths |> Array.indexed do
              let source = File.ReadAllText path
              let document =
                  FlowParser.parseDocumentWithVersion 1 path source
                  |> Result.defaultWith (fun diagnostic -> failwith (Diagnostics.render diagnostic))
              for word in document.Words do
                  if owners.Contains word.Name then
                      yield pathIndex, word.Span.Line, { kind = "word"; owner = word.Name; name = word.Name; source = word.SourceText }
              for test in document.Tests do
                  if owners.Contains test.Word then
                      yield pathIndex, test.Span.Line, { kind = "test"; owner = test.Word; name = test.CaseName; source = test.SourceText }
              for example in document.Examples do
                  if owners.Contains example.Word then
                      yield pathIndex, example.Span.Line, { kind = "example"; owner = example.Word; name = example.CaseName; source = example.SourceText } ]
        |> List.sortBy (fun (pathIndex, line, _) -> pathIndex, line)
        |> List.map (fun (_, _, declaration) -> declaration)

    for owner in requestedOwners do
        let wordCount = declarations |> List.filter (fun item -> item.kind = "word" && item.owner = owner) |> List.length
        if wordCount <> 1 then
            failwith (sprintf "Expected exactly one parsed word definition for '%s', found %d." owner wordCount)

    let words = declarations |> List.filter (fun item -> item.kind = "word")
    let tests = declarations |> List.filter (fun item -> item.kind = "test")
    let examples = declarations |> List.filter (fun item -> item.kind = "example")
    let source = declarations |> List.map (fun item -> item.source) |> String.concat (Environment.NewLine + Environment.NewLine)
    let sourceDigest = SHA256.HashData(Encoding.UTF8.GetBytes source)
    let sourceSha256 = Convert.ToHexString(sourceDigest).ToLowerInvariant()
    let response =
        {| source = source
           sourceSha256 = sourceSha256
           words = words |> List.map (fun item -> item.owner)
           tests = tests |> List.map (fun item -> sprintf "%s/%s" item.owner item.name)
           examples = examples |> List.map (fun item -> sprintf "%s/%s" item.owner item.name) |}
    Console.Out.WriteLine(JsonSerializer.Serialize(response))

main ()
