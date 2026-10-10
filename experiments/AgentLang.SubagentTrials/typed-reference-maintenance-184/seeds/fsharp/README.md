# F# baseline seed

`project/business/Business.fs` is the conventional counterpart to
`agentlang/common.flow`. `Shipment.findScan` is a pure, documented lookup with
tests and a usage example in the console test project. The baseline still
appends each valid scan; this lookup is setup-created vocabulary for the
maintenance task.

Run the inherited assertions with:

```powershell
dotnet run --project project/tests/AgentLang.TypedReference.Tests.fsproj -c Release
```

The seed uses one public single-case `ShipmentId` union over `string`, without
validation. Its public functions are `Shipment.ingest store input` and
`Shipment.ingestBatch store events`.
