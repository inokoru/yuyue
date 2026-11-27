#!/usr/bin/env node
/**
 * Layer 4: Multi-platform Midscene.js runner & preflight doctor for Yuyue.
 *
 * Supports all 5 targets from one shared scenario set (`scenarios/*.yaml`):
 *   - windows : WinUI 3 desktop (`@midscene/computer`, auto-launches isolated `--library <tmp>`)
 *   - macos   : macOS App (`@midscene/computer`, auto-launches Debug `Yuyue.app` with `-debugLibrary <tmp>`)
 *   - android : Android phone/emulator (`@midscene/android`, checks adb, auto-pushes fixtures)
 *   - ios     : iPhone/iPad/Simulator (`@midscene/ios` via WebDriverAgent, probes WDA & stages simulator fixtures)
 *   - web     : Shared Web reader (`@midscene/web`, auto-starts `reader/scripts/serve.mjs` on `/demo/`)
 *
 * Usage:
 *   node scripts/run-midscene.mjs --platform windows --render-only
 *   node scripts/run-midscene.mjs --platform all --render-only
 *   node scripts/run-midscene.mjs --platform android --doctor
 *   node scripts/run-midscene.mjs --verify-model
 *   node scripts/run-midscene.mjs --platform windows [--build release|debug] [--scenario <id>]
 *   node scripts/run-midscene.mjs --platform macos [--scenario <id>] [--allow-real-library]
 *   node scripts/run-midscene.mjs --platform android [--fresh] [--no-push] [--apk <path>]
 *   node scripts/run-midscene.mjs --platform ios [--no-push]
 *   node scripts/run-midscene.mjs --platform web [--headed] [--scenario 08-web-reader-smoke]
 */

import fs from 'node:fs';
import http from 'node:http';
import https from 'node:https';
import os from 'node:os';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const MIDSCENE_DIR = path.resolve(HERE, '..');
const REPO_ROOT = path.resolve(MIDSCENE_DIR, '..', '..');
const SCENARIOS_DIR = path.join(MIDSCENE_DIR, 'scenarios');
const GENERATED_DIR = path.join(MIDSCENE_DIR, 'generated');
const FIXTURES_DIR = path.join(REPO_ROOT, 'fixtures');
const VALID_PLATFORMS = new Set(['windows', 'macos', 'android', 'ios', 'web']);

export function parseScenarioYaml(raw, fileLabel = '<scenario>') {
  const lines = raw.replace(/\r\n/g, '\n').split('\n');
  const out = {
    id: '',
    title: '',
    testing_ref: '',
    parity_ids: [],
    fixtures: [],
    platforms: [],
    tasks: [],
  };

  let section = null;
  let curTask = null;
  let inFlow = false;

  const stripQuotes = (s) => {
    const t = s.trim();
    if ((t.startsWith('"') && t.endsWith('"')) || (t.startsWith("'") && t.endsWith("'"))) {
      return t.slice(1, -1);
    }
    return t;
  };

  for (let i = 0; i < lines.length; i++) {
    const lineNo = i + 1;
    const rawLine = lines[i];
    const trimmed = rawLine.trim();
    if (!trimmed || trimmed.startsWith('#')) continue;
    const indent = rawLine.length - rawLine.trimStart().length;

    if (indent === 0) {
      curTask = null;
      inFlow = false;
      const idx = trimmed.indexOf(':');
      if (idx < 0) throw new Error(`${fileLabel}:${lineNo}: expected key: value`);
      const k = trimmed.slice(0, idx).trim();
      const v = trimmed.slice(idx + 1).trim();
      if (['parity_ids', 'fixtures', 'platforms', 'tasks'].includes(k)) {
        section = k;
      } else {
        section = null;
        out[k] = stripQuotes(v);
      }
      continue;
    }

    if (indent === 2 && trimmed.startsWith('- ')) {
      if (section === 'tasks') {
        const rest = trimmed.slice(2).trim();
        if (!rest.startsWith('name:')) {
          throw new Error(`${fileLabel}:${lineNo}: task must start with "- name:"`);
        }
        curTask = { name: stripQuotes(rest.slice('name:'.length)), flow: [] };
        out.tasks.push(curTask);
        inFlow = false;
      } else if (section && Array.isArray(out[section])) {
        out[section].push(stripQuotes(trimmed.slice(2)));
      } else {
        throw new Error(`${fileLabel}:${lineNo}: unexpected list item under "${section}"`);
      }
      continue;
    }

    if (indent === 4 && curTask) {
      if (trimmed === 'flow:') {
        inFlow = true;
        continue;
      }
      throw new Error(`${fileLabel}:${lineNo}: unexpected task property "${trimmed}"`);
    }

    if (indent === 6 && inFlow && curTask && trimmed.startsWith('- ')) {
      const rest = trimmed.slice(2).trim();
      const idx = rest.indexOf(':');
      if (idx < 0) throw new Error(`${fileLabel}:${lineNo}: expected action: value in flow`);
      const action = rest.slice(0, idx).trim();
      const rawVal = rest.slice(idx + 1).trim();
      const val = action === 'sleep' ? Number(rawVal) : stripQuotes(rawVal);
      curTask.flow.push({ action, value: val, line: lineNo });
      continue;
    }

    throw new Error(`${fileLabel}:${lineNo}: unexpected indentation (${indent}) or syntax "${trimmed}"`);
  }

  return out;
}

function yamlStr(s) {
  return JSON.stringify(String(s));
}

export function renderMidsceneYaml(scenario, platform, env = process.env) {
  if (!VALID_PLATFORMS.has(platform)) {
    throw new Error(`Unsupported platform "${platform}"`);
  }

  const lines = [
    `# Generated by e2e/midscene/scripts/run-midscene.mjs from scenarios/${scenario.id}.yaml`,
    `# Target platform: ${platform} | testing_ref: ${scenario.testing_ref}`,
  ];

  if (platform === 'windows' || platform === 'macos') {
    const displayId = env.MIDSCENE_DISPLAY_ID || env.COMPUTER_DISPLAY_ID;
    if (displayId) {
      lines.push('computer:');
      lines.push(`  displayId: ${yamlStr(displayId)}`);
    } else {
      lines.push('computer: {}');
    }
  } else if (platform === 'android') {
    lines.push('android:');
    if (env.ANDROID_DEVICE_ID) lines.push(`  deviceId: ${yamlStr(env.ANDROID_DEVICE_ID)}`);
    if (env.ANDROID_ADB_PATH) lines.push(`  androidAdbPath: ${yamlStr(env.ANDROID_ADB_PATH)}`);
    if (env.REMOTE_ADB_HOST) lines.push(`  remoteAdbHost: ${yamlStr(env.REMOTE_ADB_HOST)}`);
    if (env.REMOTE_ADB_PORT) lines.push(`  remoteAdbPort: ${Number(env.REMOTE_ADB_PORT)}`);
    lines.push('  launch: "app.yuyue"');
    lines.push('  imeStrategy: "yadb-for-non-ascii"');
    lines.push('  autoDismissKeyboard: true');
  } else if (platform === 'ios') {
    lines.push('ios:');
    if (env.IOS_WDA_BASE_URL) {
      lines.push(`  wdaBaseUrl: ${yamlStr(env.IOS_WDA_BASE_URL)}`);
    } else {
      if (env.IOS_WDA_HOST) lines.push(`  wdaHost: ${yamlStr(env.IOS_WDA_HOST)}`);
      lines.push(`  wdaPort: ${Number(env.IOS_WDA_PORT || 8100)}`);
    }
    lines.push('  launch: "app.yuyue.Yuyue"');
    lines.push('  autoDismissKeyboard: true');
  } else if (platform === 'web') {
    const port = Number(env.YUYUE_WEB_PORT || 8787);
    const url = env.YUYUE_WEB_URL || `http://127.0.0.1:${port}/demo/`;
    lines.push('web:');
    lines.push(`  url: ${yamlStr(url)}`);
    lines.push('  viewportWidth: 1280');
    lines.push('  viewportHeight: 820');
  }

  lines.push('');
  lines.push('agent:');
  lines.push(`  reportFileName: ${yamlStr(`${platform}-${scenario.id}`)}`);
  lines.push('  generateReport: true');
  lines.push(`  groupName: ${yamlStr(`Yuyue ${platform}`)}`);
  lines.push(`  groupDescription: ${yamlStr(scenario.title)}`);

  lines.push('');
  lines.push('tasks:');
  for (const task of scenario.tasks) {
    lines.push(`  - name: ${yamlStr(task.name)}`);
    lines.push('    flow:');
    for (const step of task.flow) {
      if (step.action === 'sleep') {
        lines.push(`      - sleep: ${step.value}`);
      } else {
        lines.push(`      - ${step.action}: ${yamlStr(step.value)}`);
      }
    }
  }
  lines.push('');
  return lines.join('\n');
}

function loadEnvFile(envPath) {
  if (!fs.existsSync(envPath)) return {};
  const loaded = {};
  for (const rawLine of fs.readFileSync(envPath, 'utf8').split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line || line.startsWith('#')) continue;
    const eq = line.indexOf('=');
    if (eq <= 0) continue;
    const k = line.slice(0, eq).trim();
    let v = line.slice(eq + 1).trim();
    if ((v.startsWith('"') && v.endsWith('"')) || (v.startsWith("'") && v.endsWith("'"))) {
      v = v.slice(1, -1);
    }
    if (!(k in process.env) && v !== '') loaded[k] = v;
  }
  return loaded;
}

function loadMergedEnv() {
  return {
    ...loadEnvFile(path.join(REPO_ROOT, 'tests', 'midscene', '.env')),
    ...loadEnvFile(path.join(MIDSCENE_DIR, '.env')),
    ...process.env,
  };
}

function findWindowsExe(env, buildPref) {
  if (env.YUYUE_WIN_EXE && fs.existsSync(env.YUYUE_WIN_EXE)) return env.YUYUE_WIN_EXE;
  if (env.YUYUE_EXE && fs.existsSync(env.YUYUE_EXE)) return env.YUYUE_EXE;
  const rel = path.join(REPO_ROOT, 'windows', 'Yuyue', 'bin', 'x64', 'Release', 'net8.0-windows10.0.22621.0', 'win-x64', 'Yuyue.exe');
  const dbg = path.join(REPO_ROOT, 'windows', 'Yuyue', 'bin', 'x64', 'Debug', 'net8.0-windows10.0.22621.0', 'win-x64', 'Yuyue.exe');
  const order = (buildPref || env.YUYUE_BUILD || 'release').toLowerCase() === 'debug' ? [dbg, rel] : [rel, dbg];
  return order.find((c) => fs.existsSync(c)) || null;
}

function findMacApp(env, buildPref) {
  if (env.YUYUE_MAC_APP && fs.existsSync(env.YUYUE_MAC_APP)) return env.YUYUE_MAC_APP;
  const dbg = path.join(REPO_ROOT, 'apple', 'build', 'dd', 'Build', 'Products', 'Debug', 'Yuyue.app');
  const rel = path.join(REPO_ROOT, 'apple', 'build', 'dd', 'Build', 'Products', 'Release', 'Yuyue.app');
  const app = '/Applications/Yuyue.app';
  const order = (buildPref || 'debug').toLowerCase() === 'release' ? [rel, dbg, app] : [dbg, rel, app];
  for (const c of order) {
    if (fs.existsSync(c)) return c;
  }
  // Check Xcode default DerivedData on macOS
  if (process.platform === 'darwin') {
    const ddRoot = path.join(os.homedir(), 'Library', 'Developer', 'Xcode', 'DerivedData');
    if (fs.existsSync(ddRoot)) {
      for (const entry of fs.readdirSync(ddRoot)) {
        if (!entry.startsWith('Yuyue-')) continue;
        for (const cfg of ['Debug', 'Release']) {
          const cand = path.join(ddRoot, entry, 'Build', 'Products', cfg, 'Yuyue.app');
          if (fs.existsSync(cand)) return cand;
        }
      }
    }
  }
  return null;
}

function findSystemBrowser(env) {
  if (env.PUPPETEER_EXECUTABLE_PATH && fs.existsSync(env.PUPPETEER_EXECUTABLE_PATH)) {
    return env.PUPPETEER_EXECUTABLE_PATH;
  }
  const candidates =
    process.platform === 'win32'
      ? [
          path.join(process.env.ProgramFiles || 'C:\\Program Files', 'Google', 'Chrome', 'Application', 'chrome.exe'),
          path.join(process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)', 'Google', 'Chrome', 'Application', 'chrome.exe'),
          path.join(process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)', 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
          path.join(process.env.ProgramFiles || 'C:\\Program Files', 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
        ]
      : process.platform === 'darwin'
        ? [
            '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
            '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
            '/Applications/Chromium.app/Contents/MacOS/Chromium',
          ]
        : ['/usr/bin/google-chrome', '/usr/bin/chromium', '/usr/bin/chromium-browser', '/usr/bin/microsoft-edge'];
  return candidates.find((p) => fs.existsSync(p)) || null;
}

function findMidsceneBin() {
  const local = path.join(MIDSCENE_DIR, 'node_modules', '.bin', process.platform === 'win32' ? 'midscene.cmd' : 'midscene');
  if (fs.existsSync(local)) return local;
  return process.platform === 'win32' ? 'npx.cmd' : 'npx';
}

function runCmd(cmd, args, opts = {}) {
  return spawnSync(cmd, args, {
    encoding: 'utf8',
    windowsHide: true,
    ...opts,
  });
}

function probeHttp(url, timeoutMs = 2500) {
  return new Promise((resolve) => {
    try {
      const u = new URL(url);
      const client = u.protocol === 'https:' ? https : http;
      const req = client.get(u, { timeout: timeoutMs }, (res) => {
        let body = '';
        res.on('data', (c) => {
          if (body.length < 1024) body += c;
        });
        res.on('end', () => resolve({ ok: res.statusCode >= 200 && res.statusCode < 400, status: res.statusCode, body }));
      });
      req.on('error', (err) => resolve({ ok: false, error: err.message }));
      req.on('timeout', () => {
        req.destroy();
        resolve({ ok: false, error: 'timeout' });
      });
    } catch (e) {
      resolve({ ok: false, error: String(e.message || e) });
    }
  });
}

async function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

function listAdbDevices(adbBin) {
  const res = runCmd(adbBin, ['devices']);
  if (res.error || res.status !== 0) {
    return { available: false, error: res.error?.message || (res.stderr || '').trim() || 'adb not found', devices: [] };
  }
  const devices = [];
  for (const line of (res.stdout || '').split(/\r?\n/).slice(1)) {
    const m = /^(\S+)\s+(device|offline|unauthorized)/.exec(line.trim());
    if (m) devices.push({ id: m[1], state: m[2] });
  }
  return { available: true, devices };
}

function getWdaStatusUrl(env) {
  if (env.IOS_WDA_BASE_URL) {
    return env.IOS_WDA_BASE_URL.replace(/\/+$/, '') + '/status';
  }
  const host = env.IOS_WDA_HOST || '127.0.0.1';
  const port = Number(env.IOS_WDA_PORT || 8100);
  return `http://${host}:${port}/status`;
}

async function runDoctor(platform, env, buildPref) {
  console.log(`\n=== Yuyue Midscene Doctor (${platform}) ===`);
  let ok = true;

  // 1. Model env
  const hasKey = Boolean(env.MIDSCENE_MODEL_API_KEY || env.OPENAI_API_KEY);
  console.log(`[${hasKey ? 'OK' : 'FAIL'}] VLM Config: ${hasKey ? `${env.MIDSCENE_MODEL_NAME || 'default'} (${env.MIDSCENE_MODEL_BASE_URL || 'OpenAI'})` : '未配置 MIDSCENE_MODEL_API_KEY（请在 e2e/midscene/.env 或 tests/midscene/.env 中填写）'}`);
  if (!hasKey) ok = false;

  // 2. Midscene CLI installed
  const bin = findMidsceneBin();
  const hasLocalBin = bin.includes('node_modules');
  console.log(`[${hasLocalBin ? 'OK' : 'WARN'}] @midscene/cli: ${hasLocalBin ? bin : '未在 e2e/midscene 安装依赖，请先执行 pnpm --dir e2e/midscene install'}`);

  // 3. Platform-specific checks
  if (platform === 'windows') {
    const exe = findWindowsExe(env, buildPref);
    if (exe) {
      const st = fs.statSync(exe);
      console.log(`[OK] Windows Yuyue.exe: ${exe} (Built ${st.mtime.toLocaleString()})`);
    } else {
      console.log('[FAIL] 未找到 Yuyue.exe，请先编译: dotnet build windows/Yuyue/Yuyue.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64');
      ok = false;
    }
    if (process.platform !== 'win32') {
      console.log(`[WARN] 当前系统为 ${process.platform}，Windows 实机跑测需在 Windows 主机执行`);
    }
  } else if (platform === 'macos') {
    const app = findMacApp(env, buildPref);
    if (app) {
      const isDebug = app.includes(`${path.sep}Debug${path.sep}`);
      console.log(`[OK] macOS Yuyue.app: ${app}${isDebug ? ' (Debug，支持 -debugLibrary 隔离书库)' : ' (非 Debug 目录，运行时需注意隔离书库)'}`);
    } else {
      console.log('[FAIL] 未找到 Yuyue.app，请在 macOS 编译或设置 YUYUE_MAC_APP');
      ok = false;
    }
    if (process.platform !== 'darwin') {
      console.log(`[WARN] 当前系统为 ${process.platform}，macOS 实机跑测需在 macOS 主机执行，并在「系统设置 → 隐私与安全性」授予终端录屏与辅助功能权限`);
    }
  } else if (platform === 'android') {
    const adb = env.ANDROID_ADB_PATH || 'adb';
    const info = listAdbDevices(adb);
    if (!info.available) {
      console.log(`[FAIL] ADB 不可用: ${info.error}`);
      ok = false;
    } else {
      const ready = info.devices.filter((d) => d.state === 'device');
      if (!ready.length) {
        console.log(`[FAIL] 未检测到已授权的 Android 设备/模拟器（当前列表: ${info.devices.map((d) => `${d.id}:${d.state}`).join(', ') || '空'}）`);
        ok = false;
      } else {
        const targetId = env.ANDROID_DEVICE_ID || ready[0].id;
        console.log(`[OK] Android 设备就绪: ${targetId}（共 ${ready.length} 台在线）`);
        const pkg = runCmd(adb, ['-s', targetId, 'shell', 'pm', 'path', 'app.yuyue']);
        const installed = pkg.status === 0 && (pkg.stdout || '').includes('package:');
        console.log(`[${installed ? 'OK' : 'FAIL'}] 应用 app.yuyue: ${installed ? '已安装' : '未安装（可用 --apk <路径> 或 ./gradlew installDebug 安装）'}`);
        if (!installed) ok = false;
      }
    }
  } else if (platform === 'ios') {
    const statusUrl = getWdaStatusUrl(env);
    const probe = await probeHttp(statusUrl, 2500);
    if (probe.ok) {
      console.log(`[OK] WebDriverAgent 可达: ${statusUrl} (HTTP ${probe.status})`);
    } else {
      console.log(`[FAIL] WebDriverAgent 未响应 (${statusUrl}: ${probe.error || probe.status})。请先在 Mac 上通过 Xcode / tidevice 启动 WDA 并转发端口 ${env.IOS_WDA_PORT || 8100}`);
      ok = false;
    }
    if (process.platform === 'darwin') {
      const sim = runCmd('xcrun', ['simctl', 'list', 'devices', 'booted']);
      const bootedLine = (sim.stdout || '').split(/\r?\n/).find((l) => l.includes('(Booted)'));
      console.log(`[INFO] iOS Simulator: ${bootedLine ? bootedLine.trim() : '无运行中模拟器（若测真机可忽略）'}`);
    }
  } else if (platform === 'web') {
    const distJs = path.join(REPO_ROOT, 'reader', 'dist', 'reader.js');
    const hasDist = fs.existsSync(distJs);
    console.log(`[${hasDist ? 'OK' : 'FAIL'}] reader/dist 构建产物: ${hasDist ? distJs : '缺失，请先执行 cd reader && pnpm install && pnpm build'}`);
    const browser = findSystemBrowser(env);
    console.log(`[${browser ? 'OK' : 'WARN'}] 浏览器内核: ${browser || '未检测到系统 Chrome/Edge，将尝试使用 Puppeteer 缓存或请运行 npx puppeteer browsers install chrome'}`);
    if (!hasDist) ok = false;
  }

  console.log(ok ? 'Doctor result: READY.\n' : 'Doctor result: NOT READY (see [FAIL] items above).\n');
  return ok;
}

async function prepareAndroid(env, scenarios, { noPush, fresh, apkPath }) {
  const adb = env.ANDROID_ADB_PATH || 'adb';
  const info = listAdbDevices(adb);
  if (!info.available) {
    throw new Error(`未找到可用的 adb 工具（${info.error}）。请安装 Android Platform-Tools 或设置 ANDROID_ADB_PATH。`);
  }
  const ready = info.devices.filter((d) => d.state === 'device');
  if (!ready.length) {
    throw new Error('未检测到在线的 Android 设备或模拟器。请连接手机并开启 USB 调试，或启动 Android 模拟器后重试。');
  }
  const deviceId = env.ANDROID_DEVICE_ID || ready[0].id;
  env.ANDROID_DEVICE_ID = deviceId;
  console.log(`▸ Android device: ${deviceId}`);

  if (apkPath) {
    if (!fs.existsSync(apkPath)) throw new Error(`指定的 APK 不存在: ${apkPath}`);
    console.log(`▸ Installing APK: ${apkPath}`);
    const inst = runCmd(adb, ['-s', deviceId, 'install', '-r', apkPath], { stdio: 'inherit' });
    if (inst.status !== 0) throw new Error('adb install 失败');
  }

  const pkg = runCmd(adb, ['-s', deviceId, 'shell', 'pm', 'path', 'app.yuyue']);
  if (pkg.status !== 0 || !(pkg.stdout || '').includes('package:')) {
    throw new Error('设备上尚未安装 app.yuyue。请先执行 `cd android && ./gradlew installDebug` 或传入 `--apk <路径>`。');
  }

  if (fresh) {
    console.log('▸ Clearing app.yuyue data (--fresh) for clean empty-shelf run');
    runCmd(adb, ['-s', deviceId, 'shell', 'pm', 'clear', 'app.yuyue']);
  }

  if (!noPush) {
    const uniqueFixtures = new Set();
    for (const sc of scenarios) {
      for (const f of sc.fixtures) uniqueFixtures.add(f);
    }
    if (uniqueFixtures.size > 0) {
      runCmd(adb, ['-s', deviceId, 'shell', 'mkdir', '-p', '/sdcard/Download/yuyue-fixtures']);
      for (const rel of uniqueFixtures) {
        const src = path.join(FIXTURES_DIR, rel);
        const dst = `/sdcard/Download/yuyue-fixtures/${path.basename(rel)}`;
        if (fs.existsSync(src)) {
          const pushRes = runCmd(adb, ['-s', deviceId, 'push', src, dst]);
          if (pushRes.status !== 0) {
            console.warn(`  ! 推送夹具失败 (${rel}): ${(pushRes.stderr || pushRes.stdout || '').trim()}`);
          }
        }
      }
      console.log(`▸ Pushed ${uniqueFixtures.size} fixture(s) to /sdcard/Download/yuyue-fixtures/`);
    }
  }
}

async function prepareIos(env, scenarios, { noPush }) {
  const statusUrl = getWdaStatusUrl(env);
  const probe = await probeHttp(statusUrl, 3000);
  if (!probe.ok) {
    throw new Error(
      `无法连接 iOS WebDriverAgent (${statusUrl}: ${probe.error || probe.status})。\n` +
        '请先在 Mac 上启动 WebDriverAgent（Xcode Test 或 tidevice wdaproxy -B <wda-bundle-id> --port 8100），再重新运行。',
    );
  }
  console.log(`▸ iOS WebDriverAgent ready: ${statusUrl}`);

  if (!noPush && process.platform === 'darwin') {
    const containerRes = runCmd('xcrun', ['simctl', 'get_app_container', 'booted', 'app.yuyue.Yuyue', 'data']);
    if (containerRes.status === 0 && (containerRes.stdout || '').trim()) {
      const docsDir = path.join(containerRes.stdout.trim(), 'Documents', 'yuyue-fixtures');
      fs.mkdirSync(docsDir, { recursive: true });
      const uniqueFixtures = new Set();
      for (const sc of scenarios) {
        for (const f of sc.fixtures) uniqueFixtures.add(f);
      }
      for (const rel of uniqueFixtures) {
        const src = path.join(FIXTURES_DIR, rel);
        if (fs.existsSync(src)) {
          fs.copyFileSync(src, path.join(docsDir, path.basename(rel)));
        }
      }
      if (uniqueFixtures.size > 0) {
        console.log(`▸ Staged ${uniqueFixtures.size} fixture(s) into Simulator Documents/yuyue-fixtures/`);
      }
    }
  }
}

async function startWebServer(env) {
  const distJs = path.join(REPO_ROOT, 'reader', 'dist', 'reader.js');
  if (!fs.existsSync(distJs)) {
    throw new Error('缺少 reader/dist/reader.js，请先在 reader/ 目录运行 `pnpm install && pnpm build`。');
  }
  const port = Number(env.YUYUE_WEB_PORT || 8787);
  const demoUrl = `http://127.0.0.1:${port}/demo/`;

  // Reuse if already running
  const existing = await probeHttp(demoUrl, 1200);
  if (existing.ok) {
    console.log(`▸ Reusing running reader dev server at ${demoUrl}`);
    return null;
  }

  console.log(`▸ Starting reader dev server on port ${port}...`);
  const serveScript = path.join(REPO_ROOT, 'reader', 'scripts', 'serve.mjs');
  const proc = spawn(process.execPath, [serveScript, '--port', String(port)], {
    cwd: path.join(REPO_ROOT, 'reader'),
    stdio: 'ignore',
    windowsHide: true,
  });

  const deadline = Date.now() + 10_000;
  while (Date.now() < deadline) {
    const check = await probeHttp(demoUrl, 1000);
    if (check.ok) {
      console.log(`▸ Reader dev server ready: ${demoUrl}`);
      return proc;
    }
    await sleep(300);
  }
  try {
    proc.kill('SIGTERM');
  } catch {
    // ignore
  }
  throw new Error(`启动 reader/scripts/serve.mjs 超时，${demoUrl} 未响应`);
}

function focusWindowsProcess(pid) {
  spawnSync(
    'powershell.exe',
    [
      '-NoProfile',
      '-NonInteractive',
      '-Command',
      `Add-Type 'using System; using System.Runtime.InteropServices; public static class W { public delegate bool E(IntPtr h, IntPtr l); [StructLayout(LayoutKind.Sequential)] public struct R { public int L,T,Ri,B; } [DllImport("user32.dll")] public static extern bool EnumWindows(E cb, IntPtr l); [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p); [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h); [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r); [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); public static void FocusPid(uint pid) { IntPtr best = IntPtr.Zero; long area = 0; EnumWindows((h,l)=>{ uint p; GetWindowThreadProcessId(h, out p); if (p!=pid || !IsWindowVisible(h)) return true; R r; if (!GetWindowRect(h, out r)) return true; long w=r.Ri-r.L, ht=r.B-r.T; if (w>=400 && ht>=300 && w*ht>area){area=w*ht; best=h;} return true; }, IntPtr.Zero); if (best!=IntPtr.Zero){ ShowWindow(best, 9); SetForegroundWindow(best); } } }'; [W]::FocusPid([uint32]${pid})`,
    ],
    { stdio: 'ignore', windowsHide: true, timeout: 6000 },
  );
}

async function cli() {
  const args = process.argv.slice(2);
  const getArg = (flag) => {
    const idx = args.indexOf(flag);
    return idx >= 0 ? args[idx + 1] : null;
  };
  const platformArg = getArg('--platform') || 'windows';
  const onlyScenario = getArg('--scenario');
  const buildPref = getArg('--build');
  const apkPath = getArg('--apk');
  const renderOnly = args.includes('--render-only');
  const noLaunch = args.includes('--no-launch');
  const noPush = args.includes('--no-push');
  const fresh = args.includes('--fresh');
  const doctor = args.includes('--doctor');
  const verifyModel = args.includes('--verify-model');
  const headed = args.includes('--headed');
  const allowRealLibrary = args.includes('--allow-real-library');

  const env = loadMergedEnv();

  if (verifyModel) {
    const bin = findMidsceneBin();
    const prefix = bin.endsWith('npx') || bin.endsWith('npx.cmd') ? ['midscene'] : [];
    const res = spawnSync(bin, [...prefix, 'model', 'verify'], {
      cwd: MIDSCENE_DIR,
      env,
      stdio: 'inherit',
      shell: process.platform === 'win32' && bin.endsWith('.cmd'),
    });
    process.exit(res.status ?? 1);
  }

  if (renderOnly && platformArg === 'all') {
    const files = fs.readdirSync(SCENARIOS_DIR).filter((f) => f.endsWith('.yaml')).sort();
    for (const p of VALID_PLATFORMS) {
      const outDir = path.join(GENERATED_DIR, p);
      fs.mkdirSync(outDir, { recursive: true });
      let count = 0;
      for (const file of files) {
        const sc = parseScenarioYaml(fs.readFileSync(path.join(SCENARIOS_DIR, file), 'utf8'), file);
        if (onlyScenario && sc.id !== onlyScenario && file !== onlyScenario) continue;
        if (!sc.platforms.includes(p)) continue;
        fs.writeFileSync(path.join(outDir, `${sc.id}.yaml`), renderMidsceneYaml(sc, p, env), 'utf8');
        count++;
      }
      console.log(`Rendered ${count} flow(s) for ${p} -> generated/${p}/`);
    }
    return;
  }

  if (!VALID_PLATFORMS.has(platformArg)) {
    console.error(`Invalid --platform "${platformArg}". Use: windows | macos | android | ios | web | all (with --render-only)`);
    process.exit(2);
  }
  const platform = platformArg;

  if (doctor) {
    const ready = await runDoctor(platform, env, buildPref);
    process.exit(ready ? 0 : 1);
  }

  const files = fs
    .readdirSync(SCENARIOS_DIR)
    .filter((f) => f.endsWith('.yaml'))
    .sort();

  const matchedScenarios = [];
  for (const file of files) {
    const sc = parseScenarioYaml(fs.readFileSync(path.join(SCENARIOS_DIR, file), 'utf8'), file);
    if (onlyScenario && sc.id !== onlyScenario && file !== onlyScenario) continue;
    if (!sc.platforms.includes(platform)) continue;
    matchedScenarios.push(sc);
  }

  if (!matchedScenarios.length) {
    console.error(`No matching scenarios found for platform="${platform}"${onlyScenario ? ` scenario="${onlyScenario}"` : ''}.`);
    process.exit(1);
  }

  // Android may auto-select ANDROID_DEVICE_ID before rendering YAML
  if (!renderOnly && platform === 'android') {
    await prepareAndroid(env, matchedScenarios, { noPush, fresh, apkPath: apkPath || env.YUYUE_ANDROID_APK });
  } else if (!renderOnly && platform === 'ios') {
    await prepareIos(env, matchedScenarios, { noPush });
  }

  const outDir = path.join(GENERATED_DIR, platform);
  fs.mkdirSync(outDir, { recursive: true });

  const rendered = [];
  for (const sc of matchedScenarios) {
    const yaml = renderMidsceneYaml(sc, platform, env);
    const outPath = path.join(outDir, `${sc.id}.yaml`);
    fs.writeFileSync(outPath, yaml, 'utf8');
    rendered.push(outPath);
  }

  console.log(`Rendered ${rendered.length} Midscene flow(s) for ${platform} into ${path.relative(MIDSCENE_DIR, outDir)}/`);
  if (renderOnly) return;

  if (!env.MIDSCENE_MODEL_API_KEY && !env.OPENAI_API_KEY) {
    console.error('Missing MIDSCENE_MODEL_API_KEY (or OPENAI_API_KEY). Copy e2e/midscene/.env.example to .env (or configure tests/midscene/.env).');
    process.exit(2);
  }

  let appProc = null;
  let webServerProc = null;
  let tempLib = null;

  try {
    if (platform === 'windows' && !noLaunch) {
      const exe = findWindowsExe(env, buildPref);
      if (!exe) {
        console.error('Yuyue.exe not found. Build Windows first (`dotnet build windows/Yuyue/Yuyue.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64`) or set YUYUE_WIN_EXE.');
        process.exit(2);
      }
      tempLib = fs.mkdtempSync(path.join(os.tmpdir(), 'yuyue-midscene-win-'));
      console.log(`▸ Launching isolated Yuyue.exe (${exe}) with --library ${tempLib}`);
      appProc = spawn(exe, ['--library', tempLib], {
        cwd: path.dirname(exe),
        stdio: 'ignore',
        detached: false,
      });
      await sleep(3500);
      if (appProc.pid) focusWindowsProcess(appProc.pid);
    } else if (platform === 'macos' && !noLaunch) {
      if (process.platform !== 'darwin') {
        console.error('macOS 实机验收必须在 macOS 主机上运行（当前系统: ' + process.platform + '）。');
        process.exit(2);
      }
      const appPath = findMacApp(env, buildPref);
      if (!appPath) {
        console.error('未找到 Yuyue.app。请先在 apple/ 构建 Debug 包（xcodebuild ...）或在 .env 中设置 YUYUE_MAC_APP。');
        process.exit(2);
      }
      const isDebug = appPath.includes(`${path.sep}Debug${path.sep}`);
      if (!isDebug && !allowRealLibrary) {
        console.error(
          `检测到非 Debug 路径的 Yuyue.app (${appPath})。\n` +
            'macOS 端的 `-debugLibrary` 隔离书库参数仅在 Debug 构建启用；为保护你的真实书库，默认拒绝自动拉起 Release 包。\n' +
            '请改用 Debug 构建，或确认使用测试账号后显式加 `--allow-real-library`（或手动拉起后加 `--no-launch`）。',
        );
        process.exit(2);
      }
      const binPath = path.join(appPath, 'Contents', 'MacOS', 'Yuyue');
      tempLib = fs.mkdtempSync(path.join(os.tmpdir(), 'yuyue-midscene-mac-'));
      console.log(`▸ Launching isolated macOS Yuyue (${binPath}) with -debugLibrary ${tempLib}`);
      appProc = spawn(binPath, ['-debugLibrary', tempLib], {
        stdio: 'ignore',
        detached: false,
      });
      await sleep(3500);
      runCmd('osascript', ['-e', 'tell application "Yuyue" to activate']);
    } else if (platform === 'web') {
      if (!env.PUPPETEER_EXECUTABLE_PATH) {
        const sysBrowser = findSystemBrowser(env);
        if (sysBrowser) {
          env.PUPPETEER_EXECUTABLE_PATH = sysBrowser;
          console.log(`▸ Using system Chromium browser: ${sysBrowser}`);
        }
      }
      if (!noLaunch) {
        webServerProc = await startWebServer(env);
      }
    }

    const bin = findMidsceneBin();
    const prefix = bin.endsWith('npx') || bin.endsWith('npx.cmd') ? ['midscene'] : [];
    const cliFlags = [];
    if (platform === 'web' && headed) cliFlags.push('--headed');

    for (const flowFile of rendered) {
      console.log(`\n▸ Running Midscene [${platform}]: ${path.basename(flowFile)}`);
      const res = spawnSync(bin, [...prefix, ...cliFlags, flowFile], {
        cwd: MIDSCENE_DIR,
        env,
        stdio: 'inherit',
        shell: process.platform === 'win32' && bin.endsWith('.cmd'),
      });
      if (res.status !== 0) {
        process.exit(res.status ?? 1);
      }
    }
  } finally {
    if (appProc && !appProc.killed && appProc.pid) {
      try {
        if (process.platform === 'win32') {
          spawnSync('taskkill', ['/PID', String(appProc.pid), '/T', '/F'], { stdio: 'ignore' });
        } else {
          appProc.kill('SIGTERM');
        }
      } catch {
        // ignore
      }
    }
    if (webServerProc && !webServerProc.killed) {
      try {
        webServerProc.kill('SIGTERM');
      } catch {
        // ignore
      }
    }
    if (tempLib) {
      await sleep(500);
      const logPath = path.join(tempLib, 'yuyue.log');
      if (fs.existsSync(logPath)) {
        const logText = fs.readFileSync(logPath, 'utf8');
        if (/\[(STARTUP_FATAL|APPDOMAIN|TASK|FATAL)\]|Unhandled/i.test(logText)) {
          console.error(`\n[CRASH DETECTED] yuyue.log contained fatal errors:\n${logText.slice(-1000)}`);
        }
      }
      fs.rmSync(tempLib, { recursive: true, force: true });
    }
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  cli().catch((e) => {
    console.error(e.message || e);
    process.exit(1);
  });
}
