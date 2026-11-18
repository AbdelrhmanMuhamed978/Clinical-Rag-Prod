import asset from "../../../python-backend/clinical-text.json";

const whitespace = new Set(asset.whitespace);
const fold = new Map<number, string>(Object.entries(asset.casefold_expansions).map(([key, value]) => [Number(key), value]));
for (const [start, end, step, delta] of asset.casefold_ranges)
  for (let value = start; value <= end; value += step) fold.set(value, String.fromCodePoint(value + delta));

function excludedNameChar(value: number): boolean {
  let left = 0, right = asset.excluded_name_ranges.length - 1;
  while (left <= right) {
    const mid = Math.floor((left + right) / 2), [start, end] = asset.excluded_name_ranges[mid];
    if (value < start) right = mid - 1;
    else if (value > end) left = mid + 1;
    else return true;
  }
  return false;
}
export const hasText = (value: string) => Array.from(value).some(char => !whitespace.has(char.codePointAt(0)!));
export const validDrugName = (value: string) => hasText(value) && value.length <= 128 && !Array.from(value).some(char => excludedNameChar(char.codePointAt(0)!));
export function normalizedDrugAlias(value: string): string {
  let output = "", space = false;
  for (const char of value.normalize("NFKC")) {
    const point = char.codePointAt(0)!;
    if (whitespace.has(point)) { space = output.length !== 0; continue; }
    if (space) output += " ";
    space = false;
    output += fold.get(point) ?? char;
  }
  return output;
}

function ipv4(host: string): boolean {
  const parts = host.split(".");
  return parts.length === 4 && parts.every(part => /^(?:0|[1-9][0-9]{0,2})$/.test(part) && Number(part) <= 255);
}
function ipv6(value: string): boolean {
  let host = value;
  if (host.includes(".")) {
    const tail = host.slice(host.lastIndexOf(":") + 1);
    if (!ipv4(tail)) return false;
    host = host.slice(0, -tail.length) + "0:0";
  }
  const halves = host.split("::");
  if (halves.length > 2) return false;
  const parts = halves.filter(Boolean).flatMap(half => half.split(":"));
  return (halves.length === 2 ? parts.length < 8 : parts.length === 8) && parts.every(part => /^[0-9A-Fa-f]{1,4}$/.test(part));
}
export function validHttpSourceUrl(value: string): boolean {
  if (!value || value.length > 2048 || Array.from(value).some(char => whitespace.has(char.codePointAt(0)!) || excludedNameChar(char.codePointAt(0)!))
    || value.includes("\\") || /%(?![0-9A-Fa-f]{2})/.test(value)) return false;
  const match = /^https?:\/\/([^/?#]+)/i.exec(value);
  if (!match) return false;
  const authority = match[1];
  if (authority.includes("@") || authority.includes("%")) return false;
  let hostMatch: RegExpExecArray | null;
  if (authority.startsWith("[")) {
    hostMatch = /^\[([^\]]+)\](?::([0-9]{1,5}))?$/.exec(authority);
    if (!hostMatch || !ipv6(hostMatch[1])) return false;
  } else {
    hostMatch = /^([^:]+)(?::([0-9]{1,5}))?$/.exec(authority);
    if (!hostMatch) return false;
    const host = hostMatch[1].replace(/\.$/, ""), labels = host.split(".");
    if (host.length > 253 || labels.some(label => !/^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$/.test(label))
      || /^(?:[0-9]+|0[xX][0-9A-Fa-f]+)$/.test(labels.at(-1)!) && !ipv4(host)) return false;
  }
  return hostMatch[2] === undefined || Number(hostMatch[2]) <= 65535;
}

// Lexical inspection only. Compilation and bounded evaluation stay in .NET/Python.
export function portableRegexProblem(pattern: string): string | null {
  if (!pattern) return null;
  if (pattern.length > 512) return "Link terms must be a regular expression of at most 512 characters.";
  const invalid = "Use portable groups, classes, escapes and quantifiers (repeat bounds up to 10000); escape literal brackets and braces.";
  const escapes = String.raw`bBdDsSwWnrtfv\.*+?^$[]{}()|/-`;
  let groups = 0, inClass = false, content = false, repeatable = false, classSet = false, classRange = false;
  for (let i = 0; i < pattern.length; i++) {
    const char = pattern[i];
    if (char === "\\") {
      if (++i === pattern.length || !escapes.includes(pattern[i]) || inClass && "bB".includes(pattern[i])) return invalid;
      if (inClass && classRange && "dDsSwW".includes(pattern[i])) return invalid;
      classSet = inClass && "dDsSwW".includes(pattern[i]); classRange = false;
      content = true; repeatable = !"bB".includes(pattern[i]);
    } else if (inClass) {
      if (char === "[" || ["&&", "~~", "||", "--"].includes(pattern.slice(i, i + 2))) return invalid;
      if (char === "]") { if (!content) return invalid; inClass = false; repeatable = true; }
      else {
        const range = char === "-" && content && pattern[i + 1] !== "]";
        if (range && classSet) return invalid; // A shorthand set cannot be a range endpoint in Python.
        classRange = range; classSet = false; content = true;
      }
    } else if (char === "[") { inClass = true; content = false; classSet = false; classRange = false; if (pattern[i + 1] === "^") i++; }
    else if (char === "(") {
      if (pattern[i + 1] === "?") { if (pattern[i + 2] !== ":") return invalid; i += 2; }
      groups++; repeatable = false;
    } else if (char === ")") { if (!groups) return invalid; groups--; repeatable = true; }
    else if ("|^$".includes(char)) repeatable = false;
    else if ("*+?{".includes(char)) {
      if (!repeatable) return invalid;
      if (char === "{") {
        const end = pattern.indexOf("}", i + 1);
        if (end < 0) return invalid;
        const token = pattern.slice(i + 1, end), count = /^([0-9]{1,5})(?:,([0-9]{0,5}))?$/.exec(token);
        if (!count || count[0] !== token) return invalid;
        const lower = Number(count[1]), upper = count[2] ? Number(count[2]) : null;
        if (lower > 10000 || upper !== null && (upper > 10000 || upper < lower)) return invalid;
        i = end;
      }
      if (pattern[i + 1] === "?") i++;
      repeatable = false;
    } else if ("]}".includes(char)) return invalid;
    else repeatable = true;
  }
  return groups || inClass ? invalid : null;
}
