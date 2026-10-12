// Browser recognition and version parsing are adapted from 37signals' Campfire
// User-Agent implementation (useragent gem 0.16.11); see MIT-LICENSE.37signals.
namespace AgentLang.Campfire.Browser.Host

open AgentLang
open AgentLang.Campfire.Browser

module BrowserHost =
    let versionAtLeastId = HostOperationId "campfire.browser.version-at-least?"

    let operationIds = Set.singleton versionAtLeastId

    let private familyCase = function
        | BrowserFamily.Safari -> EnumValue("BrowserFamily", "safari")
        | BrowserFamily.Chrome -> EnumValue("BrowserFamily", "chrome")
        | BrowserFamily.Firefox -> EnumValue("BrowserFamily", "firefox")
        | BrowserFamily.Opera -> EnumValue("BrowserFamily", "opera")
        | BrowserFamily.InternetExplorer -> EnumValue("BrowserFamily", "internet-explorer")
        | BrowserFamily.Other -> EnumValue("BrowserFamily", "other")

    let toFlowValue (info: BrowserInfo) =
        RecordValue(
            "BrowserInfo",
            Map.ofList
                [ "family", familyCase info.Family
                  "version", OptionValue(TString, Option.map StringValue info.Version)
                  "is-bot", BoolValue info.IsBot ])

    /// Parses a request User-Agent and encodes the closed nominal value declared in
    /// browser-types.flow for trusted host request construction.
    let normalizeUserAgent userAgent =
        BrowserInfo.normalize userAgent |> toFlowValue

    let manifest =
        Map.ofList
            [ versionAtLeastId,
              { Id = versionAtLeastId
                WordName = "campfire.browser.version-at-least?"
                Inputs = [ TString; TString ]
                Outputs = [ TBool ]
                Effects = Set.empty
                Documentation =
                    "Compares browser versions using the pinned Ruby useragent Version semantics; this pure operation performs no I/O." } ]

    let invoke operationId values =
        match operationId, values with
        | id, [ StringValue version; StringValue minimum ]
            when id = versionAtLeastId && not (isNull version) && not (isNull minimum) ->
            Ok [ BoolValue(RubyVersion.isAtLeast version minimum) ]
        | id, _ when id = versionAtLeastId ->
            Error HostOperationFailure.ProviderContractMismatch
        | _ ->
            Error HostOperationFailure.MissingHandler

    let createProvider () : HostOperationProvider =
        { OperationIds = operationIds
          Invoke = invoke
          Dispose = ignore }

    let createEnvironment productionProvider createTestProvider : HostOperationEnvironment =
        { Manifest = manifest
          ProductionProvider = productionProvider
          CreateTestProvider = createTestProvider }
