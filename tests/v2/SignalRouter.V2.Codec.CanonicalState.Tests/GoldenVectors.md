# Golden vectors — canonical representation v1 (ADR 0012)

Hand-derived from the byte grammar frozen in
`docs/v2/adr/0012-canonical-state-representation-and-digest-policy.md`. The hex
literals below were assembled field by field from the grammar table — never from any
codec output — and the SHA-256 digests were computed from these literals with an
external standard tool (recorded at the bottom). The tests use only these literals;
the production writer, reader, `Verify`, and any shared helper are prohibited as
expectation generators.

## Vector 1 — minimal

Materialization: basis view `v@1.0`, domain `d`, scope `root` (temporal legs
`incarnation-1`/revision 7 supplied out-of-band and absent from the payload);
no nodes, no sources, `CompletenessMap.Complete`.

| Bytes (hex) | Meaning |
|---|---|
| `53 52 43 53` | magic "SRCS" |
| `01` | representation version 1 (varuint) |
| `01 76` | view contract id: len 1, "v" |
| `01` | view major 1 |
| `00` | view minor 0 |
| `01 64` | domain: len 1, "d" |
| `04 72 6F 6F 74` | scope: len 4, "root" |
| `00` | node count 0 |
| `00` | source count 0 |
| `00` | rootTruncated false |
| `00` | completeness entry count 0 |

Payload (20 bytes):

```text
535243530101760100016404726F6F7400000000
```

## Vector 2 — representative

Materialization: view `agent-standard@1.0`, domain `agent-domain`, scope `root`;
one node `save` (role `button`, parent `panel`, attributes `label`="Save" and
`secret` redacted, capability `Invoke@1.0` available, visibleChildCount 2); one
source `inventory` (contract `inventory@1.0`, no omission, field `count`=Integer 5,
redacted name `secret`); completeness rootTruncated=true with one entry
(`nodes/cut`, `BudgetTruncated`).

| Bytes (hex) | Meaning |
|---|---|
| `53 52 43 53 01` | magic + version |
| `0E 61 67 65 6E 74 2D 73 74 61 6E 64 61 72 64` | view id: len 14, "agent-standard" |
| `01 00` | view version 1.0 |
| `0C 61 67 65 6E 74 2D 64 6F 6D 61 69 6E` | domain: len 12, "agent-domain" |
| `04 72 6F 6F 74` | scope "root" |
| `01` | node count 1 |
| `04 73 61 76 65` | node key "save" |
| `06 62 75 74 74 6F 6E` | role "button" |
| `01 05 70 61 6E 65 6C` | parent present, "panel" |
| `02` | attribute count 2 |
| `05 6C 61 62 65 6C` | attr name "label" |
| `00` | not redacted |
| `01 04 53 61 76 65` | value tag String, "Save" |
| `06 73 65 63 72 65 74` | attr name "secret" |
| `01` | redacted (no value) |
| `01` | capability count 1 |
| `06 49 6E 76 6F 6B 65` | capability id "Invoke" |
| `01 00` | capability version 1.0 |
| `01` | available true |
| `02` | visibleChildCount 2 |
| `01` | source count 1 |
| `09 69 6E 76 65 6E 74 6F 72 79` | source key "inventory" |
| `09 69 6E 76 65 6E 74 6F 72 79` | source contract id "inventory" |
| `01 00` | contract version 1.0 |
| `00` | omission absent |
| `01` | field count 1 |
| `05 63 6F 75 6E 74` | field name "count" |
| `02 00 00 00 00 00 00 00 05` | value tag Integer, 5 (i64be) |
| `01` | redacted-name count 1 |
| `06 73 65 63 72 65 74` | "secret" |
| `01` | rootTruncated true |
| `01` | completeness entry count 1 |
| `09 6E 6F 64 65 73 2F 63 75 74` | region "nodes/cut" |
| `0F 42 75 64 67 65 74 54 72 75 6E 63 61 74 65 64` | reason "BudgetTruncated" |

Payload (170 bytes):

```text
53524353010E6167656E742D7374616E6461726401000C6167656E742D646F6D61696E04726F6F7401047361766506627574746F6E010570616E656C02056C6162656C0001045361766506736563726574010106496E766F6B65010001020109696E76656E746F727909696E76656E746F72790100000105636F756E7402000000000000000501067365637265740101096E6F6465732F6375740F4275646765745472756E6361746564
```

## Digests (external derivation)

Computed from the hex literals above — never from codec output — with PowerShell's
`System.Security.Cryptography.SHA256` over the literal bytes:

```powershell
function HashHex([string]$hex) {
  $clean = $hex -replace '[^0-9A-Fa-f]', ''
  $bytes = [byte[]]::new($clean.Length / 2)
  for ($i = 0; $i -lt $bytes.Length; $i++) { $bytes[$i] = [Convert]::ToByte($clean.Substring($i * 2, 2), 16) }
  ([System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes) |
    ForEach-Object { $_.ToString('x2') }) -join ''
}
```

Recorded output:

- Vector 1 (20 bytes): `a11521e8718da761ff84ca6ef5b9e8877e74f754733373cc8648a83aa878306b`
- Vector 2 (170 bytes): `77ca21927ef0f1ddf361a9ceddc08e6aadf2a65ea82cd964a7d032280488455a`
