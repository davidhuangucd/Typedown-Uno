// Host bridge shim for the Typedown editor inside an Uno Platform WebView2.
// The editor only knows window.chrome.webview (Microsoft WebView2). On Linux/macOS Uno exposes the
// WebKit message handler "unoWebView" instead; on Windows the real chrome.webview is present and is
// simply passed through. Host -> editor always goes through window.__unoDeliver(json) (ExecuteScriptAsync).
(function () {
    var native = window.chrome && window.chrome.webview;
    var listeners = [];
    function send(raw) {
        if (native) { native.postMessage(raw); return; }
        var wk = window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.unoWebView;
        if (wk) { wk.postMessage(raw); return; }
        if (window.unoWebView && window.unoWebView.postMessage) { window.unoWebView.postMessage(raw); return; }
        console.warn('uno-bridge: no host bridge available', raw);
    }
    if (!native) {
        window.chrome = window.chrome || {};
        window.chrome.webview = {
            addEventListener: function (type, listener) { if (type === 'message') listeners.push(listener); },
            removeEventListener: function (type, listener) { listeners = listeners.filter(function (l) { return l !== listener; }); },
            postMessage: send
        };
    }
    // Shell shortcuts pressed while the (native) web view has focus never reach the host window on Linux/macOS,
    // so forward the ones the shell handles as a "Shortcut" message. The editor keeps its own (Ctrl+B/I/…).
    // Which key presses the shell wants back; replaced by the host (see "ShortcutMap") once it knows the bindings.
    var forwarded = [];
    var findShortcut = { key: 'f', ctrl: true, shift: false, alt: false };
    window.addEventListener('keydown', function (e) {
        var ctrl = e.ctrlKey || e.metaKey;
        var key = e.key === 'Tab' ? 'tab' : e.key.toLowerCase();
        if (matches(findShortcut, key, ctrl, e.shiftKey, e.altKey)) { e.preventDefault(); e.stopPropagation(); showFind(); return; }
        if (key === 'escape' && !ctrl && findBar && findBar.style.display !== 'none') { e.preventDefault(); e.stopPropagation(); hideFind(); return; }
        // Ctrl+C/Ctrl+X go through the editor so the clipboard carries the Markdown source, the same as the
        // context menu and as the Windows edition. The paste listener routes Ctrl+V through the host so text and
        // images use the same native clipboard paths as the menus.
        // Muya tracks its cursor separately from the browser selection. WebKit can therefore report an empty
        // window.getSelection() even while the editor has selected text. Always let Muya decide whether there is
        // anything to copy, except while a regular input (for example the find box) owns the keystroke.
        var target = e.target;
        var nativeTextInput = target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA');
        if (ctrl && !e.shiftKey && !e.altKey && (key === 'c' || key === 'x') && window.__typedownMuya && !nativeTextInput) {
            if (key === 'x' && isReadOnly()) { e.preventDefault(); return; }
            e.preventDefault();
            e.stopPropagation();
            local(key === 'c' ? 'Copy' : 'Cut', { type: 'normal', copyInfo: null });
            return;
        }
        // Alt+1..9 belongs to the tab bar, which lives in the shell; the editor never does anything with it.
        if (e.altKey && !ctrl && !e.shiftKey && key.length === 1 && key >= '0' && key <= '9') {
            e.preventDefault();
            e.stopPropagation();
            send(JSON.stringify({ type: 'message', name: 'Shortcut', args: { key: key, ctrl: false, shift: false, alt: true } }));
            return;
        }
        for (var i = 0; i < forwarded.length; i++) {
            if (!matches(forwarded[i], key, ctrl, e.shiftKey, e.altKey)) continue;
            e.preventDefault();
            e.stopPropagation();
            send(JSON.stringify({ type: 'message', name: 'Shortcut', args: { key: key, ctrl: ctrl, shift: e.shiftKey, alt: e.altKey } }));
            return;
        }
    }, true);

    function matches(binding, key, ctrl, shift, alt) {
        return binding && binding.key === key && !!binding.ctrl === ctrl && !!binding.shift === shift && !!binding.alt === alt;
    }

    function deliver(data) {
        if (native && typeof native.dispatchEvent === 'function') {
            native.dispatchEvent(new MessageEvent('message', { data: data }));
            return;
        }
        listeners.forEach(function (l) { try { l({ data: data }); } catch (e) { console.error(e); } });
    }
    function local(name, args) { deliver(JSON.stringify({ name: name, args: args })); }

    // UI the editor expects the host to draw. The editor's reports are sent diffed, so the host reassembles them
    // and hands each one back as "ShowFloat"; the drawing happens here because a host popup cannot cover the
    // native web view on Linux.
    var floatHandlers = {
        OpenFormatPicker: function (a) { showFormatPicker(a); },
        OpenImageToolbar: function (a) { showImageToolbar(a); },
        OpenFrontMenu: function (a) { showFrontMenu(a); },
        OpenTableTools: function (a) { showTableTools(a); },
        OpenToolTip: function (a) { showToolTip(a); }
    };

    // In-page find bar. Keyboard focus cannot be moved from the native web view to the host window on Linux/macOS,
    // so the find UI lives in the page and drives the editor's own Search/Find handlers directly.
    var findBar = null, findInput = null, findCount = null, findRow = null, replaceRow = null, replaceInput = null;
    // Labels for the find bar; replaced by the host (see "FindStrings") in the interface language.
    var findStrings = {
        find: 'Find', replaceWith: 'Replace with', replace: 'Replace', replaceAll: 'Replace all',
        previous: 'Previous (Shift+Enter)', next: 'Next (Enter)', close: 'Close (Esc)',
    };
    var findOptions = { searchIsCaseSensitive: false, searchIsWholeWord: false, searchIsRegexp: false };
    // "Find next" starts after the caret, so the editor needs to be told where it is; with no caret the search
    // runs from the top of the document.
    function searchOptions() {
        var muya = window.__typedownMuya;
        var cursor = muya && muya.contentState && muya.contentState.cursor;
        return Object.assign({ selection: cursor || {} }, findOptions);
    }
    function runSearch() {
        local('Search', { value: findInput.value, opt: searchOptions() });
        setTimeout(function () {
            var muya = window.__typedownMuya;
            var matches = muya && muya.contentState && muya.contentState.searchMatches && muya.contentState.searchMatches.matches;
            var n = matches ? matches.length : document.querySelectorAll('.ag-selection, .ag-highlight').length;
            findCount.textContent = findInput.value ? String(n) : '';
        }, 80);
    }
    function ensureFindBar() {
        if (findBar) return;
        findBar = document.createElement('div');
        findBar.id = 'uno-find-bar';
        findBar.style.cssText = 'position:fixed;top:8px;right:24px;z-index:100000;display:none;flex-direction:column;gap:6px;padding:6px 8px;border-radius:6px;' +
            'background:var(--floatBgColor);border:1px solid var(--floatBorderColor);box-shadow:var(--floatShadow);' +
            'font:13px system-ui,sans-serif;color:var(--editorColor);';
        findRow = document.createElement('div');
        findRow.style.cssText = 'display:flex;align-items:center;gap:6px;';
        replaceRow = document.createElement('div');
        replaceRow.style.cssText = 'display:none;align-items:center;gap:6px;';
        findInput = document.createElement('input');
        findInput.type = 'text';
        findInput.placeholder = findStrings.find;
        findInput.style.cssText = 'width:220px;padding:4px 6px;border:1px solid var(--floatBorderColor);border-radius:4px;' +
            'background:var(--inputBgColor);color:var(--editorColor);outline:none;';
        findCount = document.createElement('span');
        findCount.style.cssText = 'min-width:24px;color:var(--editorColor60);';
        function button(text, title, onClick) {
            var b = document.createElement('button');
            b.textContent = text; b.title = title;
            b.style.cssText = 'padding:2px 8px;border:1px solid var(--floatBorderColor);border-radius:4px;' +
                'background:var(--itemBgColor);color:var(--editorColor);cursor:pointer;';
            b.tabIndex = -1; // Tab moves between the two text fields, not through the buttons
            b.addEventListener('click', function (e) { e.preventDefault(); onClick(); });
            return b;
        }
        replaceInput = document.createElement('input');
        replaceInput.type = 'text';
        replaceInput.placeholder = findStrings.replaceWith;
        replaceInput.style.cssText = findInput.style.cssText;
        findRow.appendChild(findInput);
        findRow.appendChild(findCount);
        findRow.appendChild(button('\u2191', findStrings.previous, function () { local('Find', { action: 'prev' }); }));
        findRow.appendChild(button('\u2193', findStrings.next, function () { local('Find', { action: 'next' }); }));
        findRow.appendChild(button('\u21c4', findStrings.replace, toggleReplace));
        findRow.appendChild(button('\u2715', findStrings.close, hideFind));
        replaceRow.appendChild(replaceInput);
        replaceRow.appendChild(button(findStrings.replace, findStrings.replace, function () { runReplace(false); }));
        replaceRow.appendChild(button(findStrings.replaceAll, findStrings.replaceAll, function () { runReplace(true); }));
        replaceInput.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { e.preventDefault(); runReplace(e.ctrlKey || e.metaKey); }
            else if (e.key === 'Escape') { e.preventDefault(); hideFind(); }
            else if (e.key === 'Tab' && e.shiftKey) { e.preventDefault(); findInput.focus(); }
            e.stopPropagation();
        });
        findBar.appendChild(findRow);
        findBar.appendChild(replaceRow);
        findInput.addEventListener('input', runSearch);
        findInput.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { e.preventDefault(); local('Find', { action: e.shiftKey ? 'prev' : 'next' }); }
            else if (e.key === 'Escape') { e.preventDefault(); hideFind(); }
            else if (e.key === 'Tab' && !e.shiftKey && replaceRow.style.display !== 'none') { e.preventDefault(); replaceInput.focus(); }
            e.stopPropagation();
        });
        document.body.appendChild(findBar);
    }
    // Replacing works off the current search: the editor replaces what the last Search call matched, so the
    // search is re-run before and after, otherwise "replace all" would work from a stale set of matches.
    function runReplace(all) {
        if (isReadOnly()) return;
        runSearch();
        setTimeout(function () {
            local('Replace', { value: replaceInput.value, opt: Object.assign(searchOptions(), { isSingle: !all }) });
            setTimeout(runSearch, 60);
        }, 60);
    }

    function toggleReplace(show) {
        var on = typeof show === 'boolean' ? show : replaceRow.style.display === 'none';
        replaceRow.style.display = on ? 'flex' : 'none';
        if (on) replaceInput.focus();
    }

    function showFind(value, replace) {
        ensureFindBar();
        findBar.style.display = 'flex';
        toggleReplace(!!replace);
        if (typeof value === 'string') findInput.value = value;
        local('SearchOpenChange', { open: 1 });
        findInput.focus();
        findInput.select();
        if (findInput.value) runSearch();
    }
    function hideFind() {
        if (!findBar) return;
        findBar.style.display = 'none';
        local('Search', { value: '', opt: searchOptions() });
        local('SearchOpenChange', { open: 0 });
        var editor = document.getElementById('ag-editor-id') || document.querySelector('.CodeMirror textarea');
        if (editor) editor.focus();
    }


    // In-page context menu. The editor suppresses the native menu (it expects the host to show its own), and on
    // Linux the web view is a separate native window, so a host flyout would be drawn behind it: the menu lives
    // in the page, like the find bar. Labels come from the host ("ContextMenuStrings").
    var menuStrings = { copy: 'Copy', cut: 'Cut', paste: 'Paste', selectAll: 'Select all' };
    var contextMenu = null;

    function isReadOnly() {
        if (document.body.classList.contains('read-only')) return true;
        var editable = document.querySelector('[contenteditable]');
        return !!editable && editable.getAttribute('contenteditable') === 'false';
    }

    function selectedText() {
        var s = window.getSelection();
        return s ? s.toString() : '';
    }

    function hideContextMenu() {
        if (contextMenu) contextMenu.style.display = 'none';
    }

    function selectAll() {
        var root = document.getElementById('ag-editor-id');
        if (isReadOnly()) {
            if (!root) return;
            var range = document.createRange();
            range.selectNodeContents(root);
            var sel = window.getSelection();
            sel.removeAllRanges();
            sel.addRange(range);
            return;
        }
        local('SelectAll');
    }

    function showContextMenu(x, y) {
        if (!contextMenu) {
            contextMenu = document.createElement('div');
            contextMenu.id = 'uno-context-menu';
            contextMenu.style.cssText = 'position:fixed;z-index:100001;display:none;min-width:150px;padding:4px;border-radius:6px;' +
                'background:var(--floatBgColor);border:1px solid var(--floatBorderColor);box-shadow:var(--floatShadow);' +
                'font:13px system-ui,sans-serif;color:var(--editorColor);user-select:none;';
            document.body.appendChild(contextMenu);
        }
        var readOnly = isReadOnly();
        // Remember the selection now: clicking the menu can collapse it, and WebKit refuses execCommand('copy')
        // often enough that the host is the reliable way to reach the clipboard.
        var text = selectedText();
        var hasSelection = !!text;
        function copyToHost() { send(JSON.stringify({ type: 'message', name: 'ClipboardSetText', args: { text: text } })); }
        // Clicking the menu collapses the selection, and cut/paste act on the editor's own cursor: remember it
        // while the menu opens and put it back before handing the command over.
        var muya = window.__typedownMuya;
        var savedCursor = muya && muya.contentState && muya.contentState.cursor
            ? JSON.parse(JSON.stringify(muya.contentState.cursor)) : null;
        function editorClipboard(name) {
            if (!muya) return false;
            restoreCursor();
            local(name, { type: 'normal', copyInfo: null });
            return true;
        }
        function restoreCursor() {
            if (!muya || !savedCursor) return;
            try {
                muya.contentState.cursor = savedCursor;
                muya.contentState.setCursor();
            } catch (e) { }
        }
        contextMenu.textContent = '';
        // Cut and paste change the document, so reading mode only offers copy and select all.
        var items = [
            // The editor's own copy puts Markdown and HTML on the clipboard (the host's SetClipboard), which keeps
            // bold, code and links across a copy/paste; plain text is the fallback when there is no editor.
            { label: menuStrings.copy, enabled: hasSelection, run: function () { if (!editorClipboard('Copy')) copyToHost(); } },
            { label: menuStrings.cut, enabled: hasSelection && !readOnly, hidden: readOnly, run: function () { if (!editorClipboard('Cut')) { copyToHost(); local('DeleteSelection'); } } },
            { label: menuStrings.paste, enabled: !readOnly, hidden: readOnly, run: function () { restoreCursor(); send(JSON.stringify({ type: 'message', name: 'ClipboardTextRequest', args: {} })); } },
            { separator: true },
            { label: menuStrings.selectAll, enabled: true, run: selectAll }
        ];
        items.forEach(function (item) {
            if (item.hidden) return;
            if (item.separator) {
                var line = document.createElement('div');
                line.style.cssText = 'height:1px;margin:4px 6px;background:var(--floatBorderColor);';
                contextMenu.appendChild(line);
                return;
            }
            var row = document.createElement('div');
            row.textContent = item.label;
            row.style.cssText = 'padding:6px 12px;border-radius:4px;cursor:' + (item.enabled ? 'pointer' : 'default') + ';opacity:' + (item.enabled ? '1' : '0.4') + ';';
            if (item.enabled) {
                row.addEventListener('mouseenter', function () { row.style.background = 'var(--floatHoverColor)'; });
                row.addEventListener('mouseleave', function () { row.style.background = 'transparent'; });
                row.addEventListener('mouseup', function (e) { e.preventDefault(); e.stopPropagation(); hideContextMenu(); item.run(); });
            }
            contextMenu.appendChild(row);
        });
        contextMenu.style.display = 'block';
        // keep it inside the window
        var rect = contextMenu.getBoundingClientRect();
        contextMenu.style.left = Math.min(x, Math.max(0, window.innerWidth - rect.width - 4)) + 'px';
        contextMenu.style.top = Math.min(y, Math.max(0, window.innerHeight - rect.height - 4)) + 'px';
    }

    window.addEventListener('contextmenu', function (e) {
        if (contextMenu && contextMenu.contains(e.target)) { e.preventDefault(); return; }
        e.preventDefault();
        // the editor's own handler runs too and keeps its cursor in step; only the native menu is suppressed
        showContextMenu(e.clientX, e.clientY);
        // capture: the editor stops the event from bubbling out of its container
    }, true);
    window.addEventListener('mousedown', function (e) {
        if (contextMenu && contextMenu.style.display !== 'none' && !contextMenu.contains(e.target)) hideContextMenu();
    }, true);
    window.addEventListener('keydown', function (e) { if (e.key === 'Escape') hideContextMenu(); }, true);
    window.addEventListener('scroll', hideContextMenu, true);
    window.addEventListener('blur', hideContextMenu);


    // Floating tools. The editor hands these to the host (the Windows edition draws them as XAML flyouts) and
    // this port had none of them, so selecting text or clicking an image showed nothing at all. Like the context
    // menu they live in the page, because a host popup cannot cover the native web view on Linux.
    var floatBox = null, floatKind = null;

    function hideFloat() {
        if (floatBox) { floatBox.style.display = 'none'; floatKind = null; }
    }

    function floatContainer() {
        if (!floatBox) {
            floatBox = document.createElement('div');
            floatBox.id = 'uno-float-tools';
            floatBox.style.cssText = 'position:fixed;z-index:100000;display:none;padding:3px;border-radius:6px;' +
                'background:var(--floatBgColor);border:1px solid var(--floatBorderColor);box-shadow:var(--floatShadow);' +
                'font:13px system-ui,sans-serif;color:var(--editorColor);user-select:none;white-space:nowrap;';
            document.body.appendChild(floatBox);
        }
        floatBox.textContent = '';
        return floatBox;
    }

    /// Places the box above the reference rectangle, or below it when there is no room.
    function placeFloat(rect) {
        floatBox.style.display = 'block';
        var box = floatBox.getBoundingClientRect();
        var left = Math.max(4, Math.min((rect.left + rect.width / 2) - box.width / 2, window.innerWidth - box.width - 4));
        var top = rect.top - box.height - 8;
        if (top < 4) top = rect.bottom + 8;
        floatBox.style.left = left + 'px';
        floatBox.style.top = top + 'px';
    }

    function toolButton(label, title, active, run) {
        var b = document.createElement('button');
        b.textContent = label;
        b.title = title || '';
        b.style.cssText = 'min-width:28px;height:26px;margin:0 1px;padding:0 6px;border:none;border-radius:4px;cursor:pointer;' +
            'font:13px system-ui,sans-serif;background:' + (active ? 'var(--selectionColor)' : 'transparent') + ';color:var(--editorColor);';
        b.addEventListener('mouseenter', function () { if (!active) b.style.background = 'var(--floatHoverColor)'; });
        b.addEventListener('mouseleave', function () { if (!active) b.style.background = 'transparent'; });
        b.addEventListener('mousedown', function (e) { e.preventDefault(); e.stopPropagation(); });
        b.addEventListener('mouseup', function (e) { e.preventDefault(); e.stopPropagation(); run(); });
        return b;
    }

    // Select text -> bold/italic/… bar. `formats` says which ones the selection already has.
    function showFormatPicker(args) {
        var active = {};
        (args.formats || []).forEach(function (f) { active[f.type] = true; });
        var box = floatContainer();
        [['B', 'strong', 'font-weight:700'], ['I', 'em', 'font-style:italic'], ['U', 'u', 'text-decoration:underline'],
         ['S', 'del', 'text-decoration:line-through'], ['</>', 'inline_code', ''], ['M', 'mark', 'background:rgba(255,235,0,0.5)'],
         ['∑', 'inline_math', ''], ['🔗', 'link', ''], ['⌫', 'clear', '']].forEach(function (item) {
            var b = toolButton(item[0], item[1], !!active[item[1]], function () { local('Format', item[1]); hideFloat(); });
            if (item[2]) b.style.cssText += ';' + item[2];
            box.appendChild(b);
        });
        floatKind = 'format';
        placeFloat(args.boundingClientRect);
    }

    // Click an image -> alignment and delete.
    function showImageToolbar(args) {
        var attrs = args.attrs || {};
        var align = attrs['data-align'];
        var box = floatContainer();
        [['⇤', 'left'], ['⇔', 'center'], ['⇥', 'right'], ['⇹', 'inline']].forEach(function (item) {
            box.appendChild(toolButton(item[0], item[1], align === item[1], function () {
                local('ImageEditToolbarClick', { type: item[1] });
                hideFloat();
            }));
        });
        // 'edit' would ask for the host's image selector, which this port does not draw; picking a replacement
        // file is what that button is for anyway.
        box.appendChild(toolButton('✎', 'edit', false, function () {
            hideFloat();
            send(JSON.stringify({ type: 'message', name: 'ReplaceImageRequest', args: {} }));
        }));
        box.appendChild(toolButton('🗑', 'delete', false, function () { local('ImageEditToolbarClick', { type: 'delete' }); hideFloat(); }));
        floatKind = 'image';
        placeFloat(args.boundingClientRect);
    }

    // Click the paragraph marker -> what to do with the block.
    function showFrontMenu(args) {
        var box = floatContainer();
        box.style.whiteSpace = 'normal';
        var items = [
            [menuStrings.duplicate, function () { local('Duplicate'); }],
            [menuStrings.insertBefore, function () { local('InsertParagraph', 'before'); }],
            [menuStrings.insertAfter, function () { local('InsertParagraph', 'after'); }],
            [menuStrings.deleteParagraph, function () { local('DeleteParagraph'); }]
        ];
        items.forEach(function (item) {
            var row = document.createElement('div');
            row.textContent = item[0];
            row.style.cssText = 'padding:6px 12px;border-radius:4px;cursor:pointer;';
            row.addEventListener('mouseenter', function () { row.style.background = 'var(--floatHoverColor)'; });
            row.addEventListener('mouseleave', function () { row.style.background = 'transparent'; });
            row.addEventListener('mouseup', function (e) { e.preventDefault(); e.stopPropagation(); hideFloat(); local('FrontMenuClosed'); item[1](); });
            box.appendChild(row);
        });
        floatKind = 'front';
        placeFloat(args.boundingClientRect);
    }

    // Click a table's drag bar -> rows and columns.
    function showTableTools(args) {
        var bar = (args.tableInfo || {}).barType;
        var box = floatContainer();
        var items = bar === 'left'
            ? [[menuStrings.insertRowAbove, 'insert', 'previous', 'row'], [menuStrings.insertRowBelow, 'insert', 'next', 'row'], [menuStrings.deleteRow, 'remove', 'current', 'row']]
            : [[menuStrings.insertColLeft, 'insert', 'left', 'column'], [menuStrings.insertColRight, 'insert', 'right', 'column'], [menuStrings.deleteCol, 'remove', 'current', 'column']];
        box.style.whiteSpace = 'normal';
        items.forEach(function (item) {
            var row = document.createElement('div');
            row.textContent = item[0];
            row.style.cssText = 'padding:6px 12px;border-radius:4px;cursor:pointer;';
            row.addEventListener('mouseenter', function () { row.style.background = 'var(--floatHoverColor)'; });
            row.addEventListener('mouseleave', function () { row.style.background = 'transparent'; });
            row.addEventListener('mouseup', function (e) {
                e.preventDefault(); e.stopPropagation(); hideFloat();
                local('EditTable', { action: item[1], location: item[2], target: item[3] });
            });
            box.appendChild(row);
        });
        floatKind = 'table';
        placeFloat(args.boundingClientRect);
    }

    // Hover tooltip for the editor's own icons.
    var tipBox = null;
    function showToolTip(args) {
        if (!args.open) { if (tipBox) tipBox.style.display = 'none'; return; }
        if (!tipBox) {
            tipBox = document.createElement('div');
            tipBox.id = 'uno-tooltip';
            tipBox.style.cssText = 'position:fixed;z-index:100002;display:none;padding:3px 8px;border-radius:4px;' +
                'background:var(--floatBgColor);color:var(--editorColor);border:1px solid var(--floatBorderColor);box-shadow:var(--floatShadow);' +
                'font:12px system-ui,sans-serif;pointer-events:none;white-space:nowrap;';
            document.body.appendChild(tipBox);
        }
        tipBox.textContent = menuStrings['tip_' + args.tooltip] || args.tooltip;
        tipBox.style.display = 'block';
        var rect = args.boundingClientRect, box = tipBox.getBoundingClientRect();
        tipBox.style.left = Math.max(4, Math.min(rect.left + rect.width / 2 - box.width / 2, window.innerWidth - box.width - 4)) + 'px';
        var top = rect.top - box.height - 6;
        tipBox.style.top = (top < 4 ? rect.bottom + 6 : top) + 'px';
    }

    window.addEventListener('mousedown', function (e) {
        if (floatBox && floatKind && !floatBox.contains(e.target)) hideFloat();
    }, true);
    window.addEventListener('keydown', function (e) { if (e.key === 'Escape') hideFloat(); }, true);
    window.addEventListener('scroll', hideFloat, true);

    // Files dropped on the editor: the page is a native web view, so the host never sees the drop. WebKit exposes
    // the dropped paths as text/uri-list, which is enough for the host to open documents and insert images.
    window.addEventListener('dragover', function (e) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; }, true);
    window.addEventListener('drop', function (e) {
        var list = e.dataTransfer && e.dataTransfer.getData('text/uri-list');
        if (!list) return;
        var paths = list.split(/\r?\n/)
            .filter(function (l) { return l && l.indexOf('#') !== 0 && l.indexOf('file://') === 0; })
            .map(function (l) { return decodeURIComponent(l.replace(/^file:\/\//, '')); });
        if (!paths.length) return;
        e.preventDefault();
        e.stopPropagation();
        send(JSON.stringify({ type: 'message', name: 'FilesDropped', args: { paths: paths } }));
    }, true);

    function hasImage(items) {
        for (var i = 0; i < items.length; i++) if (items[i].kind === 'file' && items[i].type.indexOf('image/') === 0) return true;
        return false;
    }

    // Pasted images are handed to the host, which stores them next to the document and inserts a link; without
    // this the editor would embed a multi-megabyte data: URL in the Markdown.
    window.addEventListener('paste', function (e) {
        var items = e.clipboardData && e.clipboardData.items;
        var hasText = false;
        for (var k = 0; items && k < items.length; k++) if (items[k].kind === 'string') hasText = true;
        if (!items || !items.length || (!hasText && !hasImage(items))) {
            // WebKit does not always expose the X11 clipboard to the page. The host can distinguish text from
            // images and use the native clipboard API when the page receives no items.
            e.preventDefault();
            e.stopImmediatePropagation();
            send(JSON.stringify({ type: 'message', name: 'ClipboardPasteRequest', args: {} }));
            return;
        }
        for (var i = 0; i < items.length; i++) {
            if (items[i].kind !== 'file' || items[i].type.indexOf('image/') !== 0) continue;
            var file = items[i].getAsFile();
            if (!file) continue;
            e.preventDefault();
            e.stopPropagation();
            var reader = new FileReader();
            reader.onload = function () {
                send(JSON.stringify({ type: 'message', name: 'ImagePasted', args: { dataUrl: reader.result } }));
            };
            reader.readAsDataURL(file);
            return;
        }
        if (hasText) {
            // Use the host path for text so keyboard and context-menu paste behave identically.
            e.preventDefault();
            e.stopImmediatePropagation();
            send(JSON.stringify({ type: 'message', name: 'ClipboardTextRequest', args: {} }));
        }
    }, true);

    window.__unoDeliver = function (data) {
        try {
            var msg = JSON.parse(data);
            if (msg && msg.name === 'ShortcutMap') {
                forwarded = (msg.args && msg.args.keys) || [];
                for (var i = 0; i < forwarded.length; i++)
                    if (forwarded[i].find) findShortcut = forwarded[i];
                return;
            }
            if (msg && msg.name === 'ContextMenuStrings') { menuStrings = Object.assign(menuStrings, msg.args || {}); return; }
            if (msg && msg.name === 'FindStrings') {
                findStrings = Object.assign(findStrings, msg.args || {});
                if (findInput) findInput.placeholder = findStrings.find;
                if (replaceInput) replaceInput.placeholder = findStrings.replaceWith;
                return;
            }
            if (msg && msg.name === 'ShowFloat') {
                var handler = floatHandlers[msg.args && msg.args.kind];
                if (handler) handler((msg.args && msg.args.args) || {});
                return;
            }
            if (msg && msg.name === 'ShowFind') { if (msg.args && msg.args.opt) findOptions = msg.args.opt; showFind(msg.args && msg.args.value, msg.args && msg.args.replace); return; }
            if (msg && msg.name === 'HideFind') { hideFind(); return; }
        } catch (e) { }
        deliver(data);
    };
})();
