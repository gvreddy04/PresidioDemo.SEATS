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

With decoded-text detection, `SEATS.txt` and `SEATS_03.txt` are published through the real worker and local Presidio, and `SEATS_02_Negative_Testing.txt` is dead-lettered with the same reason. These results use the shipped `Presidio:DefaultDetection` (off). The safety check is names-only: the 6 names among the 48 values the former field policies protected are masked by the name keys of `seats.xml`, so there are no approved exceptions, and the other 42 (passenger and order IDs, the employee ID and the seat attribute codes) are reported as out of scope. Both PNR occurrences stay unchanged, under the skipped `UARecloc` key. `SEATS.txt` has 3 changed, 29 allowed and 26 review strings, `SEATS_03.txt` 3, 31 and 16; the review strings are the IDs and attribute codes the file no longer maps, in which nothing is detected. Source hashes, payload sizes, numbers, property names, grouping, MQ headers, row framing and newline style are preserved.

## Configuration behavior (removed design)

The removed design kept the Seats policy inline in `feed-policies.xml`, with one exact JSON path per scalar and a Keep or Replace action; every populated scalar needed a configured rule, and an unknown path rejected the complete message. The recognizer file that replaced it, `seats.xml`, decides by property name instead, in the same format as the PNR-Linking worker.

## Protection with `seats.xml`

Each string is decided on its own, in this order:

| Step | Recognizer file element | Example keys | Result |
| --- | --- | --- | --- |
| 1 | `<entity_mapping>` | `FirstName`, `LastName`, `MiddleName`, `BookedAsName` (names only) | The whole string is `PERSON` and is masked with `*` at the same width. No other key is mapped: `PaxId`, `OrderId`, `EmpId` and the attribute keys are out of the names-only scope. |
| 2 | `<keys_to_skip>` | `UARecloc`, `pnr`, seat number, cabin and booking class, service and flight references, timestamps, status, channel and agent | The string is published unchanged without analysis. The record locator links the booking, so it stays unchanged. |
| 3 | `<allow_list>` | none: the item is a timestamp shape | A string that matches an item end to end is published unchanged. |
| 4 | Presidio | any key the file does not name | Local Presidio analyzes the string. The property names of its key path are passed as Presidio context words, as Presidio's own `analyze_dict` passes a key. Each span at or above the threshold gets its entity's anonymizer; a string with nothing detected is published unchanged and listed for review. |

A key is matched ignoring case, anywhere on the key path. The innermost mapped name wins. A number or boolean under a mapped key cannot be protected in its JSON type, so the message is dead-lettered (WR-016). In the supplied samples the IDs and attribute codes reach step 4.

Which recognizers run in step 4 depends on `Presidio:DefaultDetection`. Off (the shipped default), only the file's custom recognizer `passengerName` runs, so only the names act; the predefined recognizers and spaCy do not run. On, Presidio's predefined recognizers (email, phone, credit card, spaCy `PERSON`, `LOCATION`, `DATE_TIME` and the rest) run as well, and the skipped keys and the allow list keep the operational values away from them.

## Reviewed field names

JSON property paths are unchanged from the producer.

| Property | Entity in `seats.xml` | Evidence and limits |
| --- | --- | --- |
| `FirstName`, `LastName`, `MiddleName`, `BookedAsName` | `PERSON` (`passengerName`) | Names of one customer; `BookedAsName` is `SURNAME/GIVEN`, which the name pattern also finds under any other property |
| `PaxId` | not mapped (names only) | Passenger identifier within the order; not a stable identity across orders. The removed design protected it as `PASSENGER_ID` |
| `OrderId` | not mapped (names only) | The order the event belongs to. Formerly `ORDER_ID` |
| `EmpId` | not mapped (names only) | The employee or service account that made the change. Formerly `EMPLOYEE_ID` |
| `Attributes`, `IATAAttributes`, `TTYAttributes` | not mapped (names only) | Seat and passenger attribute codes, some of which describe health, age or custody. Formerly `SENSITIVE_ATTRIBUTE` |
| `UARecloc` | skipped | The record locator (PNR), published unchanged; `pnr` is skipped for the same reason |

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
