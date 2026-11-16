// Reads one file and lists what it declares, calls, registers and maps: the same facts Sightline's C#
// reader produces, so the graph is built the same way for every language.
//
// Ids, stable across machines because they use the package name and the path inside the package:
//   module (a file with functions or code)   npm:<package>::<path>
//   function or constant of a module         npm:<package>::<path>:<name>
//   class, interface, enum, type, object     npm:<package>::<path>#<Name>
//   member of one of those                   npm:<package>::<path>#<Name>.<member>

import crypto from 'node:crypto';
import path from 'node:path';
import * as fw from './frameworks.mjs';

export function idPrefix(project) {
  return `npm:${project.name}::`;
}

export function emptyFacts(file) {
  return {
    file: file.host, project: file.project.id, types: [], members: [], refs: [], impls: [], handlers: [], di: [],
    endpoints: [], groupPasses: [], dependencies: [], coverage: [], unresolved: 0, declHash: '',
  };
}

export function extractFile(ws, project, file, sf, program) {
  const pass = new Pass(ws, project, file, sf, program);
  pass.run();
  return pass.facts;
}

// -------------------------------------------------------------------- ids of declarations

/** The id of a declaration anywhere on the map, or null when it is not something the map shows. */
export function declarationId(ws, node) {
  if (!node) return null;
  const sf = node.getSourceFile?.();
  if (!sf) return null;
  const info = ws.fileInfo(sf.fileName);
  if (!info) return null;
  return idWithin(ws.ts, node, idPrefix(info.project) + info.rel);
}

export function unwrap(ts, node) {
  let n = node;
  while (n && (ts.isParenthesizedExpression(n) || ts.isAsExpression(n) || ts.isNonNullExpression(n) || ts.isTypeAssertionExpression(n)
    || (ts.isSatisfiesExpression && ts.isSatisfiesExpression(n)) || ts.isAwaitExpression(n))) n = n.expression;
  return n;
}

export function isFunctionLike(ts, node) {
  return !!node && (ts.isArrowFunction(node) || ts.isFunctionExpression(node));
}

/** memo(() => ...), forwardRef(...), asyncHandler(async (req, res) => ...): the function inside. */
export function wrappedFunction(ts, node) {
  const n = unwrap(ts, node);
  if (!n || !ts.isCallExpression(n) || n.arguments.length === 0) return null;
  const last = unwrap(ts, n.arguments[n.arguments.length - 1]);
  if (isFunctionLike(ts, last)) return last;
  return wrappedFunction(ts, last);
}

export function memberName(ts, name) {
  if (!name) return null;
  if (ts.isIdentifier(name) || ts.isPrivateIdentifier(name)) return name.text;
  if (ts.isStringLiteral(name) || ts.isNumericLiteral(name) || ts.isNoSubstitutionTemplateLiteral(name)) return name.text;
  return null;
}

function hasModifier(ts, node, kind) {
  return !!(ts.canHaveModifiers(node) && ts.getModifiers(node)?.some((m) => m.kind === kind));
}

function isTopLevelVar(ts, decl) {
  return ts.isVariableDeclarationList(decl.parent) && ts.isVariableStatement(decl.parent.parent) && ts.isSourceFile(decl.parent.parent.parent);
}

function objectHasMethods(ts, obj) {
  return obj.properties.some((p) => ts.isMethodDeclaration(p) || (ts.isPropertyAssignment(p) && (isFunctionLike(ts, unwrap(ts, p.initializer)) || wrappedFunction(ts, p.initializer))));
}

/** module.exports or exports. */
export function isModuleExports(ts, node) {
  return (ts.isIdentifier(node) && node.text === 'exports')
    || (ts.isPropertyAccessExpression(node) && ts.isIdentifier(node.expression) && node.expression.text === 'module' && node.name.text === 'exports');
}

/** exports.foo = ... or module.exports.foo = ...: "foo". */
export function cjsExportName(ts, node) {
  if (!ts.isBinaryExpression(node) || node.operatorToken.kind !== ts.SyntaxKind.EqualsToken) return null;
  const left = node.left;
  return ts.isPropertyAccessExpression(left) && isModuleExports(ts, left.expression) ? left.name.text : null;
}

function isAssignment(ts, node) {
  return ts.isBinaryExpression(node) && node.operatorToken.kind === ts.SyntaxKind.EqualsToken;
}

/** Name of a type declaration with the namespaces around it, or null when it is declared inside code. */
function qualifiedTypeName(ts, node, fallback) {
  const name = node.name?.text ?? fallback;
  if (!name) return null;
  const parts = [name];
  for (let p = node.parent; p; p = p.parent) {
    if (ts.isSourceFile(p)) return parts.join('.');
    if (ts.isModuleBlock(p)) continue;
    if (ts.isModuleDeclaration(p)) {
      if (!ts.isIdentifier(p.name)) return null;            // declare module 'x' { }: a description of a package
      parts.unshift(p.name.text);
      continue;
    }
    return null;
  }
  return null;
}

function idWithin(ts, node, moduleId) {
  if (ts.isClassDeclaration(node) || ts.isInterfaceDeclaration(node) || ts.isEnumDeclaration(node) || ts.isTypeAliasDeclaration(node)) {
    const fallback = ts.isClassDeclaration(node) && hasModifier(ts, node, ts.SyntaxKind.DefaultKeyword) ? 'default' : null;
    const name = qualifiedTypeName(ts, node, fallback);
    return name ? `${moduleId}#${name}` : null;
  }

  if (ts.isConstructorDeclaration(node) || ts.isMethodDeclaration(node) || ts.isMethodSignature(node) || ts.isPropertyDeclaration(node)
    || ts.isPropertySignature(node) || ts.isGetAccessorDeclaration(node) || ts.isSetAccessorDeclaration(node)) {
    const owner = node.parent;
    if (ts.isObjectLiteralExpression(owner)) return objectMemberId(ts, node, owner, moduleId);
    let ownerId = null;
    if (ts.isClassLike(owner) || ts.isInterfaceDeclaration(owner)) ownerId = idWithin(ts, owner, moduleId);
    else if (ts.isTypeLiteralNode(owner) && ts.isTypeAliasDeclaration(owner.parent)) ownerId = idWithin(ts, owner.parent, moduleId);
    const name = ts.isConstructorDeclaration(node) ? 'constructor' : memberName(ts, node.name);
    return ownerId && name ? `${ownerId}.${name}` : null;
  }

  if (ts.isPropertyAssignment(node) || ts.isShorthandPropertyAssignment(node)) {
    return ts.isObjectLiteralExpression(node.parent) ? objectMemberId(ts, node, node.parent, moduleId) : null;
  }

  if (ts.isFunctionDeclaration(node)) {
    if (!ts.isSourceFile(node.parent)) return null;
    const name = node.name?.text ?? (hasModifier(ts, node, ts.SyntaxKind.DefaultKeyword) ? 'default' : null);
    return name ? `${moduleId}:${name}` : null;
  }

  if (ts.isVariableDeclaration(node)) {
    if (!ts.isIdentifier(node.name) || !isTopLevelVar(ts, node) || !node.initializer) return null;
    const init = unwrap(ts, node.initializer);
    if (ts.isClassExpression(init)) return `${moduleId}#${node.name.text}`;
    if (ts.isObjectLiteralExpression(init) && objectHasMethods(ts, init)) return `${moduleId}#${node.name.text}`;
    if (isFunctionLike(ts, init) || wrappedFunction(ts, init)) return `${moduleId}:${node.name.text}`;
    return null;
  }

  if (ts.isArrowFunction(node) || ts.isFunctionExpression(node) || ts.isClassExpression(node)) {
    let p = node.parent;
    while (p && (ts.isParenthesizedExpression(p) || ts.isAsExpression(p) || (ts.isSatisfiesExpression && ts.isSatisfiesExpression(p))
      || (ts.isCallExpression(p) && wrappedFunction(ts, p) === node))) p = p.parent;
    if (!p) return null;
    if (ts.isVariableDeclaration(p) || ts.isPropertyAssignment(p) || ts.isPropertyDeclaration(p)) return idWithin(ts, p, moduleId);
    if (ts.isExportAssignment(p)) return ts.isClassExpression(node) ? `${moduleId}#default` : `${moduleId}:default`;
    if (isAssignment(ts, p) && p.right === node || (isAssignment(ts, p) && unwrap(ts, p.right) === node)) {
      const name = cjsExportName(ts, p) ?? (isModuleExports(ts, p.left) ? 'default' : null);
      if (!name) return null;
      return ts.isClassExpression(node) ? `${moduleId}#${name}` : `${moduleId}:${name}`;
    }
    return null;
  }

  if (ts.isExportAssignment(node)) {
    const e = unwrap(ts, node.expression);
    return isFunctionLike(ts, e) ? `${moduleId}:default` : null;
  }

  // exports.foo = function () { }: the symbol's declaration is the assignment, or its left side.
  if (ts.isBinaryExpression(node) && cjsExportName(ts, node)) {
    const right = unwrap(ts, node.right);
    if (ts.isClassExpression(right)) return `${moduleId}#${cjsExportName(ts, node)}`;
    return isFunctionLike(ts, right) ? `${moduleId}:${cjsExportName(ts, node)}` : null;
  }
  if (ts.isPropertyAccessExpression(node) && node.parent && ts.isBinaryExpression(node.parent) && node.parent.left === node) {
    return idWithin(ts, node.parent, moduleId);
  }
  return null;
}

/** A method of an object literal: a member of the object constant that holds it, or of the module it is exported from. */
function objectMemberId(ts, member, obj, moduleId) {
  const name = memberName(ts, member.name);
  if (!name) return null;
  let holder = obj.parent;
  while (holder && (ts.isParenthesizedExpression(holder) || ts.isAsExpression(holder) || (ts.isSatisfiesExpression && ts.isSatisfiesExpression(holder)))) holder = holder.parent;
  if (!holder) return null;
  if (ts.isVariableDeclaration(holder) && ts.isIdentifier(holder.name) && isTopLevelVar(ts, holder) && objectHasMethods(ts, obj)) return `${moduleId}#${holder.name.text}.${name}`;
  if (isAssignment(ts, holder) && isModuleExports(ts, holder.left)) return `${moduleId}:${name}`;
  if (ts.isExportAssignment(holder)) return `${moduleId}:${name}`;
  return null;
}

// -------------------------------------------------------------------- one file

const SIGNATURE_TYPE_LENGTH = 48;

class Pass {
  constructor(ws, project, file, sf, program) {
    this.ws = ws;
    this.ts = ws.ts;
    this.project = project;
    this.file = file;
    this.sf = sf;
    this.program = program;
    this.checker = program.getTypeChecker();
    this.moduleId = idPrefix(project) + file.rel;
    this.facts = emptyFacts(file);
    this.owner = null;
    this.typeRefs = new Set();
    this.ordinals = new Map();
    this.ids = new Map();
    this.declarations = new Map();       // id -> the first declaration found with it
    this.origins = new Map();
    this.exported = exportedNames(this.ts, sf);
  }

  // ------------------------------------------------------------ helpers

  line(node) { return this.sf.getLineAndCharacterOfPosition(node.getStart(this.sf)).line + 1; }
  endLine(node) { return this.sf.getLineAndCharacterOfPosition(node.end).line + 1; }

  declId(node) {
    if (!node) return null;
    if (this.ids.has(node)) return this.ids.get(node);
    const id = declarationId(this.ws, node);
    this.ids.set(node, id);
    if (id && !this.declarations.has(id)) this.declarations.set(id, node);
    return id;
  }

  safe(fn) {
    try { return fn(); } catch { return undefined; }
  }

  /** Follows imports to the declaration they name. */
  follow(symbol) {
    const ts = this.ts;
    let s = symbol;
    if (s && (s.flags & ts.SymbolFlags.Alias)) {
      const next = this.safe(() => this.checker.getAliasedSymbol(s));
      if (next && next.declarations?.length) s = next;
    }
    return s;
  }

  symbolAt(node) {
    return this.safe(() => this.checker.getSymbolAtLocation(node));
  }

  /** The id on the map of what a name refers to: the first of its declarations that has one. */
  targetOf(node) {
    const ts = this.ts;
    const sym = this.follow(this.symbolAt(node));
    if (!sym) return null;
    for (const d of sym.declarations ?? []) {
      if (ts.isShorthandPropertyAssignment(d)) {
        const value = this.follow(this.safe(() => this.checker.getShorthandAssignmentValueSymbol(d)));
        for (const v of value?.declarations ?? []) {
          const id = this.declId(v);
          if (id) return id;
        }
        continue;
      }
      const id = this.declId(d);
      if (id) return id;
    }
    return null;
  }

  /** The declaration (function or method node) a name refers to, for reading its parameters. */
  functionOf(node) {
    const ts = this.ts;
    const sym = this.follow(this.symbolAt(node));
    for (const d of sym?.declarations ?? []) {
      if (ts.isFunctionDeclaration(d) || ts.isMethodDeclaration(d)) return d;
      if ((ts.isVariableDeclaration(d) || ts.isPropertyAssignment(d) || ts.isPropertyDeclaration(d)) && d.initializer) {
        const init = unwrap(ts, d.initializer);
        if (isFunctionLike(ts, init)) return init;
        const inner = wrappedFunction(ts, init);
        if (inner) return inner;
      }
    }
    return null;
  }

  ref(to, kind, line, label, via, request) {
    if (!this.owner || !to) return;
    if (kind === 'references' || kind === 'ctor-param') {
      const k = `${this.owner}|${to}|${kind}`;
      if (this.typeRefs.has(k)) return;
      this.typeRefs.add(k);
    }
    const r = { from: this.owner, to, kind, line };
    if (label) r.label = label;
    if (via) r.via = via;
    if (request) r.request = request;
    this.facts.refs.push(r);
  }

  withOwner(owner, fn) {
    const before = this.owner;
    this.owner = owner;
    try { fn(); } finally { this.owner = before; }
  }

  nextOrdinal(key) {
    const n = this.ordinals.get(key) ?? 0;
    this.ordinals.set(key, n + 1);
    return n;
  }

  access(node, name) {
    const ts = this.ts;
    if (hasModifier(ts, node, ts.SyntaxKind.ExportKeyword) || hasModifier(ts, node, ts.SyntaxKind.DefaultKeyword)) return 'public';
    return name && this.exported.has(name) ? 'public' : 'private';
  }

  memberAccess(node) {
    const ts = this.ts;
    if (hasModifier(ts, node, ts.SyntaxKind.PrivateKeyword) || (node.name && ts.isPrivateIdentifier(node.name))) return 'private';
    if (hasModifier(ts, node, ts.SyntaxKind.ProtectedKeyword)) return 'protected';
    return 'public';
  }

  compact(text) {
    const t = text.replace(/\s+/g, ' ').trim();
    return t.length > SIGNATURE_TYPE_LENGTH ? t.slice(0, SIGNATURE_TYPE_LENGTH - 1) + '…' : t;
  }

  signature(name, parameters) {
    const ts = this.ts;
    const params = (parameters ?? []).map((p) => {
      const n = p.name.getText(this.sf);
      const optional = p.questionToken || p.initializer ? '?' : '';
      const rest = p.dotDotDotToken ? '...' : '';
      return `${rest}${this.compact(n)}${optional}${p.type ? ': ' + this.compact(p.type.getText(this.sf)) : ''}`;
    });
    return `${name}(${params.join(', ')})`;
  }

  /** Types of the map named in a type annotation, as "references" (or "ctor-param") from a type. */
  typeRefsIn(typeNode, fromTypeId, kind, line) {
    const ts = this.ts;
    if (!typeNode) return;
    const visit = (n) => {
      if (ts.isTypeReferenceNode(n) || ts.isExpressionWithTypeArguments(n)) {
        const nameNode = ts.isTypeReferenceNode(n) ? n.typeName : n.expression;
        const target = this.typeTarget(nameNode);
        if (target && target !== fromTypeId) this.withOwner(fromTypeId, () => this.ref(target, kind, line));
      }
      ts.forEachChild(n, visit);
    };
    visit(typeNode);
  }

  /** The type on the map that a type name refers to. */
  typeTarget(nameNode) {
    const ts = this.ts;
    const node = ts.isQualifiedName(nameNode) ? nameNode.right : ts.isPropertyAccessExpression(nameNode) ? nameNode.name : nameNode;
    const sym = this.follow(this.symbolAt(node));
    for (const d of sym?.declarations ?? []) {
      if (ts.isClassDeclaration(d) || ts.isInterfaceDeclaration(d) || ts.isEnumDeclaration(d) || ts.isTypeAliasDeclaration(d) || ts.isClassExpression(d)) {
        const id = this.declId(d);
        if (id) return id;
      }
    }
    return null;
  }

  isTypeId(id) {
    return id && id.includes('#') && !id.slice(id.indexOf('#') + 1).includes('.');
  }

  // ------------------------------------------------------------ declarations

  run() {
    const ts = this.ts;
    const members = [];
    const code = [];
    for (const statement of this.sf.statements) this.declare(statement, code, members);

    if (members.length > 0 || code.length > 0) {
      const lines = this.sf.getLineAndCharacterOfPosition(this.sf.end).line + 1;
      this.facts.types.push({
        id: this.moduleId, name: moduleName(this.file.rel), ns: folderOf(this.file.rel), kind: 'module', access: 'public',
        containerId: null, baseId: null, baseName: null, interfaceIds: [], externalInterfaces: [],
        tags: this.entryPoint(code) ? ['entrypoint'] : [], line: 1, endLine: lines,
      });
    }
    for (const m of members) this.addModuleMember(m);

    if (code.length > 0) {
      const id = `${this.moduleId}:(top level)`;
      this.facts.members.push({
        id, typeId: this.moduleId, name: '(top level)', kind: 'method', signature: '(top level)', returns: null,
        access: 'private', isStatic: false, isAbstract: false, isAsync: false, line: this.line(code[0]), endLine: this.endLine(code[code.length - 1]),
      });
      this.withOwner(id, () => { for (const s of code) this.walk(s); });
    }

    this.countUnresolved();
    this.facts.declHash = declarationHash(this.facts);
  }

  /** Sorts a top-level statement into declarations, module members, and code that runs when the module loads. */
  declare(statement, code, members) {
    const ts = this.ts;
    const s = statement;
    if (ts.isImportDeclaration(s) || ts.isExportDeclaration(s) || ts.isImportEqualsDeclaration(s)) return;
    if (ts.isClassDeclaration(s)) return this.addClass(s);
    if (ts.isInterfaceDeclaration(s)) return this.addInterface(s);
    if (ts.isEnumDeclaration(s)) return this.addEnum(s);
    if (ts.isTypeAliasDeclaration(s)) return this.addTypeAlias(s);
    if (ts.isModuleDeclaration(s)) return this.addNamespace(s);

    if (ts.isFunctionDeclaration(s)) {
      if (!s.body) return;                                          // an overload or a declaration
      const id = this.declId(s);
      if (id) members.push({ node: s, id, name: id.slice(id.lastIndexOf(':') + 1), fn: s });
      return;
    }

    if (ts.isVariableStatement(s)) {
      if (hasModifier(ts, s, ts.SyntaxKind.DeclareKeyword)) return;
      for (const d of s.declarationList.declarations) {
        const init = d.initializer ? unwrap(ts, d.initializer) : null;
        if (!init) continue;
        if (ts.isIdentifier(d.name)) {
          if (isFunctionLike(ts, init) || wrappedFunction(ts, init)) {
            members.push({ node: d, id: `${this.moduleId}:${d.name.text}`, name: d.name.text, fn: isFunctionLike(ts, init) ? init : wrappedFunction(ts, init), around: init });
            continue;
          }
          if (ts.isClassExpression(init)) { this.addClass(init, d.name.text, d); continue; }
          if (ts.isObjectLiteralExpression(init) && objectHasMethods(ts, init)) { this.addObject(d, init); continue; }
        }
        if (isRequire(ts, init)) continue;                          // const x = require('x'): an import
        code.push(d);
      }
      return;
    }

    if (ts.isExportAssignment(s)) {
      const e = unwrap(ts, s.expression);
      if (isFunctionLike(ts, e)) members.push({ node: s, id: `${this.moduleId}:default`, name: 'default', fn: e });
      else if (ts.isClassExpression(e)) this.addClass(e, 'default', s);
      else if (ts.isObjectLiteralExpression(e)) this.addExportedObject(e, members, code);
      else if (!ts.isIdentifier(e)) code.push(s);
      return;
    }

    if (ts.isExpressionStatement(s)) {
      const e = s.expression;
      if (ts.isStringLiteral(e)) return;                            // "use strict"
      if (isAssignment(ts, e)) {
        const right = unwrap(ts, e.right);
        const name = cjsExportName(ts, e);
        if (name && isFunctionLike(ts, right)) { members.push({ node: e, id: `${this.moduleId}:${name}`, name, fn: right }); return; }
        if (name && ts.isClassExpression(right)) { this.addClass(right, name, e); return; }
        if (name && ts.isIdentifier(right)) return;
        if (isModuleExports(ts, e.left)) {
          if (isFunctionLike(ts, right)) { members.push({ node: e, id: `${this.moduleId}:default`, name: 'default', fn: right }); return; }
          if (ts.isIdentifier(right) || isRequire(ts, right)) return;
          if (ts.isObjectLiteralExpression(right)) { this.addExportedObject(right, members, code); return; }
          if (ts.isClassExpression(right)) { this.addClass(right, 'default', e); return; }
        }
      }
      code.push(s);
      return;
    }

    if (ts.isEmptyStatement(s)) return;
    code.push(s);
  }

  /** module.exports = { a() { }, b: () => ... } and export default { ... }: functions of the module. */
  addExportedObject(obj, members, code) {
    const ts = this.ts;
    let hasCode = false;
    for (const p of obj.properties) {
      const name = memberName(ts, p.name);
      if (!name) continue;
      if (ts.isMethodDeclaration(p) && p.body) members.push({ node: p, id: `${this.moduleId}:${name}`, name, fn: p });
      else if (ts.isPropertyAssignment(p)) {
        const init = unwrap(ts, p.initializer);
        if (isFunctionLike(ts, init)) members.push({ node: p, id: `${this.moduleId}:${name}`, name, fn: init });
        else if (!ts.isIdentifier(init) && !isRequire(ts, init)) hasCode = true;
      }
    }
    if (hasCode) code.push(obj);
  }

  entryPoint(code) {
    const ts = this.ts;
    const main = [this.project.manifest?.main, this.project.manifest?.module, typeof this.project.manifest?.bin === 'string' ? this.project.manifest.bin : null]
      .filter((m) => typeof m === 'string').map((m) => m.replace(/^\.\//, '').replace(/\.(m|c)?(j|t)sx?$/, ''));
    const stem = this.file.rel.replace(/\.(m|c)?(j|t)sx?$/, '');
    if (code.length && main.some((m) => m === stem || m.replace(/^(dist|lib|build|out)\//, 'src/') === stem)) return true;
    let found = false;
    const visit = (n) => {
      if (found) return;
      if (ts.isCallExpression(n) && ts.isPropertyAccessExpression(n.expression)) {
        const name = n.expression.name.text;
        if (name === 'listen' || name === 'createRoot' || name === 'render' && /ReactDOM|root/i.test(n.expression.expression.getText(this.sf))
          || (name === 'create' && /NestFactory/.test(n.expression.expression.getText(this.sf)))) found = true;
      }
      ts.forEachChild(n, visit);
    };
    for (const s of code) visit(s);
    return found;
  }

  addModuleMember({ node, id, name, fn, around }) {
    const ts = this.ts;
    const body = fn ?? node;
    const isAsync = hasModifier(ts, body, ts.SyntaxKind.AsyncKeyword);
    this.facts.members.push({
      id, typeId: this.moduleId, name, kind: 'method', signature: this.signature(name, body.parameters), returns: body.type ? this.compact(body.type.getText(this.sf)) : null,
      access: name === 'default' ? 'public' : this.access(ts.isVariableDeclaration(node) ? node.parent.parent : node, name),
      isStatic: false, isAbstract: false, isAsync, line: this.line(node), endLine: this.endLine(node),
    });
    for (const p of body.parameters ?? []) this.typeRefsIn(p.type, this.moduleId, 'references', this.line(p));
    this.typeRefsIn(body.type, this.moduleId, 'references', this.line(node));
    this.withOwner(id, () => {
      // memo(() => ...) and the like: the call around the function belongs to it too.
      if (around && around !== fn) for (const a of around.arguments ?? []) if (unwrap(ts, a) !== fn) this.walk(a);
      if (body.body) this.walk(body.body);
    });
    fw.moduleMemberRoutes(this, node, id, name, body);
  }

  addNamespace(node) {
    const ts = this.ts;
    if (!ts.isIdentifier(node.name) || hasModifier(ts, node, ts.SyntaxKind.DeclareKeyword)) return;
    let body = node.body;
    while (body && ts.isModuleDeclaration(body)) body = body.body;
    if (!body || !ts.isModuleBlock(body)) return;
    for (const s of body.statements) {
      if (ts.isClassDeclaration(s)) this.addClass(s);
      else if (ts.isInterfaceDeclaration(s)) this.addInterface(s);
      else if (ts.isEnumDeclaration(s)) this.addEnum(s);
      else if (ts.isTypeAliasDeclaration(s)) this.addTypeAlias(s);
      else if (ts.isModuleDeclaration(s)) this.addNamespace(s);
    }
  }

  typeFact(id, name, kind, access, node, extra = {}) {
    const fact = {
      id, name, ns: folderOf(this.file.rel), kind, access, containerId: null, baseId: null, baseName: null,
      interfaceIds: [], externalInterfaces: [], tags: [], line: this.line(node), endLine: this.endLine(node), ...extra,
    };
    this.facts.types.push(fact);
    return fact;
  }

  /** Interfaces a class or object says it implements, split into those on the map and the others. */
  heritage(clauses, kind) {
    const ts = this.ts;
    const onMap = [];
    const others = [];
    for (const clause of clauses ?? []) {
      if (clause.token !== kind) continue;
      for (const t of clause.types) {
        const id = this.typeTarget(t.expression);
        if (id) onMap.push(id);
        else others.push(t.expression.getText(this.sf).split('.').pop());
      }
    }
    return { onMap, others };
  }

  addClass(node, nameOverride, holder) {
    const ts = this.ts;
    const id = nameOverride ? `${this.moduleId}#${nameOverride}` : this.declId(node);
    if (!id) return;
    const name = node.name?.text ?? nameOverride ?? 'default';
    const extendsClause = node.heritageClauses?.find((c) => c.token === ts.SyntaxKind.ExtendsKeyword)?.types[0];
    const baseId = extendsClause ? this.typeTarget(extendsClause.expression) : null;
    const baseName = extendsClause ? extendsClause.expression.getText(this.sf).split('.').pop() : null;
    const { onMap, others } = this.heritage(node.heritageClauses, ts.SyntaxKind.ImplementsKeyword);
    const fact = this.typeFact(id, name, 'class', this.access(holder ? (ts.isVariableDeclaration(holder) ? holder.parent.parent : holder) : node, name), holder ?? node, {
      baseId, baseName, interfaceIds: [...new Set(onMap)], externalInterfaces: [...new Set(others)],
      tags: fw.classTags(this, node, baseName, others),
    });

    const seen = new Set();
    for (const m of node.members) {
      if (ts.isConstructorDeclaration(m)) {
        if (!m.body && !m.parameters.length) continue;
        const ctorId = `${id}.constructor`;
        this.memberFact(ctorId, id, name, 'ctor', m, { signature: this.signature(name, m.parameters), returns: null });
        for (const p of m.parameters) this.typeRefsIn(p.type, id, 'ctor-param', this.line(p));
        fw.constructorDependencies(this, id, m);
        if (m.body) this.withOwner(ctorId, () => this.walk(m.body));
        continue;
      }
      const memberName_ = memberName(ts, m.name);
      if (!memberName_) continue;
      const memberId = `${id}.${memberName_}`;
      if (ts.isMethodDeclaration(m)) {
        if (seen.has(memberId)) { if (m.body) this.withOwner(memberId, () => this.walk(m.body)); continue; }
        if (!m.body && !hasModifier(ts, m, ts.SyntaxKind.AbstractKeyword) && node.members.some((o) => o !== m && ts.isMethodDeclaration(o) && memberName(ts, o.name) === memberName_ && o.body)) continue;
        seen.add(memberId);
        this.memberFact(memberId, id, memberName_, 'method', m, {
          signature: this.signature(memberName_, m.parameters), returns: m.type ? this.compact(m.type.getText(this.sf)) : null,
          isAbstract: hasModifier(ts, m, ts.SyntaxKind.AbstractKeyword),
        });
        for (const p of m.parameters) this.typeRefsIn(p.type, id, 'references', this.line(p));
        this.typeRefsIn(m.type, id, 'references', this.line(m));
        if (m.body) this.withOwner(memberId, () => this.walk(m.body));
      } else if (ts.isPropertyDeclaration(m)) {
        const init = m.initializer ? unwrap(ts, m.initializer) : null;
        const fn = init && (isFunctionLike(ts, init) ? init : wrappedFunction(ts, init));
        if (seen.has(memberId)) continue;
        seen.add(memberId);
        this.memberFact(memberId, id, memberName_, fn ? 'method' : 'property', m, {
          signature: fn ? this.signature(memberName_, fn.parameters) : memberName_,
          returns: m.type ? this.compact(m.type.getText(this.sf)) : fn?.type ? this.compact(fn.type.getText(this.sf)) : null,
          isAbstract: hasModifier(ts, m, ts.SyntaxKind.AbstractKeyword),
        });
        this.typeRefsIn(m.type, id, 'references', this.line(m));
        if (m.initializer) this.withOwner(memberId, () => this.walk(m.initializer));
      } else if (ts.isGetAccessorDeclaration(m) || ts.isSetAccessorDeclaration(m)) {
        if (!seen.has(memberId)) {
          seen.add(memberId);
          this.memberFact(memberId, id, memberName_, 'property', m, { signature: memberName_, returns: m.type ? this.compact(m.type.getText(this.sf)) : null });
        }
        if (m.body) this.withOwner(memberId, () => this.walk(m.body));
      }
    }

    this.implementations(node, id, onMap, baseId);
    fw.classFrameworks(this, node, id, name, fact);
  }

  memberFact(id, typeId, name, kind, node, extra) {
    const ts = this.ts;
    this.facts.members.push({
      id, typeId, name, kind, signature: name, returns: null, access: this.memberAccess(node),
      isStatic: hasModifier(ts, node, ts.SyntaxKind.StaticKeyword), isAbstract: false,
      isAsync: hasModifier(ts, node, ts.SyntaxKind.AsyncKeyword) || (node.initializer && hasModifier(ts, unwrap(ts, node.initializer), ts.SyntaxKind.AsyncKeyword)) || false,
      line: this.line(node), endLine: this.endLine(node), ...extra,
    });
  }

  /** Class methods to the interface methods they implement, and to the base methods they override. */
  implementations(node, classId, interfaceIds, baseId) {
    const ts = this.ts;
    const own = new Map();
    for (const m of node.members) {
      const n = memberName(ts, m.name);
      if (n && (ts.isMethodDeclaration(m) || ts.isPropertyDeclaration(m) || ts.isGetAccessorDeclaration(m))) own.set(n, `${classId}.${n}`);
    }
    if (own.size === 0) return;

    const seen = new Set();
    const viaInterface = (ifaceId, depth) => {
      if (depth > 6 || seen.has(ifaceId)) return;
      seen.add(ifaceId);
      const decl = this.declarationFor(ifaceId);
      if (!decl) return;
      const members = ts.isInterfaceDeclaration(decl) ? decl.members : ts.isTypeAliasDeclaration(decl) && ts.isTypeLiteralNode(decl.type) ? decl.type.members
        : ts.isClassLike(decl) ? decl.members : [];
      for (const m of members) {
        const n = memberName(ts, m.name);
        if (n && own.has(n) && (ts.isMethodSignature(m) || ts.isPropertySignature(m) || ts.isMethodDeclaration(m))) {
          this.facts.impls.push({ abstractId: `${ifaceId}.${n}`, implId: own.get(n), kind: 'implements' });
        }
      }
      if (ts.isInterfaceDeclaration(decl)) {
        for (const parent of this.heritage(decl.heritageClauses, ts.SyntaxKind.ExtendsKeyword).onMap) viaInterface(parent, depth + 1);
      }
    };
    for (const i of interfaceIds) viaInterface(i, 0);

    // Methods with the name of a base method override it, all the way up the classes on the map.
    let base = baseId;
    for (let depth = 0; base && depth < 8; depth++) {
      const decl = this.declarationFor(base);
      if (!decl || !ts.isClassLike(decl)) break;
      for (const m of decl.members) {
        const n = memberName(ts, m.name);
        if (n && own.has(n) && ts.isMethodDeclaration(m)) this.facts.impls.push({ abstractId: `${base}.${n}`, implId: own.get(n), kind: 'overrides' });
      }
      for (const i of this.heritage(decl.heritageClauses, ts.SyntaxKind.ImplementsKeyword).onMap) viaInterface(i, 0);
      const next = decl.heritageClauses?.find((c) => c.token === ts.SyntaxKind.ExtendsKeyword)?.types[0];
      base = next ? this.typeTarget(next.expression) : null;
    }
  }

  /** The declaration node behind a type id that this pass resolved before. */
  declarationFor(typeId) {
    return this.declarations.get(typeId) ?? null;
  }

  addInterface(node) {
    const ts = this.ts;
    const id = this.declId(node);
    if (!id) return;
    const { onMap, others } = this.heritage(node.heritageClauses, ts.SyntaxKind.ExtendsKeyword);
    this.typeFact(id, node.name.text, 'interface', this.access(node, node.name.text), node, { interfaceIds: [...new Set(onMap)], externalInterfaces: [...new Set(others)] });
    this.abstractMembers(id, node.members);
  }

  addTypeAlias(node) {
    const ts = this.ts;
    if (!ts.isTypeLiteralNode(node.type)) return;                  // only object shapes are types on the map
    const id = this.declId(node);
    if (!id) return;
    this.typeFact(id, node.name.text, 'type', this.access(node, node.name.text), node);
    this.abstractMembers(id, node.type.members);
  }

  abstractMembers(typeId, members) {
    const ts = this.ts;
    for (const m of members) {
      const n = memberName(ts, m.name);
      if (!n) continue;
      const fnType = ts.isPropertySignature(m) && m.type && ts.isFunctionTypeNode(m.type) ? m.type : null;
      const method = ts.isMethodSignature(m) || fnType;
      this.facts.members.push({
        id: `${typeId}.${n}`, typeId, name: n, kind: method ? 'method' : 'property',
        signature: method ? this.signature(n, (fnType ?? m).parameters) : n,
        returns: (fnType ?? m).type ? this.compact((fnType ?? m).type.getText(this.sf)) : null,
        access: 'public', isStatic: false, isAbstract: true, isAsync: false, line: this.line(m), endLine: this.endLine(m),
      });
      for (const p of (fnType ?? m).parameters ?? []) this.typeRefsIn(p.type, typeId, 'references', this.line(p));
      this.typeRefsIn((fnType ?? m).type, typeId, 'references', this.line(m));
    }
  }

  addEnum(node) {
    const id = this.declId(node);
    if (id) this.typeFact(id, node.name.text, 'enum', this.access(node, node.name.text), node);
  }

  /** export const orders = { list() { }, place: async () => ... }: an object of functions, shown like a class. */
  addObject(decl, obj) {
    const ts = this.ts;
    const id = `${this.moduleId}#${decl.name.text}`;
    const iface = decl.type ? this.typeTarget(ts.isTypeReferenceNode(decl.type) ? decl.type.typeName : decl.type) : null;
    this.typeFact(id, decl.name.text, 'object', this.access(decl.parent.parent, decl.name.text), decl, { interfaceIds: iface ? [iface] : [] });

    for (const p of obj.properties) {
      const n = memberName(ts, p.name);
      if (!n) continue;
      const memberId = `${id}.${n}`;
      let fn = null;
      if (ts.isMethodDeclaration(p)) fn = p;
      else if (ts.isPropertyAssignment(p)) {
        const init = unwrap(ts, p.initializer);
        fn = isFunctionLike(ts, init) ? init : wrappedFunction(ts, init);
      }
      if (!fn) {
        if (ts.isPropertyAssignment(p)) this.withOwner(id, () => this.walk(p.initializer));
        continue;
      }
      this.facts.members.push({
        id: memberId, typeId: id, name: n, kind: 'method', signature: this.signature(n, fn.parameters), returns: fn.type ? this.compact(fn.type.getText(this.sf)) : null,
        access: 'public', isStatic: false, isAbstract: false, isAsync: hasModifier(ts, fn, ts.SyntaxKind.AsyncKeyword), line: this.line(p), endLine: this.endLine(p),
      });
      for (const param of fn.parameters ?? []) this.typeRefsIn(param.type, id, 'references', this.line(param));
      if (fn.body) this.withOwner(memberId, () => this.walk(fn.body));
    }

    if (iface) {
      const decl2 = this.declarationFor(iface);
      const members = decl2 && (ts.isInterfaceDeclaration(decl2) ? decl2.members : ts.isTypeAliasDeclaration(decl2) && ts.isTypeLiteralNode(decl2.type) ? decl2.type.members : []);
      for (const m of members ?? []) {
        const n = memberName(ts, m.name);
        if (n && obj.properties.some((p) => memberName(ts, p.name) === n)) this.facts.impls.push({ abstractId: `${iface}.${n}`, implId: `${id}.${n}`, kind: 'implements' });
      }
    }
  }

  // ------------------------------------------------------------ code

  walk(node) {
    const ts = this.ts;
    if (!node) return;
    const visit = (n) => {
      if (ts.isCallExpression(n)) {
        if (this.onCall(n)) return;
      } else if (ts.isNewExpression(n)) {
        this.onNew(n);
      } else if (ts.isJsxSelfClosingElement(n) || ts.isJsxOpeningElement(n)) {
        this.onJsx(n);
      } else if (ts.isTaggedTemplateExpression(n)) {
        fw.tryDbTag(this, n);
      } else if (ts.isClassDeclaration(n) || ts.isClassExpression(n)) {
        // a class inside code: its methods run as part of the code around it
      }
      ts.forEachChild(n, visit);
    };
    visit(node);
  }

  /** True when the call was fully handled here, its arguments included. */
  onCall(call) {
    const ts = this.ts;
    const line = this.line(call);
    if (fw.dynamicImport(this, call)) return false;
    if (fw.tryEndpoint(this, call)) return true;
    fw.tryMount(this, call);
    if (!fw.tryHttp(this, call)) fw.tryDb(this, call);
    fw.tryCoverage(this, call);

    const message = fw.dispatchMessage(this, call);
    const target = this.resolveCall(call);
    if (target) {
      this.ref(target, 'calls', line, null, message);
      fw.groupPassesForCall(this, call, target);
    }
    return false;
  }

  resolveCall(call) {
    const ts = this.ts;
    let e = call.expression;
    while (ts.isParenthesizedExpression(e) || ts.isNonNullExpression(e)) e = e.expression;
    if (e.kind === ts.SyntaxKind.SuperKeyword || e.kind === ts.SyntaxKind.ImportKeyword) return null;
    const name = ts.isPropertyAccessExpression(e) ? e.name : ts.isElementAccessExpression(e) ? e.argumentExpression : e;
    let id = this.targetOf(name);
    if (!id) {
      const sig = this.safe(() => this.checker.getResolvedSignature(call));
      if (sig?.declaration) id = this.declId(sig.declaration);
    }
    // Only functions and methods: a call through a type name is something else.
    return id && !this.isTypeId(id) && id !== this.moduleId ? id : null;
  }

  onNew(node) {
    const ts = this.ts;
    let e = node.expression;
    while (ts.isParenthesizedExpression(e)) e = e.expression;
    const id = this.typeTarget(e);
    if (id) this.ref(id, 'creates', this.line(node));
  }

  /** <OrderForm /> calls the OrderForm component. */
  onJsx(node) {
    const ts = this.ts;
    const tag = node.tagName;
    const text = tag.getText(this.sf);
    if (!/^[A-Z]/.test(text.split('.').pop())) return;              // <div>: an element, not a component
    const target = this.targetOf(ts.isPropertyAccessExpression(tag) ? tag.name : tag);
    if (!target) return;
    this.ref(target, this.isTypeId(target) ? 'creates' : 'calls', this.line(node));
  }

  /** Imports of packages that are not installed: calls into them cannot be followed. */
  countUnresolved() {
    const ts = this.ts;
    const missing = new Set();
    const check = (literal) => {
      const name = literal.text;
      if (name.startsWith('.') || name.startsWith('/') || name.startsWith('node:') || builtin(name)) return;
      let mode;
      try { mode = ts.getModeForUsageLocation(this.sf, literal, this.program.getCompilerOptions()); } catch { mode = undefined; }
      const resolved = this.safe(() => this.program.getResolvedModule?.(this.sf, name, mode)?.resolvedModule);
      if (!resolved) missing.add(name);
    };
    for (const s of this.sf.statements) {
      if ((ts.isImportDeclaration(s) || ts.isExportDeclaration(s)) && s.moduleSpecifier && ts.isStringLiteral(s.moduleSpecifier) && !s.importClause?.isTypeOnly) check(s.moduleSpecifier);
    }
    const visit = (n) => {
      if (ts.isCallExpression(n) && isRequire(ts, n) && ts.isStringLiteral(n.arguments[0])) check(n.arguments[0]);
      ts.forEachChild(n, visit);
    };
    visit(this.sf);
    this.facts.unresolved = missing.size;
  }
}

// -------------------------------------------------------------------- small helpers

export function isRequire(ts, node) {
  const n = unwrap(ts, node);
  if (!n) return false;
  if (ts.isCallExpression(n) && ts.isIdentifier(n.expression) && n.expression.text === 'require' && n.arguments.length === 1) return true;
  // require('x').y and require('x')()
  if (ts.isPropertyAccessExpression(n)) return isRequire(ts, n.expression);
  return false;
}

const BUILTIN = new Set(['assert', 'async_hooks', 'buffer', 'child_process', 'cluster', 'console', 'crypto', 'dgram', 'diagnostics_channel', 'dns', 'events',
  'fs', 'fs/promises', 'http', 'http2', 'https', 'inspector', 'module', 'net', 'os', 'path', 'perf_hooks', 'process', 'querystring', 'readline', 'repl',
  'stream', 'stream/promises', 'string_decoder', 'timers', 'timers/promises', 'tls', 'tty', 'url', 'util', 'v8', 'vm', 'worker_threads', 'zlib']);

function builtin(name) { return BUILTIN.has(name); }

function exportedNames(ts, sf) {
  const names = new Set();
  for (const s of sf.statements) {
    if (ts.isExportDeclaration(s) && !s.moduleSpecifier && s.exportClause && ts.isNamedExports(s.exportClause)) {
      for (const e of s.exportClause.elements) names.add((e.propertyName ?? e.name).text);
    }
    if (ts.isExportAssignment(s) && ts.isIdentifier(s.expression)) names.add(s.expression.text);
    if (ts.isExpressionStatement(s) && isAssignment(ts, s.expression)) {
      const e = s.expression;
      if (isModuleExports(ts, e.left)) {
        const right = unwrap(ts, e.right);
        if (ts.isIdentifier(right)) names.add(right.text);
        if (ts.isObjectLiteralExpression(right)) {
          for (const p of right.properties) {
            if (ts.isShorthandPropertyAssignment(p)) names.add(p.name.text);
            else if (ts.isPropertyAssignment(p) && ts.isIdentifier(unwrap(ts, p.initializer))) names.add(unwrap(ts, p.initializer).text);
          }
        }
      }
      const name = cjsExportName(ts, e);
      if (name) {
        names.add(name);
        const right = unwrap(ts, e.right);
        if (ts.isIdentifier(right)) names.add(right.text);
      }
    }
  }
  return names;
}

/** "src/orders/service.ts" -> "service"; index files are named after their folder: "orders/index". */
function moduleName(rel) {
  const base = path.posix.basename(rel).replace(/\.(m|c)?(j|t)sx?$/, '');
  if (base !== 'index') return base;
  const folder = path.posix.basename(path.posix.dirname(rel));
  return folder && folder !== '.' ? `${folder}/index` : 'index';
}

function folderOf(rel) {
  const dir = path.posix.dirname(rel);
  return dir === '.' ? '' : dir;
}

/** Changes only when declarations change, not code inside them. */
function declarationHash(f) {
  const lines = [];
  for (const t of f.types) lines.push([t.id, t.baseId, t.baseName, t.interfaceIds.join(','), t.access].join('|'));
  for (const m of f.members) lines.push([m.id, m.signature, m.returns, m.access, m.isStatic, m.isAbstract].join('|'));
  return crypto.createHash('sha256').update(lines.join('\n')).digest('hex');
}
