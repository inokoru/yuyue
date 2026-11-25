#!/usr/bin/env node
/**
 * Layer 3: Generates `reader/schema/bridge-messages.schema.json` directly from the TypeScript
 * type declarations in:
 *   - `reader/src/marks.ts`    (Point, Style, MarkSpec)
 *   - `reader/src/foreign.ts`  (WordHit)
 *   - `reader/src/bridge.ts`   (Geometry, Link, Message)
 *   - `reader/src/settings.ts` (Settings)
 *
 * Zero external dependencies (works across TypeScript 5/6/7).
 *
 * Usage:
 *   node scripts/generate-schema.mjs          # write schema
 *   node scripts/generate-schema.mjs --check  # fail if committed schema is out of date
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const OUT_PATH = path.join(ROOT, 'schema', 'bridge-messages.schema.json');
const CHECK = process.argv.includes('--check');

function stripComments(src) {
  let out = '';
  let i = 0;
  while (i < src.length) {
    const ch = src[i];
    if (ch === "'" || ch === '"') {
      const q = ch;
      out += ch;
      i++;
      while (i < src.length) {
        if (src[i] === '\\' && i + 1 < src.length) {
          out += src[i] + src[i + 1];
          i += 2;
        } else {
          out += src[i];
          if (src[i++] === q) break;
        }
      }
    } else if (ch === '/' && src[i + 1] === '/') {
      while (i < src.length && src[i] !== '\n') i++;
    } else if (ch === '/' && src[i + 1] === '*') {
      i += 2;
      while (i < src.length && !(src[i] === '*' && src[i + 1] === '/')) i++;
      i += 2;
    } else {
      out += ch;
      i++;
    }
  }
  return out;
}

function tokenize(src) {
  const tokens = [];
  let i = 0;
  while (i < src.length) {
    const ch = src[i];
    if (/\s/.test(ch)) {
      i++;
      continue;
    }
    if (ch === "'" || ch === '"') {
      const q = ch;
      let val = '';
      i++;
      while (i < src.length && src[i] !== q) {
        if (src[i] === '\\' && i + 1 < src.length) {
          val += src[i + 1];
          i += 2;
        } else {
          val += src[i++];
        }
      }
      i++;
      tokens.push({ kind: 'str', value: val });
      continue;
    }
    if (/[A-Za-z_$]/.test(ch)) {
      let id = ch;
      i++;
      while (i < src.length && /[A-Za-z0-9_$]/.test(src[i])) id += src[i++];
      tokens.push({ kind: 'id', value: id });
      continue;
    }
    if (/[0-9]/.test(ch)) {
      let num = ch;
      i++;
      while (i < src.length && /[0-9.]/.test(src[i])) num += src[i++];
      tokens.push({ kind: 'num', value: Number(num) });
      continue;
    }
    tokens.push({ kind: 'sym', value: ch });
    i++;
  }
  return tokens;
}

function parseExportedTypes(relPath, registry) {
  const src = stripComments(fs.readFileSync(path.join(ROOT, relPath), 'utf8'));
  const tokens = tokenize(src);
  let pos = 0;

  const peek = (o = 0) => tokens[pos + o];
  const next = () => tokens[pos++];
  const eat = (kind, val) => {
    const t = peek();
    if (t && t.kind === kind && (val === undefined || t.value === val)) {
      pos++;
      return t;
    }
    return null;
  };
  const expect = (kind, val) => {
    const t = eat(kind, val);
    if (!t) throw new Error(`${relPath}: expected ${kind} ${val ?? ''} near ${JSON.stringify(peek())}`);
    return t;
  };

  function parseObjectBody() {
    expect('sym', '{');
    const props = {};
    const required = [];
    while (peek() && !(peek().kind === 'sym' && peek().value === '}')) {
      const nameTok = next();
      const name = nameTok.value;
      const optional = Boolean(eat('sym', '?'));
      expect('sym', ':');
      const typeNode = parseUnion();
      eat('sym', ';') || eat('sym', ',');
      props[name] = typeNode;
      if (!optional) required.push(name);
    }
    expect('sym', '}');
    return { kind: 'object', props, required };
  }

  function parsePrimary() {
    if (peek()?.kind === 'sym' && peek().value === '{') return parseObjectBody();
    if (eat('sym', '(')) {
      const inner = parseUnion();
      expect('sym', ')');
      return inner;
    }
    if (peek()?.kind === 'str') return { kind: 'literal', value: next().value };
    if (peek()?.kind === 'num') return { kind: 'literal', value: next().value };
    if (peek()?.kind === 'id') {
      const id = next().value;
      if (id === 'true') return { kind: 'literal', value: true };
      if (id === 'false') return { kind: 'literal', value: false };
      if (['string', 'number', 'boolean', 'null', 'undefined'].includes(id)) {
        return { kind: 'prim', value: id };
      }
      return { kind: 'ref', name: id };
    }
    throw new Error(`${relPath}: unexpected token ${JSON.stringify(peek())}`);
  }

  function parsePostfix() {
    let node = parsePrimary();
    while (peek()?.kind === 'sym' && peek().value === '[' && peek(1)?.kind === 'sym' && peek(1).value === ']') {
      pos += 2;
      node = { kind: 'array', elem: node };
    }
    return node;
  }

  function parseIntersection() {
    const parts = [parsePostfix()];
    while (eat('sym', '&')) parts.push(parsePostfix());
    return parts.length === 1 ? parts[0] : { kind: 'intersection', parts };
  }

  function parseUnion() {
    eat('sym', '|'); // allow leading `|`
    const parts = [parseIntersection()];
    while (eat('sym', '|')) parts.push(parseIntersection());
    return parts.length === 1 ? parts[0] : { kind: 'union', parts };
  }

  while (pos < tokens.length) {
    if (peek().kind === 'id' && peek().value === 'export') {
      const nextTok = peek(1);
      if (nextTok?.kind === 'id' && nextTok.value === 'interface') {
        pos += 2;
        const name = expect('id').value;
        registry[name] = parseObjectBody();
        continue;
      }
      if (nextTok?.kind === 'id' && nextTok.value === 'type') {
        pos += 2;
        const name = expect('id').value;
        expect('sym', '=');
        registry[name] = parseUnion();
        eat('sym', ';');
        continue;
      }
    }
    pos++;
  }
}

function toJsonSchema(node, registry) {
  switch (node.kind) {
    case 'prim':
      return { type: node.value };
    case 'literal':
      return { const: node.value };
    case 'ref': {
      const target = registry[node.name];
      if (!target) throw new Error(`Unresolved TS type reference: ${node.name}`);
      return toJsonSchema(target, registry);
    }
    case 'array':
      return { type: 'array', items: toJsonSchema(node.elem, registry) };
    case 'object': {
      const properties = {};
      for (const [k, v] of Object.entries(node.props)) {
        properties[k] = toJsonSchema(v, registry);
      }
      const obj = { type: 'object', additionalProperties: false, properties };
      if (node.required.length) obj.required = [...node.required];
      return obj;
    }
    case 'intersection': {
      const mergedProps = {};
      const mergedReq = new Set();
      for (const part of node.parts) {
        const s = toJsonSchema(part, registry);
        if (s.type !== 'object') throw new Error(`Expected object in intersection, got ${JSON.stringify(s)}`);
        Object.assign(mergedProps, s.properties);
        for (const r of s.required || []) mergedReq.add(r);
      }
      const obj = { type: 'object', additionalProperties: false, properties: mergedProps };
      if (mergedReq.size) obj.required = [...mergedReq];
      return obj;
    }
    case 'union': {
      const members = node.parts
        .filter((p) => !(p.kind === 'prim' && p.value === 'undefined'))
        .map((p) => toJsonSchema(p, registry));
      if (members.length === 1) return members[0];
      if (members.every((m) => Object.keys(m).length === 1 && typeof m.const === 'string')) {
        return { type: 'string', enum: members.map((m) => m.const) };
      }
      if (members.every((m) => Object.keys(m).length === 1 && typeof m.type === 'string')) {
        return { type: [...new Set(members.map((m) => m.type))] };
      }
      return { anyOf: members };
    }
    default:
      throw new Error(`Unknown AST node: ${JSON.stringify(node)}`);
  }
}

function buildSchema() {
  const registry = {};
  parseExportedTypes('src/marks.ts', registry);
  parseExportedTypes('src/foreign.ts', registry);
  parseExportedTypes('src/bridge.ts', registry);
  parseExportedTypes('src/settings.ts', registry);

  const msgNode = registry.Message;
  if (!msgNode || msgNode.kind !== 'union') {
    throw new Error('Expected union type `Message` in reader/src/bridge.ts');
  }

  const variants = [];
  for (const part of msgNode.parts) {
    const schema = toJsonSchema(part, registry);
    const typeProp = schema.properties?.type;
    if (typeProp?.enum && Array.isArray(typeProp.enum)) {
      for (const kind of typeProp.enum) {
        variants.push({
          ...schema,
          properties: { ...schema.properties, type: { const: kind } },
        });
      }
    } else {
      variants.push(schema);
    }
  }

  variants.sort((a, b) =>
    String(a.properties?.type?.const || '').localeCompare(String(b.properties?.type?.const || '')),
  );
  const messageTypes = variants.map((v) => v.properties?.type?.const).filter(Boolean);

  return {
    $schema: 'https://json-schema.org/draft/2020-12/schema',
    $id: 'https://yuyue.app/schema/bridge-messages.schema.json',
    title: 'Yuyue Reader <-> Native Bridge Contract',
    description:
      'Generated from reader/src/bridge.ts (Message), reader/src/settings.ts (Settings), and reader/src/marks.ts (MarkSpec). Do not edit by hand; run `cd reader && pnpm schema`.',
    'x-message-types': messageTypes,
    $defs: {
      ReaderToNativeMessage: {
        oneOf: variants,
      },
      NativeToReaderSettings: toJsonSchema(registry.Settings, registry),
      NativeToReaderMarkSpec: toJsonSchema(registry.MarkSpec, registry),
    },
    $ref: '#/$defs/ReaderToNativeMessage',
  };
}

function main() {
  const schema = buildSchema();
  const content = JSON.stringify(schema, null, 2) + '\n';

  if (CHECK) {
    if (!fs.existsSync(OUT_PATH)) {
      console.error(`Missing ${path.relative(ROOT, OUT_PATH)}. Run \`cd reader && pnpm schema\`.`);
      process.exit(1);
    }
    const existing = fs.readFileSync(OUT_PATH, 'utf8').replace(/\r\n/g, '\n');
    if (existing !== content) {
      console.error(`${path.relative(ROOT, OUT_PATH)} is out of sync with reader/src/{bridge,settings,marks,foreign}.ts.`);
      console.error('Run `cd reader && pnpm schema` and commit the updated schema.');
      process.exit(1);
    }
    console.log(
      `Schema up to date (${schema['x-message-types'].length} message types: ${schema['x-message-types'].join(', ')}).`,
    );
    return;
  }

  fs.mkdirSync(path.dirname(OUT_PATH), { recursive: true });
  fs.writeFileSync(OUT_PATH, content, 'utf8');
  console.log(`Wrote ${path.relative(ROOT, OUT_PATH)} (${schema['x-message-types'].length} message types).`);
}

main();
