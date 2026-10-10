namespace AgentLang.TypedReferenceMaintenance.Tests

open AgentLang.TypedReferenceMaintenance.Domain

module Program =
    let private id value = ShipmentId.create value

    let private shipment key label scans = {
        Id = id key
        Label = label
        Scans = scans
    }

    let private input shipmentId reference status = {
        ShipmentId = id shipmentId
        Reference = reference
        Status = status
    }

    let private store shipments = {
        Shipments = shipments
        Audit = "sentinel-audit"
        Generation = 19
    }

    let private testScanLookup equal =
        let first = { Reference = "scan-1"; Status = "received" }
        let later = { Reference = "scan-1"; Status = "delivered" }
        let target = shipment "shipment-1" "Box" [ first; later ]
        equal "reference lookup returns the first matching scan" (Some first) (Shipment.findScan target "scan-1")
        equal "reference lookup returns none when absent" None (Shipment.findScan target "missing")

    let private testSingleScan equal =
        let first = shipment "shipment-1" "Box" []
        let second = shipment "shipment-2" "Envelope" []
        let current = store [ first; second ]
        let expected = store [ { first with Scans = [ { Reference = "scan-1"; Status = "received" } ] }; second ]
        equal "first scan appends and preserves all Store fields" (Ok expected) (Shipment.ingest current (input "shipment-1" "scan-1" "received"))

    let private testErrorPrecedence equal =
        let empty = store []
        equal "unknown shipment precedes invalid status" (Error UnknownShipment) (Shipment.ingest empty (input "missing" "scan-1" "invalid"))

        let known = store [ shipment "shipment-1" "Box" [] ]
        equal "known shipment reports invalid status" (Error InvalidStatus) (Shipment.ingest known (input "shipment-1" "scan-1" "invalid"))

    let private testBatch equal =
        let first = shipment "shipment-1" "Box" []
        let second = shipment "shipment-2" "Envelope" []
        let current = store [ first; second ]
        equal "empty batch is identity" (Ok current) (Shipment.ingestBatch current [])

        let ordered = [ input "shipment-2" "scan-b" "received"; input "shipment-1" "scan-a" "delivered" ]
        let expected = store [ { first with Scans = [ { Reference = "scan-a"; Status = "delivered" } ] }; { second with Scans = [ { Reference = "scan-b"; Status = "received" } ] } ]
        equal "batch applies input order and preserves shipment order" (Ok expected) (Shipment.ingestBatch current ordered)

        let failing = [ input "shipment-2" "scan-b" "received"; input "shipment-1" "scan-bad" "invalid"; input "missing" "later" "received" ]
        equal "batch returns first error" (Error InvalidStatus) (Shipment.ingestBatch current failing)

    [<EntryPoint>]
    let main _ =
        let mutable assertions = 0
        let equal label expected actual =
            assertions <- assertions + 1
            if expected <> actual then failwith $"FAIL: {label}"
        try
            testScanLookup equal
            testSingleScan equal
            testErrorPrecedence equal
            testBatch equal
            printfn "Shipment baseline: %d assertions" assertions
            0
        with error ->
            eprintfn "FAIL after %d assertions: %s" assertions error.Message
            1
