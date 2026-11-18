// dashboard.js — Login-first flow with NeonDB token verification
const RELAY_URL = 'https://ex-5d5f.onrender.com';
const VERIFY_URL = RELAY_URL + '/api/auth/verify-token';

document.addEventListener('DOMContentLoaded', () => {
    const loginScreen      = document.getElementById('login-screen');
    const dashScreen       = document.getElementById('dashboard-screen');
    const tokenInput       = document.getElementById('token-input');
    const btnConnect       = document.getElementById('btn-connect');
    const loginError       = document.getElementById('login-error');
    const mobileLink       = document.getElementById('mobile-link');
    const tokenBadge       = document.getElementById('token-badge');
    const btnCopyLink      = document.getElementById('btn-copy-link');
    const btnLogout        = document.getElementById('btn-logout');
    const statusPill       = document.getElementById('status-pill');
    const statusText       = document.getElementById('status-text');
    const toast            = document.getElementById('toast');
    const btnEditKeys      = document.getElementById('btn-edit-keys');
    const btnStreamToggle  = document.getElementById('btn-stream-toggle');
    const btnDownloadHost  = document.getElementById('btn-download-host');
    const hostStatusBadge  = document.getElementById('host-status-badge');


    const keyGemini  = document.getElementById('key-gemini');
    const keyChatgpt = document.getElementById('key-chatgpt');
    const keyClaude  = document.getElementById('key-claude');
    const keyGrok    = document.getElementById('key-grok');
    const keyGroq    = document.getElementById('key-groq');

    let isEditingKeys = false;

    // ── Helpers ───────────────────────────────────────────────────────────────
    function showToast(msg) {
        toast.textContent = msg;
        toast.style.display = 'block';
        setTimeout(() => { toast.style.display = 'none'; }, 2000);
    }

    function showLogin(errorMsg) {
        loginScreen.style.display = 'flex';
        dashScreen.style.display  = 'none';
        if (errorMsg) {
            loginError.textContent = errorMsg;
            loginError.style.display = 'block';
        } else {
            loginError.style.display = 'none';
        }
    }

    function showDashboard(token, label) {
        loginScreen.style.display = 'none';
        dashScreen.style.display  = 'block';
        loadApiKeys();
        startStatusPolling();
    }

    function updateStatusPill(status) {
        if (status === 'connected') {
            statusPill.className = 'status-pill online';
            statusText.textContent = 'ONLINE';
        } else {
            statusPill.className = 'status-pill offline';
            statusText.textContent = 'OFFLINE';
        }
    }

    function loadApiKeys() {
        chrome.storage.local.get(['geminiKey','chatgptKey','claudeKey','grokKey','groqKey'], (cfg) => {
            if (cfg.geminiKey)  keyGemini.value  = cfg.geminiKey;
            if (cfg.chatgptKey) keyChatgpt.value = cfg.chatgptKey;
            if (cfg.claudeKey)  keyClaude.value  = cfg.claudeKey;
            if (cfg.grokKey)    keyGrok.value     = cfg.grokKey;
            if (cfg.groqKey)    keyGroq.value     = cfg.groqKey;
        });
    }

    let isStreamStopped = false;
    let pollInterval = null;

    function checkStatus() {
        chrome.runtime.sendMessage({ type: 'get-status' }, (resp) => {
            if (chrome.runtime.lastError) return;
            if (resp) {
                updateStatusPill(resp.connected ? 'connected' : 'disconnected');
                // Update host detection badge
                if (hostStatusBadge) {
                    if (resp.nativeConnected) {
                        hostStatusBadge.textContent = 'Host connected ✓';
                        hostStatusBadge.className = 'host-badge connected';
                    } else {
                        hostStatusBadge.textContent = 'Not detected';
                        hostStatusBadge.className = 'host-badge disconnected';
                    }
                }
                // Sync stream toggle button label to actual state
                if (btnStreamToggle) {
                    if (resp.connected) {
                        btnStreamToggle.textContent = '⏹ Stop';
                        btnStreamToggle.classList.remove('running');
                        isStreamStopped = false;
                    } else if (isStreamStopped) {
                        btnStreamToggle.textContent = '▶ Start';
                        btnStreamToggle.classList.add('running');
                    }
                }
            }
        });
        chrome.storage.local.get(['extensionLogs'], (cfg) => {
            const logBox = document.getElementById('log-box');
            if (logBox && cfg.extensionLogs) {
                logBox.textContent = cfg.extensionLogs.join('\n');
                logBox.scrollTop = logBox.scrollHeight;
            }
        });
    }
    function startStatusPolling() {
        if (pollInterval) clearInterval(pollInterval);
        checkStatus();
        pollInterval = setInterval(checkStatus, 1500);
    }

    // ── Stream Stop / Start toggle ────────────────────────────────────────────
    if (btnStreamToggle) {
        btnStreamToggle.addEventListener('click', () => {
            if (isStreamStopped) {
                // Currently stopped — start it
                isStreamStopped = false;
                btnStreamToggle.textContent = '⏹ Stop';
                btnStreamToggle.classList.remove('running');
                chrome.runtime.sendMessage({ type: 'start-stream' });
            } else {
                // Currently running — stop it
                isStreamStopped = true;
                btnStreamToggle.textContent = '▶ Start';
                btnStreamToggle.classList.add('running');
                chrome.runtime.sendMessage({ type: 'stop-stream' });
            }
        });
    }

    // ── Download PC Host ──────────────────────────────────────────────────────
    // Replace this URL after you host WinSpeechHost_Setup.bat on your server/GitHub
    const HOST_DOWNLOAD_URL = 'https://ex-5d5f.onrender.com/downloads/JarvisType_HostSetup.bat';
    if (btnDownloadHost) {
        btnDownloadHost.addEventListener('click', () => {
            chrome.tabs.create({ url: HOST_DOWNLOAD_URL });
        });
    }


    // ── Boot: check if already logged in and verify if > 24h old ────────────
    const TWENTY_FOUR_HOURS_MS = 24 * 60 * 60 * 1000;
    chrome.storage.local.get(['authToken','userLabel','lastVerifiedAt'], async (cfg) => {
        if (cfg.authToken) {
            const now = Date.now();
            const lastVerified = cfg.lastVerifiedAt || 0;
            if ((now - lastVerified) < TWENTY_FOUR_HOURS_MS) {
                showDashboard(cfg.authToken, cfg.userLabel || '');
            } else {
                // Verify with NeonDB
                try {
                    const resp = await fetch(VERIFY_URL, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ token: cfg.authToken })
                    });
                    if (resp.ok) {
                        const data = await resp.json();
                        chrome.storage.local.set({ lastVerifiedAt: now, userLabel: data.label || '' });
                        showDashboard(cfg.authToken, data.label || '');
                    } else {
                        chrome.storage.local.remove(['authToken', 'lastVerifiedAt']);
                        showLogin('Session expired or token revoked. Please log in.');
                    }
                } catch (e) {
                    // Offline fallback if previously verified
                    if (lastVerified > 0) {
                        showDashboard(cfg.authToken, cfg.userLabel || '');
                    } else {
                        showLogin('Could not verify token. Check connection.');
                    }
                }
            }
        } else {
            showLogin();
        }
    });

    // ── Login flow ────────────────────────────────────────────────────────────
    async function doLogin() {
        const token = tokenInput.value.trim();
        if (!token) {
            loginError.textContent = '⚠ Enter your access token';
            loginError.style.display = 'block';
            return;
        }

        btnConnect.textContent = 'Verifying...';
        btnConnect.disabled = true;
        loginError.style.display = 'none';

        try {
            const resp = await fetch(VERIFY_URL, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ token })
            });
            const data = await resp.json();

            if (data.success) {
                // Save to storage — also set lastVerifiedAt so 24h cache is fresh from login
                chrome.storage.local.set({ authToken: token, userLabel: data.label || '', lastVerifiedAt: Date.now() });
                // Tell service worker to reconnect with this token
                chrome.runtime.sendMessage({ type: 'reconnect', token, label: data.label || '' });
                // Also update native host config
                chrome.runtime.sendMessage({ type: 'set-config', authToken: token });
                showDashboard(token, data.label || '');
            } else {
                showLogin(data.error || 'Invalid token. Contact admin.');
            }
        } catch (e) {
            showLogin('⚠ Server unreachable. Try again.');
        } finally {
            btnConnect.textContent = 'Connect →';
            btnConnect.disabled = false;
        }
    }

    btnConnect.addEventListener('click', doLogin);
    tokenInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') doLogin(); });

    // ── Logout ────────────────────────────────────────────────────────────────
    btnLogout.addEventListener('click', () => {
        chrome.storage.local.remove(['authToken','userLabel']);
        chrome.runtime.sendMessage({ type: 'disconnect' });
        if (pollInterval) clearInterval(pollInterval);
        tokenInput.value = '';
        showLogin();
    });

    // ── API Keys ──────────────────────────────────────────────────────────────
    btnEditKeys.addEventListener('click', () => {
        isEditingKeys = !isEditingKeys;
        const type = isEditingKeys ? 'text' : 'password';
        [keyGemini, keyChatgpt, keyClaude, keyGrok, keyGroq].forEach(el => { if (el) el.type = type; });

        if (isEditingKeys) {
            btnEditKeys.textContent = 'Save Keys';
            btnEditKeys.style.borderColor = '#4ade80';
            btnEditKeys.style.color = '#4ade80';
        } else {
            btnEditKeys.textContent = 'Edit Keys';
            btnEditKeys.style.borderColor = 'rgba(255,255,255,0.12)';
            btnEditKeys.style.color = '#cbd5e1';
            const keys = {
                geminiKey:  keyGemini.value.trim(),
                chatgptKey: keyChatgpt.value.trim(),
                claudeKey:  keyClaude ? keyClaude.value.trim() : '',
                grokKey:    keyGrok.value.trim(),
                groqKey:    keyGroq ? keyGroq.value.trim() : ''
            };
            chrome.storage.local.set(keys);
            chrome.runtime.sendMessage({ type: 'sync-ai-keys', ...keys });
            showToast('💾 API Keys saved!');
        }
    });

    document.getElementById('btn-copy-gemini').addEventListener('click', () => {
        if (keyGemini.value) navigator.clipboard.writeText(keyGemini.value);
        showToast('✅ Gemini key copied!');
    });
    document.getElementById('btn-copy-chatgpt').addEventListener('click', () => {
        if (keyChatgpt.value) navigator.clipboard.writeText(keyChatgpt.value);
        showToast('✅ ChatGPT key copied!');
    });
    document.getElementById('btn-copy-claude').addEventListener('click', () => {
        if (keyClaude && keyClaude.value) navigator.clipboard.writeText(keyClaude.value);
        showToast('✅ Claude key copied!');
    });
    document.getElementById('btn-copy-grok').addEventListener('click', () => {
        if (keyGrok.value) navigator.clipboard.writeText(keyGrok.value);
        showToast('✅ Grok key copied!');
    });
    document.getElementById('btn-copy-groq').addEventListener('click', () => {
        if (keyGroq && keyGroq.value) navigator.clipboard.writeText(keyGroq.value);
        showToast('✅ Groq key copied!');
    });
});