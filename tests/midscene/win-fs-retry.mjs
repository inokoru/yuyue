import fs from 'node:fs';
import fsPromises from 'node:fs/promises';
import { syncBuiltinESMExports } from 'node:module';

if (process.platform === 'win32') {
  const RETRYABLE = new Set(['EPERM', 'EBUSY', 'EACCES']);
  const MAX_RETRIES = 12;

  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const sleepSync = (ms) => {
    try {
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
    } catch {
      const until = Date.now() + ms;
      while (Date.now() < until) {
        // fallback spin
      }
    }
  };

  const origRenameAsync = fsPromises.rename.bind(fsPromises);
  const wrappedRenameAsync = async (oldPath, newPath) => {
    for (let attempt = 0; ; attempt++) {
      try {
        return await origRenameAsync(oldPath, newPath);
      } catch (err) {
        if (attempt >= MAX_RETRIES || !err || !RETRYABLE.has(err.code)) {
          throw err;
        }
        await sleep(Math.min(200, 25 * (attempt + 1)));
      }
    }
  };
  fsPromises.rename = wrappedRenameAsync;
  fs.promises.rename = wrappedRenameAsync;

  const origRenameCb = fs.rename.bind(fs);
  fs.rename = (oldPath, newPath, cb) => {
    let attempt = 0;
    const run = () => {
      origRenameCb(oldPath, newPath, (err) => {
        if (err && RETRYABLE.has(err.code) && attempt < MAX_RETRIES) {
          attempt++;
          setTimeout(run, Math.min(200, 25 * attempt));
          return;
        }
        cb(err);
      });
    };
    run();
  };

  const origRenameSync = fs.renameSync.bind(fs);
  fs.renameSync = (oldPath, newPath) => {
    for (let attempt = 0; ; attempt++) {
      try {
        return origRenameSync(oldPath, newPath);
      } catch (err) {
        if (attempt >= MAX_RETRIES || !err || !RETRYABLE.has(err.code)) {
          throw err;
        }
        sleepSync(Math.min(200, 25 * (attempt + 1)));
      }
    }
  };

  syncBuiltinESMExports();
}
