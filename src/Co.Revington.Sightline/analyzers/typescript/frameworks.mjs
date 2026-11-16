// What frameworks make of the code: endpoints (Express and its kind, Fastify, Koa, Hono, NestJS, Next.js),
// dependency injection (NestJS), database access and outbound HTTP calls.
//
// Framework APIs are recognised by where a value comes from: the import or require it traces back to,
// through variables, parameters with a type, class fields and the functions that return it. That works
// without node_modules, so a fresh clone gets its endpoints too.

import path from 'node:path';
import { unwrap, isFunctionLike, wrappedFunction, memberName, isRequire, isModuleExports } from './extract.mjs';
import { requestOf, nestRequest, routeNames } from './samples.mjs';

const VERBS = { get: 'GET', post: 'POST', put: 'PUT', delete: 'DELETE', del: 'DELETE', patch: 'PATCH', options: 'OPTIONS', head: 'HEAD', all: 'ANY' };

/** Packages whose objects take routes, and the style their endpoints are listed under. */
const ROUTERS = {
  express: 'express', router: 'express', polka: 'express', restify: 'express', 'express-promise-router': 'express', 'express-async-router': 'express',
  fastify: 'fastify', koa: 'koa', '@koa/router': 'koa', 'koa-router': 'koa', hono: 'hono', elysia: 'elysia', h3: 'h3',
};

const DATABASES = new Set([
  '@prisma/client', 'typeorm', 'mongoose', 'sequelize', 'sequelize-typescript', 'knex', 'drizzle-orm', 'pg', 'postgres', 'mysql', 'mysql2',
  'better-sqlite3', 'sqlite3', 'sqlite', 'mongodb', '@mikro-orm/core', 'objection', 'kysely', '@neondatabase/serverless', '@vercel/postgres',
  '@planetscale/database', 'mssql', 'oracledb', 'redis', 'ioredis', '@supabase/supabase-js', 'firebase-admin', '@google-cloud/firestore',
  '@aws-sdk/client-dynamodb', '@aws-sdk/lib-dynamodb', 'dynamoose', 'mongodb-memory-server', '@nestjs/typeorm', '@nestjs/mongoose',
]);

const HTTP_CLIENTS = new Set(['axios', 'got', 'ky', 'node-fetch', 'undici', 'cross-fetch', 'isomorphic-fetch', 'ofetch', 'superagent', 'needle',
  'http', 'https', '@angular/common/http', 'jquery', 'request', 'request-promise', 'wretch']);

/** Calls on a database object that do not touch data. */
const NOT_DATA = new Set(['connect', 'disconnect', 'end', 'close', 'on', 'once', 'off', 'release', 'initialize', 'destroy', 'sync', 'authenticate',
  '$connect', '$disconnect', '$on', '$use', '$extends', 'use', 'pipe', 'emit', 'quit', 'duplicate', 'model', 'Schema', 'set', 'get', 'createClient']);

export function rootPackage(spec) {
  const s = String(spec).replace(/^node:/, '');
  const parts = s.split('/');
  return s.startsWith('@') ? parts.slice(0, 2).join('/') : parts[0];
}

function isString(ts, node) {
  const n = unwrap(ts, node);
  return !!n && (ts.isStringLiteral(n) || ts.isNoSubstitutionTemplateLiteral(n));
}

function stringOf(ts, node) {
  const n = unwrap(ts, node);
  return n && (ts.isStringLiteral(n) || ts.isNoSubstitutionTemplateLiteral(n)) ? n.text : null;
}

function prop(ts, obj, name) {
  const o = unwrap(ts, obj);
  if (!o || !ts.isObjectLiteralExpression(o)) return null;
  for (const p of o.properties) {
    if (memberName(ts, p.name) !== name) continue;
    if (ts.isPropertyAssignment(p)) return p.initializer;
    if (ts.isShorthandPropertyAssignment(p)) return p.name;
    if (ts.isMethodDeclaration(p)) return p;
  }
  return null;
}

// -------------------------------------------------------------------- where values come from

/** { module, name } of the package a value comes from, or null when it is the code's own or unknown. */
export function origin(pass, node, depth = 0) {
  const ts = pass.ts;
  const n = unwrap(ts, node);
  if (!n || depth > 10) return null;
  if (pass.origins.has(n)) return pass.origins.get(n);
  pass.origins.set(n, null);                                          // a loop ends here
  const found = originOf(pass, n, depth) ?? originFromType(pass, n);
  pass.origins.set(n, found);
  return found;
}

function originOf(pass, n, depth) {
  const ts = pass.ts;
  if (ts.isIdentifier(n)) {
    const sym = pass.symbolAt(n);
    if (!sym) return null;
    return originOfSymbol(pass, sym, depth);
  }
  if (ts.isCallExpression(n)) {
    if (isRequire(ts, n)) return { module: stringOf(ts, n.arguments[0]) ?? '', name: 'default' };
    const callee = unwrap(ts, n.expression);
    const fromCallee = origin(pass, callee, depth + 1);
    if (fromCallee) return fromCallee;
    // A function of the code that returns something from a package: createApp() { return express(); }
    const fn = calledFunction(pass, callee);
    return fn ? originOfReturns(pass, fn, depth + 1) : null;
  }
  if (ts.isNewExpression(n)) return origin(pass, n.expression, depth + 1);
  if (ts.isPropertyAccessExpression(n)) {
    const sym = pass.symbolAt(n.name);
    const decl = sym?.valueDeclaration ?? sym?.declarations?.[0];
    if (decl && pass.ws.fileInfo(decl.getSourceFile().fileName)) return originOfSymbol(pass, sym, depth);
    return origin(pass, n.expression, depth + 1);
  }
  if (ts.isElementAccessExpression(n)) return origin(pass, n.expression, depth + 1);
  if (ts.isTaggedTemplateExpression(n)) return origin(pass, n.tag, depth + 1);
  return null;
}

function calledFunction(pass, callee) {
  const ts = pass.ts;
  const name = ts.isPropertyAccessExpression(callee) ? callee.name : callee;
  if (!ts.isIdentifier(name)) return null;
  return pass.functionOf(name);
}

function originOfReturns(pass, fn, depth) {
  const ts = pass.ts;
  if (!fn.body) return null;
  if (!ts.isBlock(fn.body)) return origin(pass, fn.body, depth);
  let found = null;
  const visit = (n) => {
    if (found || isFunctionLike(ts, n) || ts.isFunctionDeclaration(n)) return;
    if (ts.isReturnStatement(n) && n.expression) found = origin(pass, n.expression, depth);
    ts.forEachChild(n, visit);
  };
  ts.forEachChild(fn.body, visit);
  return found;
}

function originOfSymbol(pass, sym, depth) {
  const ts = pass.ts;
  const decl = sym.declarations?.[0];
  if (!decl) return null;

  // An import: from a package, or from a file of the code, which is followed.
  if (ts.isImportSpecifier(decl) || ts.isImportClause(decl) || ts.isNamespaceImport(decl) || ts.isImportEqualsDeclaration(decl)) {
    const spec = moduleSpecifierOf(ts, decl);
    if (spec !== null && !spec.startsWith('.') && !spec.startsWith('/') && !pass.ws.localPackage(spec)) {
      const name = ts.isImportSpecifier(decl) ? (decl.propertyName ?? decl.name).text : ts.isImportClause(decl) ? 'default' : '*';
      return { module: spec, name };
    }
    const target = pass.follow(sym);
    return target && target !== sym ? originOfSymbol(pass, target, depth + 1) : null;
  }

  const file = decl.getSourceFile();
  if (!pass.ws.fileInfo(file.fileName)) {
    // Declared in a package or in the standard library: fetch, $, a global.
    const lib = /\/lib\.[^/]*\.d\.ts$/.test(file.fileName);
    return lib ? { module: 'global', name: sym.name } : packageOfFile(file.fileName, sym.name);
  }

  if (ts.isBindingElement(decl)) {
    let v = decl.parent;
    while (v && !ts.isVariableDeclaration(v) && !ts.isParameter(v)) v = v.parent;
    if (v?.initializer) {
      const from = origin(pass, v.initializer, depth + 1);
      return from ? { module: from.module, name: (decl.propertyName ?? decl.name).getText() } : null;
    }
    return v?.type ? originOfType(pass, v.type, depth + 1) : null;
  }
  if (ts.isVariableDeclaration(decl) || ts.isPropertyDeclaration(decl) || ts.isPropertySignature(decl) || ts.isParameter(decl)) {
    if (decl.type) {
      const typed = originOfType(pass, decl.type, depth + 1);
      if (typed) return typed;
    }
    if (ts.isParameter(decl)) return parameterDecorators(pass, decl);
    return decl.initializer ? origin(pass, decl.initializer, depth + 1) : null;
  }
  if (ts.isPropertyAssignment(decl)) return origin(pass, decl.initializer, depth + 1);
  if (ts.isShorthandPropertyAssignment(decl)) {
    const value = pass.safe(() => pass.checker.getShorthandAssignmentValueSymbol(decl));
    return value ? originOfSymbol(pass, value, depth + 1) : null;
  }
  if (ts.isClassDeclaration(decl) || ts.isClassExpression(decl)) {
    const base = decl.heritageClauses?.find((c) => c.token === ts.SyntaxKind.ExtendsKeyword)?.types[0];
    return base ? origin(pass, base.expression, depth + 1) : null;
  }
  if (ts.isGetAccessorDeclaration(decl) && decl.type) return originOfType(pass, decl.type, depth + 1);
  if (ts.isBinaryExpression(decl)) return origin(pass, decl.right, depth + 1);
  return null;
}

/** @InjectRepository(Order) repo, @InjectModel(Order.name) model: objects the framework hands in. */
function parameterDecorators(pass, param) {
  const ts = pass.ts;
  for (const d of ts.canHaveDecorators(param) ? ts.getDecorators(param) ?? [] : []) {
    const e = d.expression;
    const name = ts.isCallExpression(e) ? e.expression.getText() : e.getText();
    if (name === 'InjectRepository' || name === 'InjectDataSource' || name === 'InjectEntityManager') return { module: 'typeorm', name };
    if (name === 'InjectModel' || name === 'InjectConnection') return { module: 'mongoose', name };
  }
  return null;
}

function originOfType(pass, typeNode, depth) {
  const ts = pass.ts;
  if (!typeNode || depth > 10) return null;
  if (ts.isTypeReferenceNode(typeNode)) {
    const nameNode = ts.isQualifiedName(typeNode.typeName) ? leftmost(ts, typeNode.typeName) : typeNode.typeName;
    const sym = pass.symbolAt(nameNode);
    return sym ? originOfSymbol(pass, sym, depth + 1) : null;
  }
  if (ts.isUnionTypeNode(typeNode) || ts.isIntersectionTypeNode(typeNode)) {
    for (const t of typeNode.types) {
      const found = originOfType(pass, t, depth + 1);
      if (found) return found;
    }
    return null;
  }
  if (ts.isImportTypeNode(typeNode) && ts.isLiteralTypeNode(typeNode.argument) && ts.isStringLiteral(typeNode.argument.literal)) {
    return { module: typeNode.argument.literal.text, name: typeNode.qualifier?.getText() ?? '*' };
  }
  return null;
}

function leftmost(ts, name) {
  let n = name;
  while (ts.isQualifiedName(n)) n = n.left;
  return n;
}

/** With packages installed, the type of a value says which package it belongs to. */
function originFromType(pass, n) {
  const ts = pass.ts;
  if (!ts.isIdentifier(n) && !ts.isPropertyAccessExpression(n) && !ts.isCallExpression(n)) return null;
  const type = pass.safe(() => pass.checker.getTypeAtLocation(n));
  const sym = type?.aliasSymbol ?? type?.getSymbol?.();
  const decl = sym?.declarations?.[0];
  if (!decl) return null;
  const file = decl.getSourceFile().fileName;
  return pass.ws.fileInfo(file) ? null : packageOfFile(file, sym.name);
}

function packageOfFile(fileName, name) {
  const at = fileName.lastIndexOf('/node_modules/');
  if (at < 0) return null;
  const rest = fileName.slice(at + '/node_modules/'.length).split('/');
  let pkg = rest[0].startsWith('@') ? rest.slice(0, 2).join('/') : rest[0];
  if (pkg.startsWith('@types/')) pkg = pkg.slice(7).replace('__', '/').replace(/^([^/]+)\/(.+)$/, '@$1/$2');
  if (pkg === '.prisma') pkg = '@prisma/client';
  return { module: pkg, name };
}

function moduleSpecifierOf(ts, decl) {
  let n = decl;
  while (n && !ts.isImportDeclaration(n) && !ts.isImportEqualsDeclaration(n)) n = n.parent;
  if (!n) return null;
  if (ts.isImportDeclaration(n)) return ts.isStringLiteral(n.moduleSpecifier) ? n.moduleSpecifier.text : null;
  const ref = n.moduleReference;
  return ts.isExternalModuleReference(ref) && ts.isStringLiteral(ref.expression) ? ref.expression.text : null;
}

// -------------------------------------------------------------------- route builders

/** The style of the object a route is added to, or null when it is not one. */
function builderStyle(pass, recv) {
  return builder(pass, recv)?.style ?? null;
}

/**
 * { style, guessed } for an object routes are added to. Known by where it comes from; or guessed by its
 * name (app, router, ...) when nothing says where it comes from, such as a parameter without a type in
 * JavaScript, and only in a package that depends on a router.
 */
function builder(pass, recv) {
  const ts = pass.ts;
  const from = origin(pass, recv);
  if (from) {
    const style = ROUTERS[rootPackage(from.module)];
    return style ? { style, guessed: false } : null;
  }
  const style = defaultStyle(pass.project);
  if (!style || ownValue(pass, recv)) return null;
  const name = receiverName(ts, recv);
  if (/^(app|router|api|routes?|server|fastify|instance|r|v\d+)$/i.test(name) || /(Router|App|Routes|Api)$/.test(name)) return { style, guessed: true };
  return null;
}

/** A value of the code's own making: new ApiClient(), a field typed with a class of the code, an object. */
function ownValue(pass, recv) {
  const ts = pass.ts;
  const n = unwrap(ts, recv);
  const sym = pass.follow(pass.symbolAt(ts.isPropertyAccessExpression(n) ? n.name : n));
  const decl = sym?.valueDeclaration ?? sym?.declarations?.[0];
  if (!decl) return false;
  if (decl.type && ts.isTypeReferenceNode(decl.type) && pass.typeTarget(decl.type.typeName)) return true;
  const init = decl.initializer ? unwrap(ts, decl.initializer) : null;
  if (!init) return false;
  if (ts.isNewExpression(init) && pass.typeTarget(init.expression)) return true;
  return ts.isObjectLiteralExpression(init) || ts.isArrayLiteralExpression(init) || ts.isNewExpression(init) && /^(Map|Set|WeakMap|URLSearchParams|Headers|FormData)$/.test(init.expression.getText());
}

function receiverName(ts, recv) {
  const n = unwrap(ts, recv);
  if (ts.isIdentifier(n)) return n.text;
  if (ts.isPropertyAccessExpression(n)) return n.name.text;
  return '';
}

/** The router style of a package's dependencies, or null when it depends on none. */
function defaultStyle(project) {
  for (const [pkg, style] of Object.entries(ROUTERS)) if (project.deps.has(pkg)) return style;
  return null;
}

/** A key for a group of routes: the variable, field or function parameter that holds the router. */
function groupKey(pass, decl, fallback) {
  const ts = pass.ts;
  const id = pass.declId(decl);
  if (id) return id;
  const info = pass.ws.fileInfo(decl.getSourceFile().fileName);
  if (!info) return null;
  const name = decl.name && ts.isIdentifier(decl.name) ? decl.name.text : fallback ?? 'value';
  return `npm:${info.project.name}::${info.rel}~${name}@${decl.pos}`;
}

/** The key of a function, for the routes its parameters receive: its id, or its place in the file. */
function functionKey(pass, fn) {
  return pass.declId(fn) ?? groupKey(pass, fn, 'fn');
}

/**
 * The prefix a router adds to its routes, and where more of it comes from: the router itself when it is
 * mounted somewhere else (app.use('/api', router)), or the parameter it arrives in.
 */
export function builderRef(pass, expr, depth = 0) {
  const ts = pass.ts;
  const n = unwrap(ts, expr);
  const none = { prefix: '', param: null };
  if (!n || depth > 10) return none;

  if (ts.isCallExpression(n) && ts.isPropertyAccessExpression(n.expression)) {
    const method = n.expression.name.text;
    const inner = builderRef(pass, n.expression.expression, depth + 1);
    const arg = stringOf(ts, n.arguments[0]);
    if ((method === 'route' && n.arguments.length === 1) || method === 'basePath' || method === 'prefix') return { prefix: join(inner.prefix, arg), param: inner.param };
    if (method === 'routes' || VERBS[method] || method === 'use') return inner;          // app.route('/x').get(h).post(h), router.routes()
    return none;
  }

  const nameNode = ts.isPropertyAccessExpression(n) ? n.name : n;
  if (!ts.isIdentifier(nameNode) && !ts.isPrivateIdentifier(nameNode)) return none;
  const sym = pass.follow(pass.symbolAt(nameNode));
  const decl = sym?.valueDeclaration ?? sym?.declarations?.[0];
  if (!decl) return none;

  if (ts.isParameter(decl)) {
    const fn = decl.parent;
    const key = fn ? functionKey(pass, fn) : null;
    return key ? { prefix: '', param: { methodId: key, ordinal: fn.parameters.indexOf(decl) } } : none;
  }
  if (ts.isVariableDeclaration(decl) || ts.isPropertyDeclaration(decl)) {
    const init = decl.initializer ? unwrap(ts, decl.initializer) : null;
    // const api = app.basePath('/api'): the routes of api are the routes of app.
    if (init && ts.isCallExpression(init) && ts.isPropertyAccessExpression(init.expression)
      && ['basePath', 'route'].includes(init.expression.name.text) && init.arguments.length === 1) return builderRef(pass, init, depth + 1);
    const key = groupKey(pass, decl);
    return key ? { prefix: ownPrefix(pass, init), param: { methodId: key, ordinal: 0 } } : none;
  }
  if (ts.isSourceFile(decl) || ts.isBinaryExpression(decl) || ts.isExportAssignment(decl)) {
    const exported = exportedValue(pass, ts.isSourceFile(decl) ? decl : decl.getSourceFile());
    return exported ? builderRef(pass, exported, depth + 1) : none;
  }
  return none;
}

/** new Router({ prefix: '/api' }): a prefix the router gives itself. */
function ownPrefix(pass, init) {
  const ts = pass.ts;
  if (!init || !ts.isNewExpression(init) && !ts.isCallExpression(init)) return '';
  const options = init.arguments?.[0];
  return stringOf(ts, prop(ts, options, 'prefix')) ?? '';
}

/** What a CommonJS or ES module exports as a whole: module.exports = x, export = x, export default x. */
function exportedValue(pass, sf) {
  const ts = pass.ts;
  for (const s of sf.statements) {
    if (ts.isExportAssignment(s)) return s.expression;
    if (ts.isExpressionStatement(s) && ts.isBinaryExpression(s.expression) && isModuleExports(ts, s.expression.left)) return s.expression.right;
  }
  return null;
}

function join(...parts) {
  const segments = [];
  for (const p of parts) {
    if (!p) continue;
    const t = String(p).trim().replace(/^\/+|\/+$/g, '');
    if (t) segments.push(t);
  }
  return segments.length ? '/' + segments.join('/') : '';
}

// -------------------------------------------------------------------- endpoints

function pathOf(pass, node) {
  const ts = pass.ts;
  const n = unwrap(ts, node);
  if (!n) return null;
  if (ts.isStringLiteral(n) || ts.isNoSubstitutionTemplateLiteral(n)) return n.text;
  if (ts.isArrayLiteralExpression(n) && n.elements.length) return pathOf(pass, n.elements[0]);
  if (ts.isRegularExpressionLiteral(n)) return n.text;
  if (ts.isIdentifier(n)) {
    const value = constantText(pass, n);
    return value;
  }
  return null;
}

/** The text of a constant: const ORDERS = '/orders'. */
function constantText(pass, id, depth = 0) {
  const ts = pass.ts;
  if (depth > 4) return null;
  const sym = pass.follow(pass.symbolAt(id));
  const decl = sym?.valueDeclaration;
  if (decl && ts.isVariableDeclaration(decl) && decl.initializer) {
    const init = unwrap(ts, decl.initializer);
    if (ts.isStringLiteral(init) || ts.isNoSubstitutionTemplateLiteral(init)) return init.text;
    if (ts.isIdentifier(init)) return constantText(pass, init, depth + 1);
  }
  return null;
}

/** What a request carries, or nothing when working it out fails: one odd endpoint must not cost the whole file. */
function shapeOf(work) {
  try {
    return work();
  } catch (err) {
    console.error('A request shape could not be worked out: ' + String(err?.message ?? err));
    return null;
  }
}

function isHandler(pass, node) {
  const ts = pass.ts;
  const n = unwrap(ts, node);
  return !!n && (isFunctionLike(ts, n) || ts.isIdentifier(n) || ts.isPropertyAccessExpression(n) || ts.isCallExpression(n) || ts.isArrayLiteralExpression(n));
}

/** A function written there, or a name that refers to one. */
function isFunctionHandler(pass, node) {
  const ts = pass.ts;
  const n = unwrap(ts, node);
  if (!n) return false;
  if (isFunctionLike(ts, n)) return true;
  if (ts.isIdentifier(n) || ts.isPropertyAccessExpression(n)) return !!pass.functionOf(ts.isPropertyAccessExpression(n) ? n.name : n);
  return ts.isCallExpression(n) && n.arguments.some((a) => isFunctionHandler(pass, a));
}

const AUTH = /auth|jwt|passport|protect|require(User|Login|Session)|ensure(Logged|Auth)|logged.?in|verify.?token|guard|session/i;

/** Middleware that checks who is calling: auth, jwt, passport.authenticate(...), but not auth.optional. */
const requiresAuth = (node) => {
  const text = node.getText();
  return AUTH.test(text) && !/optional|maybe|try|public|anonymous/i.test(text);
};

/** router.get('/orders/:id', auth, handler), app.post(...), fastify.get(...), app.route('/x').get(...), hono.on('GET', ...). */
export function tryEndpoint(pass, call) {
  const ts = pass.ts;
  const callee = call.expression;
  if (!ts.isPropertyAccessExpression(callee)) return false;
  const method = callee.name.text;
  const args = call.arguments;

  if (method === 'route' && args.length === 1 && ts.isObjectLiteralExpression(unwrap(ts, args[0]))) return routeObject(pass, call, callee.expression, unwrap(ts, args[0]));

  let verb = VERBS[method];
  let pathIndex = 0;
  if (method === 'on' && args.length >= 3 && isString(ts, args[0])) {
    verb = String(stringOf(ts, args[0])).toUpperCase();
    pathIndex = 1;
  }
  if (!verb) return false;

  let routeName = null;
  if (method !== 'on' && args.length >= 3 && isString(ts, args[0]) && isString(ts, args[1]) && String(stringOf(ts, args[1])).startsWith('/')) {
    routeName = stringOf(ts, args[0]);                                // koa-router: router.get('user', '/users/:id', ...)
    pathIndex = 1;
  }

  const chain = routeChain(pass, callee.expression);
  let pattern;
  let handlers;
  if (chain && !(args[pathIndex] && pathOf(pass, args[pathIndex]) !== null)) {
    pattern = '';
    handlers = [...args];
  } else {
    if (args.length < pathIndex + 2) return false;
    pattern = pathOf(pass, args[pathIndex]);
    if (pattern === null || !(pattern === '' || pattern.startsWith('/') || pattern === '*' || pattern.startsWith('^'))) return false;
    handlers = args.slice(pathIndex + 1);
  }
  if (handlers.length === 0 || !isHandler(pass, handlers[handlers.length - 1])) return false;
  const recv = chain ? chain.receiver : callee.expression;
  const found = builder(pass, recv);
  if (!found) return false;
  // A router guessed by its name must get a function, not data: api.post('/orders', order) sends a request.
  if (found.guessed && !isFunctionHandler(pass, handlers[handlers.length - 1])) return false;

  const ref = builderRef(pass, recv);
  addEndpoint(pass, call, {
    verb, pattern, prefix: join(ref.prefix, chain?.prefix), param: ref.param, style: found.style, routeName,
    handler: handlers[handlers.length - 1], middlewares: handlers.slice(0, -1), visit: [recv],
  });
  return true;
}

/** app.route('/book') in app.route('/book').get(h).post(h): the prefix and the router. */
function routeChain(pass, expr) {
  const ts = pass.ts;
  const n = unwrap(ts, expr);
  if (!n || !ts.isCallExpression(n) || !ts.isPropertyAccessExpression(n.expression)) return null;
  const method = n.expression.name.text;
  if (method === 'route' && n.arguments.length === 1 && isString(ts, n.arguments[0])) return { prefix: stringOf(ts, n.arguments[0]), receiver: n.expression.expression };
  if (VERBS[method]) return routeChain(pass, n.expression.expression);
  return null;
}

/** fastify.route({ method, url, handler }) and server.route({ method, path, handler }). */
function routeObject(pass, call, recv, obj) {
  const ts = pass.ts;
  const style = builderStyle(pass, recv);
  const url = stringOf(ts, prop(ts, obj, 'url')) ?? stringOf(ts, prop(ts, obj, 'path'));
  const handler = prop(ts, obj, 'handler');
  if (!style || url === null || !handler) return false;
  const methods = unwrap(ts, prop(ts, obj, 'method'));
  const verbs = methods && ts.isArrayLiteralExpression(methods) ? methods.elements.map((e) => stringOf(ts, e)).filter(Boolean) : [stringOf(ts, methods) ?? 'ANY'];
  const ref = builderRef(pass, recv);
  const auth = [prop(ts, obj, 'preHandler'), prop(ts, obj, 'onRequest'), prop(ts, obj, 'auth')].some((m) => m && requiresAuth(m));
  addEndpoint(pass, call, {
    verb: verbs.map((v) => v.toUpperCase()).join(','), pattern: url, prefix: ref.prefix, param: ref.param, style, routeName: null,
    handler: ts.isMethodDeclaration(handler) ? handler : handler, middlewares: [], visit: [recv], auth,
  });
  return true;
}

function addEndpoint(pass, call, e) {
  const ts = pass.ts;
  const keyBase = `${pass.owner}#${e.verb}#${e.pattern}`;
  const ordinal = pass.nextOrdinal(keyBase);
  const key = ordinal === 0 ? keyBase : `${keyBase}#${ordinal}`;
  const declaredIn = pass.owner;
  for (const v of e.visit) pass.walk(v);
  for (const m of e.middlewares) pass.walk(m);

  let handlerId = null;
  let fn = null;
  const h = unwrap(ts, e.handler);
  if (ts.isMethodDeclaration(h) || isFunctionLike(ts, h)) {
    fn = h;
    pass.withOwner(key, () => pass.walk(h.body));
  } else {
    const found = handlerTarget(pass, h);
    if (found.id) {
      handlerId = found.id;
      fn = found.fn;
    } else if (found.inline) {
      fn = found.inline;
      pass.withOwner(key, () => pass.walk(found.inline.body));
    } else {
      pass.walk(h);
    }
  }

  const auth = e.auth || e.middlewares.some(requiresAuth);
  pass.facts.endpoints.push({
    key, verb: e.verb, pattern: e.pattern, prefix: e.prefix ?? '', param: e.param ?? null, style: e.style, handlerId, targetTypeId: null,
    owner: declaredIn, auth, routeName: e.routeName ?? null, line: pass.line(call), endLine: pass.endLine(call),
    request: shapeOf(() => requestOf(pass, fn, e.pattern, e.verb, call)),
  });
}

/** The function behind a handler: orders.list, listOrders, asyncHandler(listOrders), asyncHandler(async (req, res) => ...). */
function handlerTarget(pass, h) {
  const ts = pass.ts;
  if (ts.isIdentifier(h) || ts.isPropertyAccessExpression(h)) {
    const name = ts.isPropertyAccessExpression(h) ? h.name : h;
    return { id: pass.targetOf(name), fn: pass.functionOf(name) };
  }
  if (ts.isCallExpression(h)) {
    for (let i = h.arguments.length - 1; i >= 0; i--) {
      const a = unwrap(ts, h.arguments[i]);
      if (isFunctionLike(ts, a)) {
        for (const other of h.arguments) if (other !== h.arguments[i]) pass.walk(other);
        return { inline: a };
      }
      if (ts.isIdentifier(a) || ts.isPropertyAccessExpression(a)) {
        const found = handlerTarget(pass, a);
        if (found.id) return found;
      }
    }
  }
  if (ts.isArrayLiteralExpression(h) && h.elements.length) return handlerTarget(pass, unwrap(ts, h.elements[h.elements.length - 1]));
  return {};
}

/** app.use('/api', router), parent.use('/p', child.routes()), fastify.register(plugin, { prefix }), hono.route('/x', sub), app.setGlobalPrefix('api'). */
export function tryMount(pass, call) {
  const ts = pass.ts;
  const callee = call.expression;
  if (!ts.isPropertyAccessExpression(callee)) return;
  const method = callee.name.text;
  const args = call.arguments;

  if (method === 'setGlobalPrefix' && isString(ts, args[0])) {
    pass.facts.groupPasses.push({ methodId: `nest-global:${pass.project.id}`, ordinal: 0, prefix: join(stringOf(ts, args[0])), param: null });
    return;
  }

  const recv = callee.expression;
  if (method === 'use' && args.length > 0) {
    if (!builderStyle(pass, recv)) return;
    const outer = builderRef(pass, recv);
    const prefix = isString(ts, args[0]) ? stringOf(ts, args[0]) : '';
    for (const a of isString(ts, args[0]) ? args.slice(1) : args) mount(pass, a, join(outer.prefix, prefix), outer.param);
    return;
  }
  if (method === 'route' && args.length === 2 && isString(ts, args[0])) {
    if (!builderStyle(pass, recv)) return;
    const outer = builderRef(pass, recv);
    mount(pass, args[1], join(outer.prefix, stringOf(ts, args[0])), outer.param);
    return;
  }
  if (method === 'register' && args.length > 0) {
    if (!builderStyle(pass, recv)) return;
    const outer = builderRef(pass, recv);
    const prefix = stringOf(ts, prop(ts, args[1], 'prefix')) ?? '';
    const plugin = unwrap(ts, args[0]);
    let key = null;
    if (isFunctionLike(ts, plugin)) key = functionKey(pass, plugin);
    else if (ts.isIdentifier(plugin) || ts.isPropertyAccessExpression(plugin)) {
      const fn = pass.functionOf(ts.isPropertyAccessExpression(plugin) ? plugin.name : plugin);
      key = fn ? functionKey(pass, fn) : pass.targetOf(ts.isPropertyAccessExpression(plugin) ? plugin.name : plugin);
    }
    if (key) pass.facts.groupPasses.push({ methodId: key, ordinal: 0, prefix: join(outer.prefix, prefix), param: outer.param });
  }
}

function mount(pass, arg, prefix, param) {
  const ts = pass.ts;
  const a = unwrap(ts, arg);
  if (!a) return;
  if (ts.isArrayLiteralExpression(a)) {
    for (const e of a.elements) mount(pass, e, prefix, param);
    return;
  }
  const target = ts.isCallExpression(a) && ts.isPropertyAccessExpression(a.expression) && a.expression.name.text === 'routes' ? a.expression.expression : a;
  if (!ts.isIdentifier(target) && !ts.isPropertyAccessExpression(target) && !ts.isCallExpression(target)) return;
  const ref = builderRef(pass, target);
  if (ref.param) pass.facts.groupPasses.push({ methodId: ref.param.methodId, ordinal: ref.param.ordinal, prefix: join(prefix), param: param ?? null });
}

/** registerOrderRoutes(app): the routes that function adds get the prefix app has. */
export function groupPassesForCall(pass, call, targetId) {
  const ts = pass.ts;
  call.arguments.forEach((arg, i) => {
    const a = unwrap(ts, arg);
    if (!ts.isIdentifier(a) && !ts.isPropertyAccessExpression(a)) return;
    if (!builderStyle(pass, a)) return;
    const ref = builderRef(pass, a);
    pass.facts.groupPasses.push({ methodId: targetId, ordinal: i, prefix: ref.prefix, param: ref.param });
  });
}

// Next.js: a route.ts file under app/ exports GET, POST, ...; a file under pages/api/ exports a default handler.
export function moduleMemberRoutes(pass, node, id, name, fn) {
  if (!pass.project.deps.has('next')) return;
  const rel = pass.file.rel;
  const app = /^(?:src\/)?app\/(.*?)\/?route\.(?:m?[jt]sx?)$/.exec(rel) ?? (/^(?:src\/)?app\/route\.(?:m?[jt]sx?)$/.test(rel) ? [rel, ''] : null);
  const pages = /^(?:src\/)?pages\/(api\/.*)\.(?:m?[jt]sx?)$/.exec(rel);
  let route;
  let verb;
  if (app && /^(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)$/.test(name)) {
    route = '/' + app[1].split('/').filter((s) => s && !/^\(.*\)$/.test(s) && !s.startsWith('@')).join('/');
    verb = name;
  } else if (pages && name === 'default') {
    route = '/' + pages[1].replace(/\/index$/, '');
    verb = 'ANY';
  } else {
    return;
  }
  const key = `${id}#${verb}#${route}`;
  pass.facts.endpoints.push({
    key, verb, pattern: route, prefix: '', param: null, style: 'next', handlerId: id, targetTypeId: null, owner: id,
    auth: false, routeName: null, line: pass.line(node), endLine: pass.endLine(node), request: shapeOf(() => requestOf(pass, fn, route, verb, node)),
  });
}

// -------------------------------------------------------------------- NestJS

function decorators(pass, node) {
  const ts = pass.ts;
  const list = [];
  for (const d of ts.canHaveDecorators(node) ? ts.getDecorators(node) ?? [] : []) {
    const e = d.expression;
    const call = ts.isCallExpression(e) ? e : null;
    const callee = call ? call.expression : e;
    const name = ts.isPropertyAccessExpression(callee) ? callee.name.text : ts.isIdentifier(callee) ? callee.text : null;
    if (name) list.push({ name, args: call ? [...call.arguments] : [], node: d });
  }
  return list;
}

const NEST_VERBS = { Get: 'GET', Post: 'POST', Put: 'PUT', Delete: 'DELETE', Patch: 'PATCH', Options: 'OPTIONS', Head: 'HEAD', All: 'ANY', Search: 'SEARCH' };
const HOOKS = new Set(['onModuleInit', 'onApplicationBootstrap', 'onModuleDestroy', 'beforeApplicationShutdown', 'onApplicationShutdown']);
const SCHEDULED = new Set(['Cron', 'Interval', 'Timeout', 'OnEvent', 'Process', 'OnQueueActive', 'OnWorkerEvent', 'MessagePattern', 'EventPattern']);

export function classTags(pass, node, baseName, externalInterfaces) {
  const ts = pass.ts;
  const tags = new Set();
  const names = decorators(pass, node).map((d) => d.name);
  if (names.includes('Controller')) tags.add('controller');
  if (names.includes('WebSocketGateway')) tags.add('hub');
  if (names.includes('CommandHandler') || names.includes('QueryHandler') || names.includes('EventsHandler')) tags.add('handler');
  if (names.includes('Module')) tags.add('module');
  if (names.includes('Entity') || names.includes('Schema')) tags.add('entity');
  if (externalInterfaces.includes('NestMiddleware')) tags.add('middleware');
  if (externalInterfaces.some((i) => /^(OnModuleInit|OnApplicationBootstrap|OnModuleDestroy)$/.test(i))
    || node.members.some((m) => HOOKS.has(memberName(ts, m.name)) || decorators(pass, m).some((d) => SCHEDULED.has(d.name)))) tags.add('hosted');
  if (baseName && /(^|\b)(Error|Exception|HttpException)$/.test(baseName)) tags.add('exception');
  if (ts.canHaveModifiers(node) && ts.getModifiers(node)?.some((m) => m.kind === ts.SyntaxKind.AbstractKeyword)) tags.add('abstract');
  return [...tags];
}

/** NestJS creates a class with what its constructor asks for: classes by type, other values by @Inject(token). */
export function constructorDependencies(pass, classId, ctor) {
  const ts = pass.ts;
  for (const p of ctor.parameters) {
    const decs = decorators(pass, p);
    if (decs.some((d) => /^Inject(Repository|Model|DataSource|EntityManager|Connection|Queue|Redis)$/.test(d.name))) continue;
    const inject = decs.find((d) => d.name === 'Inject');
    let service = null;
    let wrapper = '';
    if (inject && inject.args[0]) {
      let arg = unwrap(ts, inject.args[0]);
      if (ts.isCallExpression(arg) && ts.isIdentifier(arg.expression) && arg.expression.text === 'forwardRef') {
        wrapper = 'lazy';
        const fn = unwrap(ts, arg.arguments[0]);
        arg = fn && isFunctionLike(ts, fn) && !ts.isBlock(fn.body) ? unwrap(ts, fn.body) : arg;
      }
      service = tokenOf(pass, arg);
    } else if (p.type && ts.isTypeReferenceNode(p.type)) {
      const id = pass.typeTarget(p.type.typeName);
      if (id) service = { id, name: p.type.typeName.getText().split('.').pop() };
    }
    if (!service) continue;
    const optional = !!p.questionToken || !!p.initializer || decs.some((d) => d.name === 'Optional');
    pass.facts.dependencies.push({ consumer: classId, serviceId: service.id, serviceName: service.name, wrapper, optional, line: pass.line(p) });
  }
}

/** A provider token: a class of the map, or a name (a string or a constant). */
function tokenOf(pass, expr) {
  const ts = pass.ts;
  const e = unwrap(ts, expr);
  if (!e) return null;
  if (ts.isIdentifier(e) || ts.isPropertyAccessExpression(e)) {
    const id = pass.typeTarget(e);
    if (id) return { id, name: e.getText().split('.').pop() };
    const text = e.getText();
    return { id: `token:${text}`, name: text };
  }
  if (ts.isStringLiteral(e) || ts.isNoSubstitutionTemplateLiteral(e)) return { id: `token:${e.text}`, name: e.text };
  return null;
}

function scopeOf(pass, classDecl) {
  const ts = pass.ts;
  for (const d of decorators(pass, classDecl)) {
    if (d.name !== 'Injectable' && d.name !== 'Controller') continue;
    const scope = d.args.map((a) => prop(ts, a, 'scope')).find(Boolean);
    if (scope) return lifetimeOf(scope.getText());
  }
  return 'singleton';
}

function lifetimeOf(text) {
  return /REQUEST/.test(text) ? 'request' : /TRANSIENT/.test(text) ? 'transient' : 'singleton';
}

function classDeclOf(pass, expr) {
  const ts = pass.ts;
  const e = unwrap(ts, expr);
  const sym = pass.follow(pass.symbolAt(ts.isPropertyAccessExpression(e) ? e.name : e));
  return sym?.declarations?.find((d) => ts.isClassDeclaration(d) || ts.isClassExpression(d)) ?? null;
}

export function classFrameworks(pass, node, classId, name) {
  const ts = pass.ts;
  const decs = decorators(pass, node);

  // @Module({ providers: [...] }): registrations.
  const mod = decs.find((d) => d.name === 'Module');
  if (mod) {
    const providers = unwrap(ts, prop(ts, mod.args[0], 'providers'));
    for (const el of arrayItems(pass, providers)) provider(pass, el, classId);
  }

  // @Controller('orders') with @Get(':id') methods: endpoints.
  const controller = decs.find((d) => d.name === 'Controller');
  if (controller) {
    const arg = controller.args[0];
    const base = pathOf(pass, arg) ?? stringOf(ts, prop(ts, arg, 'path')) ?? '';
    const classAuth = decs.some((d) => d.name === 'UseGuards' || /^Auth/.test(d.name));
    for (const m of node.members) {
      if (!ts.isMethodDeclaration(m) || !m.body) continue;
      const mdecs = decorators(pass, m);
      const memberId = `${classId}.${memberName(ts, m.name)}`;
      for (const d of mdecs) {
        const verb = NEST_VERBS[d.name];
        if (!verb) continue;
        const route = d.args.length ? pathOf(pass, d.args[0]) ?? '' : '';
        const auth = (classAuth || mdecs.some((x) => x.name === 'UseGuards' || /^Auth/.test(x.name))) && !mdecs.some((x) => /^(Public|AllowAnonymous|SkipAuth)$/.test(x.name));
        pass.facts.endpoints.push({
          key: `${memberId}#${verb}#${route}`, verb, pattern: route, prefix: base, param: { methodId: `nest-global:${pass.project.id}`, ordinal: 0 },
          style: 'nest', handlerId: memberId, targetTypeId: null, owner: memberId, auth, routeName: null,
          line: pass.line(m), endLine: pass.endLine(m), request: shapeOf(() => nestRequest(pass, m, join(base, route), verb)),
        });
      }
    }
  }

  // @CommandHandler(PlaceOrder): the message goes to execute.
  for (const d of decs) {
    if (!/^(CommandHandler|QueryHandler|EventsHandler)$/.test(d.name)) continue;
    const method = d.name === 'EventsHandler' ? 'handle' : 'execute';
    for (const arg of d.args) {
      const message = pass.typeTarget(unwrap(ts, arg));
      if (message) pass.facts.handlers.push({ messageTypeId: message, handlerMemberId: `${classId}.${method}`, interface: d.name });
    }
  }

  // What reading the code does not turn into endpoints.
  if (decs.some((d) => d.name === 'Resolver')) coverage(pass, 'endpoints', 'GraphQL resolvers are not listed as endpoints', node, classId);
  if (decs.some((d) => d.name === 'WebSocketGateway')) coverage(pass, 'endpoints', 'WebSocket gateways are not listed as endpoints', node, classId);
  if (node.members.some((m) => decorators(pass, m).some((d) => d.name === 'MessagePattern' || d.name === 'EventPattern'))) {
    coverage(pass, 'endpoints', 'Microservice message handlers (@MessagePattern, @EventPattern) are not listed as endpoints', node, classId);
  }
}

function arrayItems(pass, node) {
  const ts = pass.ts;
  const n = unwrap(ts, node);
  if (!n) return [];
  if (ts.isArrayLiteralExpression(n)) {
    const items = [];
    for (const e of n.elements) {
      if (ts.isSpreadElement(e)) items.push(...arrayItems(pass, e.expression));
      else items.push(e);
    }
    return items;
  }
  if (ts.isIdentifier(n)) {
    const sym = pass.follow(pass.symbolAt(n));
    const decl = sym?.valueDeclaration;
    if (decl && ts.isVariableDeclaration(decl) && decl.initializer) return arrayItems(pass, decl.initializer);
  }
  return [];
}

function provider(pass, el, moduleId) {
  const ts = pass.ts;
  const e = unwrap(ts, el);
  const line = pass.line(el);
  if (ts.isIdentifier(e) || ts.isPropertyAccessExpression(e)) {
    const cls = classDeclOf(pass, e);
    const id = cls ? pass.declId(cls) : null;
    if (!id) return;
    const name = e.getText().split('.').pop();
    pass.facts.di.push({ serviceId: id, serviceName: name, implId: id, implName: name, lifetime: scopeOf(pass, cls), method: 'providers', owner: moduleId, line, factory: false });
    return;
  }
  if (!ts.isObjectLiteralExpression(e)) return;
  const token = tokenOf(pass, prop(ts, e, 'provide'));
  if (!token) return;
  const scope = prop(ts, e, 'scope');
  let impl = null;
  let factory = false;
  let lifetime = scope ? lifetimeOf(scope.getText()) : null;
  for (const key of ['useClass', 'useExisting']) {
    const value = prop(ts, e, key);
    if (!value) continue;
    const cls = classDeclOf(pass, value);
    if (cls) {
      impl = { id: pass.declId(cls), name: unwrap(ts, value).getText().split('.').pop() };
      lifetime ??= scopeOf(pass, cls);
    }
  }
  const made = prop(ts, e, 'useFactory') ?? prop(ts, e, 'useValue');
  if (made) {
    factory = true;
    const created = firstNew(pass, made);
    if (created) impl = created;
  }
  pass.facts.di.push({
    serviceId: token.id, serviceName: token.name, implId: impl?.id ?? null, implName: impl?.name ?? null,
    lifetime: lifetime ?? 'singleton', method: 'providers', owner: moduleId, line, factory,
  });
}

/** The class of the map that a factory creates: useFactory: () => new StripeGateway(...). */
function firstNew(pass, node) {
  const ts = pass.ts;
  let found = null;
  const visit = (n) => {
    if (found) return;
    if (ts.isNewExpression(n)) {
      const id = pass.typeTarget(n.expression);
      if (id) found = { id, name: n.expression.getText().split('.').pop() };
    }
    ts.forEachChild(n, visit);
  };
  visit(node);
  return found;
}

function coverage(pass, kind, label, node, owner) {
  pass.facts.coverage.push({ kind, label, line: pass.line(node), owner });
}

// -------------------------------------------------------------------- messages

/** commandBus.execute(new PlaceOrder(...)), eventBus.publish(new OrderPlaced()): sends a message to its handler. */
export function dispatchMessage(pass, call) {
  const ts = pass.ts;
  const callee = call.expression;
  if (!ts.isPropertyAccessExpression(callee) || !/^(execute|publish|publishAll|dispatch|send|sendCommand|emit|emitAsync)$/.test(callee.name.text)) return null;
  const arg = unwrap(ts, call.arguments[0]);
  if (!arg || !ts.isNewExpression(arg)) return null;
  const id = pass.typeTarget(arg.expression);
  if (!id) return null;
  pass.ref(id, 'dispatch', pass.line(call), arg.expression.getText().split('.').pop());
  return id;
}

// -------------------------------------------------------------------- database

export function tryDb(pass, call) {
  const ts = pass.ts;
  const callee = unwrap(ts, call.expression);
  const line = pass.line(call);

  // knex('orders'), db('orders') where db = knex(config)
  if (ts.isIdentifier(callee)) {
    const from = origin(pass, callee);
    if (from && rootPackage(from.module) === 'knex' && isString(ts, call.arguments[0])) {
      pass.ref('ext:db', 'db', line, stringOf(ts, call.arguments[0]));
      return true;
    }
    return false;
  }
  if (!ts.isPropertyAccessExpression(callee)) return false;
  const method = callee.name.text;
  const recv = unwrap(ts, callee.expression);
  if (!recv || ts.isCallExpression(recv)) return false;             // further steps of a query that was already counted
  const from = origin(pass, recv);
  if (!from || !DATABASES.has(rootPackage(from.module)) || NOT_DATA.has(method)) return false;
  if (['redis', 'ioredis'].includes(rootPackage(from.module))) return false;
  pass.ref('ext:db', 'db', line, dbLabel(pass, recv, method, call));
  return true;
}

/** sql`select ...` from postgres or a similar package. */
export function tryDbTag(pass, tagged) {
  const ts = pass.ts;
  const from = origin(pass, tagged.tag);
  if (from && DATABASES.has(rootPackage(from.module))) pass.ref('ext:db', 'db', pass.line(tagged), 'query');
}

function dbLabel(pass, recv, method, call) {
  const ts = pass.ts;
  // prisma.order.findMany(): the model
  if (ts.isPropertyAccessExpression(recv) && !recv.name.text.startsWith('$')) {
    const holder = origin(pass, recv.expression);
    if (holder && rootPackage(holder.module) === '@prisma/client') return recv.name.text;
  }
  // dataSource.getRepository(Order), db.collection('orders'), db.insert(orders)
  const arg = unwrap(ts, call.arguments[0]);
  if (/^(getRepository|getMongoRepository|collection|from|insert|update|delete|table|into|selectFrom|insertInto|deleteFrom|updateTable)$/.test(method) && arg) {
    if (ts.isStringLiteral(arg)) return arg.text;
    if (ts.isIdentifier(arg) || ts.isPropertyAccessExpression(arg)) return arg.getText();
  }
  // this.orders.find() with orders: Repository<Order> or Model<OrderDocument>
  const sym = pass.symbolAt(ts.isPropertyAccessExpression(recv) ? recv.name : recv);
  const decl = sym?.valueDeclaration;
  const type = decl && (ts.isParameter(decl) || ts.isPropertyDeclaration(decl) || ts.isVariableDeclaration(decl)) ? decl.type : null;
  if (type && ts.isTypeReferenceNode(type) && type.typeArguments?.length) return type.typeArguments[0].getText().replace(/Document$/, '');
  if (ts.isIdentifier(recv) && /^[A-Z]/.test(recv.text)) return recv.text;   // Order.findAll(), a model class
  return method.replace(/^\$/, '');
}

// -------------------------------------------------------------------- outbound HTTP

const HTTP_VERBS = { get: 'GET', post: 'POST', put: 'PUT', patch: 'PATCH', delete: 'DELETE', del: 'DELETE', head: 'HEAD', options: 'OPTIONS' };

export function tryHttp(pass, call) {
  const ts = pass.ts;
  const callee = unwrap(ts, call.expression);
  const args = call.arguments;
  let verb = null;
  let url = null;
  let base;                                                         // undefined: none; null: set at run time; string: known
  let label = null;

  if (ts.isIdentifier(callee)) {
    const from = origin(pass, callee);
    const pkg = from ? rootPackage(from.module) : null;
    const name = callee.text;
    const fetchLike = (name === 'fetch' || name === '$fetch' || name === 'ofetch') && (!from || pkg === 'global' || HTTP_CLIENTS.has(pkg));
    if (fetchLike || ['node-fetch', 'undici', 'cross-fetch', 'isomorphic-fetch', 'ofetch', 'ky', 'got', 'wretch'].includes(pkg)) {
      url = args[0];
      verb = stringOf(ts, prop(ts, args[1], 'method'));
      label = name;
      base = instanceBase(pass, callee);
    } else if (pkg === 'axios') {
      const config = ts.isObjectLiteralExpression(unwrap(ts, args[0])) ? args[0] : args[1];
      url = ts.isObjectLiteralExpression(unwrap(ts, args[0])) ? prop(ts, args[0], 'url') : args[0];
      verb = stringOf(ts, prop(ts, config, 'method'));
      label = 'axios';
      base = instanceBase(pass, callee);
    } else {
      return false;
    }
  } else if (ts.isPropertyAccessExpression(callee)) {
    const method = callee.name.text;
    const recv = callee.expression;
    const from = origin(pass, recv);
    const pkg = from ? rootPackage(from.module) : null;
    const jquery = !from && ts.isIdentifier(recv) && (recv.text === '$' || recv.text === 'jQuery');
    if (!pkg && !jquery) return false;
    if (pkg && !HTTP_CLIENTS.has(pkg) && !['@angular/common'].includes(pkg)) return false;
    if (pkg === '@angular/common' && from.module !== '@angular/common/http') return false;

    if (jquery || pkg === 'jquery') {
      if (method === 'ajax') {
        url = prop(ts, args[0], 'url') ?? args[0];
        verb = stringOf(ts, prop(ts, args[0], 'method')) ?? stringOf(ts, prop(ts, args[0], 'type'));
      } else if (method === 'get' || method === 'getJSON' || method === 'post') {
        url = args[0];
        verb = method === 'post' ? 'POST' : 'GET';
      } else return false;
    } else if ((pkg === 'http' || pkg === 'https') && (method === 'request' || method === 'get')) {
      verb = method === 'get' ? 'GET' : stringOf(ts, prop(ts, args[0], 'method')) ?? 'GET';
      if (isString(ts, args[0]) || ts.isTemplateExpression(unwrap(ts, args[0]))) {
        url = args[0];
      } else {
        // http.request({ hostname, path }): without a host it goes to localhost; a named host is kept, so a
        // request to another machine is not taken for one to this code.
        const host = prop(ts, args[0], 'hostname') ?? prop(ts, args[0], 'host');
        const pathText = urlText(pass, prop(ts, args[0], 'path') ?? ts.factory.createStringLiteral('/'));
        const hostText = host ? (stringOf(ts, host) ?? '*') : 'localhost';
        const address = pathText === null ? null : `${pkg}://${hostText}${pathText.startsWith('/') ? '' : '/'}${pathText}`;
        url = address === null ? null : ts.factory.createStringLiteral(address);
      }
    } else if (HTTP_VERBS[method] || method === 'request' || method === 'fetch') {
      if (method === 'request' && ts.isObjectLiteralExpression(unwrap(ts, args[0]))) {
        url = prop(ts, args[0], 'url');
        verb = stringOf(ts, prop(ts, args[0], 'method'));
      } else {
        url = args[0];
        verb = HTTP_VERBS[method] ?? stringOf(ts, prop(ts, args[1], 'method'));
      }
      base = instanceBase(pass, recv);
    } else {
      return false;
    }
    label = method;
  } else {
    return false;
  }

  verb = (verb ?? 'GET').toUpperCase();
  const text = url ? urlText(pass, url) : null;
  let full = text;
  if (text !== null && base !== undefined && !/^[a-z]+:\/\//i.test(text)) {
    full = base === null ? '*' + (text.startsWith('/') ? text : '/' + text) : base.replace(/\/+$/, '') + '/' + text.replace(/^\/+/, '');
  }
  const shown = full ?? '?';
  pass.ref('ext:http', 'http', pass.line(call), `${verb} ${shown.length > 60 ? shown.slice(0, 59) + '…' : shown}`, null,
    full === null ? null : { verb, path: full });
  return true;
}

/** The address an HTTP client instance was made with: axios.create({ baseURL }), got.extend({ prefixUrl }), ky.create(...). */
function instanceBase(pass, expr) {
  const ts = pass.ts;
  const n = unwrap(ts, expr);
  const sym = pass.follow(pass.symbolAt(ts.isPropertyAccessExpression(n) ? n.name : n));
  const decl = sym?.valueDeclaration;
  let init = decl && (ts.isVariableDeclaration(decl) || ts.isPropertyDeclaration(decl)) && decl.initializer ? unwrap(ts, decl.initializer) : null;
  if (!init || !ts.isCallExpression(init) || !ts.isPropertyAccessExpression(init.expression)) return undefined;
  if (!/^(create|extend)$/.test(init.expression.name.text)) return undefined;
  const value = prop(ts, init.arguments[0], 'baseURL') ?? prop(ts, init.arguments[0], 'prefixUrl') ?? prop(ts, init.arguments[0], 'baseUrl');
  if (!value) return undefined;
  const text = urlText(pass, value);
  return text === null || text.startsWith('*') ? null : text;
}

/** An address as written: literal parts as they are, "*" for parts worked out at run time. */
export function urlText(pass, node, depth = 0) {
  const ts = pass.ts;
  const n = unwrap(ts, node);
  if (!n || depth > 6) return null;
  if (ts.isStringLiteral(n) || ts.isNoSubstitutionTemplateLiteral(n)) return n.text;
  if (ts.isTemplateExpression(n)) {
    let text = n.head.text;
    for (const span of n.templateSpans) text += (constantPart(pass, span.expression, depth) ?? '*') + span.literal.text;
    return text;
  }
  if (ts.isBinaryExpression(n) && n.operatorToken.kind === ts.SyntaxKind.PlusToken) {
    return (urlText(pass, n.left, depth + 1) ?? '*') + (urlText(pass, n.right, depth + 1) ?? '*');
  }
  if (ts.isNewExpression(n) && n.expression.getText() === 'URL' && n.arguments?.length) {
    const first = urlText(pass, n.arguments[0], depth + 1);
    if (first && /^[a-z]+:\/\//i.test(first)) return first;
    if (n.arguments.length > 1) {
      const base = urlText(pass, n.arguments[1], depth + 1);
      if (base && !base.startsWith('*') && first !== null) return base.replace(/\/+$/, '') + '/' + first.replace(/^\/+/, '');
      return first === null ? null : '*' + (first.startsWith('/') ? first : '/' + first);
    }
    return first;
  }
  const constant = constantPart(pass, n, depth);
  return constant ?? '*';
}

function constantPart(pass, node, depth) {
  const ts = pass.ts;
  const n = unwrap(ts, node);
  if (!n) return null;
  if (ts.isStringLiteral(n) || ts.isNoSubstitutionTemplateLiteral(n)) return n.text;
  if (ts.isNumericLiteral(n)) return n.text;
  if (ts.isIdentifier(n) || ts.isPropertyAccessExpression(n)) {
    const sym = pass.follow(pass.symbolAt(ts.isPropertyAccessExpression(n) ? n.name : n));
    const decl = sym?.valueDeclaration;
    const constant = !!decl && (
      (ts.isVariableDeclaration(decl) && (decl.parent.flags & ts.NodeFlags.Const) !== 0)
      || (ts.isPropertyDeclaration(decl) && !!ts.getModifiers(decl)?.some((m) => m.kind === ts.SyntaxKind.ReadonlyKeyword))
      || ts.isPropertyAssignment(decl));
    if (constant && decl.initializer) {
      const init = unwrap(ts, decl.initializer);
      if (ts.isStringLiteral(init) || ts.isNoSubstitutionTemplateLiteral(init) || ts.isTemplateExpression(init) || ts.isBinaryExpression(init)) return urlText(pass, init, depth + 1);
    }
  }
  return null;
}

// -------------------------------------------------------------------- not followed

export function dynamicImport(pass, call) {
  const ts = pass.ts;
  const isImport = call.expression.kind === ts.SyntaxKind.ImportKeyword;
  const isReq = ts.isIdentifier(call.expression) && call.expression.text === 'require';
  if ((isImport || isReq) && call.arguments.length && !isString(ts, call.arguments[0])) {
    coverage(pass, 'reflection', 'Modules loaded by a name worked out at run time', call, pass.owner);
  }
  return false;
}

export function tryCoverage(pass, call) {
  const ts = pass.ts;
  const callee = unwrap(ts, call.expression);
  if (!ts.isIdentifier(callee) && !ts.isPropertyAccessExpression(callee)) return;
  const name = ts.isIdentifier(callee) ? callee.text : callee.name.text;
  if (name !== 'router' && name !== 'createTRPCRouter' && name !== 'initTRPC') return;
  const from = origin(pass, callee);
  if (from && rootPackage(from.module) === '@trpc/server') coverage(pass, 'endpoints', 'tRPC procedures are not listed as endpoints', call, pass.owner);
}

export { routeNames };
export const _internals = { join, path };
