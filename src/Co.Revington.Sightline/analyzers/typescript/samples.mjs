// What a request to an endpoint carries, and sample values to start from, so "Try it" works without
// typing a body by hand. The same shape Sightline's C# reader produces: params in route, query, header
// or body, and a JSON body.

import { unwrap, memberName } from './extract.mjs';

/** Names of the values in a route: "/orders/:id", "/orders/[id]" and "/orders/{id}" all give id. */
export function routeNames(route) {
  const names = [];
  for (const segment of String(route ?? '').split('/')) {
    let name = '';
    if (/^[:*]./.test(segment)) name = segment.slice(1);
    else if (/^\[.+\]$/.test(segment)) name = segment.replace(/^\[+\.{0,3}|\]+$/g, '');
    else if (/^\{.+\}$/.test(segment)) name = segment.slice(1, -1).replace(/^\*+/, '').split(/[:=?]/)[0];
    name = (name.match(/^[A-Za-z0-9_$]+/) ?? [''])[0];
    if (name && !names.includes(name)) names.push(name);
  }
  return names;
}

const TEXT = [
  ['email', 'user@example.com'], ['firstname', 'Ada'], ['lastname', 'Lovelace'], ['username', 'ada'], ['password', 'P@ssw0rd!'],
  ['phone', '+1 555 0100'], ['url', 'https://example.com'], ['uri', 'https://example.com'], ['city', 'Toronto'], ['country', 'CA'],
  ['currency', 'USD'], ['slug', 'sample-slug'], ['sku', 'ABC-123'], ['code', 'ABC-123'], ['title', 'Sample title'],
  ['description', 'Sample text'], ['note', 'Sample text'], ['comment', 'Sample text'], ['name', 'Sample name'], ['status', 'active'],
  ['token', 'token'], ['date', '2026-01-01'], ['from', '2026-01-01'], ['to', '2026-01-31'], ['id', 'abc123'],
];

function textSample(name) {
  const n = String(name ?? '').toLowerCase();
  for (const [part, value] of TEXT) if (n.includes(part)) return value;
  return 'string';
}

function numberSample(name) {
  const n = String(name ?? '').toLowerCase();
  if (/price|amount|total|cost|weight/.test(n)) return 19.99;
  if (/pagesize|limit|take|size/.test(n)) return 10;
  if (/year/.test(n)) return 2026;
  if (/age/.test(n)) return 30;
  return 1;
}

/** A value for a field known only by its name. */
function guess(name) {
  const n = String(name ?? '');
  if (/^(is|has|can|should)[A-Z_]|enabled$|active$|^include/i.test(n)) return true;
  if (/(id|count|quantity|qty|amount|price|total|cost|page|size|limit|offset|age|year|number|num)$/i.test(n) || /weight|kg$|km$|lbs$|cm$/i.test(n)) return numberSample(n);
  return textSample(n);
}

function routeSample(name) {
  return /id$/i.test(name) ? '1' : textSample(name);
}

function singular(name) {
  return /s$/.test(name) && name.length > 1 ? name.slice(0, -1) : name;
}

// -------------------------------------------------------------------- from types

/** A sample value for a type. budget: how many fields one sample may have in all, so a wide model stays small. */
export function sampleOfType(pass, type, name, depth = 0, seen = new Set(), budget = { left: 200 }) {
  const ts = pass.ts;
  const checker = pass.checker;
  if (!type) return guess(name);
  const f = type.flags;
  if (f & ts.TypeFlags.StringLiteral) return type.value;
  if (f & ts.TypeFlags.NumberLiteral) return type.value;
  if (f & ts.TypeFlags.BooleanLiteral) return checker.typeToString(type) === 'true';
  if (f & ts.TypeFlags.StringLike) return textSample(name);
  if (f & ts.TypeFlags.NumberLike) return numberSample(name);
  if (f & ts.TypeFlags.BooleanLike) return true;
  if (f & ts.TypeFlags.BigIntLike) return 1;
  if (f & (ts.TypeFlags.Null | ts.TypeFlags.Undefined | ts.TypeFlags.Void)) return null;
  if (f & (ts.TypeFlags.Any | ts.TypeFlags.Unknown)) return guess(name);
  if (type.isUnion()) {
    const parts = type.types.filter((t) => !(t.flags & (ts.TypeFlags.Null | ts.TypeFlags.Undefined)));
    return parts.length ? sampleOfType(pass, parts[0], name, depth, seen, budget) : null;
  }
  const symbolName = type.getSymbol()?.name;
  if (symbolName === 'Date') return '2026-01-01T12:00:00Z';
  if (depth > 4 || seen.has(type) || budget.left <= 0) return {};
  if (checker.isArrayType?.(type) || symbolName === 'Array' || symbolName === 'ReadonlyArray') {
    const element = checker.getTypeArguments?.(type)?.[0];
    return [sampleOfType(pass, element, singular(name), depth + 1, seen, budget)];
  }
  seen.add(type);
  const result = {};
  for (const p of checker.getPropertiesOfType(type)) {
    if (--budget.left < 0) break;
    const decl = p.valueDeclaration ?? p.declarations?.[0];
    if (decl && (ts.isMethodDeclaration(decl) || ts.isMethodSignature(decl))) continue;
    const pt = pass.safe(() => checker.getTypeOfSymbolAtLocation(p, decl ?? pass.sf));
    if (pt?.getCallSignatures?.().length) continue;
    result[p.name] = sampleOfType(pass, pt, p.name, depth + 1, seen, budget);
  }
  seen.delete(type);
  return result;
}

function typeOfNode(pass, typeNode) {
  return typeNode ? pass.safe(() => pass.checker.getTypeFromTypeNode(typeNode)) : undefined;
}

function isAny(pass, type) {
  return !type || (type.flags & (pass.ts.TypeFlags.Any | pass.ts.TypeFlags.Unknown)) !== 0;
}

function typeName(pass, type, fallback = 'string') {
  if (!type || isAny(pass, type)) return fallback;
  return pass.checker.typeToString(type);
}

class Shape {
  constructor() {
    this.params = [];
    this.body = new Map();
    this.bodyType = null;
    this.bodySample = null;
    this.usesBody = false;
  }

  add(name, where, type, sample) {
    if (!name || this.params.some((p) => p.name === name && p.in === where)) return;
    this.params.push({ name, in: where, type, sample: JSON.stringify(sample) });
  }

  done(verb) {
    if (!this.bodySample && this.body.size) this.bodySample = JSON.stringify(Object.fromEntries(this.body), null, 2);
    if (!this.bodySample && this.usesBody && /POST|PUT|PATCH|ANY/.test(verb)) this.bodySample = '{}';
    return { params: this.params, bodyType: this.bodyType, bodySample: this.bodySample };
  }
}

// -------------------------------------------------------------------- Express and its kind

/** From the route and the handler: what it reads from the request (req.body.x, req.query.page, ...). */
export function requestOf(pass, fn, pattern, verb, call) {
  const ts = pass.ts;
  const shape = new Shape();
  for (const name of routeNames(pattern)) shape.add(name, 'route', 'string', routeSample(name));

  // fastify.post<{ Body: CreateOrder; Querystring: Page }>(...)
  if (call && ts.isCallExpression(call) && call.typeArguments?.length) {
    const generic = typeOfNode(pass, call.typeArguments[0]);
    for (const [key, where] of [['Body', 'body'], ['Querystring', 'query'], ['Params', 'route'], ['Headers', 'header']]) {
      const p = generic && pass.checker.getPropertyOfType(generic, key);
      if (!p) continue;
      const t = pass.checker.getTypeOfSymbolAtLocation(p, call);
      if (where === 'body') {
        shape.bodyType = pass.checker.typeToString(t);
        shape.bodySample = JSON.stringify(sampleOfType(pass, t, 'body'), null, 2);
      } else {
        for (const q of pass.checker.getPropertiesOfType(t)) {
          const qt = pass.checker.getTypeOfSymbolAtLocation(q, call);
          shape.add(q.name, where, typeName(pass, qt), sampleOfType(pass, qt, q.name));
        }
      }
    }
  }

  const first = fn?.parameters?.[0];
  if (first) {
    // Express: (req: Request<Params, ResBody, ReqBody, Query>)
    if (first.type && ts.isTypeReferenceNode(first.type) && (first.type.typeArguments?.length ?? 0) >= 3) {
      const body = typeOfNode(pass, first.type.typeArguments[2]);
      if (body && !isAny(pass, body)) {
        shape.bodyType = first.type.typeArguments[2].getText();
        shape.bodySample = JSON.stringify(sampleOfType(pass, body, 'body'), null, 2);
      }
      const query = first.type.typeArguments[3] ? typeOfNode(pass, first.type.typeArguments[3]) : null;
      for (const q of query && !isAny(pass, query) ? pass.checker.getPropertiesOfType(query) : []) {
        const qt = pass.checker.getTypeOfSymbolAtLocation(q, first);
        shape.add(q.name, 'query', typeName(pass, qt), sampleOfType(pass, qt, q.name));
      }
    }
    if (ts.isIdentifier(first.name) && fn.body) readsOf(pass, fn.body, first.name.text, shape);
  }
  return shape.done(verb);
}

function chainOf(ts, node) {
  const parts = [];
  let n = unwrap(ts, node);
  while (n) {
    if (ts.isPropertyAccessExpression(n)) { parts.unshift(n.name.text); n = unwrap(ts, n.expression); }
    else if (ts.isElementAccessExpression(n) && ts.isStringLiteral(n.argumentExpression)) { parts.unshift(n.argumentExpression.text); n = unwrap(ts, n.expression); }
    else if (ts.isIdentifier(n)) { parts.unshift(n.text); break; }
    else return [];
  }
  return parts;
}

/** req.body.x, req.query.x, req.params.x, req.headers.x, req.get('x'), const { a } = req.body; ctx.request.body (Koa); c.req.query('x') (Hono). */
function readsOf(pass, body, req, shape) {
  const ts = pass.ts;
  const place = (chain) => {
    if (chain[0] !== req) return null;
    const rest = chain[1] === 'request' ? chain.slice(2) : chain.slice(1);
    const where = { body: 'body', query: 'query', params: 'route', headers: 'header' }[rest[0]];
    return where ? { where, field: rest[1] } : null;
  };
  const note = (where, field) => {
    if (!field) {
      if (where === 'body') shape.usesBody = true;
      return;
    }
    if (where === 'body') { shape.usesBody = true; if (!shape.bodySample && !shape.body.has(field)) shape.body.set(field, guess(field)); }
    else shape.add(field, where, 'string', where === 'route' ? routeSample(field) : String(guess(field)));
  };

  const visit = (n) => {
    if (ts.isPropertyAccessExpression(n) || ts.isElementAccessExpression(n)) {
      const hit = place(chainOf(ts, n));
      if (hit) note(hit.where, hit.field);
      if (hit) return;                                  // the outer access says it all
    }
    if (ts.isVariableDeclaration(n) && ts.isObjectBindingPattern(n.name) && n.initializer) {
      const hit = place(chainOf(ts, n.initializer));
      if (hit && !hit.field) for (const e of n.name.elements) note(hit.where, memberName(ts, e.propertyName ?? e.name));
    }
    if (ts.isCallExpression(n) && ts.isPropertyAccessExpression(n.expression)) {
      const chain = chainOf(ts, n.expression);
      const arg = n.arguments[0] && ts.isStringLiteral(n.arguments[0]) ? n.arguments[0].text : null;
      if (chain[0] === req && (chain[1] === 'get' || chain[1] === 'header') && arg) shape.add(arg, 'header', 'string', 'value');
      if (chain[0] === req && chain[1] === 'req') {                 // Hono: c.req.query('page'), c.req.param('id'), c.req.json()
        if (chain[2] === 'query' && arg) shape.add(arg, 'query', 'string', String(guess(arg)));
        if (chain[2] === 'header' && arg) shape.add(arg, 'header', 'string', 'value');
        if (chain[2] === 'json' || chain[2] === 'parseBody') shape.usesBody = true;
      }
      if (chain[0] === req && chain[1] === 'json') shape.usesBody = true;   // Next.js: await request.json()
    }
    ts.forEachChild(n, visit);
  };
  visit(body);
}

// -------------------------------------------------------------------- NestJS

function decoratorsOf(ts, node) {
  return (ts.canHaveDecorators(node) ? ts.getDecorators(node) ?? [] : []).map((d) => {
    const e = d.expression;
    const call = ts.isCallExpression(e) ? e : null;
    const callee = call ? call.expression : e;
    return { name: ts.isIdentifier(callee) ? callee.text : ts.isPropertyAccessExpression(callee) ? callee.name.text : '', args: call ? [...call.arguments] : [] };
  });
}

/** @Param('id') id, @Query('page') page, @Query() filter: Filter, @Body() order: CreateOrder, @Headers('x-key') key. */
export function nestRequest(pass, method, route, verb) {
  const ts = pass.ts;
  const shape = new Shape();
  for (const name of routeNames(route)) shape.add(name, 'route', 'string', routeSample(name));
  for (const p of method.parameters) {
    const type = pass.safe(() => pass.checker.getTypeAtLocation(p));
    const paramName = ts.isIdentifier(p.name) ? p.name.text : 'value';
    for (const d of decoratorsOf(ts, p)) {
      const key = d.args[0] && ts.isStringLiteral(d.args[0]) ? d.args[0].text : null;
      if (d.name === 'Param' && key) shape.add(key, 'route', typeName(pass, type), routeSample(key));
      else if (d.name === 'Query' && key) shape.add(key, 'query', typeName(pass, type), sampleOfType(pass, type, key));
      else if (d.name === 'Query' && type && !isAny(pass, type)) {
        for (const q of pass.checker.getPropertiesOfType(type)) {
          const qt = pass.checker.getTypeOfSymbolAtLocation(q, p);
          shape.add(q.name, 'query', typeName(pass, qt), sampleOfType(pass, qt, q.name));
        }
      } else if (d.name === 'Headers' && key) shape.add(key, 'header', 'string', 'value');
      else if (d.name === 'Body' && key) { shape.usesBody = true; shape.body.set(key, sampleOfType(pass, type, key)); }
      else if (d.name === 'Body') {
        shape.usesBody = true;
        if (type && !isAny(pass, type)) {
          shape.bodyType = p.type ? p.type.getText() : pass.checker.typeToString(type);
          shape.bodySample = JSON.stringify(sampleOfType(pass, type, paramName), null, 2);
        }
      }
    }
  }
  return shape.done(verb);
}
