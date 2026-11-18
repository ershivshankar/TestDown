// JarvisType — Background Service Worker
// Replaces relay_client.js from the Electron app
// Connects to Render relay server, receives phone commands, executes them
importScripts('socket.io.min.js');
let socket = null;
let nativePort = null;
let relayUrl = 'https://ex-5d5f.onrender.com';
let authToken = '';  // Must be verified via NeonDB before connecting
let isConnected = false;
let isCapturing = false;
let isConnectingNative = false;
let nativeHostPaused = false;  // true = user stopped host, don't auto-respawn
let nativeRetryDelay = 5000;   // exponential backoff — resets on success
let lastTokenVerifiedAt = 0;   // Timestamp of last successful 24h verification

// ── Overlay hotkey handled by RuntimeHost.exe (WH_KEYBOARD_LL native hook) ──
// chrome.commands block removed — interview/inject.js no longer exists,
// Ctrl+Shift+S is owned by the native desktop app.



// --- OPTIMIZATION STATE ---
let lastFrameHash = null;
let idleFrameCount = 0;
const IDLE_PAUSE_AFTER = 15; // pause after ~3s of no change
let forceNextFrame = false;  // Force sending the next frame regardless of hash

let currentInterval = 200;         // 5 FPS base (ms between frames)
const FPS_IDLE     = 250;          // 4 FPS  — idle minimum — never slower so app-switch lag ≤250ms
const FPS_ACTIVE   = 200;          // 5 FPS  — moderate activity
const FPS_BURST    = 80;           // 12 FPS — rapid movement / typing
let consecutiveChanges = 0;        // how many frames in a row had changes
let streamTimeout = null;          // We will use setTimeout instead of setInterval for dynamic scheduling
let isStreamActive = false;

// Force immediate screenshot when user switches Chrome tabs
chrome.tabs.onActivated.addListener(() => {
    forceNextFrame = true;
    consecutiveChanges = 3;
    currentInterval = FPS_BURST;
});

// Force immediate screenshot when Chrome window focus changes (catches alt-tab to other apps)
chrome.windows.onFocusChanged.addListener(() => {
    forceNextFrame = true;
    consecutiveChanges = 3;
    currentInterval = FPS_BURST;
});

function quickHash(str) {
    if (!str) return 0;
    let h = str.length;
    const step = Math.max(1, Math.floor(str.length / 256));
    for (let i = 0; i < str.length; i += step) {
        h = (h * 31 + str.charCodeAt(i)) | 0;
    }
    return h;
}

function processIncomingFrame(img) {
    const frameHash = quickHash(img);
    if (frameHash === lastFrameHash && !forceNextFrame) {
        idleFrameCount++;
        consecutiveChanges = 0;
        
        // Downscale FPS since screen is idle
        rescheduleStream(FPS_IDLE);
        return;
    }

    forceNextFrame = false; // Reset the force flag once frame is sent

    // Frame changed — update hash and reset idle counter
    lastFrameHash = frameHash;
    idleFrameCount = 0;
    consecutiveChanges++;

    // Boost FPS on active screen changes
    if (consecutiveChanges >= 3) {
        rescheduleStream(FPS_BURST); // 10 FPS
    } else {
        rescheduleStream(FPS_ACTIVE); // 4 FPS
    }

    // Send frame to mobile client
    const sent = sendToMobile('peek:data', { image: img });
    if (!sent) logToStorage('⚠️ sendToMobile failed — socket not connected when frame ready');
}

function rescheduleStream(newInterval) {
    // Just update interval — captureTick picks it up on next run
    currentInterval = newInterval;
}

function captureTick() {
    if (!isStreamActive) return; // Stream was stopped, exit loop

    // Attempt capture only if everything is ready
    if (socket && socket.connected && isConnected && !isCapturing) {
        captureAndSendScreenshot();
    }

    // ALWAYS reschedule next tick — stream never stops itself
    streamTimeout = setTimeout(captureTick, currentInterval);
}

function boostStreamActivity() {
    consecutiveChanges = 3; // force burst FPS on user activity
    rescheduleStream(FPS_BURST);
}

// ── Watchdog: Auto-reset isCapturing if it gets stuck ─────────
// If a screenshot request is never answered (e.g. native host crashed),
// this prevents the flag from being stuck forever.
let capturingWatchdogTimer = null;
function setCapturing(val) {
    if (capturingWatchdogTimer) {
        clearTimeout(capturingWatchdogTimer);
        capturingWatchdogTimer = null;
    }
    isCapturing = val;
    if (val) {
        // Auto-reset after 3 seconds max — prevents deadlock
        capturingWatchdogTimer = setTimeout(() => {
            if (isCapturing) {
                logToStorage('⚠️ isCapturing watchdog: stuck flag reset');
                isCapturing = false;
            }
        }, 3000);
    }
}

// ── Log to Storage for Debugging ──────────────────────────────
function logToStorage(msg) {
    const timestamp = new Date().toLocaleTimeString();
    const logLine = `[${timestamp}] ${msg}`;
    console.log(logLine);
    chrome.storage.local.get(['extensionLogs'], (cfg) => {
        let logs = cfg.extensionLogs || [];
        logs.push(logLine);
        if (logs.length > 50) logs.shift(); // Keep last 50 logs
        chrome.storage.local.set({ extensionLogs: logs });
    });
}

// Clear logs on startup
chrome.storage.local.set({ extensionLogs: [`[SW Startup] Service Worker initialized`] });

// ── 24-Hour Token Verification Gate ─────────────────────────
const TOKEN_VERIFY_URL = 'https://ex-5d5f.onrender.com/api/auth/verify-token';
const TWENTY_FOUR_HOURS_MS = 24 * 60 * 60 * 1000;

async function verifyTokenWithServer(token) {
    if (!token) return { valid: false, error: 'No token provided' };
    try {
        const resp = await fetch(TOKEN_VERIFY_URL, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ token: token.trim() })
        });
        if (resp.ok) {
            const data = await resp.json();
            return { valid: true, label: data.label || '' };
        } else {
            return { valid: false, error: 'Token rejected by server' };
        }
    } catch (e) {
        logToStorage('Token verification network error: ' + e.message);
        // If offline and within cached 24h, allow fallback
        return { valid: null, error: e.message };
    }
}

async function verifyAndInitializeConnection() {
    chrome.storage.local.get(['relayUrl', 'authToken', 'lastVerifiedAt', 'userLabel'], async (cfg) => {
        const loadedUrl = cfg.relayUrl || 'https://ex-5d5f.onrender.com';
        const fixedUrl = (loadedUrl === 'https://remotelink-relay.onrender.com' ||
                          loadedUrl === 'https://extension-test-k9cc.onrender.com')
            ? 'https://ex-5d5f.onrender.com' : loadedUrl;
        relayUrl = fixedUrl;

        const loadedToken = cfg.authToken || '';
        if (!loadedToken) {
            logToStorage('No token stored — login required via popup.');
            chrome.storage.local.set({ connectionStatus: 'unauthenticated' });
            return;
        }

        const now = Date.now();
        const lastVerified = cfg.lastVerifiedAt || 0;
        const isCacheValid = (now - lastVerified) < TWENTY_FOUR_HOURS_MS;

        if (isCacheValid) {
            logToStorage('Token verification cached (< 24h). Initializing connection...');
            authToken = loadedToken;
            initConnection();
        } else {
            logToStorage('24-hour token verification check in progress...');
            const result = await verifyTokenWithServer(loadedToken);
            if (result.valid === true) {
                logToStorage('Token verified successfully via NeonDB!');
                authToken = loadedToken;
                chrome.storage.local.set({ lastVerifiedAt: now, userLabel: result.label });
                initConnection();
            } else if (result.valid === false) {
                logToStorage('Token is INVALID or EXPIRED in database. Access blocked.');
                authToken = '';
                chrome.storage.local.remove(['authToken', 'lastVerifiedAt']);
                chrome.storage.local.set({ connectionStatus: 'token_expired' });
                if (socket) {
                    try { socket.disconnect(); } catch (e) {}
                    socket = null;
                }
            } else {
                // Network unreachable — if lastVerified exists at all, grace period connect
                if (lastVerified > 0) {
                    logToStorage('Server unreachable — allowing offline grace connect.');
                    authToken = loadedToken;
                    initConnection();
                } else {
                    logToStorage('Server unreachable and token never verified. Access blocked.');
                }
            }
        }
    });
}

// ── Keep service worker alive and re-verify every 24 hours ─────
chrome.alarms.create('keepAlive', { periodInMinutes: 0.2 });
chrome.alarms.create('serverPing', { periodInMinutes: 5 });  // Keep Render server warm (prevents spin-down)
chrome.alarms.create('dailyTokenCheck', { periodInMinutes: 60 }); // Check token freshness hourly

chrome.alarms.onAlarm.addListener((alarm) => {
    if (alarm.name === 'keepAlive') {
        if (socket && socket.connected) {
            socket.emit('ping-rtt', {}, () => {
                logToStorage('Socket heartbeat ping-rtt OK');
            });
        } else if (!socket && authToken) {
            logToStorage('Socket instance missing, initializing connection...');
            initConnection();
        } else if (socket && !socket.connected && authToken) {
            logToStorage('Socket is currently offline, reconnecting...');
            // Don't just call socket.connect() — that may hang; rebuild the socket
            socket = null;
            initConnection();
        }
        if (!nativePort) {
            logToStorage('Native port missing, attempting reconnect...');
            connectNativeHost();
        }
    } else if (alarm.name === 'serverPing') {
        // HTTP ping to keep Render free-tier server warm (prevents spin-down sleep)
        fetch(relayUrl + '/ping', { method: 'GET', signal: AbortSignal.timeout(8000) })
            .then(() => logToStorage('Server warm-ping OK'))
            .catch(() => logToStorage('Server warm-ping failed (server may be starting up)'));
    } else if (alarm.name === 'dailyTokenCheck') {
        // Enforce 24h re-verification
        verifyAndInitializeConnection();
    }
});

// ── Load config and connect on startup ───────────────────────
connectNativeHost();
verifyAndInitializeConnection();


// ── Listen for config updates from popup ──────────────────────
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    if (msg.type === 'get-status') {
        sendResponse({ connected: isConnected, relayUrl, authToken, nativeConnected: !!nativePort });
        return true;
    }
    // ── Interview overlay handlers ─────────────────────────────────────────────
    if (msg.type === 'interview-grab-screen') {
        // Ask native host for a screenshot and return it to the overlay
        if (nativePort) {
            const msgId = 'igrab_' + Date.now();
            const handler = (response) => {
                if (response && response._grabId === msgId) {
                    nativePort.onMessage.removeListener(handler);
                    sendResponse({ image: response.image || null });
                }
            };
            nativePort.onMessage.addListener(handler);
            try { nativePort.postMessage({ cmd: 'screenshot', _grabId: msgId }); } catch(e) {
                nativePort.onMessage.removeListener(handler);
                sendResponse({ image: null, error: 'Native host error' });
            }
            setTimeout(() => { try { nativePort.onMessage.removeListener(handler); } catch(e) {} sendResponse({ image: null, error: 'timeout' }); }, 5000);
        } else {
            // Fallback: capture visible tab
            chrome.tabs.captureVisibleTab(null, { format: 'jpeg', quality: 80 }, (img) => {
                sendResponse({ image: img || null });
            });
        }
        return true;
    }
    if (msg.type === 'interview-autotype-start') {
        if (nativePort) { try { nativePort.postMessage({ cmd: 'type', text: msg.data?.text || '', speed: msg.data?.speed || 3, _atId: msg.data?.id }); } catch(e) {} }
        sendResponse({ ok: true }); return true;
    }
    if (msg.type === 'interview-autotype-stop') {
        if (nativePort) { try { nativePort.postMessage({ cmd: 'type-stop' }); } catch(e) {} }
        sendResponse({ ok: true }); return true;
    }
    if (msg.type === 'interview-autotype-speed') {
        if (nativePort) { try { nativePort.postMessage({ cmd: 'type-speed', speed: msg.speed }); } catch(e) {} }
        sendResponse({ ok: true }); return true;
    }
    if (msg.type === 'interview-protect') {
        if (nativePort) { try { nativePort.postMessage({ cmd: 'protect-window' }); } catch(e) {} }
        sendResponse({ ok: true }); return true;
    }
    if (msg.type === 'interview-unprotect') {
        if (nativePort) { try { nativePort.postMessage({ cmd: 'unprotect-window' }); } catch(e) {} }
        sendResponse({ ok: true }); return true;
    }

    if (msg.type === 'reconnect') {
        logToStorage(`Reconnect requested with token: ${msg.token ? '****' : '(none)'}`);
        authToken = msg.token;
        if (socket) {
            try { socket.disconnect(); } catch (e) {}
            socket = null;
        }
        initConnection();
        sendResponse({ ok: true });
        return true;
    }
    if (msg.type === 'set-config') {
        if (msg.authToken) {
            authToken = msg.authToken;
            if (nativePort) {
                try { nativePort.postMessage({ cmd: 'set-config', relayUrl, authToken }); } catch(e) {}
            }
        }
        sendResponse({ ok: true });
        return true;
    }
    if (msg.type === 'save-config') {
        const tokenChanged = msg.authToken !== authToken;
        const urlChanged   = msg.relayUrl  !== relayUrl;
        relayUrl  = msg.relayUrl;
        authToken = msg.authToken;
        chrome.storage.local.set({ relayUrl, authToken });
        logToStorage(`Config updated. Token changed: ${tokenChanged}`);
        if (tokenChanged || urlChanged || !socket || !socket.connected) {
            if (socket) { try { socket.disconnect(); } catch(e) {} socket = null; }
            initConnection();
        }
        // Push updated config to native host so standalone relay reconnects
        // with new credentials even when extension is blocked by exam browser
        if (nativePort) {
            try { nativePort.postMessage({ cmd: 'set-config', relayUrl, authToken }); } catch(e) {}
        }
        sendResponse({ ok: true });
        return true;
    }
    if (msg.type === 'disconnect') {
        logToStorage('Manual disconnect requested');
        authToken = '';
        stopBackgroundStream();
        if (socket) {
            try { socket.disconnect(); } catch(e) {}
            socket = null;
        }
        if (nativePort) {
            try { nativePort.postMessage({ cmd: 'set-config', relayUrl, authToken: '' }); } catch(e) {}
        }
        isConnected = false;
        chrome.storage.local.set({ connectionStatus: 'disconnected' });
        sendResponse({ ok: true });
        return true;
    }
    if (msg.type === 'stop-stream') {
        logToStorage('Stream stopped by user — native host respawn paused');
        nativeHostPaused = true;        // stop Chrome from respawning WinSpeechHost
        stopBackgroundStream();
        // Kill current native port so WinSpeechHost.exe exits
        if (nativePort) {
            try { nativePort.disconnect(); } catch(e) {}
            nativePort = null;
        }
        isConnectingNative = false;
        if (socket) {
            try { socket.disconnect(); } catch(e) {}
            socket = null;
        }
        isConnected = false;
        chrome.storage.local.set({ connectionStatus: 'stopped' });
        sendResponse({ ok: true });
        return true;
    }
    if (msg.type === 'start-stream') {
        logToStorage('Stream started by user');
        nativeHostPaused = false;       // allow native host respawn again
        nativeRetryDelay = 5000;        // reset backoff
        connectNativeHost();            // reconnect native host immediately
        if (!authToken) {
            chrome.storage.local.get(['authToken'], (cfg) => {
                if (cfg.authToken) { authToken = cfg.authToken; initConnection(); }
            });
        } else {
            initConnection();
        }
        sendResponse({ ok: true });
        return true;
    }
    // From content script: clipboard data read back
    if (msg.type === 'clipboard:data') {
        sendToMobile('clipboard:data', { text: msg.text });
    }
    // screenshot response from content script (fallback if native host not available)
    if (msg.type === 'peek:data') {
        sendToMobile('peek:data', { image: msg.image });
    }
});

// ── Connect to Native Messaging Host ──────────────────────────
function connectNativeHost() {
    if (nativeHostPaused) return;     // user stopped — don't respawn
    if (isConnectingNative) return;   // prevent simultaneous duplicate calls
    isConnectingNative = true;
    try {
        if (nativePort) {
            try { nativePort.disconnect(); } catch (e) {}
            nativePort = null;
        }
        nativePort = chrome.runtime.connectNative('com.jarvistype.host');

        nativePort.onMessage.addListener((msg) => {
            if (msg.image !== undefined) {
                setCapturing(false);
                const imgLen = msg.image ? msg.image.length : 0;
                if (imgLen > 100) {
                    processIncomingFrame(msg.image);
                } else {
                    // Native host returned empty — fall back to tab capture
                    chrome.tabs.captureVisibleTab(null, { format: 'jpeg', quality: 45 }, (img) => {
                        if (!chrome.runtime.lastError && img) {
                            processIncomingFrame(img);
                        }
                    });
                }
            }
            if (msg.clipboardText) {
                sendToMobile('clipboard:data', { text: msg.clipboardText });
            }
        });

        nativePort.onDisconnect.addListener(() => {
            const errMsg = chrome.runtime.lastError?.message || 'Unknown reason';
            logToStorage(`Native host disconnected: ${errMsg}`);
            nativePort = null;
            isConnectingNative = false;
            setCapturing(false);
            if (nativeHostPaused) return; // user stopped — do NOT retry
            // Exponential backoff: double delay each failure, cap at 60s
            nativeRetryDelay = Math.min(nativeRetryDelay * 2, 60000);
            logToStorage(`Native host retry in ${nativeRetryDelay / 1000}s`);
            setTimeout(connectNativeHost, nativeRetryDelay);
        });

        // Connected successfully — reset backoff
        nativeRetryDelay = 5000;
        isConnectingNative = false;
        logToStorage('Native host connect call finished');
    } catch (e) {
        isConnectingNative = false;
        logToStorage(`Native host connection failed: ${e.message}`);
    }
}

function sendToNative(cmd) {
    if (nativePort) {
        nativePort.postMessage(cmd);
        return true;
    }
    logToStorage(`Skipped send to native: host not connected (${cmd.cmd})`);
    return false;
}

function sendToMobile(event, payload) {
    if (socket && socket.connected) {
        socket.emit('to-mobile', {
            deviceId: 'jarvistype-extension',
            event: event,
            payload: payload
        });
        return true;
    }
    return false;
}

function captureAndSendScreenshot() {
    if (!socket || !socket.connected) return;
    if (isCapturing) return; // Prevent queue buildup on the stdio channel

    if (nativePort) {
        try {
            setCapturing(true);
            nativePort.postMessage({ cmd: 'screenshot', quality: 45 });
            return;
        } catch (e) {
            setCapturing(false);
            logToStorage(`Native screenshot post failed: ${e.message}`);
        }
    }

    try {
        setCapturing(true);
        chrome.tabs.captureVisibleTab(null, { format: 'jpeg', quality: 45 }, (img) => {
            setCapturing(false);
            if (!chrome.runtime.lastError && img) {
                processIncomingFrame(img);
            }
        });
    } catch(e) {
        setCapturing(false);
        logToStorage(`Visible tab capture failed: ${e.message}`);
    }
}

// ── Connect to Relay Server ────────────────────────────────────
// FIX: Track whether command handlers have been registered.
// If initConnection() is called again on reconnect, we ONLY reconnect the
// transport — we do NOT register handlers again. Registering handlers multiple
// times causes every command to be executed 2x, 4x, 8x... breaking everything.
function initConnection() {
    // GUARD: Never connect with an empty token
    if (!authToken) {
        logToStorage('initConnection() aborted — no token saved.');
        return;
    }

    // GUARD: Don't destroy a working connection — skip if already connected
    if (socket && socket.connected) {
        logToStorage('Already connected — skipping initConnection().');
        return;
    }

    if (socket) {
        try { socket.disconnect(); } catch(e) {}
        socket = null;
    }

    let authData = {};
    if (authToken && authToken.length > 20) {
        authData.token = authToken;
    } else {
        authData.password = authToken;
    }
    authData.deviceId = 'jarvistype-extension';

    logToStorage(`Connecting to Render relay socket: ${relayUrl}`);

    socket = io(relayUrl, {
        auth: authData,
        transports: ['websocket'], // Must use websocket exclusively in Manifest V3 Service Workers (XHR/polling is unsupported)
        reconnection: true,
        reconnectionAttempts: Infinity,
        reconnectionDelay: 2000,
        timeout: 10000
    });

    socket.on('connect', () => {
        isConnected = true;
        logToStorage('Websocket connected! Sending identify...');
        socket.emit('identify', {
            type: 'desktop',
            pairingToken: authToken,
            deviceId: 'jarvistype-extension',
            hostname: 'chrome-extension',
            username: 'user'
        });
        chrome.storage.local.set({ connectionStatus: 'connected' });
        startBackgroundStream();
    });

    socket.on('disconnect', (reason) => {
        isConnected = false;
        logToStorage(`Websocket disconnected: ${reason}`);
        chrome.storage.local.set({ connectionStatus: 'disconnected' });
        stopBackgroundStream();
        // For transport-level drops (server gone, network blip), rebuild socket
        // socket.io's internal reconnect sometimes stalls in MV3 service workers
        if (reason === 'transport close' || reason === 'transport error' || reason === 'ping timeout') {
            logToStorage(`Transport-level drop (${reason}) — scheduling socket rebuild in 3s`);
            setTimeout(() => {
                if (!isConnected && authToken) {
                    logToStorage('Rebuilding socket after transport drop...');
                    if (socket) { try { socket.disconnect(); } catch(e) {} socket = null; }
                    initConnection();
                }
            }, 3000);
        }
    });

    socket.on('connect_error', (err) => {
        isConnected = false;
        logToStorage(`Websocket connection error: ${err.message}`);
        chrome.storage.local.set({ connectionStatus: 'error: ' + err.message });
    });

    socket.on('relay:conflict', () => {
        // When standalone WinSpeechHost or another client is active, gracefully stay offline/standby without infinite reconnect churn
        logToStorage('relay:conflict — standalone host active on this session. Extension standing by.');
        stopBackgroundStream();
        isConnected = false;
        chrome.storage.local.set({ connectionStatus: 'standby' });
        // Don't null out the socket — keep it alive so keepAlive alarm can reconnect
        // when the conflict resolves (e.g. WinSpeechHost exits)
        // Schedule a reconnect attempt after 30 seconds
        setTimeout(() => {
            if (!isConnected && authToken) {
                logToStorage('relay:conflict cooldown over — attempting reconnect...');
                if (socket) { try { socket.disconnect(); } catch(e) {} socket = null; }
                initConnection();
            }
        }, 30000);
    });


    socket.on('relay:ready', () => {
        logToStorage('Relay session confirmed ready.');
    });

    // ── Command handlers registered on the socket instance ──
    socket.on('command:type', (data) => {
        logToStorage(`Command received: type`);
        boostStreamActivity();
        // Path 1: native host Win32 SendInput — works in ANY OS window including chrome-extension://
        if (sendToNative({ cmd: 'type', text: data.text })) return;
        // Path 2: content script — works in regular web pages
        sendToActiveTab({ event: 'command:type', data });
        // Path 3: clipboard-paste via native — bypasses chrome-extension:// and exam browser restrictions
        // Fires regardless: if native port was down, this queues once reconnected
        sendToNative({ cmd: 'type-clipboard', text: data.text });
    });


    socket.on('command:key', (data) => {
        logToStorage(`Command received: key (${data.key || data.action})`);
        boostStreamActivity();
        if (!sendToNative({ cmd: 'key', key: data.key || data.action })) {
            sendToActiveTab({ event: 'command:key', data });
        }
    });

    socket.on('command:click', (data) => {
        logToStorage(`Command received: click`);
        boostStreamActivity();
        if (!sendToNative({ cmd: 'click', button: data.button || 'left', x: data.x, y: data.y })) {
            sendToActiveTab({ event: 'command:click', data });
        }
    });

    socket.on('command:move-mouse', (data) => {
        boostStreamActivity();
        if (!sendToNative({ cmd: 'move', x: data.x, y: data.y, mode: data.mode })) {
            sendToActiveTab({ event: 'command:move-mouse', data });
        }
    });

    socket.on('command:scroll', (data) => {
        boostStreamActivity();
        if (!sendToNative({ cmd: 'scroll', delta: data.delta })) {
            sendToActiveTab({ event: 'command:scroll', data });
        }
    });

    socket.on('command:peek', () => {
        forceNextFrame = true;
        captureAndSendScreenshot();
    });

    socket.on('command:camera', () => {
        forceNextFrame = true;
        captureAndSendScreenshot();
    });

    socket.on('command:camera-stop', () => {
        // no-op
    });

    socket.on('mobile-connected', () => {
        logToStorage('Mobile client connected — starting stream.');
        forceNextFrame = true;
        startBackgroundStream();
        captureAndSendScreenshot();
    });

    socket.on('relay:mobile-connected', () => {
        logToStorage('Mobile client connected (relay) — starting stream.');
        forceNextFrame = true;
        startBackgroundStream();
        captureAndSendScreenshot();
    });

    socket.on('mobile-disconnected', () => {
        logToStorage('Mobile client disconnected — pausing stream.');
        stopBackgroundStream();
    });

    socket.on('relay:mobile-disconnected', () => {
        logToStorage('Mobile client disconnected (relay) — pausing stream.');
        stopBackgroundStream();
    });

    socket.on('session-expired', () => {
        logToStorage('Session expired — stopping stream.');
        stopBackgroundStream();
    });

    socket.on('command:clipboard-set', (data) => {
        sendToNative({ cmd: 'clipboard-set', text: data.text });
        if (data.paste) {
            // 400ms: gives native host time to fully set Windows clipboard before Ctrl+V
            setTimeout(() => sendToNative({ cmd: 'key', key: 'paste' }), 400);
        }
    });

    socket.on('command:pasteline', (data) => {
        if (!data || data.text === undefined) return;
        sendToNative({ cmd: 'clipboard-set', text: String(data.text) });
        setTimeout(() => {
            sendToNative({ cmd: 'key', key: 'paste' });
            setTimeout(() => {
                sendToNative({ cmd: 'key', key: 'enter' });
            }, 150);
        }, 150);
    });

    socket.on('command:pasteline-noeol', (data) => {
        if (!data || data.text === undefined) return;
        sendToNative({ cmd: 'clipboard-set', text: String(data.text) });
        setTimeout(() => {
            sendToNative({ cmd: 'key', key: 'paste' });
        }, 150);
    });

    socket.on('command:clipboard-get', () => {
        sendToNative({ cmd: 'clipboard-get' });
    });

    socket.on('command:lock', () => {
        sendToNative({ cmd: 'lock' });
    });

    socket.on('command:media', (data) => {
        sendToNative({ cmd: 'media', action: data.action });
    });

    socket.on('command:mic-start', () => {
        sendToNative({ cmd: 'mic-start' });
    });
    socket.on('command:mic-stop', () => {
        sendToNative({ cmd: 'mic-stop' });
    });

    socket.on('command:autotype-stop', () => {
        sendToActiveTab({ event: 'command:autotype-stop' });
    });

    logToStorage('Command handlers registered on socket instance');
}

function startBackgroundStream() {
    if (isStreamActive) return;
    isStreamActive = true;
    lastFrameHash = null;
    idleFrameCount = 0;
    consecutiveChanges = 0;
    currentInterval = FPS_ACTIVE;
    if (streamTimeout) clearTimeout(streamTimeout);
    streamTimeout = setTimeout(captureTick, currentInterval);
    logToStorage('Background screenshot stream started');
}

function stopBackgroundStream() {
    isStreamActive = false;
    if (streamTimeout) {
        clearTimeout(streamTimeout);
        streamTimeout = null;
    }
    logToStorage('Background screenshot stream stopped');
}

// ── Helper: send message to active tab content script ─────────
function sendToActiveTab(msg) {
    chrome.tabs.query({ active: true, currentWindow: true }, (tabs) => {
        if (tabs && tabs[0]) {
            chrome.tabs.sendMessage(tabs[0].id, msg, () => {
                if (chrome.runtime.lastError) {}
            });
        }
    });
}

// ── Message handler (from popup, overlay iframe, content scripts) ──
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    if (!msg || !msg.type) return;

    // Ctrl+Shift+I → open/close interview overlay (also handled by commands API)
    if (msg.type === 'toggle-overlay-window') {
        toggleOverlayWindow();
        sendResponse({ ok: true });
        return true;
    }

    // Dashboard saved API keys → sync geminiKey into native host → host_config.json
    if (msg.type === 'sync-ai-keys') {
        chrome.storage.local.set({
            geminiKey:  msg.geminiKey  || '',
            chatgptKey: msg.chatgptKey || '',
            grokKey:    msg.grokKey    || ''
        });
        // Push to native host so stealth overlay picks it up without restart
        if (nativePort) {
            try {
                nativePort.postMessage({
                    cmd:        'set-api-keys',
                    geminiKey:  msg.geminiKey  || '',
                    chatgptKey: msg.chatgptKey || '',
                    grokKey:    msg.grokKey    || ''
                });
            } catch(e) {}
        }
        sendResponse({ ok: true });
        return true;
    }

    // Overlay iframe requests a screenshot of the active tab
    if (msg.type === 'grab-screen') {
        chrome.tabs.query({ active: true, currentWindow: true }, (tabs) => {
            if (!tabs || !tabs[0]) { sendResponse({ dataUrl: null }); return; }
            chrome.tabs.captureVisibleTab(tabs[0].windowId, { format: 'jpeg', quality: 80 }, (dataUrl) => {
                sendResponse({ dataUrl: dataUrl || null });
            });
        });
        return true; // keep sendResponse channel open
    }

    // Auto-type from overlay → native host
    if (msg.type === 'auto-type-start') {
        sendToNative({ cmd: 'type', text: msg.text });
        sendResponse({ ok: true });
        return true;
    }
    if (msg.type === 'auto-type-stop') {
        sendToNative({ cmd: 'type-stop' });
        sendResponse({ ok: true });
        return true;
    }
});
