#!/usr/bin/env node
// Builds the website into _site/ (or --out <dir>):
//
//   1. the landing page, from site/
//   2. a demo made of the tool's real UI files (src/.../wwwroot), with site/demo/demo-api.js
//      taking the place of the server
//   3. a recording for that demo, taken from the real tool running on samples/Shop
//
// Because the demo is rebuilt from the tool on every run, it never drifts from the real UI.
//
// Usage:  node site/generate.mjs [--out _site] [--serve [port]]
// Needs:  the .NET SDK, Node 20 or newer, and "dotnet restore samples/Shop/Shop.sln" done once.

import { execFileSync, spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const option = (name, fallback) => {
  const i = args.indexOf(name);
  return i < 0 ? fallback : args[i + 1];
};

const out = path.resolve(root, option('--out', '_site'));
const site = path.join(root, 'site');
const project = 'src/Co.Revington.Sightline';
const sample = 'samples/Shop/Shop.sln';

// The one code edit the demo can replay. It is typed into GetOrderHandler.Handle while the
// flow of this endpoint is on screen, so a new step visibly appears.
const EDIT = {
  endpoint: { verb: 'GET', route: '/api/orders/{id:int}' },
  station: 'GetOrderHandler',
  anchor: 'var order = await orders.FindAsync(request.Id, ct);',
  text: '        await orders.SaveAsync(ct);',
  marker: 'orders.SaveAsync(',
};

// -------------------------------------------------------------------- landing page

/** Latest released version: the newest tag, or the project file before the first release. */
function latestVersion() {
  try {
    const tag = execFileSync('git', ['describe', '--tags', '--abbrev=0', '--match', 'v[0-9]*'],
      { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
    if (tag) return tag.replace(/^v/, '');
  } catch {
    // no tags yet
  }
  const props = fs.readFileSync(path.join(root, 'src', 'Directory.Build.props'), 'utf8');
  return /<VersionPrefix>([^<]+)</.exec(props)?.[1] ?? '0.0.0';
}

function buildLandingPage(version) {
  fs.rmSync(out, { recursive: true, force: true });
  fs.mkdirSync(out, { recursive: true });

  // sightline.schema.json: editors fetch it to suggest the settings in sightline.json.
  for (const name of ['site.css', 'site.js', 'sightline.schema.json']) fs.copyFileSync(path.join(site, name), path.join(out, name));
  fs.cpSync(path.join(site, 'fonts'), path.join(out, 'fonts'), { recursive: true });
  fs.copyFileSync(path.join(root, 'assets', 'icon.png'), path.join(out, 'icon.png'));

  const html = fs.readFileSync(path.join(site, 'index.html'), 'utf8').replaceAll('{{version}}', version);
  fs.writeFileSync(path.join(out, 'index.html'), html);
}

// -------------------------------------------------------------------- demo

function buildDemo() {
  const demo = path.join(out, 'demo');
  fs.cpSync(path.join(root, project, 'wwwroot'), demo, { recursive: true });

  for (const name of ['demo.js', 'demo.css']) fs.copyFileSync(path.join(site, 'demo', name), path.join(demo, name));
  fs.copyFileSync(path.join(site, 'demo', 'demo-api.js'), path.join(demo, 'js', 'api.js'));      // the server's stand-in

  // The tool serves its page from "/". On the website the demo lives in a sub-folder, so links become relative.
  const page = path.join(demo, 'index.html');
  const html = fs.readFileSync(page, 'utf8')
    .replaceAll('href="/', 'href="./')
    .replaceAll('src="/', 'src="./')
    .replace('__SIGHTLINE_TOKEN__', 'demo')
    .replace('<title>Sightline</title>', '<title>Sightline demo</title>')
    .replace('</head>', '  <meta name="robots" content="noindex">\n  <link rel="stylesheet" href="./demo.css">\n</head>')
    .replace('</body>', '  <script type="module" src="./demo.js"></script>\n</body>');

  if (!html.includes('./demo.js') || !html.includes('./demo.css') || /(href|src)="\//.test(html))
    throw new Error('The demo page could not be adapted: the tool\'s index.html has changed shape.');
  fs.writeFileSync(page, html);
  return demo;
}

// -------------------------------------------------------------------- recording

/** Runs the real tool on the sample and records what the demo needs. */
async function record() {
  const token = 'site-recording';
  const child = spawn('dotnet', ['run', '--project', project, '-c', 'Release', '--', sample, '--no-open', '--port', '5391', '--token', token], {
    cwd: root,
    stdio: ['ignore', 'pipe', 'inherit'],
    detached: process.platform !== 'win32',          // own process group, so the whole tree can be stopped
  });

  try {
    const base = await addressOf(child);
    const call = async (target, init = {}) => {
      const response = await fetch(base + target, { ...init, headers: { 'X-Sightline-Token': token, ...init.headers } });
      if (!response.ok) throw new Error(`${init.method ?? 'GET'} ${target} answered ${response.status}`);
      return response;
    };

    // 1. Wait until the solution is loaded.
    let graph;
    for (const started = Date.now(); ;) {
      graph = await (await call('/api/graph')).json();
      if (graph.status.state === 'ready') break;
      if (graph.status.state === 'error') throw new Error('Sightline could not load the sample: ' + graph.status.message);
      if (Date.now() - started > 300_000) throw new Error('Sightline did not finish loading the sample within 5 minutes.');
      await new Promise((resolve) => setTimeout(resolve, 500));
    }

    // 2. The source of every file the graph points at.
    const files = [...new Set(graph.nodes.filter((n) => n.file && n.kind !== 'project').map((n) => n.file))];
    const sources = {};
    for (const file of files)
      sources[file] = (await (await call('/api/source?file=' + encodeURIComponent(file))).json()).text;

    // 3. One real edit, to capture the live patches the tool sends for it and for taking it back.
    const file = graph.nodes.find((n) => n.kind === 'type' && n.name === EDIT.station)?.file;
    const original = sources[file];
    if (!original?.includes(EDIT.anchor))
      throw new Error(`The sample no longer has the line the demo edit follows: ${EDIT.anchor}`);

    const eol = original.includes('\r\n') ? '\r\n' : '\n';
    const lines = original.split(eol);
    const at = lines.findIndex((l) => l.includes(EDIT.anchor));
    const edited = [...lines.slice(0, at + 1), EDIT.text, ...lines.slice(at + 1)].join(eol);

    const patches = await listen(base, token);
    await call('/api/overlay', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ file, text: edited }) });
    const apply = await patches.next();
    await call('/api/overlay?file=' + encodeURIComponent(file), { method: 'DELETE' });
    const revert = await patches.next();
    patches.close();

    if (!apply.edges.added.some((e) => e.kind === 'calls' && e.target.includes('SaveAsync')))
      throw new Error('The recorded edit did not add the expected call. The analyzer or the sample has changed.');

    const recording = withoutLocalPaths({ graph, sources, edit: { ...EDIT, file, apply, revert } });
    assertNoLocalPaths(recording);
    return recording;
  } finally {
    stop(child);
  }
}

/** Resolves with the address the tool prints once it is listening. */
function addressOf(child) {
  return new Promise((resolve, reject) => {
    let log = '';
    const timer = setTimeout(() => reject(new Error('Sightline did not start within 5 minutes.\n' + log)), 300_000);
    child.stdout.on('data', (chunk) => {
      log += chunk;
      const match = /url\s+(http:\/\/localhost:\d+)/.exec(log);
      if (match) {
        clearTimeout(timer);
        resolve(match[1]);
      }
    });
    child.on('error', (err) => { clearTimeout(timer); reject(err); });
    child.on('exit', (code) => { clearTimeout(timer); reject(new Error(`Sightline exited early (code ${code}).\n${log}`)); });
  });
}

/** Follows the tool's live event stream. next() gives the next patch caused by an edit. */
async function listen(base, token) {
  const abort = new AbortController();
  const response = await fetch(`${base}/api/events?token=${token}`, { signal: abort.signal });
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';

  return {
    async next() {
      const deadline = Date.now() + 30_000;
      for (;;) {
        for (let cut; (cut = buffer.indexOf('\n\n')) >= 0;) {
          const block = buffer.slice(0, cut);
          buffer = buffer.slice(cut + 2);
          if (!/^event: patch$/m.test(block)) continue;
          const patch = JSON.parse(/^data: (.*)$/m.exec(block)[1]);
          if (patch.reason === 'edit') return patch;
        }
        if (Date.now() > deadline) throw new Error('The tool sent no patch for the demo edit within 30 seconds.');
        const { value, done } = await reader.read();     // the tool pings every 15 s, so this never waits long
        if (done) throw new Error('The tool closed its event stream.');
        buffer += decoder.decode(value, { stream: true });
      }
    },
    close() {
      abort.abort();
    },
  };
}

function stop(child) {
  if (child.exitCode !== null) return;
  if (process.platform === 'win32') spawnSync('taskkill', ['/pid', String(child.pid), '/T', '/F'], { stdio: 'ignore' });
  else {
    try { process.kill(-child.pid, 'SIGTERM'); } catch { /* already gone */ }
  }
}

// The tool reports absolute file paths. On the website they are shown relative to samples/,
// and nothing from the machine that built the site may be published.

const samples = path.join(root, 'samples');
const prefixes = [samples + path.sep, samples.replaceAll('\\', '/') + '/'].map((p) => p.toLowerCase());

function relative(text) {
  const lower = text.toLowerCase();
  const prefix = prefixes.find((p) => lower.startsWith(p));
  return prefix ? text.slice(prefix.length).replaceAll('\\', '/') : text;
}

function withoutLocalPaths(value) {
  if (typeof value === 'string') return relative(value);
  if (Array.isArray(value)) return value.map(withoutLocalPaths);
  if (value && typeof value === 'object')
    return Object.fromEntries(Object.entries(value).map(([key, child]) => [relative(key), withoutLocalPaths(child)]));
  return value;
}

function assertNoLocalPaths(recording) {
  const forbidden = [root, os.homedir()].flatMap((p) => [p, p.replaceAll('\\', '/')]).map((p) => p.toLowerCase());
  const visit = (value, where) => {
    if (typeof value === 'string') {
      const lower = value.toLowerCase();
      if (forbidden.some((p) => lower.includes(p)))
        throw new Error(`The recording still holds a path from this machine at ${where}. Refusing to publish it.`);
    } else if (value && typeof value === 'object') {
      for (const [key, child] of Object.entries(value)) {
        visit(key, where);
        visit(child, `${where}.${key}`);
      }
    }
  };
  visit(recording, 'recording');
}

// -------------------------------------------------------------------- preview server

function serve(port) {
  const types = {
    '.html': 'text/html; charset=utf-8', '.css': 'text/css', '.js': 'text/javascript', '.json': 'application/json',
    '.png': 'image/png', '.woff2': 'font/woff2', '.md': 'text/plain', '.txt': 'text/plain',
  };

  http.createServer((request, response) => {
    let target = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
    let file = path.join(out, target);
    if (!file.startsWith(out)) return response.writeHead(403).end();

    if (fs.existsSync(file) && fs.statSync(file).isDirectory()) {
      if (!target.endsWith('/')) return response.writeHead(301, { Location: target + '/' }).end();
      file = path.join(file, 'index.html');
    }
    if (!fs.existsSync(file)) return response.writeHead(404).end('Not found');

    response.writeHead(200, { 'Content-Type': types[path.extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-store' });
    fs.createReadStream(file).pipe(response);
  }).listen(port, '127.0.0.1', () => console.log(`Preview: http://localhost:${port}/  (Ctrl+C to stop)`));
}

// -------------------------------------------------------------------- run

const version = latestVersion();
console.log(`Building the Sightline website for version ${version}`);
buildLandingPage(version);
const demo = buildDemo();

console.log('Recording the demo: starting the real tool on the sample solution...');
const recording = await record();
fs.writeFileSync(path.join(demo, 'demo-data.json'), JSON.stringify(recording));
console.log(`Recorded ${recording.graph.nodes.length} nodes, ${Object.keys(recording.sources).length} source files, one edit.`);
console.log(`Website written to ${out}`);

if (args.includes('--serve')) serve(Number(option('--serve')) || 4173);
