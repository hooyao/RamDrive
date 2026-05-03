// Mimic Playwright's chromium launch: fd 3/4 used for --remote-debugging-pipe
const { spawn } = require('child_process');
const fs = require('fs');

const exe = 'C:\\Users\\HuYao\\AppData\\Local\\ms-playwright\\chromium-1208\\chrome-win64\\chrome.exe';
const userDataDir = process.argv[2] || 'Z:\\Temp\\rarbg_browser_72j1evkz';

const args = [
  '--disable-field-trial-config','--disable-background-networking','--disable-background-timer-throttling',
  '--disable-backgrounding-occluded-windows','--disable-back-forward-cache','--disable-breakpad',
  '--disable-client-side-phishing-detection','--disable-component-extensions-with-background-pages',
  '--disable-component-update','--no-default-browser-check','--disable-default-apps','--disable-dev-shm-usage',
  '--disable-extensions',
  '--disable-features=AvoidUnnecessaryBeforeUnloadCheckSync,BoundaryEventDispatchTracksNodeRemoval,DestroyProfileOnBrowserClose,DialMediaRouteProvider,GlobalMediaControls,HttpsUpgrades,LensOverlay,MediaRouter,PaintHolding,ThirdPartyStoragePartitioning,Translate,AutoDeElevate,RenderDocument,OptimizationHints',
  '--enable-features=CDPScreenshotNewSurface','--allow-pre-commit-input','--disable-hang-monitor',
  '--disable-ipc-flooding-protection','--disable-popup-blocking','--disable-prompt-on-repost',
  '--disable-renderer-backgrounding','--force-color-profile=srgb','--metrics-recording-only',
  '--no-first-run','--password-store=basic','--use-mock-keychain','--no-service-autorun',
  '--export-tagged-pdf','--disable-search-engine-choice-screen','--unsafely-disable-devtools-self-xss-warnings',
  '--edge-skip-compat-layer-relaunch','--enable-automation','--disable-infobars',
  '--disable-search-engine-choice-screen','--disable-sync','--enable-unsafe-swiftshader','--no-sandbox',
  '--disable-blink-features=AutomationControlled',
  `--user-data-dir=${userDataDir}`,
  '--remote-debugging-pipe',
  '--enable-logging=stderr','--v=0',
  'about:blank'
];

console.log('[repro] launching chrome with userDataDir=', userDataDir);
try { fs.rmSync(userDataDir, { recursive: true, force: true }); } catch {}

const child = spawn(exe, args, {
  stdio: ['ignore', 'pipe', 'pipe', 'pipe', 'pipe'],
  windowsHide: true,
});

child.stderr.on('data', d => process.stderr.write(d));
child.stdout.on('data', d => process.stdout.write(d));
child.stdio[3].on('error', () => {});
child.stdio[4].on('error', () => {});

setTimeout(() => {
  console.log('[repro] 30s elapsed, killing');
  try { child.kill(); } catch {}
}, 30000);

child.on('exit', (code, signal) => {
  console.log(`[repro] EXIT code=${code} signal=${signal}`);
  if (code !== null) {
    const u = code >>> 0;
    console.log(`[repro] hex=0x${u.toString(16).toUpperCase().padStart(8,'0')}`);
    if (u === 0x80000003) console.log('[repro] STATUS_BREAKPOINT — repro confirmed');
  }
  process.exit(0);
});
