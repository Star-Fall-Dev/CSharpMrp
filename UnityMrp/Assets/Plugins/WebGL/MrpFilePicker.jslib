mergeInto(LibraryManager.library, {
    $MRPFilePicker: { active: null },

    MRPWebChooseFile__deps: ['$MRPFilePicker', '$FS'],
    MRPWebChooseFile: function(receiverPtr, requestPtr, cachePtr, maxFileBytes, choosePtr, cancelPtr) {
        var receiver = UTF8ToString(receiverPtr);
        var request = UTF8ToString(requestPtr);
        var cache = UTF8ToString(cachePtr);
        var chooseLabel = UTF8ToString(choosePtr);
        var cancelLabel = UTF8ToString(cancelPtr);
        var input = document.createElement('input');
        input.type = 'file';
        input.accept = '.mrp';
        input.style.display = 'none';
        input.setAttribute('aria-label', chooseLabel);
        var overlay = null;
        var reader = null;
        var stream = null;
        var path = null;
        var finished = false;

        function finish(status, name, message, notify) {
            if (finished) {
                return;
            }
            finished = true;
            input.remove();
            if (overlay) {
                overlay.remove();
            }
            if (reader && reader.readyState === 1) {
                reader.abort();
            }
            try {
                if (stream) {
                    FS.close(stream);
                    stream = null;
                }
            } catch (error) {
                status = 'error';
                message = String(error);
            }
            if (status !== 'ok' && path) {
                try {
                    FS.unlink(path);
                } catch (error) {
                    console.error('MRP picker cache cleanup failed', error);
                }
            }
            MRPFilePicker.active = null;
            if (notify !== false) {
                SendMessage(receiver, 'OnFilePicked', JSON.stringify({
                    request: request, status: status, path: path,
                    name: name || '', message: message || ''
                }));
            }
        }

        if (MRPFilePicker.active) {
            throw new Error('A file picker is already open.');
        }
        MRPFilePicker.active = function() {
            finish('cancel', '', '', false);
        };
        input.addEventListener('cancel', function() {
            finish('cancel');
        });
        input.addEventListener('change', function() {
            var file = input.files && input.files[0];
            if (!file) {
                finish('cancel');
                return;
            }
            if (overlay) {
                overlay.remove();
            }
            var extension = /\.mrp$/i.test(file.name);
            if (!extension) {
                finish('error', file.name, 'Choose an MRP file.');
                return;
            }
            if (file.size > maxFileBytes) {
                finish('error', file.name, 'File exceeds the import size limit.');
                return;
            }
            try {
                FS.mkdirTree(cache);
                path = cache + '/web-picker-' + request + '.mrp';
                stream = FS.open(path, 'w');
                reader = new FileReader();
                var offset = 0;
                function readNext() {
                    if (offset === file.size) {
                        finish('ok', file.name);
                        return;
                    }
                    reader.readAsArrayBuffer(file.slice(offset, offset + 1024 * 1024));
                }
                reader.onerror = function() {
                    finish('error', file.name, String(reader.error || 'File read failed.'));
                };
                reader.onabort = function() {
                    finish('cancel');
                };
                reader.onload = function() {
                    if (finished) {
                        return;
                    }
                    try {
                        var bytes = new Uint8Array(reader.result);
                        if (bytes.length === 0) {
                            throw new Error('Unexpected end of file.');
                        }
                        var written = FS.write(stream, bytes, 0, bytes.length, offset);
                        if (written !== bytes.length) {
                            throw new Error('Incomplete cache write.');
                        }
                        offset += written;
                        readNext();
                    } catch (error) {
                        finish('error', file.name, String(error));
                    }
                };
                readNext();
            } catch (error) {
                finish('error', file.name, String(error));
            }
        });
        try {
            document.body.appendChild(input);
            if (!navigator.userActivation || navigator.userActivation.isActive) {
                input.click();
            } else {
                // Unity may receive a click on a later frame. A DOM button restores activation.
                overlay = document.createElement('div');
                overlay.style.cssText = 'position:fixed;inset:0;z-index:2147483647;background:#0009;display:flex;align-items:center;justify-content:center;gap:16px';
                var choose = document.createElement('button');
                choose.textContent = chooseLabel;
                choose.style.cssText = 'padding:16px;font-size:18px';
                choose.onclick = function() {
                    input.click();
                };
                var cancel = document.createElement('button');
                cancel.textContent = cancelLabel;
                cancel.style.cssText = choose.style.cssText;
                cancel.onclick = function() {
                    finish('cancel');
                };
                overlay.appendChild(choose);
                overlay.appendChild(cancel);
                document.body.appendChild(overlay);
                choose.focus();
            }
        } catch (error) {
            finish('error', '', String(error));
        }
    },

    MRPWebCancelFilePicker__deps: ['$MRPFilePicker'],
    MRPWebCancelFilePicker: function() {
        if (MRPFilePicker.active) {
            MRPFilePicker.active();
        }
    },

    MRPWebFlushFileSystem__deps: ['$FS'],
    MRPWebFlushFileSystem: function(receiverPtr) {
        var receiver = UTF8ToString(receiverPtr);
        function reply(error) {
            SendMessage(receiver, 'OnWebFileSystemSynced', error ? String(error) : '');
        }
        try {
            FS.syncfs(false, reply);
        } catch (error) {
            reply(error);
        }
    }
});
