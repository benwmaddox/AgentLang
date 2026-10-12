// User-Agent recognition is adapted from 37signals' Campfire user_agent.rs
// at pinned source revision 6dae2fd3c1a2dce61ca11beb731bcce2b42b67ad
// (useragent gem 0.16.11); see MIT-LICENSE.37signals.
namespace AgentLang.Campfire.Browser

open System

[<RequireQualifiedAccess>]
type BrowserFamily =
    | Safari
    | Chrome
    | Firefox
    | Opera
    | InternetExplorer
    | Other

type BrowserInfo =
    { Family: BrowserFamily
      Version: string option
      IsBot: bool }

module BrowserInfo =
    type private Product =
        { Name: string
          Version: string
          Comment: string list option }

    type private Kind =
        | Base
        | Edge
        | InternetExplorer
        | Opera
        | WechatBrowser
        | Vivaldi
        | Chrome
        | ITunes
        | PlayStation
        | PodcastAddict
        | Webkit
        | Gecko
        | WindowsMediaPlayer
        | AppleCoreMedia
        | Libavformat

    let private defaultUserAgent = "Mozilla/4.0 (compatible)"

    let private isRubySpace = function
        | ' ' | '\t' | '\n' | '\u000b' | '\u000c' | '\r' -> true
        | _ -> false

    let private isProductCharacter value =
        value <> '/' && not (isRubySpace value)

    let private rubyStrip (value: string) =
        value.Trim([| '\000'; ' '; '\t'; '\n'; '\u000b'; '\u000c'; '\r' |])

    let private splitRuby (separator: string) (value: string) =
        let parts = value.Split([| separator |], StringSplitOptions.None)
        let mutable last = parts.Length - 1
        while last >= 0 && parts[last].Length = 0 do
            last <- last - 1
        if last < 0 then [] else parts[0..last] |> Array.toList

    let private sameIgnoringCase (left: string) (right: string) =
        String.Equals(left.ToLowerInvariant(), right.ToLowerInvariant(), StringComparison.Ordinal)

    let private containsIgnoringCase (value: string) (needle: string) =
        value.ToLowerInvariant().Contains(needle.ToLowerInvariant(), StringComparison.Ordinal)

    let private isAsciiDigit value = value >= '0' && value <= '9'

    let private runWhile (value: string) start predicate =
        let mutable index = start
        while index < value.Length && predicate value[index] do
            index <- index + 1
        index - start

    let private matchProduct (value: string) =
        let quoteCount = runWhile value 0 (fun character -> character = '\'' || character = '"')
        let start =
            if quoteCount < value.Length && isProductCharacter value[quoteCount] then
                Some quoteCount
            elif quoteCount > 0 then
                Some(quoteCount - 1)
            else
                None

        match start with
        | None -> None
        | Some start ->
            let mutable index = start + 1
            index <- index + runWhile value index isProductCharacter
            let name = value.Substring(start, index - start)

            if index < value.Length && value[index] = '/' then
                index <- index + 1

            let versionStart = index
            index <- index + runWhile value index (fun character -> not (isRubySpace character) && character <> ',')
            let version = value.Substring(versionStart, index - versionStart)

            let mutable comment = None
            if index < value.Length
               && isRubySpace value[index]
               && index + 1 < value.Length
               && value[index + 1] = '(' then
                let close = value.IndexOf(')', index + 2)
                if close >= 0 then
                    comment <- Some(splitRuby "; " (value.Substring(index + 2, close - index - 2)))
                    index <- close + 1
            elif value.AsSpan(index).StartsWith(",gzip(gfe)".AsSpan(), StringComparison.Ordinal) then
                index <- index + ",gzip(gfe)".Length

            Some(
                index,
                { Name = name
                  Version = version
                  Comment = comment }
            )

    let private parse (input: string option) =
        let value =
            match input with
            | Some value when not (isNull value) -> value
            | _ -> ""

        let initial = if String.IsNullOrEmpty(rubyStrip value) then defaultUserAgent else value

        let rec collect rest products =
            match matchProduct rest with
            | Some(length, product) -> collect (rubyStrip (rest.Substring length)) (product :: products)
            | None -> List.rev products

        collect initial []

    let private first products = List.tryHead products
    let private last products = List.tryLast products

    let private productNamed (products: Product list) (name: string) =
        products |> List.tryFind (fun product -> sameIgnoringCase product.Name name)

    let private hasProduct products name = productNamed products name |> Option.isSome

    let private hasExactProduct (products: Product list) (name: string) =
        products |> List.exists (fun product -> product.Name = name)

    let private hasComment (product: Product) =
        product.Comment |> Option.exists (List.isEmpty >> not)

    let private application kind products =
        let hasCommentedApplication =
            match kind with
            | Chrome
            | Vivaldi
            | Webkit
            | ITunes
            | AppleCoreMedia -> true
            | _ -> false

        if hasCommentedApplication then
            products |> List.tryFind hasComment
        else
            first products

    let private applicationComment kind products =
        application kind products |> Option.bind (fun product -> product.Comment)

    let private joinedComment comments =
        comments |> Option.map (String.concat "; ") |> Option.defaultValue ""

    let private commentAt (product: Product) index =
        product.Comment |> Option.bind (fun comments -> List.tryItem index comments)

    let private commentContains (product: Product) (index: int) (value: string) =
        commentAt product index
        |> Option.exists (fun comment -> comment.Contains(value, StringComparison.Ordinal))

    let private tridentHasRv (comment: string) =
        let mutable searchFrom = 0
        let mutable found = false
        let mutable trident = comment.IndexOf("Trident", searchFrom, StringComparison.Ordinal)

        while trident >= 0 && not found do
            let start = trident + "Trident".Length
            let lineEnd = comment.IndexOf('\n', start)
            let finish = if lineEnd < 0 then comment.Length else lineEnd
            let rv = comment.IndexOf("rv:", start, finish - start, StringComparison.Ordinal)
            found <- rv >= start + 1
            searchFrom <- start
            trident <- if found then -1 else comment.IndexOf("Trident", searchFrom, StringComparison.Ordinal)

        found

    let private webkitCommentVersion (comment: string) =
        let marker = "AppleWebKit"
        if comment.Length <= marker.Length
           || not (sameIgnoringCase (comment.Substring(0, marker.Length)) marker)
           || comment[marker.Length] <> '/' then
            None
        else
            let tail = comment.Substring(marker.Length + 1)
            let length = runWhile tail 0 (fun character -> isAsciiDigit character || character = '.')
            if length = 0 then None else Some(tail.Substring(0, length))

    let private kindExtends kind products =
        let firstProduct = first products
        let lastProduct = last products

        match kind with
        | Base -> true
        | Edge -> lastProduct |> Option.exists (fun product -> product.Name = "Edge")
        | InternetExplorer ->
            firstProduct
            |> Option.exists (fun product ->
                product.Comment.IsSome
                && (commentContains product 1 "MSIE"
                    || tridentHasRv (joinedComment product.Comment)))
        | Opera ->
            firstProduct |> Option.exists (fun product -> product.Name = "Opera")
            || lastProduct |> Option.exists (fun product -> product.Name = "OPR")
        | WechatBrowser ->
            products |> List.exists (fun product -> containsIgnoringCase product.Name "micromessenger")
        | Vivaldi -> hasExactProduct products "Vivaldi"
        | Chrome -> hasExactProduct products "Chrome" || hasExactProduct products "CriOS"
        | ITunes -> hasExactProduct products "iTunes"
        | PlayStation ->
            firstProduct
            |> Option.exists (fun product ->
                commentContains product 0 "PLAYSTATION 3"
                || commentContains product 0 "PlayStation Vita"
                || commentContains product 0 "PlayStation 4")
        | PodcastAddict ->
            match products with
            | podcast :: addict :: dash :: _ ->
                podcast.Name = "Podcast" && addict.Name = "Addict" && dash.Name = "-"
            | _ -> false
        | Webkit ->
            products
            |> List.exists (fun product ->
                sameIgnoringCase product.Name "AppleWebKit"
                || (product.Comment
                    |> Option.defaultValue []
                    |> List.exists (webkitCommentVersion >> Option.isSome)))
        | Gecko -> firstProduct |> Option.exists (fun product -> product.Name = "Mozilla")
        | WindowsMediaPlayer ->
            products
            |> List.exists (fun product ->
                [ "NSPlayer"; "Windows-Media-Player"; "WMFSDK" ]
                |> List.contains product.Name
                && (firstProduct
                    |> Option.exists (fun first ->
                        first.Version <> "4.1.0.3856"
                        && first.Version <> "7.10.0.3059"
                        && first.Version <> "7.0.0.1956")))
        | AppleCoreMedia -> hasExactProduct products "AppleCoreMedia"
        | Libavformat ->
            hasExactProduct products "Lavf"
            || (hasExactProduct products "NSPlayer"
                && (firstProduct |> Option.exists (fun product -> product.Version = "4.1.0.3856")))

    let private browserKind products =
        [ Edge
          InternetExplorer
          Opera
          WechatBrowser
          Vivaldi
          Chrome
          ITunes
          PlayStation
          PodcastAddict
          Webkit
          Gecko
          WindowsMediaPlayer
          AppleCoreMedia
          Libavformat ]
        |> List.tryFind (fun kind -> kindExtends kind products)
        |> Option.defaultValue Base

    let private playStationBrowser products =
        let firstComment = applicationComment PlayStation products |> Option.bind List.tryHead
        if firstComment |> Option.exists (fun value -> value.Contains("PLAYSTATION 3", StringComparison.Ordinal)) then
            Some "PS3 Internet Browser"
        elif last products |> Option.exists (fun product -> product.Name = "Silk") then
            Some "Silk"
        elif firstComment |> Option.exists (fun value -> value.Contains("PlayStation 4", StringComparison.Ordinal)) then
            Some "PS4 Internet Browser"
        else
            None

    let private geckoBrowser products =
        [ "PaleMoon"; "Firefox"; "Camino"; "Iceweasel"; "Seamonkey" ]
        |> List.tryFind (hasProduct products)
        |> Option.orElseWith (fun () -> first products |> Option.map (fun product -> product.Name))

    let private webkitPlatform products =
        match applicationComment Webkit products with
        | None -> None
        | Some [] -> None
        | Some comments ->
            let firstComment = List.tryHead comments |> Option.defaultValue ""
            if firstComment.Contains("Windows", StringComparison.Ordinal) then
                Some "Windows"
            elif firstComment = "BB10" then
                Some "BlackBerry"
            elif comments |> List.exists (fun comment -> comment.Contains("Android", StringComparison.Ordinal)) then
                Some "Android"
            else
                Some firstComment

    let private iosVersion (value: string) =
        let mutable searchFrom = 0
        let mutable result = None
        let mutable cpu = value.IndexOf("CPU ", searchFrom, StringComparison.Ordinal)

        while cpu >= 0 && result.IsNone do
            let start = cpu + 4
            let afterDevice =
                if value.AsSpan(start).StartsWith("iPhone ".AsSpan(), StringComparison.Ordinal) then start + "iPhone ".Length
                elif value.AsSpan(start).StartsWith("iPod ".AsSpan(), StringComparison.Ordinal) then start + "iPod ".Length
                else start

            if value.AsSpan(afterDevice).StartsWith("OS ".AsSpan(), StringComparison.Ordinal) then
                let versionStart = afterDevice + 3
                let length = runWhile value versionStart (fun character -> isAsciiDigit character || character = '_')
                let ending = versionStart + length
                if length > 0
                   && value.AsSpan(ending).StartsWith(" like Mac OS X".AsSpan(), StringComparison.Ordinal) then
                    result <- Some(value.Substring(versionStart, length))

            searchFrom <- start
            cpu <- value.IndexOf("CPU ", searchFrom, StringComparison.Ordinal)

        result

    let private normalizedWebkitOs value =
        match iosVersion value with
        | Some version -> Some("iOS " + version.Replace('_', '.'))
        | None -> Some value

    let private webkitOs products =
        match applicationComment Webkit products with
        | None -> None
        | Some comments ->
            let at index = List.tryItem index comments
            let firstComment = at 0
            let secondComment = at 1
            let thirdComment = at 2

            if firstComment |> Option.exists (fun comment -> comment.Contains("Windows NT", StringComparison.Ordinal)) then
                firstComment |> Option.bind normalizedWebkitOs
            elif thirdComment.IsNone
                 || (secondComment |> Option.exists (fun comment -> comment.Contains("Android", StringComparison.Ordinal))) then
                secondComment |> Option.bind normalizedWebkitOs
            else
                comments
                |> List.tryPick (fun comment -> iosVersion comment |> Option.map (fun _ -> comment))
                |> Option.bind normalizedWebkitOs
                |> Option.orElseWith (fun () -> thirdComment |> Option.bind normalizedWebkitOs)

    let private webkitBrowser products =
        if webkitOs products |> Option.exists (fun os -> os.Contains("Android", StringComparison.Ordinal)) then
            "Android"
        elif webkitPlatform products = Some "BlackBerry" then
            "BlackBerry"
        else
            "Safari"

    let private browserName kind products =
        match kind with
        | Base -> application kind products |> Option.map (fun product -> product.Name)
        | Edge -> Some "Edge"
        | InternetExplorer -> Some "Internet Explorer"
        | Opera -> Some "Opera"
        | WechatBrowser -> Some "Wechat Browser"
        | Vivaldi -> Some "Vivaldi"
        | Chrome -> if hasProduct products "Iron" then Some "Iron" else Some "Chrome"
        | ITunes -> Some "iTunes"
        | PlayStation -> playStationBrowser products
        | PodcastAddict -> Some "Podcast Addict"
        | Webkit -> Some(webkitBrowser products)
        | Gecko -> geckoBrowser products
        | WindowsMediaPlayer -> Some "Windows Media Player"
        | AppleCoreMedia -> Some "AppleCoreMedia"
        | Libavformat -> Some "libavformat"

    let private ieVersion (comment: string) =
        let mutable searchFrom = 0
        let mutable result = None

        while searchFrom < comment.Length && result.IsNone do
            let msieAt = comment.IndexOf("MSIE", searchFrom, StringComparison.Ordinal)
            let rvAt = comment.IndexOf("rv:", searchFrom, StringComparison.Ordinal)
            let useMsie = msieAt >= 0 && (rvAt < 0 || msieAt < rvAt)

            if useMsie then
                let afterMarker = msieAt + "MSIE".Length
                if afterMarker < comment.Length && isRubySpace comment[afterMarker] then
                    let versionStart = afterMarker + 1
                    let length = runWhile comment versionStart (fun character -> isAsciiDigit character || character = '.')
                    if length > 0 then result <- Some(comment.Substring(versionStart, length))
                searchFrom <- afterMarker
            elif rvAt >= 0 then
                let versionStart = rvAt + "rv:".Length
                let length = runWhile comment versionStart (fun character -> isAsciiDigit character || character = '.')
                if length > 0 then result <- Some(comment.Substring(versionStart, length))
                searchFrom <- versionStart
            else
                searchFrom <- comment.Length

        result

    let private operaMini (products: Product list) =
        first products
        |> Option.exists (fun product ->
            (joinedComment product.Comment).Contains("Opera Mini", StringComparison.Ordinal))

    let private captureAfter (value: string) (marker: string) predicate =
        let mutable searchFrom = 0
        let mutable result = None
        let mutable found = value.IndexOf(marker, searchFrom, StringComparison.Ordinal)

        while found >= 0 && result.IsNone do
            let start = found + marker.Length
            let length = runWhile value start predicate
            if length > 0 then result <- Some(value.Substring(start, length))
            searchFrom <- start
            found <- value.IndexOf(marker, searchFrom, StringComparison.Ordinal)

        result

    let private operaVersion products =
        if operaMini products then
            applicationComment Opera products
            |> Option.defaultValue []
            |> List.tryPick (fun comment -> captureAfter comment "Opera Mini/" (fun c -> isAsciiDigit c || c = '.'))
            |> Option.orElse (Some "")
        else
            productNamed products "Version"
            |> Option.orElseWith (fun () -> productNamed products "OPR")
            |> Option.map (fun product -> product.Version)
            |> Option.orElseWith (fun () -> application Opera products |> Option.map (fun product -> product.Version))

    let private playStationVersion kind products =
        let os = applicationComment kind products |> Option.map (String.concat " ") |> Option.defaultValue ""
        if playStationBrowser products = Some "Silk" then
            last products |> Option.map (fun product -> product.Version)
        else
            let marker =
                if os.Contains("PLAYSTATION 3", StringComparison.Ordinal) then Some "PLAYSTATION 3 "
                elif os.Contains("PlayStation 4", StringComparison.Ordinal) then Some "PlayStation 4 "
                elif os.Contains("PlayStation Vita", StringComparison.Ordinal) then Some "PlayStation Vita "
                else None

            marker
            |> Option.map (fun marker ->
                splitRuby marker os |> List.tryLast |> Option.defaultValue "")

    let private webkitBuildVersion build =
        match build with
        | "85.7" -> Some "1.0"
        | "85.8.5"
        | "85.8.2" -> Some "1.0.3"
        | "124" -> Some "1.2"
        | "125.2" -> Some "1.2.2"
        | "125.4" -> Some "1.2.3"
        | "125.5.5"
        | "125.5.6"
        | "125.5.7" -> Some "1.2.4"
        | "312.1.1"
        | "312.1" -> Some "1.3"
        | "312.5"
        | "312.5.1"
        | "312.5.2" -> Some "1.3.1"
        | "312.8"
        | "312.8.1" -> Some "1.3.2"
        | "412"
        | "412.6"
        | "412.6.2" -> Some "2.0"
        | "412.7" -> Some "2.0.1"
        | "416.11"
        | "416.12" -> Some "2.0.2"
        | "417.9"
        | "418" -> Some "2.0.3"
        | "418.8"
        | "418.9"
        | "418.9.1"
        | "419" -> Some "2.0.4"
        | "425.13" -> Some "2.2"
        | "534.52.7" -> Some "5.1.2"
        | _ -> None

    let private webkitVersion products =
        match productNamed products "Version" with
        | Some product -> Some product.Version
        | None ->
            let ios =
                webkitOs products
                |> Option.bind (fun os -> captureAfter os "iOS " (fun c -> isAsciiDigit c || c = '.'))
            if ios.IsSome && webkitBrowser products = "Safari" then
                ios |> Option.map (fun version -> version)
            else
                let build =
                    productNamed products "AppleWebKit"
                    |> Option.map (fun product -> product.Version)
                    |> Option.orElseWith (fun () ->
                        products
                        |> List.collect (fun product -> product.Comment |> Option.defaultValue [])
                        |> List.tryPick webkitCommentVersion)
                    |> Option.defaultValue ""

                webkitBuildVersion build |> Option.orElse (Some "")

    let private versionString kind products =
        let baseVersion () = application kind products |> Option.map (fun product -> product.Version)

        match kind with
        | Base
        | WindowsMediaPlayer
        | AppleCoreMedia -> baseVersion ()
        | Edge
        | Vivaldi -> last products |> Option.map (fun product -> product.Version)
        | InternetExplorer ->
            let comment = applicationComment kind products |> joinedComment
            ieVersion comment |> Option.orElse (Some "")
        | Opera -> operaVersion products
        | WechatBrowser -> productNamed products "MicroMessenger" |> Option.map (fun product -> product.Version)
        | Chrome ->
            productNamed products "CriOs"
            |> Option.orElseWith (fun () -> productNamed products "Chrome")
            |> Option.map (fun product -> product.Version)
        | ITunes -> productNamed products "iTunes" |> Option.map (fun product -> product.Version)
        | PlayStation -> playStationVersion kind products
        | PodcastAddict -> None
        | Webkit -> webkitVersion products
        | Gecko ->
            let browser = geckoBrowser products
            let version = browser |> Option.bind (fun name -> productNamed products name |> Option.map (fun product -> product.Version))
            match version with
            | Some value when not (value |> Seq.forall isRubySpace) -> Some value
            | _ -> baseVersion ()
        | Libavformat ->
            if hasProduct products "NSPlayer" then None else baseVersion ()

    let private isBot kind products =
        match application kind products with
        | None -> true
        | Some app ->
            let commentHasBot =
                products
                |> List.collect (fun product -> product.Comment |> Option.defaultValue [])
                |> List.exists (fun comment -> containsIgnoringCase comment "bot")

            commentHasBot
            || hasProduct products "Chrome-Lighthouse"
            || app.Name.Contains("bot", StringComparison.Ordinal)

    let private familyForBrowser (browser: string option) =
        match browser with
        | None -> BrowserFamily.Other
        | Some browser ->
            match browser.ToLowerInvariant() with
            | "safari" -> BrowserFamily.Safari
            | "chrome" -> BrowserFamily.Chrome
            | "firefox" -> BrowserFamily.Firefox
            | "opera" -> BrowserFamily.Opera
            | "internet explorer" -> BrowserFamily.InternetExplorer
            | _ -> BrowserFamily.Other

    /// Normalizes a raw User-Agent using the pinned useragent-gem behavior needed by
    /// Campfire's allow_browser policy. Ruby raises and absent browser names map to Other;
    /// blank, missing, or raised versions map to None.
    let normalize userAgent =
        let products = parse userAgent
        let kind = browserKind products
        let browser = browserName kind products
        let version =
            versionString kind products
            |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace value))

        { Family = familyForBrowser browser
          Version = version
          IsBot = isBot kind products }
