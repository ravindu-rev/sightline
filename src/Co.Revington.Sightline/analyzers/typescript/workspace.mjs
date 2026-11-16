// The packages Sightline sent, each with a TypeScript language service over its files. Texts are shared
// between them: a file has one version, so an edit reaches every package whose program includes it.

import fs from 'node:fs';
import path from 'node:path';
import { extractFile, emptyFacts } from './extract.mjs';

const EXTENSIONS = ['.ts', '.tsx', '.mts', '.cts', '.js', '.jsx', '.mjs', '.cjs'];

export class Workspace {
  constructor(ts) {
    this.ts = ts;
    this.caseSensitive = ts.sys.useCaseSensitiveFileNames;
    this.reset();
  }

  reset() {
    this.projects = new Map();          // id -> project
    this.files = new Map();             // key -> { project, host, rel, version, text }: the files on the map
    this.texts = new Map();             // key -> text of other files the programs read (declarations, packages)
    this.byPackageName = new Map();     // package.json name -> project
    this.registry = this.ts.createDocumentRegistry(this.caseSensitive);
    this.version = 0;
    this.mirror = null;
    this.moduleHost = {
      fileExists: (f) => this.files.has(this.key(f)) || this.ts.sys.fileExists(f),
      readFile: (f) => this.read(f),
      directoryExists: (d) => this.ts.sys.directoryExists(d),
      realpath: (f) => this.ts.sys.realpath ? this.ts.sys.realpath(f) : f,
      getCurrentDirectory: () => process.cwd(),
      getDirectories: (d) => this.ts.sys.getDirectories(d),
      useCaseSensitiveFileNames: this.caseSensitive,
    };
  }

  norm(p) { return String(p).replace(/\\/g, '/'); }

  key(p) {
    const n = this.norm(p);
    return this.caseSensitive ? n : n.toLowerCase();
  }

  /** The file on the map with this name, or undefined. */
  fileInfo(fileName) { return this.files.get(this.key(fileName)); }

  read(fileName) {
    const k = this.key(fileName);
    const mapped = this.files.get(k);
    if (mapped) {
      if (mapped.text === undefined) mapped.text = readText(fileName) ?? '';
      return mapped.text;
    }
    if (this.texts.has(k)) return this.texts.get(k);
    const text = readText(fileName);
    this.texts.set(k, text);
    return text;
  }

  // ---------------------------------------------------------------- open

  open({ projects, packagesFrom }, emit) {
    this.reset();
    if (packagesFrom?.from && packagesFrom?.to) this.mirror = { from: this.norm(packagesFrom.from), to: this.norm(packagesFrom.to) };
    const diagnostics = [];

    for (const p of projects) {
      const project = {
        id: p.id, name: p.name, packageName: p.packageName, dir: this.norm(p.dir),
        deps: new Set(p.dependencies ?? []), manifest: readJson(p.manifest) ?? {},
        files: new Set(), roots: [],
      };
      for (const file of p.files ?? []) {
        const k = this.key(file);
        this.files.set(k, { project, host: file, rel: path.posix.relative(project.dir, this.norm(file)), version: 1, text: undefined });
        project.files.add(k);
        project.roots.push(this.norm(file));
      }
      for (const d of p.declarations ?? []) project.roots.push(this.norm(d));
      project.options = this.options(project, p.config, diagnostics);
      this.projects.set(project.id, project);
      if (!this.byPackageName.has(project.packageName)) this.byPackageName.set(project.packageName, project);
    }
    for (const project of this.projects.values()) project.ls = this.languageService(project);

    let batch = [];
    for (const project of this.projects.values()) {
      emit({ event: 'progress', message: `Reading ${project.name}` });
      for (const k of project.files) {
        batch.push(this.extractOne(k, diagnostics));
        if (batch.length >= 40) {
          emit({ event: 'docs', docs: batch });
          batch = [];
        }
      }
    }
    if (batch.length) emit({ event: 'docs', docs: batch });
    return { diagnostics: diagnostics.slice(0, 200) };
  }

  /** Compiler options: the package's tsconfig.json or jsconfig.json, made fit for reading only. */
  options(project, configFile, diagnostics) {
    const ts = this.ts;
    let options = {};
    if (configFile) {
      const file = this.norm(configFile);
      const read = ts.readConfigFile(file, (f) => ts.sys.readFile(f));
      if (read.error) {
        diagnostics.push(`${project.name}: ${path.basename(file)} could not be read: ${flatten(ts, read.error)}`);
      } else {
        const parsed = ts.parseJsonConfigFileContent(read.config, ts.sys, path.posix.dirname(file), undefined, file);
        options = parsed.options;
        // "No inputs were found" and friends are about building, which Sightline does not do.
        const relevant = parsed.errors.filter((e) => ![18003, 5069, 5070, 5095, 5096, 5101, 5107, 5110].includes(e.code));
        for (const e of relevant.slice(0, 3)) diagnostics.push(`${project.name}: ${path.basename(file)}: ${flatten(ts, e)}`);
      }
    }

    const jsx = [...project.files].some((k) => /\.(tsx|jsx)$/.test(k));
    const nest = project.deps.has('@nestjs/common') || project.deps.has('@nestjs/core');
    const merged = {
      target: ts.ScriptTarget.ES2022,
      module: ts.ModuleKind.ESNext,
      moduleResolution: ts.ModuleResolutionKind.Bundler,
      esModuleInterop: true,
      allowSyntheticDefaultImports: true,
      resolveJsonModule: true,
      ...(jsx ? { jsx: ts.JsxEmit.ReactJSX } : {}),
      ...(nest ? { experimentalDecorators: true } : {}),
      ...options,
      // Reading, never building: every JavaScript file, no output, no checking of installed packages.
      allowJs: true, checkJs: false, noEmit: true, skipLibCheck: true, skipDefaultLibCheck: true,
      declaration: false, declarationMap: false, sourceMap: false, inlineSourceMap: false, composite: false,
      incremental: false, emitDeclarationOnly: false, allowImportingTsExtensions: true, ignoreDeprecations: '6.0',
    };
    delete merged.tsBuildInfoFile;
    delete merged.outFile;
    const bundler = merged.moduleResolution === ts.ModuleResolutionKind.Bundler;
    const esm = [ts.ModuleKind.ES2015, ts.ModuleKind.ES2020, ts.ModuleKind.ES2022, ts.ModuleKind.ESNext, ts.ModuleKind.Preserve].includes(merged.module);
    if (bundler && !esm) merged.module = ts.ModuleKind.ESNext;
    return merged;
  }

  languageService(project) {
    const ts = this.ts;
    const canonical = (f) => (this.caseSensitive ? f : f.toLowerCase());
    const cache = ts.createModuleResolutionCache(project.dir, canonical, project.options);
    const host = {
      getCompilationSettings: () => project.options,
      getScriptFileNames: () => project.roots,
      getScriptVersion: (f) => String(this.files.get(this.key(f))?.version ?? 0),
      getScriptSnapshot: (f) => {
        const text = this.read(f);
        return text === undefined ? undefined : ts.ScriptSnapshot.fromString(text);
      },
      getProjectVersion: () => String(this.version),
      getCurrentDirectory: () => project.dir,
      getDefaultLibFileName: (o) => ts.getDefaultLibFilePath(o),
      useCaseSensitiveFileNames: () => this.caseSensitive,
      fileExists: (f) => this.moduleHost.fileExists(f),
      readFile: (f) => this.read(f),
      readDirectory: (...args) => ts.sys.readDirectory(...args),
      directoryExists: (d) => ts.sys.directoryExists(d),
      getDirectories: (d) => ts.sys.getDirectories(d),
      realpath: (f) => this.moduleHost.realpath(f),
      resolveModuleNameLiterals: (literals, containingFile, redirected, options, sourceFile) =>
        literals.map((literal) => this.resolve(literal, containingFile, options, sourceFile, cache, redirected)),
    };
    return ts.createLanguageService(host, this.registry);
  }

  /** Imports of a package in the repository go to its source, also when nothing is installed or built. */
  resolve(literal, containingFile, options, sourceFile, cache, redirected) {
    const ts = this.ts;
    const name = literal.text;
    const local = this.localPackage(name);
    if (local) {
      return { resolvedModule: { resolvedFileName: local, extension: ts.extensionFromPath(local), isExternalLibraryImport: false, resolvedUsingTsExtension: false } };
    }
    let mode;
    try { mode = ts.getModeForUsageLocation(sourceFile, literal, options); } catch { mode = undefined; }
    let result = ts.resolveModuleName(name, containingFile, options, this.moduleHost, cache, redirected, mode);
    if (!result.resolvedModule && this.mirror && isBare(name)) {
      const mirrored = this.mirrored(containingFile);
      if (mirrored) result = ts.resolveModuleName(name, mirrored, options, this.moduleHost, undefined, undefined, mode);
    }
    return result;
  }

  /** "@shop/shared" or "@shop/shared/money" -> the source file in that package's folder, if it is on the map. */
  localPackage(spec) {
    if (!isBare(spec)) return null;
    const parts = spec.split('/');
    const scoped = spec.startsWith('@');
    const name = (scoped ? parts.slice(0, 2) : parts.slice(0, 1)).join('/');
    const sub = parts.slice(scoped ? 2 : 1).join('/');
    const project = this.byPackageName.get(name);
    if (!project) return null;

    const m = project.manifest;
    const candidates = [];
    if (!sub) {
      for (const field of [m.source, exportTarget(m.exports, '.'), m.types, m.typings, m.module, m.main]) if (typeof field === 'string') candidates.push(field);
      candidates.push('src/index', 'index', 'src/main', 'lib/index');
    } else {
      const target = exportTarget(m.exports, './' + sub);
      if (target) candidates.push(target);
      candidates.push(sub, 'src/' + sub);
    }
    for (const c of candidates) {
      const found = this.sourceIn(project.dir, c);
      if (found) return found;
    }
    return null;
  }

  /** A path a package.json names ("dist/index.js", "./src/index.ts") as the source file it is built from. */
  sourceIn(dir, relative) {
    const clean = String(relative).replace(/^\.\//, '');
    const stems = [clean, clean.replace(/^(dist|lib|build|out|esm|cjs|types)\//, 'src/')];
    for (const s of stems) {
      const stem = s.replace(/\.d\.(ts|mts|cts)$/, '').replace(/\.(m|c)?(j|t)sx?$/, '');
      for (const candidate of [...EXTENSIONS.map((e) => stem + e), ...EXTENSIONS.map((e) => stem + '/index' + e)]) {
        const full = path.posix.join(dir, candidate);
        if (this.files.has(this.key(full))) return full;
      }
    }
    return null;
  }

  mirrored(file) {
    const f = this.norm(file);
    const from = this.mirror.from.replace(/\/$/, '') + '/';
    const same = this.caseSensitive ? f.startsWith(from) : f.toLowerCase().startsWith(from.toLowerCase());
    return same ? this.mirror.to.replace(/\/$/, '') + '/' + f.slice(from.length) : null;
  }

  // ---------------------------------------------------------------- edits

  /** changes: { path, text } for new text (also a new file, with project), or { path, remove: true }. */
  apply({ changes }) {
    const touched = [];
    for (const c of changes ?? []) {
      const k = this.key(c.path);
      if (c.remove) {
        const f = this.files.get(k);
        if (!f) continue;
        f.project.files.delete(k);
        f.project.roots = f.project.roots.filter((r) => this.key(r) !== k);
        this.files.delete(k);
        continue;
      }
      let f = this.files.get(k);
      if (!f && c.project) {
        const project = this.projects.get(c.project);
        if (!project) continue;
        f = { project, host: c.path, rel: path.posix.relative(project.dir, this.norm(c.path)), version: 0, text: undefined };
        this.files.set(k, f);
        project.files.add(k);
        project.roots.push(this.norm(c.path));
      }
      if (!f || typeof c.text !== 'string') continue;
      if (f.text === c.text && f.version > 0) continue;
      f.text = c.text;
      f.version++;
      touched.push(k);
    }
    this.version++;
    return { docs: touched.map((k) => this.extractOne(k)) };
  }

  extract({ paths }) {
    return { docs: (paths ?? []).map((p) => this.key(p)).filter((k) => this.files.has(k)).map((k) => this.extractOne(k)) };
  }

  /** The facts of one file. A file that cannot be read still answers, with no facts, and the reason goes to the log. */
  extractOne(k, diagnostics) {
    const f = this.files.get(k);
    try {
      const program = f.project.ls.getProgram();
      const sf = program.getSourceFile(this.norm(f.host));
      if (!sf) return emptyFacts(f);
      return extractFile(this, f.project, f, sf, program);
    } catch (err) {
      const message = `${f.project.name}/${f.rel} could not be read: ${String(err?.message ?? err).split('\n')[0]}`;
      if (diagnostics) diagnostics.push(message);
      console.error(message + '\n' + (err?.stack ?? ''));
      return emptyFacts(f);
    }
  }
}

function readText(file) {
  try {
    return fs.readFileSync(file, 'utf8').replace(/^﻿/, '');
  } catch {
    return undefined;
  }
}

function readJson(file) {
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8').replace(/^﻿/, ''));
  } catch {
    return null;
  }
}

function isBare(spec) {
  return !spec.startsWith('.') && !spec.startsWith('/') && !/^[a-zA-Z]:/.test(spec) && !spec.startsWith('node:') && !spec.includes(':');
}

/** "exports" of a package.json, for one entry: the plain path, or the best condition. */
function exportTarget(exports, entry) {
  if (!exports) return null;
  if (typeof exports === 'string') return entry === '.' ? exports : null;
  if (Array.isArray(exports)) return exportTarget(exports[0], entry);
  if (typeof exports !== 'object') return null;
  const keys = Object.keys(exports);
  const value = keys.some((k) => k.startsWith('.')) ? exports[entry] : entry === '.' ? exports : undefined;
  return condition(value);
}

function condition(value) {
  if (!value) return null;
  if (typeof value === 'string') return value;
  if (Array.isArray(value)) return condition(value[0]);
  for (const name of ['source', 'development', 'types', 'import', 'module', 'default', 'require', 'node']) {
    const found = condition(value[name]);
    if (found) return found;
  }
  return null;
}

function flatten(ts, diagnostic) {
  return ts.flattenDiagnosticMessageText(diagnostic.messageText, ' ');
}
