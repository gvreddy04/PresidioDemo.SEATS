# Seats feed sample review and structure

> **Superseded design.** This review describes the field policies and the inline JSON policy paths that decoded-text detection replaced. Those policies are removed. Protection now uses the shared JSON parser and the Seats recognizer file; see [Decoded-text detection](../README.md#decoded-text-detection). The sample review, the structure findings and the field meanings below stay valid as analysis; the policy notes refer to the removed design. The protection results they produced are frozen, value-free, in [tests/fixtures/parity-baseline.json](../tests/fixtures/parity-baseline.json).
>
> This repository is the Seats worker and keeps the Seats feed only. The review of the other feeds is in the repository history at commit `dffbdad`.

All three files in `docs/sample-docs/Seats/` were reviewed without changing source bytes. Each is an MQ text export: `A` rows carry metadata and `X` rows carry hexadecimal payload bytes. All three payloads are UTF-8 JSON with an object root. `SEATS_02_Negative_Testing.txt` is a negative test.

## JSON representation and the shared parser

The Seats route selects `SeatsParser` in `src/PresidioDemo.Core/Decoder`, which sets the route and the required object root and inherits `JsonFeedParser`. The shared class uses `System.Text.Json.Utf8JsonReader` to record the exact byte span of every scalar and `JsonSerializer` to write a changed string; there are no separate JSON decoder, encoder, model or structure-definition files.

Each scalar has an instance path (a JSON Pointer, such as `/OrderChangeNotif/Old/Customers/0/FirstName`) that locates it in one message, and a wildcard path with array positions as `*` (`/OrderChangeNotif/Old/Customers/*/FirstName`). JSON field names and nesting supply the structure. Array position never establishes passenger identity. Only string scalars are detection candidates; numbers, booleans and nulls are kept byte for byte.

The parser keeps the original JSON bytes, scalar types and grouping, including empty arrays and objects, numeric spellings and whitespace. A protected output may differ only within changed string spans; MQ metadata, row framing, the newline style and all other payload bytes are validated before publication. Comparing the byte gaps between scalars also detects changes to property names and empty containers. Duplicate property names (also when written with escapes), a property named `*`, trailing data, truncated JSON, an array root and a JSON message without scalars are rejected. An MQ export must declare CCSID 1208 (UTF-8); no code-page conversion is guessed.

## Seats

A Seats message is one `OrderChangeNotif` event: event metadata (`EventVersion`, `EventTime`, `EventName`, `EventType`), the order (`OrderId`, `OrderCreateDateTime`, the numeric `OrderVersion`), an `Old` and a `New` section that each list `Customers`, and `EventDetail` (the channel, the agent and the employee ID). A customer carries `PaxId`, `SeqInPNR`, the names (`FirstName`, `LastName`, `BookedAsName` in `SURNAME/GIVEN` form), the record locator `UARecloc` and its creation date, and `ServiceItems`. A seat service item carries its identifiers, timestamps, booking class, cabin and seat number, the seat attribute lists (`Attributes`, `IATAAttributes`, `TTYAttributes`), the occupancy status, the flight segment and leg references and times, the source and the action type.

`SEATS.txt` (1,362 payload bytes) is a `PNRUpdate` event whose `Old` section holds one customer with one deleted seat and whose `New` section is empty. `SEATS_03.txt` (1,329 bytes) is a `SeatAssign` event with the customer in `New` and an empty `Old`, and its `EventDetail` adds `ChannelId` and `ChannelName`. `SEATS_02_Negative_Testing.txt` holds the same payload as `SEATS.txt`, exported with CCSID 437 instead of 1208, and is rejected before detection. `SEATS.txt` has 59 scalars (58 strings and one number), `SEATS_03.txt` 51 (50 and one).

The samples do not establish a general producer contract: they show one customer and one service item each, and neither a passenger with several seats nor a `MiddleName`. The two-passenger fixture `tests/unit-tests/fixtures/seats-mq-export.txt` (fictional values) covers two customers and both sections.

## Reviewed samples and results

Counts below describe JSON scalar occurrences under the removed field policies, whose inline Seats policy listed 68 exact paths (both samples and `New`-section mirrors of reviewed `Old`-section rules). Retained verification evidence records source hashes and calculated field paths without personal values.

| Sample | Payload bytes | Kept | Protected |
| --- | ---: | ---: | ---: |
| SEATS.txt | 1,362 | 30 | 29 Replace |
| SEATS_03.txt | 1,329 | 32 | 19 Replace |
| SEATS_02_Negative_Testing.txt | 1,362 | Rejected | MQ export without UTF-8 CCSID 1208 |

With decoded-text detection, `SEATS.txt` and `SEATS_03.txt` are published through the real worker and local Presidio, and `SEATS_02_Negative_Testing.txt` is dead-lettered with the same reason. All 48 values the former field policies protected (names, passenger and order IDs, the employee ID and the seat attribute codes) are protected by the recognizer keys of `seats.xml`, so there are no approved exceptions. Both PNR occurrences stay unchanged, under the allowed `UARecloc` key. `SEATS.txt` has 29 changed and 29 allowed strings, `SEATS_03.txt` 19 and 31; the extra kept occurrence of the removed design is the number `OrderVersion`, which is not a string. Source hashes, payload sizes, numbers, property names, grouping, MQ headers, row framing and newline style are preserved.

## Configuration behavior (removed design)

The removed design kept the Seats policy inline in `feed-policies.xml`, with one exact JSON path per scalar and a Keep or Replace action; every populated scalar needed a configured rule, and an unknown path rejected the complete message. The recognizer file that replaced it, `seats.xml`, decides by property name instead: recognizer keys for the personal-data properties, allow-list keys for the operational ones, and Presidio for any string under a property it does not name.

## Reviewed field names

JSON property paths are unchanged from the producer.

| Property | Entity in `seats.xml` | Evidence and limits |
| --- | --- | --- |
| `FirstName`, `LastName`, `MiddleName`, `BookedAsName` | `PERSON` (`passengerName`) | Names of one customer; `BookedAsName` is `SURNAME/GIVEN`, which the name pattern also finds under any other property |
| `PaxId` | `PASSENGER_ID` | Passenger identifier within the order; not a stable identity across orders |
| `OrderId` | `ORDER_ID` | The order the event belongs to |
| `EmpId` | `EMPLOYEE_ID` | The employee or service account that made the change |
| `Attributes`, `IATAAttributes`, `TTYAttributes` | `SENSITIVE_ATTRIBUTE` | Seat and passenger attribute codes, some of which describe health, age or custody; every value of the array |
| `UARecloc` | allowed | The record locator (PNR), published unchanged; `pnr` is allowed for the same reason |

## Reproduce verification

```powershell
dotnet build PresidioDemo.slnx
dotnet run --project tests/GenericCodecChecks
dotnet run --project tests/FeedPipelineChecks
dotnet run --project tests/WorkerChecks
src/.venv/Scripts/python.exe -m unittest discover -s tests/unit-tests -p test_*.py -v
src/.venv/Scripts/python.exe tests/run_sample_verification.py
```

The sample verification runs all three Seats samples through `tests/FeedPipelineHarness`. It keeps the outputs, the dead letter, transaction logs and a value-free `verification.json` in a fresh `.test-runs/e2e-*` folder. The harness exits with code 2 because `SEATS_02_Negative_Testing.txt` is rejected by design; the verification passes when exactly that sample is rejected with its reason.
