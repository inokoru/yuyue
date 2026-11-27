import { type ChildProcess, spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { ComputerAgent, agentForComputer } from '@midscene/computer';
import { type NodeExecutionContext, defineNode, z } from '@midscene/test';
import { defineFileSetup, defineTestProject } from '@midscene/test/config';
import { createMidsceneNodes } from '@midscene/test/midscene';
import { config as loadEnv } from 'dotenv';

const PROJECT_DIR = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(PROJECT_DIR, '..', '..');
const FIXTURES_BOOKS_DIR = path.join(REPO_ROOT, 'fixtures', 'books');

loadEnv({ path: path.join(PROJECT_DIR, '.env') });

interface FileContext {
  agent: ComputerAgent;
  appProcess: ChildProcess | null;
  libraryDir: string | null;
  ownsLibraryDir: boolean;
}

const getAgent = ({ context }: NodeExecutionContext<unknown, FileContext>) => {
  return context.agent;
};

function sleepWithSignal(ms: number, signal?: AbortSignal): Promise<void> {
  if (ms <= 0) return Promise.resolve();
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(signal.reason ?? new Error('Aborted'));
      return;
    }
    const timer = setTimeout(() => {
      signal?.removeEventListener('abort', onAbort);
      resolve();
    }, ms);
    const onAbort = () => {
      clearTimeout(timer);
      reject(signal?.reason ?? new Error('Aborted'));
    };
    signal?.addEventListener('abort', onAbort, { once: true });
  });
}

function findExeRecursively(dir: string): string | null {
  if (!fs.existsSync(dir)) return null;
  const entries = fs.readdirSync(dir, { withFileTypes: true });
  for (const entry of entries) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isFile() && entry.name.toLowerCase() === 'yuyue.exe') {
      return fullPath;
    }
    if (entry.isDirectory()) {
      const found = findExeRecursively(fullPath);
      if (found) return found;
    }
  }
  return null;
}

function resolveYuyueExe(explicitPath?: string): string {
  const debugExe = path.join(
    REPO_ROOT,
    'windows',
    'Yuyue',
    'bin',
    'x64',
    'Debug',
    'net8.0-windows10.0.22621.0',
    'win-x64',
    'Yuyue.exe',
  );
  const releaseExe = path.join(
    REPO_ROOT,
    'windows',
    'Yuyue',
    'bin',
    'x64',
    'Release',
    'net8.0-windows10.0.22621.0',
    'win-x64',
    'Yuyue.exe',
  );

  const buildPref = (process.env.YUYUE_BUILD || '').trim().toLowerCase();
  const orderedDefaults =
    buildPref === 'release' ? [releaseExe, debugExe] : [debugExe, releaseExe];

  const candidates = [
    explicitPath,
    process.env.YUYUE_EXE,
    ...orderedDefaults,
  ].filter((p): p is string => Boolean(p && p.trim()));

  for (const candidate of candidates) {
    const resolved = path.isAbsolute(candidate)
      ? candidate
      : path.resolve(REPO_ROOT, candidate);
    if (fs.existsSync(resolved)) return resolved;
  }

  const discovered = findExeRecursively(
    path.join(REPO_ROOT, 'windows', 'Yuyue', 'bin'),
  );
  if (discovered) return discovered;

  throw new Error(
    '未找到 Yuyue.exe。请先在仓库根目录构建 Windows 端（dotnet build windows/Yuyue/Yuyue.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64），或在 tests/midscene/.env 中设置 YUYUE_EXE。',
  );
}

function seedWindowsPrefs(
  libraryDir: string,
  theme?: 'light' | 'dark',
  prefsOverride?: Record<string, unknown>,
): void {
  if (!theme && !prefsOverride) return;
  const prefsPath = path.join(libraryDir, 'windows-prefs.json');
  let current: Record<string, unknown> = {};
  if (fs.existsSync(prefsPath)) {
    try {
      const parsed = JSON.parse(fs.readFileSync(prefsPath, 'utf8'));
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
        current = parsed as Record<string, unknown>;
      }
    } catch {
      current = {};
    }
  }
  if (theme) {
    current.ThemeMode = theme;
  }
  if (prefsOverride) {
    Object.assign(current, prefsOverride);
  }
  fs.writeFileSync(prefsPath, JSON.stringify(current, null, 2), 'utf8');
}

function bringProcessWindowToFront(pid: number, timeoutMs = 12_000): boolean {
  if (process.platform !== 'win32') return true;
  const psScript = `
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WinFocus {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
  public static IntPtr FindMainWindow(uint targetPid) {
    IntPtr best = IntPtr.Zero;
    long bestArea = 0;
    EnumWindows((h, l) => {
      uint p;
      GetWindowThreadProcessId(h, out p);
      if (p != targetPid || !IsWindowVisible(h)) return true;
      RECT r;
      if (!GetWindowRect(h, out r)) return true;
      long w = r.Right - r.Left, ht = r.Bottom - r.Top;
      if (w >= 400 && ht >= 300 && w * ht > bestArea) {
        bestArea = w * ht;
        best = h;
      }
      return true;
    }, IntPtr.Zero);
    return best;
  }
  public static void Focus(IntPtr h) {
    uint curThread = GetCurrentThreadId();
    IntPtr fg = GetForegroundWindow();
    uint fgPid;
    uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out fgPid) : 0;
    uint targetPid;
    uint targetThread = GetWindowThreadProcessId(h, out targetPid);
    if (fgThread != 0 && fgThread != curThread) AttachThreadInput(curThread, fgThread, true);
    if (targetThread != 0 && targetThread != curThread) AttachThreadInput(curThread, targetThread, true);
    ShowWindow(h, 9);
    SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
    SetWindowPos(h, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
    BringWindowToTop(h);
    SetForegroundWindow(h);
    if (targetThread != 0 && targetThread != curThread) AttachThreadInput(curThread, targetThread, false);
    if (fgThread != 0 && fgThread != curThread) AttachThreadInput(curThread, fgThread, false);
  }
}
"@
$deadline = (Get-Date).AddMilliseconds(${timeoutMs})
while ((Get-Date) -lt $deadline) {
  $p = Get-Process -Id ${pid} -ErrorAction SilentlyContinue
  if (-not $p) { exit 2 }
  $h = [WinFocus]::FindMainWindow([uint32]${pid})
  if ($h -ne [IntPtr]::Zero) {
    [WinFocus]::Focus($h)
    exit 0
  }
  Start-Sleep -Milliseconds 200
}
exit 1
`;
  const res = spawnSync(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-Command', psScript],
    { stdio: 'ignore', windowsHide: true, timeout: timeoutMs + 5_000 },
  );
  return res.status === 0;
}

function readCrashLogSnippet(libraryDir: string): string {
  const logPath = path.join(libraryDir, 'yuyue.log');
  if (!fs.existsSync(logPath)) return '';
  try {
    const content = fs.readFileSync(logPath, 'utf8').trim();
    return content.length > 1200 ? content.slice(-1200) : content;
  } catch {
    return '';
  }
}

function findFatalCrashEntries(libraryDir: string): string | null {
  const snippet = readCrashLogSnippet(libraryDir);
  if (!snippet) return null;
  if (/\[(STARTUP_FATAL|APPDOMAIN|TASK|FATAL)\]|Unhandled/i.test(snippet)) {
    return snippet;
  }
  return null;
}

async function stopYuyueProcess(
  context: FileContext,
  cleanupLibrary: boolean,
  checkCrashLog = false,
): Promise<string | null> {
  const proc = context.appProcess;
  context.appProcess = null;

  if (proc?.pid && proc.exitCode === null) {
    if (process.platform === 'win32') {
      spawnSync('taskkill', ['/PID', String(proc.pid), '/T', '/F'], {
        stdio: 'ignore',
        windowsHide: true,
      });
    } else {
      try {
        proc.kill('SIGTERM');
      } catch {
        // ignore
      }
    }
    // Wait briefly so WebView2 / SQLite handles release before inspecting or deleting temp library.
    await sleepWithSignal(450);
  }

  const crashReport =
    checkCrashLog && context.libraryDir
      ? findFatalCrashEntries(context.libraryDir)
      : null;

  if (cleanupLibrary && context.ownsLibraryDir && context.libraryDir) {
    const dirToDelete = context.libraryDir;
    context.libraryDir = null;
    context.ownsLibraryDir = false;

    for (let attempt = 0; attempt < 4; attempt++) {
      try {
        fs.rmSync(dirToDelete, { recursive: true, force: true });
        break;
      } catch {
        await sleepWithSignal(350);
      }
    }
  }

  return crashReport;
}

const fileSetup = defineFileSetup<FileContext, undefined>({
  name: 'computer',
  async setup({ env, onTeardown }) {
    const agent = await agentForComputer({
      displayId: env.COMPUTER_DISPLAY_ID || undefined,
    });
    const ctx: FileContext = {
      agent,
      appProcess: null,
      libraryDir: null,
      ownsLibraryDir: false,
    };
    onTeardown(async () => {
      await stopYuyueProcess(ctx, true, false);
      await agent.destroy();
    });
    return ctx;
  },
});

const yuyueLaunchInputSchema = z.strictObject({
  exePath: z
    .string()
    .optional()
    .describe('Optional override path to Yuyue.exe.'),
  libraryDir: z
    .string()
    .optional()
    .describe(
      'Optional custom library folder. Omit to use an isolated temporary library directory.',
    ),
  reuseLibrary: z
    .boolean()
    .optional()
    .describe(
      'Reuse the previous isolated temporary library directory (useful for restart persistence tests).',
    ),
  seedDemo: z
    .boolean()
    .optional()
    .describe('Pass --seed-demo (Debug build only) to populate demo books.'),
  fixtures: z
    .array(z.string())
    .optional()
    .describe(
      'Fixture file names under fixtures/books/ (e.g. ["beiying.epub"]) or absolute file paths to import & open on launch.',
    ),
  open: z
    .string()
    .optional()
    .describe('Pass --open <query> (Debug build only) to open a matching book.'),
  theme: z
    .enum(['light', 'dark'])
    .optional()
    .describe(
      'Force Light or Dark mode. Seeds windows-prefs.json so it works identically in BOTH Release and Debug builds.',
    ),
  tab: z
    .string()
    .optional()
    .describe('Pass --tab <name> to switch the shelf tab on launch (e.g. shelf, notes, podcasts, vocab).'),
  view: z
    .enum(['grid', 'list'])
    .optional()
    .describe('Pass --view <grid|list> to choose the shelf view layout.'),
  settings: z
    .union([z.boolean(), z.string()])
    .optional()
    .describe(
      'Pass --settings or --settings <page> (e.g. general, appearance, reading, foreign, listening, companion, sync, export, storage, data, about) to open Settings directly.',
    ),
  settingsDialog: z
    .enum(['companion-catalog', 'voice-catalog', 'voice-picker'])
    .optional()
    .describe('Pass --settings-dialog <name> (Debug build only) to open a Settings dialog directly.'),
  panel: z
    .string()
    .optional()
    .describe('Pass --panel <name> (Debug build only) to open a right-side reader panel (e.g. companion, notes, translate).'),
  leftPanel: z
    .string()
    .optional()
    .describe('Pass --left-panel <name> (Debug build only) to open a left-side reader panel (e.g. toc, bookmarks, search).'),
  listenBar: z
    .boolean()
    .optional()
    .describe('Pass --listen-bar (Debug build only) to show the bottom TTS bar immediately.'),
  prefs: z
    .record(z.string(), z.unknown())
    .optional()
    .describe(
      'Optional PascalCase DevicePrefs overrides written to windows-prefs.json before launch (works in BOTH Release and Debug builds, e.g. { HiddenVoiceProviders: ["edge", "free"], UiLanguage: "en" }).',
    ),
  extraArgs: z
    .array(z.string())
    .optional()
    .describe('Additional raw CLI arguments forwarded to Yuyue.exe.'),
  waitMs: z
    .number()
    .int()
    .min(0)
    .max(30_000)
    .optional()
    .describe('Milliseconds to wait after spawning Yuyue.exe (default: 2200).'),
});

const yuyueLaunch = defineNode<
  typeof yuyueLaunchInputSchema,
  { pid: number; exePath: string; libraryDir: string; args: string[] },
  FileContext
>({
  name: 'yuyue.launch',
  title: 'Launch Yuyue Windows with an isolated library',
  description:
    'Start Yuyue.exe against an isolated temporary --library directory so tests never touch the user book library.',
  inputSchema: yuyueLaunchInputSchema,
  async execute({ input, context, signal }) {
    signal.throwIfAborted();

    const shouldReuse = Boolean(input.reuseLibrary && context.libraryDir);
    await stopYuyueProcess(context, !shouldReuse, false);

    const exePath = resolveYuyueExe(input.exePath);

    let libraryDir: string;
    if (input.libraryDir) {
      libraryDir = path.resolve(REPO_ROOT, input.libraryDir);
      fs.mkdirSync(libraryDir, { recursive: true });
      context.ownsLibraryDir = false;
    } else if (shouldReuse && context.libraryDir) {
      libraryDir = context.libraryDir;
    } else {
      libraryDir = fs.mkdtempSync(path.join(os.tmpdir(), 'yuyue-midscene-'));
      context.ownsLibraryDir = true;
    }
    context.libraryDir = libraryDir;

    // Pre-seed windows-prefs.json so theme/prefs work identically in Release AND Debug builds.
    seedWindowsPrefs(libraryDir, input.theme, input.prefs);

    const args: string[] = ['--library', libraryDir];
    if (input.seedDemo) args.push('--seed-demo');
    if (input.theme === 'dark') args.push('--dark');
    if (input.theme === 'light') args.push('--light');
    if (input.tab) args.push('--tab', input.tab);
    if (input.view) args.push('--view', input.view);
    if (input.open) args.push('--open', input.open);
    if (input.panel) args.push('--panel', input.panel);
    if (input.leftPanel) args.push('--left-panel', input.leftPanel);
    if (input.listenBar) args.push('--listen-bar');
    if (input.settings === true) {
      args.push('--settings');
    } else if (typeof input.settings === 'string' && input.settings.trim()) {
      args.push('--settings', input.settings.trim());
    }
    if (input.settingsDialog) {
      args.push('--settings-dialog', input.settingsDialog);
    }
    if (input.extraArgs?.length) {
      args.push(...input.extraArgs);
    }
    if (input.fixtures?.length) {
      for (const item of input.fixtures) {
        const candidate = path.isAbsolute(item)
          ? item
          : path.join(FIXTURES_BOOKS_DIR, item);
        if (!fs.existsSync(candidate)) {
          throw new Error(`测试夹具文件不存在: ${candidate}`);
        }
        args.push(candidate);
      }
    }

    const child = spawn(exePath, args, {
      cwd: path.dirname(exePath),
      stdio: 'ignore',
      windowsHide: false,
    });
    if (!child.pid) {
      throw new Error(`启动 Yuyue.exe 失败: ${exePath}`);
    }
    context.appProcess = child;

    const focused = bringProcessWindowToFront(child.pid, 12_000);
    await sleepWithSignal(input.waitMs ?? 2200, signal);
    signal.throwIfAborted();

    if (child.exitCode !== null) {
      const logTail = readCrashLogSnippet(libraryDir);
      throw new Error(
        `Yuyue.exe 启动后提前退出，exitCode=${child.exitCode}${logTail ? `\nyuyue.log:\n${logTail}` : ''}`,
      );
    }

    if (!focused) {
      const logTail = readCrashLogSnippet(libraryDir);
      throw new Error(
        `Yuyue.exe (PID ${child.pid}) 在 12s 内未创建主窗口${logTail ? `\nyuyue.log:\n${logTail}` : ''}`,
      );
    }

    return {
      summary: `Launched ${path.basename(path.dirname(path.dirname(path.dirname(exePath))))} Yuyue.exe (PID ${child.pid}) with isolated library ${libraryDir}`,
      data: {
        pid: child.pid,
        exePath,
        libraryDir,
        args,
      },
    };
  },
});

const yuyueCloseInputSchema = z.strictObject({
  keepLibrary: z
    .boolean()
    .optional()
    .describe(
      'Keep the isolated temporary library on disk so a subsequent yuyue.launch({ reuseLibrary: true }) can verify persistence.',
    ),
  ignoreCrashLog: z
    .boolean()
    .optional()
    .describe(
      'Skip asserting that yuyue.log has no fatal/unhandled exception entries before cleanup.',
    ),
});

const yuyueClose = defineNode<typeof yuyueCloseInputSchema, void, FileContext>({
  name: 'yuyue.close',
  title: 'Close Yuyue Windows test instance',
  description:
    'Terminate the test-launched Yuyue.exe process, check yuyue.log for fatal crashes, and optionally clean up its temporary library folder.',
  inputSchema: yuyueCloseInputSchema,
  async execute({ input, context }) {
    const crash = await stopYuyueProcess(
      context,
      !input.keepLibrary,
      !input.ignoreCrashLog,
    );
    if (crash) {
      throw new Error(`运行期间在 yuyue.log 检测到未捕获异常或致命错误:\n${crash}`);
    }
    return {
      summary: input.keepLibrary
        ? 'Closed Yuyue.exe cleanly (zero crash logs) and kept temporary library for relaunch.'
        : 'Closed Yuyue.exe cleanly (zero crash logs) and cleaned up temporary library.',
    };
  },
});

const yuyueCheckLogInputSchema = z.strictObject({});

const yuyueCheckLog = defineNode<
  typeof yuyueCheckLogInputSchema,
  { clean: boolean },
  FileContext
>({
  name: 'yuyue.checkLog',
  title: 'Assert Yuyue runtime crash log is clean',
  description:
    'Verify the isolated library yuyue.log does not contain unhandled exceptions or startup fatal errors.',
  inputSchema: yuyueCheckLogInputSchema,
  async execute({ context }) {
    if (!context.libraryDir) {
      return { summary: 'No active libraryDir to inspect.', data: { clean: true } };
    }
    const crash = findFatalCrashEntries(context.libraryDir);
    if (crash) {
      throw new Error(`yuyue.log 检测到异常:\n${crash}`);
    }
    return {
      summary: 'yuyue.log is clean (no fatal/unhandled exceptions).',
      data: { clean: true },
    };
  },
});

const caseFilesOverride = (process.env.YUYUE_CASE_FILES || '')
  .split(',')
  .map((s) => s.trim())
  .filter(Boolean);

export default defineTestProject<FileContext, undefined>({
  projects: [
    {
      name: 'computer',
      fileSetup,
      maxConcurrentFiles: 1,
      files: {
        include:
          caseFilesOverride.length > 0
            ? caseFilesOverride
            : ['cases/**/*.{yaml,yml}'],
      },
    },
  ],
  nodes: [
    yuyueLaunch,
    yuyueClose,
    yuyueCheckLog,
    ...createMidsceneNodes<FileContext>({
      agentClass: ComputerAgent,
      getAgent,
    }),
  ],
  test: { maxConcurrentFiles: 1, testTimeout: 360_000 },
});
