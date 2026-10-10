namespace AgentLang.TypedReferenceMaintenance

module Domain =
    /// A nominal shipment key. Raw text is retained without validation.
    [<Struct; StructuralEquality; StructuralComparison>]
    type ShipmentId = ShipmentId of string

    module ShipmentId =
        let create (value: string) = ShipmentId value
        let value (ShipmentId value) = value

    /// A nominal scan reference. Raw text is retained without validation.
    [<Struct; StructuralEquality; StructuralComparison>]
    type TrackingReference = TrackingReference of string

    module TrackingReference =
        let create (value: string) = TrackingReference value
        let value (TrackingReference value) = value

    type Scan = {
        Reference: TrackingReference
        Status: string
    }

    type Shipment = {
        Id: ShipmentId
        Label: string
        Scans: Scan list
    }

    type Store = {
        Shipments: Shipment list
        Audit: string
        Generation: int
    }

    type ScanInput = {
        ShipmentId: ShipmentId
        Reference: TrackingReference
        Status: string
    }

    type ScanError =
        | UnknownShipment
        | InvalidStatus

    module Store =
        /// Return the first shipment with this nominal ID, preserving list order.
        let findShipment (id: ShipmentId) (store: Store) =
            store.Shipments |> List.tryFind (fun shipment -> shipment.Id = id)

        /// Replace the shipment with the matching ID without changing Store metadata.
        let withShipment (store: Store) (replacement: Shipment) =
            let _, reversed =
                store.Shipments
                |> List.fold (fun (replaced, output) candidate ->
                    if not replaced && candidate.Id = replacement.Id then
                        true, replacement :: output
                    else
                        replaced, candidate :: output) (false, [])
            let shipments = List.rev reversed
            { store with Shipments = shipments }

        let empty = {
            Shipments = []
            Audit = ""
            Generation = 0
        }

    module Shipment =
        /// Return the first exact-reference match, if one exists.
        let findScan (shipment: Shipment) (reference: TrackingReference) =
            shipment.Scans |> List.tryFind (fun scan -> scan.Reference = reference)

        /// Append one scan to an existing shipment after validating its status.
        let ingestRule (shipment: Shipment) (input: ScanInput) : Result<Shipment, ScanError> =
            let validStatus =
                input.Status = "received"
                || input.Status = "in-transit"
                || input.Status = "delivered"
            if not validStatus then
                Error InvalidStatus
            else
                match findScan shipment input.Reference with
                | Some _ -> Ok shipment
                | None ->
                    let scan = { Reference = input.Reference; Status = input.Status }
                    Ok { shipment with Scans = shipment.Scans @ [ scan ] }

        /// Ingest one scan, checking shipment existence before status validity.
        let ingest (store: Store) (input: ScanInput) : Result<Store, ScanError> =
            match Store.findShipment input.ShipmentId store with
            | None -> Error UnknownShipment
            | Some shipment ->
                ingestRule shipment input
                |> Result.map (Store.withShipment store)

        /// Ingest scans in order and retain the first error.
        let ingestBatch (store: Store) (events: ScanInput list) : Result<Store, ScanError> =
            events
            |> List.fold (fun outcome input ->
                outcome |> Result.bind (fun current -> ingest current input)) (Ok store)
