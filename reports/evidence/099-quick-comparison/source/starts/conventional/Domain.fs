namespace AgentLang.BusinessPolicy

open System
open System.Globalization
open AgentLang.Business

/// A canonical UTC timestamp represented as round-trip text.
type Instant = private Instant of string

module Instant =
    /// Parses a timestamp and stores its canonical UTC round-trip form.
    let parse (value: string) : Result<Instant, string> =
        if String.IsNullOrWhiteSpace value then
            Error "Instant must contain a timestamp."
        else
            match DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None) with
            | true, parsed ->
                let canonical = parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                if String.Equals(value, canonical, StringComparison.Ordinal) then
                    Ok(Instant canonical)
                else
                    Error "Instant must use canonical UTC round-trip text."
            | false, _ -> Error "Instant must be parseable round-trip timestamp text."

    /// Returns the stored canonical UTC representation.
    let value (Instant value) = value

/// Public policy input. Kind intentionally remains an unnormalized string.
type Customer =
    { Id: Domain.CustomerId
      Email: Domain.Email
      Kind: string
      Balance: Domain.Money
      CreatedAt: Instant }

/// Builds a fixed-valid Customer while preserving the caller's raw Kind.
module Fixtures =
    let private unwrapDomain label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %s" label (Domain.DomainError.message error)

    let customer (kind: string) (balanceMinor: int64) : Customer =
        let id =
            Domain.CustomerId.parse "10000000-0000-0000-0000-000000000001"
            |> unwrapDomain "fixture customer id"

        let email =
            Domain.Email.create "ada@example.test"
            |> unwrapDomain "fixture customer email"

        let createdAt =
            Instant.parse "2026-01-01T12:00:00.0000000+00:00"
            |> Result.defaultWith (fun error -> failwithf "fixture customer timestamp: %s" error)

        { Id = id
          Email = email
          Kind = kind
          Balance = Domain.Money.ofMinorUnits balanceMinor
          CreatedAt = createdAt }
