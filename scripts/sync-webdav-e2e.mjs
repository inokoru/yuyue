#!/usr/bin/env node
/**
 * Layer 5: Cross-device WebDAV sync E2E harness driven by real `yuyue-cli` processes.
 *
 * Starts an embedded RFC 4918 WebDAV server (OPTIONS, MKCOL, PROPFIND, GET, PUT, DELETE + Basic Auth)
 * implementing the exact folder layout shared by Apple (`Sync.swift`), Android (`Sync.kt`), and
 * Windows (`SyncService.cs`):
 *   - `<deviceId>.yuyue-sync.json`  (full CRDT op log batch per device)
 *   - `<bookId>.book.<ext>`         (original book file shared across devices)
 *
 * Scenarios verified:
 *  1. webdav.bidirectional_sync             — Device A imports book/progress/settings/mark -> WebDAV -> Device B pulls & imports
 *  2. webdav.disjoint_settings_merge        — Offline edits to distinct settings fields merge cleanly on both devices
 *  3. webdav.offline_conflict_resolution    — Concurrent thought edits surface conflict -> resolve `both` -> both converge
 *  4. webdav.delete_no_resurrection         — Deleting a book propagates and never resurrects; repeat sync is idempotent
 *
 * Usage:
 *   node scripts/sync-webdav-e2e.mjs
 *   node scripts/sync-webdav-e2e.mjs --cli path/to/yuyue-cli
 *   node scripts/sync-webdav-e2e.mjs --webdav-url http://127.0.0.1:5000/dav/yuyue/
 */

import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const FIXTURE_EPUB = path.join(ROOT, 'fixtures', 'books', 'beiying.epub');

function xmlEscape(s) {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

/**
 * Minimal RFC 4918 WebDAV server with Basic Auth (`alice:secret`) over a local directory.
 */
function startWebDavServer(storageDir, expectedAuth) {
  fs.mkdirSync(storageDir, { recursive: true });

  const resolveSafe = (urlPath) => {
    const clean = decodeURIComponent(urlPath.split('?')[0]);
    const rel = clean.replace(/^\/+/, '');
    const full = path.resolve(storageDir, rel);
    if (!full.startsWith(path.resolve(storageDir))) {
      throw new Error('path traversal');
    }
    return { clean, full };
  };

  const server = http.createServer((req, res) => {
    if (expectedAuth && req.headers.authorization !== expectedAuth) {
      res.writeHead(401, { 'WWW-Authenticate': 'Basic realm="yuyue-test"' });
      res.end();
      return;
    }

    const method = req.method || 'GET';
    let target;
    try {
      target = resolveSafe(req.url || '/');
    } catch {
      res.writeHead(403);
      res.end();
      return;
    }

    if (method === 'OPTIONS') {
      res.writeHead(200, { DAV: '1, 2', Allow: 'OPTIONS, GET, PUT, DELETE, MKCOL, PROPFIND' });
      res.end();
      return;
    }

    if (method === 'MKCOL') {
      if (fs.existsSync(target.full)) {
        res.writeHead(405);
        res.end();
        return;
      }
      fs.mkdirSync(target.full, { recursive: true });
      res.writeHead(201);
      res.end();
      return;
    }

    if (method === 'PROPFIND') {
      if (!fs.existsSync(target.full) || !fs.statSync(target.full).isDirectory()) {
        res.writeHead(404);
        res.end();
        return;
      }
      const baseHref = target.clean.endsWith('/') ? target.clean : `${target.clean}/`;
      const entries = fs.readdirSync(target.full, { withFileTypes: true });
      const items = [
        `<d:response><d:href>${xmlEscape(baseHref)}</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>`,
      ];
      for (const ent of entries) {
        const full = path.join(target.full, ent.name);
        const st = fs.statSync(full);
        const href = baseHref + encodeURIComponent(ent.name) + (ent.isDirectory() ? '/' : '');
        if (ent.isDirectory()) {
          items.push(
            `<d:response><d:href>${xmlEscape(href)}</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>`,
          );
        } else {
          const etag = `"${crypto.createHash('sha1').update(fs.readFileSync(full)).digest('hex').slice(0, 16)}"`;
          items.push(
            `<d:response><d:href>${xmlEscape(href)}</d:href><d:propstat><d:prop><d:resourcetype/><d:getetag>${etag}</d:getetag><d:getcontentlength>${st.size}</d:getcontentlength><d:getlastmodified>${st.mtime.toUTCString()}</d:getlastmodified></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>`,
          );
        }
      }
      const body = `<?xml version="1.0" encoding="utf-8"?><d:multistatus xmlns:d="DAV:">${items.join('')}</d:multistatus>`;
      res.writeHead(207, { 'Content-Type': 'application/xml; charset=utf-8' });
      res.end(body);
      return;
    }

    if (method === 'PUT') {
      const chunks = [];
      req.on('data', (c) => chunks.push(c));
      req.on('end', () => {
        fs.mkdirSync(path.dirname(target.full), { recursive: true });
        const existed = fs.existsSync(target.full);
        fs.writeFileSync(target.full, Buffer.concat(chunks));
        res.writeHead(existed ? 204 : 201);
        res.end();
      });
      return;
    }

    if (method === 'GET') {
      if (!fs.existsSync(target.full) || fs.statSync(target.full).isDirectory()) {
        res.writeHead(404);
        res.end();
        return;
      }
      const buf = fs.readFileSync(target.full);
      const ctype = target.full.endsWith('.json') ? 'application/json; charset=utf-8' : 'application/octet-stream';
      res.writeHead(200, { 'Content-Type': ctype, 'Content-Length': buf.length });
      res.end(buf);
      return;
    }

    if (method === 'DELETE') {
      if (fs.existsSync(target.full)) fs.rmSync(target.full, { recursive: true, force: true });
      res.writeHead(204);
      res.end();
      return;
    }

    res.writeHead(405);
    res.end();
  });

  return new Promise((resolve) => {
    server.listen(0, '127.0.0.1', () => {
      const addr = server.address();
      resolve({ server, baseUrl: `http://127.0.0.1:${addr.port}/dav/语阅/` });
    });
  });
}

function findOrBuildCli() {
  const idx = process.argv.indexOf('--cli');
  if (idx >= 0 && process.argv[idx + 1]) return path.resolve(process.argv[idx + 1]);

  const exeName = process.platform === 'win32' ? 'yuyue.exe' : 'yuyue';
  const targetDir = process.env.CARGO_TARGET_DIR || path.join(ROOT, 'core', 'target');
  const candidates = [
    path.join(targetDir, 'debug', exeName),
    path.join(targetDir, 'release', exeName),
    path.join(ROOT, 'core', 'target', 'debug', exeName),
    path.join(ROOT, 'core', 'target', 'release', exeName),
  ];
  for (const c of candidates) {
    if (fs.existsSync(c)) return c;
  }

  const cargoBin = process.platform === 'win32' ? path.join(os.homedir(), '.cargo', 'bin', 'cargo.exe') : 'cargo';
  const cargo = fs.existsSync(cargoBin) ? cargoBin : 'cargo';
  console.log('Building yuyue-cli...');
  const build = spawnSync(cargo, ['build', '--manifest-path', path.join(ROOT, 'core', 'Cargo.toml'), '-p', 'yuyue-cli'], {
    stdio: 'inherit',
    env: { ...process.env, CARGO_INCREMENTAL: '0' },
  });
  if (build.status !== 0) throw new Error(`cargo build -p yuyue-cli failed (${build.status})`);
  const built = path.join(targetDir, 'debug', exeName);
  if (!fs.existsSync(built)) throw new Error(`yuyue-cli not found at ${built}`);
  return built;
}

class WebDavClient {
  constructor(folderUrl, authHeader) {
    this.folderUrl = folderUrl.endsWith('/') ? folderUrl : `${folderUrl}/`;
    this.authHeader = authHeader;
  }

  async prepare() {
    const u = new URL(this.folderUrl);
    const segments = u.pathname.split('/').filter(Boolean);
    let cur = `${u.origin}/`;
    for (const seg of segments) {
      cur += `${seg}/`;
      const r = await fetch(cur, { method: 'MKCOL', headers: { Authorization: this.authHeader } });
      assert.ok([201, 301, 405].includes(r.status), `MKCOL ${cur} failed: ${r.status}`);
    }
  }

  async list() {
    const r = await fetch(this.folderUrl, {
      method: 'PROPFIND',
      headers: {
        Authorization: this.authHeader,
        Depth: '1',
        'Content-Type': 'application/xml; charset=utf-8',
      },
      body: '<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getetag/><d:getcontentlength/></d:prop></d:propfind>',
    });
    assert.equal(r.status, 207, `PROPFIND expected 207, got ${r.status}`);
    const xml = await r.text();
    const files = new Map();
    const re = /<d:response>([\s\S]*?)<\/d:response>/g;
    let m;
    while ((m = re.exec(xml)) !== null) {
      const block = m[1];
      if (block.includes('<d:collection')) continue;
      const href = /<d:href>([^<]+)<\/d:href>/.exec(block)?.[1];
      const etag = /<d:getetag>([^<]+)<\/d:getetag>/.exec(block)?.[1] || '';
      if (!href) continue;
      const name = decodeURIComponent(href.replace(/\/$/, '').split('/').pop() || '');
      if (name) files.set(name, etag);
    }
    return files;
  }

  async put(name, data, contentType = 'application/octet-stream') {
    const url = new URL(encodeURIComponent(name), this.folderUrl);
    const r = await fetch(url, {
      method: 'PUT',
      headers: { Authorization: this.authHeader, 'Content-Type': contentType },
      body: data,
    });
    assert.ok([200, 201, 204].includes(r.status), `PUT ${name} failed: ${r.status}`);
  }

  async get(name) {
    const url = new URL(encodeURIComponent(name), this.folderUrl);
    const r = await fetch(url, { method: 'GET', headers: { Authorization: this.authHeader } });
    assert.equal(r.status, 200, `GET ${name} failed: ${r.status}`);
    return Buffer.from(await r.arrayBuffer());
  }
}

function makeDevice(cliPath, workDir, slug, name, platform) {
  const dir = path.join(workDir, `lib-${slug}`);
  fs.mkdirSync(dir, { recursive: true });
  const run = (...args) =>
    execFileSync(cliPath, ['--device', name, '--platform', platform, ...args], {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe'],
    }).trim();
  const id = run('device-id', dir);
  return { dir, name, platform, id, run };
}

/**
 * Mirrors Apple `Sync.exchange` + `Sync.exchangeFiles` over WebDAV:
 *  1. PROPFIND folder
 *  2. Import every other device's `<id>.yuyue-sync.json`
 *  3. Export & PUT this device's `<id>.yuyue-sync.json`
 *  4. Upload missing `<bookId>.book.<ext>` and download missing remote books
 */
async function syncDevice(dev, dav, workDir) {
  await dav.prepare();
  const mine = `${dev.id}.yuyue-sync.json`;
  const files = await dav.list();

  let totalApplied = 0;
  for (const [fname] of files) {
    if (fname.endsWith('.yuyue-sync.json') && fname !== mine) {
      const payload = await dav.get(fname);
      const tmp = path.join(workDir, `in-${dev.id}-${fname}`);
      fs.writeFileSync(tmp, payload);
      const out = dev.run('import-changes', dev.dir, tmp);
      const n = Number(/^(\d+)/.exec(out)?.[1] || 0);
      totalApplied += n;
    }
  }

  const exported = dev.run('export', dev.dir);
  await dav.put(mine, Buffer.from(exported, 'utf8'), 'application/json; charset=utf-8');

  // Book files: upload local books absent remotely; download remote books absent locally
  const listOut = dev.run('list', dev.dir);
  const bookIds = listOut
    .split(/\r?\n/)
    .map((l) => l.trim().split(/\s+/)[0])
    .filter((id) => id && id.length === 32);

  const afterFiles = await dav.list();
  for (const bid of bookIds) {
    const alreadyRemote = [...afterFiles.keys()].some((k) => k.startsWith(`${bid}.book.`));
    if (!alreadyRemote) {
      try {
        const orig = dev.run('original-file', dev.dir, bid);
        const ext = path.extname(orig).replace(/^\./, '') || 'epub';
        await dav.put(`${bid}.book.${ext}`, fs.readFileSync(orig));
      } catch {
        // book not available locally yet
      }
    } else {
      let hasLocal = false;
      try {
        dev.run('original-file', dev.dir, bid);
        hasLocal = true;
      } catch {
        hasLocal = false;
      }
      if (!hasLocal) {
        const remoteName = [...afterFiles.keys()].find((k) => k.startsWith(`${bid}.book.`));
        if (remoteName) {
          const ext = path.extname(remoteName);
          const dl = path.join(workDir, `dl-${dev.id}-${bid}${ext}`);
          fs.writeFileSync(dl, await dav.get(remoteName));
          dev.run('import', dev.dir, dl);
        }
      }
    }
  }

  return totalApplied;
}

async function main() {
  const cliPath = findOrBuildCli();
  const workDir = fs.mkdtempSync(path.join(os.tmpdir(), 'yuyue-webdav-e2e-'));
  const auth = `Basic ${Buffer.from('alice:secret').toString('base64')}`;

  const extUrlIdx = process.argv.indexOf('--webdav-url');
  let serverObj = null;
  let folderUrl;
  if (extUrlIdx >= 0 && process.argv[extUrlIdx + 1]) {
    folderUrl = process.argv[extUrlIdx + 1];
  } else {
    serverObj = await startWebDavServer(path.join(workDir, 'webdav-root'), auth);
    folderUrl = serverObj.baseUrl;
  }

  const dav = new WebDavClient(folderUrl, auth);

  try {
    const mac = makeDevice(cliPath, workDir, 'mac', 'MacBook', 'macos');
    const android = makeDevice(cliPath, workDir, 'android', 'Pixel', 'android');

    // Scenario 1: webdav.bidirectional_sync
    const importOut = mac.run('import', mac.dir, FIXTURE_EPUB);
    const bookId = importOut.split(/\s+/)[0];
    assert.equal(bookId.length, 32, `unexpected book id: ${importOut}`);
    mac.run('progress', mac.dir, bookId, '0', '2');
    mac.run('settings', mac.dir, 'dark', '19');
    const markId = mac.run('mark', mac.dir, bookId, '0', '0', '我与父亲', '初稿');

    await syncDevice(mac, dav, workDir);
    const applied1 = await syncDevice(android, dav, workDir);
    assert.ok(applied1 > 0, 'Android should apply Mac changes over WebDAV');
    assert.ok(android.run('list', android.dir).includes(bookId), 'Android should have synced book');
    assert.equal(JSON.parse(android.run('settings', android.dir)).paper, 'dark');
    const androidMarks1 = JSON.parse(android.run('marks', android.dir, bookId));
    assert.equal(androidMarks1.length, 1);
    assert.equal(androidMarks1[0].note, '初稿');
    // Verify original book file also synced via WebDAV so Android can read sentences locally
    assert.ok(android.run('sentences', android.dir, bookId, '0').length > 0);
    console.log('✓ webdav.bidirectional_sync');

    // Scenario 2: webdav.disjoint_settings_merge
    mac.run('settings', mac.dir, 'kami', '-');
    android.run('settings', android.dir, '-', '22');
    await syncDevice(mac, dav, workDir);
    await syncDevice(android, dav, workDir);
    await syncDevice(mac, dav, workDir);
    for (const dev of [mac, android]) {
      const s = JSON.parse(dev.run('settings', dev.dir));
      assert.equal(s.paper, 'kami', `${dev.name} paper should merge to kami`);
      assert.equal(s.font_size, 22, `${dev.name} font_size should merge to 22`);
    }
    console.log('✓ webdav.disjoint_settings_merge');

    // Scenario 3: webdav.offline_conflict_resolution
    mac.run('mark-note', mac.dir, bookId, markId, 'Mac 端想法');
    await new Promise((r) => setTimeout(r, 10));
    android.run('mark-note', android.dir, bookId, markId, 'Android 端想法');
    await syncDevice(mac, dav, workDir);
    await syncDevice(android, dav, workDir);
    const conflicts = JSON.parse(android.run('conflicts', android.dir));
    assert.equal(conflicts.length, 1, 'Android must detect offline concurrent note conflict');
    assert.equal(conflicts[0].older_note, 'Mac 端想法');
    assert.equal(conflicts[0].newer_note, 'Android 端想法');

    android.run('resolve-conflict', android.dir, bookId, markId, 'both');
    assert.equal(JSON.parse(android.run('conflicts', android.dir)).length, 0);
    await syncDevice(android, dav, workDir);
    await syncDevice(mac, dav, workDir);

    const expectedMerged = 'Mac 端想法\n\nAndroid 端想法';
    assert.equal(JSON.parse(android.run('marks', android.dir, bookId))[0].note, expectedMerged);
    assert.equal(JSON.parse(mac.run('marks', mac.dir, bookId))[0].note, expectedMerged);
    console.log('✓ webdav.offline_conflict_resolution');

    // Scenario 4: webdav.delete_no_resurrection
    android.run('delete-book', android.dir, bookId);
    await syncDevice(android, dav, workDir);
    await syncDevice(mac, dav, workDir);
    assert.equal(mac.run('list', mac.dir), '', 'Deleted book must disappear on Mac');
    assert.equal(android.run('list', android.dir), '', 'Deleted book must stay deleted on Android');
    // Repeat sync must be strictly idempotent and must not resurrect the deleted book
    const repeatMac = await syncDevice(mac, dav, workDir);
    const repeatAndroid = await syncDevice(android, dav, workDir);
    assert.equal(repeatMac, 0, 'Repeat sync on Mac must apply 0 changes');
    assert.equal(repeatAndroid, 0, 'Repeat sync on Android must apply 0 changes');
    assert.equal(mac.run('list', mac.dir), '', 'Deleted book must never resurrect');
    console.log('✓ webdav.delete_no_resurrection');

    console.log('\nWebDAV multi-device sync E2E PASSED (4/4 scenarios).');
  } finally {
    if (serverObj) serverObj.server.close();
    fs.rmSync(workDir, { recursive: true, force: true });
  }
}

main().catch((err) => {
  console.error('\nWebDAV multi-device sync E2E FAILED:', err);
  process.exit(1);
});
