#!/usr/bin/env node
/**
 * Regenerates `src/Pidge.Core/Idna/IdnaTables.g.cs` from the Unicode
 * Character Database: the UTS 46 mapping table, plus the four properties the
 * IDNA validity checks read (bidi class, joining type, general category and
 * canonical combining class).
 *
 *   node scripts/gen-idna-tables.mjs [--unicode 17.0.0]
 *
 * The source files are downloaded once into `artifacts/unicode/<version>/`.
 */
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { readFlag } from "./rid.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const version = readFlag("--unicode") ?? "17.0.0";
const cache = join(root, "artifacts", "unicode", version);
const output = join(root, "src", "Pidge.Core", "Idna", "IdnaTables.g.cs");

const sources = {
  idna: [
    `https://www.unicode.org/Public/${version}/idna/IdnaMappingTable.txt`,
    `https://www.unicode.org/Public/idna/${version}/IdnaMappingTable.txt`,
  ],
  bidi: [`https://www.unicode.org/Public/${version}/ucd/extracted/DerivedBidiClass.txt`],
  joining: [`https://www.unicode.org/Public/${version}/ucd/extracted/DerivedJoiningType.txt`],
  category: [`https://www.unicode.org/Public/${version}/ucd/extracted/DerivedGeneralCategory.txt`],
  combining: [`https://www.unicode.org/Public/${version}/ucd/extracted/DerivedCombiningClass.txt`],
};

async function load(name) {
  mkdirSync(cache, { recursive: true });
  const file = join(cache, `${name}.txt`);
  if (existsSync(file)) return readFileSync(file, "utf8");
  for (const url of sources[name]) {
    const response = await fetch(url);
    if (!response.ok) continue;
    const text = await response.text();
    writeFileSync(file, text);
    return text;
  }
  throw new Error(`could not download ${name} for Unicode ${version}`);
}

const CODE_POINTS = 0x110000;

/** Yields `[first, last, fields]` for each data line, `@missing` lines first. */
function* records(text) {
  const lines = text.split("\n");
  for (const line of lines) {
    const missing = line.match(/^# @missing: (.*)$/);
    if (missing) yield parse(missing[1]);
  }
  for (const line of lines) {
    const data = line.replace(/#.*/, "").trim();
    if (data) yield parse(data);
  }
}

function parse(data) {
  const [range, ...fields] = data.split(";").map((field) => field.trim());
  const [first, last = first] = range.split("..").map((hex) => parseInt(hex, 16));
  return [first, last, fields];
}

function fill(table, text, valueOf) {
  for (const [first, last, fields] of records(text)) {
    const value = valueOf(fields);
    if (value !== undefined) table.fill(value, first, last + 1);
  }
}

// Bidi classes the bidi rule distinguishes; everything else is OTHER.
const BIDI = ["L", "R", "AL", "AN", "EN", "ES", "CS", "ET", "ON", "BN", "NSM"];
const BIDI_LONG = {
  Left_To_Right: "L",
  Right_To_Left: "R",
  Arabic_Letter: "AL",
  Arabic_Number: "AN",
  European_Number: "EN",
  European_Separator: "ES",
  Common_Separator: "CS",
  European_Terminator: "ET",
  Other_Neutral: "ON",
  Boundary_Neutral: "BN",
  Nonspacing_Mark: "NSM",
};
const bidiCode = (name) => {
  const index = BIDI.indexOf(BIDI_LONG[name] ?? name);
  return index < 0 ? BIDI.length : index;
};

const JOINING = ["U", "C", "D", "L", "R", "T"];
const JOINING_LONG = {
  Non_Joining: "U",
  Join_Causing: "C",
  Dual_Joining: "D",
  Left_Joining: "L",
  Right_Joining: "R",
  Transparent: "T",
};
const joiningCode = (name) => JOINING.indexOf(JOINING_LONG[name] ?? name);

const MARKS = new Set(["Mn", "Mc", "Me", "Nonspacing_Mark", "Spacing_Mark", "Enclosing_Mark"]);

// Properties, packed: bidi (4 bits) | joining type (3) | mark (1) | virama (1).
const bidi = new Uint8Array(CODE_POINTS).fill(bidiCode("L"));
fill(bidi, await load("bidi"), ([name]) => bidiCode(name));
const joining = new Uint8Array(CODE_POINTS);
fill(joining, await load("joining"), ([name]) => joiningCode(name));
const mark = new Uint8Array(CODE_POINTS);
fill(mark, await load("category"), ([name]) => (MARKS.has(name) ? 1 : 0));
const virama = new Uint8Array(CODE_POINTS);
fill(virama, await load("combining"), ([ccc]) => (ccc === "9" || ccc === "Virama" ? 1 : 0));

const properties = new Uint16Array(CODE_POINTS);
for (let c = 0; c < CODE_POINTS; c++) {
  properties[c] = bidi[c] | (joining[c] << 4) | (mark[c] << 7) | (virama[c] << 8);
}

// Mapping, packed: status (2 bits) | length of the replacement (6) | its offset
// into MappingText (the rest). Deviations are valid: processing is never
// transitional. The STD3 statuses are valid or mapped; the URL deny list is
// applied separately.
const VALID = 0;
const IGNORED = 1;
const MAPPED = 2;
const DISALLOWED = 3;

const status = new Uint8Array(CODE_POINTS).fill(DISALLOWED);
const replacement = new Array(CODE_POINTS);
for (const [first, last, [kind, target]] of records(await load("idna"))) {
  for (let c = first; c <= last; c++) {
    switch (kind) {
      case "valid":
      case "deviation":
      case "disallowed_STD3_valid":
        status[c] = VALID;
        break;
      case "ignored":
        status[c] = IGNORED;
        break;
      case "mapped":
      case "disallowed_STD3_mapped":
        status[c] = MAPPED;
        replacement[c] = String.fromCodePoint(
          ...target.split(/\s+/).map((hex) => parseInt(hex, 16)),
        );
        break;
      case "disallowed":
        status[c] = DISALLOWED;
        break;
      default:
        throw new Error(`unknown IDNA status ${kind}`);
    }
  }
}

let mappingText = "";
const offsets = new Map();
const mapping = new Int32Array(CODE_POINTS);
for (let c = 0; c < CODE_POINTS; c++) {
  if (status[c] !== MAPPED) {
    mapping[c] = status[c];
    continue;
  }
  const text = replacement[c];
  if (text.length >= 64) throw new Error(`mapping for U+${c.toString(16)} is too long to pack`);
  let offset = offsets.get(text);
  if (offset === undefined) {
    offset = mappingText.indexOf(text);
    if (offset < 0) {
      offset = mappingText.length;
      mappingText += text;
    }
    offsets.set(text, offset);
  }
  mapping[c] = (offset << 8) | (text.length << 2) | MAPPED;
}

/** Collapses a per-code-point table into the starts of its runs and their values. */
function runs(table) {
  const starts = [];
  const values = [];
  for (let c = 0; c < CODE_POINTS; c++) {
    if (c === 0 || table[c] !== table[c - 1]) {
      starts.push(c);
      values.push(table[c]);
    }
  }
  return { starts, values };
}

const csString = (text) =>
  [...text]
    .flatMap((ch) => {
      const units = [];
      for (let i = 0; i < ch.length; i++) units.push(ch.charCodeAt(i));
      return units;
    })
    .map((unit) =>
      unit >= 0x20 && unit < 0x7f && unit !== 0x22 && unit !== 0x5c
        ? String.fromCharCode(unit)
        : `\\u${unit.toString(16).toUpperCase().padStart(4, "0")}`,
    )
    .join("");

function csArray(numbers, perLine = 12) {
  const lines = [];
  for (let i = 0; i < numbers.length; i += perLine) {
    lines.push(
      `        ${numbers
        .slice(i, i + perLine)
        .map((n) => `0x${n.toString(16).toUpperCase()}`)
        .join(", ")},`,
    );
  }
  return lines.join("\n");
}

function csText(text, perLine = 16) {
  const escaped = csString(text);
  const pieces = escaped.match(/\\u[0-9A-F]{4}|[^\\]/g) ?? [];
  const lines = [];
  for (let i = 0; i < pieces.length; i += perLine)
    lines.push(`        "${pieces.slice(i, i + perLine).join("")}"`);
  return lines.join(" +\n");
}

const map = runs(mapping);
const props = runs(properties);

writeFileSync(
  output,
  `// <auto-generated>
// Generated by scripts/gen-idna-tables.mjs from the Unicode ${version} data files.
// Do not edit by hand; rerun the script instead.
// </auto-generated>

namespace Pidge.Core.Idna;

internal static partial class IdnaTables
{
    public const string UnicodeVersion = "${version}";

    private static ReadOnlySpan<int> MappingStarts =>
    [
${csArray(map.starts)}
    ];

    private static ReadOnlySpan<int> MappingValues =>
    [
${csArray(map.values)}
    ];

    private const string MappingText =
${csText(mappingText)};

    private static ReadOnlySpan<int> PropertyStarts =>
    [
${csArray(props.starts)}
    ];

    private static ReadOnlySpan<ushort> PropertyValues =>
    [
${csArray(props.values)}
    ];
}
`,
);

console.log(
  `wrote ${output}: ${map.starts.length} mapping runs, ${mappingText.length} mapping chars, ${props.starts.length} property runs`,
);
