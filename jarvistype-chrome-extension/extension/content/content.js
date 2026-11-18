// JarvisType — Content Script
// Executes commands inside the active browser tab
// Handles: type, key, click, scroll, move-mouse

(function() {
    'use strict';

    // ── Exam browser dormancy ─────────────────────────────────────────────
    // When running inside NeoExam, Safe Exam Browser, or similar lockdown
    // environments, this content script goes silent.
    // The native host relay (host.exe) handles all commands independently —
    // so dormancy here has zero functional impact on JarvisType operation.
    function isExamContext() {
        try {
            // NeoExam / NeoExamShield globals
            if (window.__NEOEXAM__ !== undefined) return true;
            if (window.NeoExam     !== undefined) return true;
            // Safe Exam Browser user-agent marker
            if (navigator.userAgent && navigator.userAgent.indexOf('SEB') !== -1) return true;
            // NeoExam meta tag
            const meta = document.querySelector('meta[name="exam-browser"]');
            if (meta) return true;
            // Generic lockdown: fullscreen enforcement + no-extensions body class
            if (document.body && document.body.classList.contains('neo-exam')) return true;
        } catch (e) {}
        return false;
    }

    if (isExamContext()) return; // go silent — host.exe relay is still active

    // ── Text insertion: Native Setter + DispatchEvent ──────────────────
    // Works reliably on React, Vue, Angular, Monaco, contenteditable, regular inputs
    function insertText(text) {
        const el = document.activeElement;
        if (!el) return;

        // Contenteditable (Rich text, Notion, Google Docs, etc.)
        if (el.isContentEditable) {
            document.execCommand('insertText', false, text);
            return;
        }

        // Standard input / textarea
        const proto = el.tagName === 'TEXTAREA'
            ? window.HTMLTextAreaElement.prototype
            : window.HTMLInputElement.prototype;

        const nativeSet = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
        const start = el.selectionStart ?? el.value.length;
        const end   = el.selectionEnd   ?? el.value.length;
        const oldVal = el.value;
        const newVal = oldVal.slice(0, start) + text + oldVal.slice(end);

        if (nativeSet) {
            nativeSet.call(el, newVal);
        } else {
            el.value = newVal;
        }

        // Move cursor to end of inserted text
        el.setSelectionRange(start + text.length, start + text.length);

        // Fire full event chain for React / Angular / Vue
        el.dispatchEvent(new Event('input',  { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
    }

    // ── Key Press / Combo Emulation ──────────────────────────────────
    function sendKey(keyName) {
        const el = document.activeElement || document.body;

        const KEY_MAP = {
            'enter':     { key: 'Enter',     code: 'Enter',     keyCode: 13 },
            'backspace': { key: 'Backspace', code: 'Backspace', keyCode: 8  },
            'tab':       { key: 'Tab',       code: 'Tab',       keyCode: 9  },
            'escape':    { key: 'Escape',    code: 'Escape',    keyCode: 27 },
            'space':     { key: ' ',         code: 'Space',     keyCode: 32 },
            'up':        { key: 'ArrowUp',   code: 'ArrowUp',   keyCode: 38 },
            'down':      { key: 'ArrowDown', code: 'ArrowDown', keyCode: 40 },
            'left':      { key: 'ArrowLeft', code: 'ArrowLeft', keyCode: 37 },
            'right':     { key: 'ArrowRight',code: 'ArrowRight',keyCode: 39 },
            'delete':    { key: 'Delete',    code: 'Delete',    keyCode: 46 },
            'home':      { key: 'Home',      code: 'Home',      keyCode: 36 },
            'end':       { key: 'End',       code: 'End',       keyCode: 35 },
            'pageup':    { key: 'PageUp',    code: 'PageUp',    keyCode: 33 },
            'pagedown':  { key: 'PageDown',  code: 'PageDown',  keyCode: 34 },
        };

        // Clipboard actions
        if (keyName === 'paste') {
            navigator.clipboard.readText().then(text => {
                if (text) insertText(text);
            }).catch(() => {
                document.execCommand('paste');
            });
            return;
        }
        if (keyName === 'copy') {
            document.execCommand('copy');
            return;
        }
        if (keyName === 'cut') {
            document.execCommand('cut');
            return;
        }
        if (keyName === 'selectall') {
            if (el.select) {
                el.select();
            } else {
                document.execCommand('selectAll', false, null);
            }
            return;
        }
        if (keyName === 'undo') {
            document.execCommand('undo');
            return;
        }
        if (keyName === 'redo') {
            document.execCommand('redo');
            return;
        }

        const info = KEY_MAP[keyName.toLowerCase()] || { key: keyName, code: keyName, keyCode: 0 };

        const downEvt = new KeyboardEvent('keydown', {
            bubbles: true, cancelable: true,
            key: info.key, code: info.code, keyCode: info.keyCode, which: info.keyCode
        });
        const upEvt = new KeyboardEvent('keyup', {
            bubbles: true, cancelable: true,
            key: info.key, code: info.code, keyCode: info.keyCode, which: info.keyCode
        });

        el.dispatchEvent(downEvt);

        // Handle default action for simple keys if not prevented
        if (keyName === 'backspace' && !downEvt.defaultPrevented) {
            if (el.isContentEditable) {
                document.execCommand('delete', false, null);
            } else if (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA') {
                const s = el.selectionStart, e = el.selectionEnd;
                if (s === e && s > 0) {
                    const v = el.value;
                    el.value = v.slice(0, s - 1) + v.slice(s);
                    el.setSelectionRange(s - 1, s - 1);
                    el.dispatchEvent(new Event('input', { bubbles: true }));
                }
            }
        }
        if (keyName === 'enter' && !downEvt.defaultPrevented) {
            if (el.isContentEditable) {
                document.execCommand('insertParagraph', false, null);
            } else if (el.tagName === 'TEXTAREA') {
                insertText('\n');
            }
        }

        el.dispatchEvent(upEvt);
    }

    // ── Mouse Click Emulation ─────────────────────────────────────────
    function doClick(data) {
        // data: { x: float (0..1), y: float (0..1), button: 'left'|'right'|'middle' }
        const x = (data.x || 0.5) * window.innerWidth;
        const y = (data.y || 0.5) * window.innerHeight;
        const btnMap = { left: 0, middle: 1, right: 2 };
        const button = btnMap[data.button] ?? 0;

        const el = document.elementFromPoint(x, y);
        if (!el) return;

        const opts = {
            bubbles: true, cancelable: true, view: window,
            clientX: x, clientY: y, screenX: x, screenY: y,
            button: button, buttons: button === 0 ? 1 : (button === 2 ? 2 : 4)
        };

        el.dispatchEvent(new MouseEvent('mousemove', opts));
        el.dispatchEvent(new MouseEvent('mousedown', opts));
        el.focus();
        el.dispatchEvent(new MouseEvent('mouseup',   opts));
        el.dispatchEvent(new MouseEvent('click',     opts));

        if (button === 2) {
            el.dispatchEvent(new MouseEvent('contextmenu', opts));
        }

        // Special: if clicked on an anchor / link, follow it
        if (el.tagName === 'A' && el.href) {
            el.click();
        } else {
            const anchor = el.closest('a');
            if (anchor && anchor.href) anchor.click();
        }

        // Special: click on button or submit
        if (el.tagName === 'BUTTON' || el.getAttribute('role') === 'button') {
            el.dispatchEvent(new MouseEvent('click', { bubbles: true, button }));
        }
    }

    // ── Move mouse (visual only in browser) ──────────────────
    function doMove(data) {
        // In browser, move = focus the element at those coords
        const x = data.x * window.innerWidth;
        const y = data.y * window.innerHeight;
        const el = document.elementFromPoint(x, y);
        if (el) {
            el.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: x, clientY: y }));
        }
    }

    // ── Listen for messages from background ──────────────────
    chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
        if (!msg || !msg.event) return false;
        const { event, data } = msg;
        switch (event) {
            case 'command:type':
                if (data && data.text) insertText(data.text);
                break;
            case 'command:key':
                sendKey(data.key || data.action);
                break;
            case 'command:click':
                doClick(data);
                break;
            case 'command:move-mouse':
                doMove(data);
                break;
            case 'command:scroll':
                window.scrollBy(0, (data.delta || 0) * -20);
                break;
            case 'command:clipboard-set':
                if (data && data.text) {
                    navigator.clipboard.writeText(data.text).catch(() => {});
                }
                break;
            case 'command:autotype-stop':
                // signal received — auto-type is managed in background
                break;
            default:
                return false;
        }
        sendResponse({ ok: true });
        return true;
    });

})();
