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

## Vector 3 — multi-node / multi-source, reverse registration order

Materialization: basis `v@1.0` / `d` / `root`; two nodes and two sources whose
fixture registers every list in **reverse canonical order** (nodes `[b, a]`,
sources `[s2, s1]`, completeness `[sources/s2, nodes/b]`) so the vector pins the
`string.CompareOrdinal` normalization, not the input order. Node `a`: role `r`,
no parent, empty. Node `b`: role `r`, parent `a`, attribute `x`=Boolean true,
capability `Cap@2.3` unavailable, visibleChildCount 1. Source `s1`
(`s1@1.0`): field `m`=Null. Source `s2` (`s2@1.0`): omission `Stale`, redacted
name `z`. Completeness: `nodes/b`→`Virtualized`, `sources/s2`→`Stale`.

| Bytes (hex) | Meaning |
|---|---|
| `53 52 43 53 01` | magic + version |
| `01 76 01 00` | view `v@1.0` |
| `01 64` | domain `d` |
| `04 72 6F 6F 74` | scope `root` |
| `02` | node count 2 |
| `01 61 01 72 00 00 00 00` | node `a`: role `r`, no parent, 0 attrs, 0 caps, child 0 |
| `01 62 01 72 01 01 61` | node `b`: role `r`, parent present `a` |
| `01 01 78 00 03 01` | 1 attribute: `x`, not redacted, Boolean true |
| `01 03 43 61 70 02 03 00` | 1 capability: `Cap@2.3`, available false |
| `01` | visibleChildCount 1 |
| `02` | source count 2 |
| `02 73 31 02 73 31 01 00` | source `s1`, contract `s1@1.0` |
| `00 01 01 6D 05 00` | no omission, 1 field `m`=Null, 0 redacted |
| `02 73 32 02 73 32 01 00` | source `s2`, contract `s2@1.0` |
| `01 05 53 74 61 6C 65` | omission present, `Stale` |
| `00 01 01 7A` | 0 fields, 1 redacted name `z` |
| `00 02` | rootTruncated false, 2 completeness entries |
| `07 6E 6F 64 65 73 2F 62 0B 56 69 72 74 75 61 6C 69 7A 65 64` | `nodes/b`: `Virtualized` |
| `0A 73 6F 75 72 63 65 73 2F 73 32 05 53 74 61 6C 65` | `sources/s2`: `Stale` |

Payload (120 bytes):

```text
535243530101760100016404726F6F74020161017200000000016201720101610101780003010103436170020300010202733102733101000001016D0500027332027332010001055374616C650001017A0002076E6F6465732F620B5669727475616C697A65640A736F75726365732F7332055374616C65
```

## Vector 4 — UTF-16 ordinal order vs UTF-8 byte order

Materialization: basis `v@1.0` / `d` / `root`; two empty nodes with role `r`
whose keys diverge between orderings: `U+10000` (LINEAR B SYLLABLE B008 A;
UTF-16 `D800 DC00`, UTF-8 `F0 90 80 80`) and `U+FF21` (FULLWIDTH LATIN CAPITAL
A; UTF-16 `FF21`, UTF-8 `EF BC A1`). `string.CompareOrdinal` compares UTF-16
code units, so `U+10000` sorts **first** (`D800 < FF21`) — raw UTF-8 byte order
would reverse them (`EF < F0`). An implementation that sorts by encoded bytes
drifts on exactly this vector.

| Bytes (hex) | Meaning |
|---|---|
| `53 52 43 53 01` | magic + version |
| `01 76 01 00 01 64 04 72 6F 6F 74` | view `v@1.0`, domain `d`, scope `root` |
| `02` | node count 2 |
| `04 F0 90 80 80 01 72 00 00 00 00` | node `U+10000` first (UTF-16 ordinal order) |
| `03 EF BC A1 01 72 00 00 00 00` | node `U+FF21` second |
| `00 00 00` | 0 sources, rootTruncated false, 0 entries |

Payload (41 bytes):

```text
535243530101760100016404726F6F740204F090808001720000000003EFBCA1017200000000000000
```

## Vector 5 — LEB128 length boundary at 127 / 128

Materialization: basis `v@1.0` / `d` / `root`; no nodes; one source `s`
(`s@1.0`) with two string fields: `p` = 127 × `a` and `q` = 128 × `a`. The
UTF-8 length 127 must encode as the single byte `7F`; length 128 must encode as
the minimal two-byte form `80 01`.

| Bytes (hex) | Meaning |
|---|---|
| `53 52 43 53 01 01 76 01 00 01 64 04 72 6F 6F 74` | header, view `v@1.0`, `d`, `root` |
| `00 01` | 0 nodes, 1 source |
| `01 73 01 73 01 00 00` | source `s`, contract `s@1.0`, no omission |
| `02` | field count 2 |
| `01 70 01 7F` + 127 × `61` | field `p`: String, length 127 (one-byte varint) |
| `01 71 01 80 01` + 128 × `61` | field `q`: String, length 128 (minimal two-byte varint) |
| `00 00 00` | 0 redacted, rootTruncated false, 0 entries |

Payload (293 bytes): the pattern above concatenated; the test builds the literal
from this pattern (`"7f" + "61"×127`, `"8001" + "61"×128`) rather than spelling
out 255 repeated bytes here.

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
- Vector 3 (120 bytes): `c6e70db8360244147ea525b6934077b0e67a0437ba3e6a1b436a23ca19fa4216`
- Vector 4 (41 bytes): `821c9b7fb64aba35f87b61e84852ea7e1ed55d4913071d1f85b54fc94031171d`
- Vector 5 (293 bytes): `0a9ab9d1d1efedcf44c38dd332fede13e1d3c45a426c11fa0bb6aa070ad25069`