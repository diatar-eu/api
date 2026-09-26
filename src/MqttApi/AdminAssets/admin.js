(function () {
    'use strict';

    const I18N = window.__I18N__ || {};

    function t(key) {
        const value = I18N[key] || key;
        const args = Array.prototype.slice.call(arguments, 1);
        if (args.length === 0) return value;
        return value.replace(/\{(\d+)\}/g, function (match, index) {
            const replacement = args[Number(index)];
            return replacement === undefined || replacement === null ? match : replacement;
        });
    }

    function el(tag, props) {
        const node = document.createElement(tag);
        if (props) {
            Object.keys(props).forEach(function (key) {
                if (key === 'text') node.textContent = props[key];
                else if (key === 'class') node.className = props[key];
                else node[key] = props[key];
            });
        }
        for (let i = 2; i < arguments.length; i++) {
            if (arguments[i]) node.appendChild(arguments[i]);
        }
        return node;
    }

    function applyTranslations() {
        document.title = t('admin_title');
        document.querySelectorAll('[data-i18n]').forEach(function (node) {
            node.textContent = t(node.dataset.i18n);
        });
        document.querySelectorAll('[data-i18n-placeholder]').forEach(function (node) {
            node.placeholder = t(node.dataset.i18nPlaceholder);
        });
    }

    function toast(message, kind) {
        const box = document.getElementById('toasts');
        if (!box) return;
        const node = el('div', { className: 'toast ' + (kind || ''), text: message });
        box.appendChild(node);
        setTimeout(function () { node.remove(); }, 4000);
    }

    // ------------------------------------------------------------------ api

    async function api(method, url, body, options) {
        const settings = options || {};
        const request = { method: method, credentials: 'same-origin' };
        if (body !== undefined) {
            request.headers = { 'Content-Type': 'application/json' };
            request.body = JSON.stringify(body);
        }

        const response = await fetch(url, request);
        const data = await readJson(response);

        if (response.status === 401) {
            // A login képernyőn maradunk, ott a hiba a formban jelenik meg.
            if (!settings.stayOnFailure) window.location.href = '/admin';
            const unauthorized = new Error((data && data.message) || t('admin_login_failed'));
            unauthorized.status = 401;
            throw unauthorized;
        }

        if (!response.ok) {
            const error = new Error((data && data.message) || t('admin_login_failed'));
            error.status = response.status;
            throw error;
        }
        return data;
    }

    async function readJson(response) {
        try {
            return await response.json();
        } catch {
            return null;
        }
    }

    // ---------------------------------------------------------------- login

    const loginForm = document.getElementById('loginForm');
    if (loginForm) {
        loginForm.addEventListener('submit', async function (event) {
            event.preventDefault();
            const errorBox = document.getElementById('loginError');
            const button = loginForm.querySelector('button[type=submit]');
            errorBox.hidden = true;
            button.disabled = true;

            try {
                await api('POST', '/admin/login', {
                    username: loginForm.username.value.trim(),
                    password: loginForm.password.value
                }, { stayOnFailure: true });
                window.location.href = '/admin';
            } catch (error) {
                errorBox.textContent = error.message;
                errorBox.hidden = false;
                button.disabled = false;
            }
        });
        applyTranslations();
        return;
    }

    // ---------------------------------------------------------------- modal

    const modal = document.getElementById('modal');
    const modalForm = document.getElementById('modalForm');
    const modalFields = document.getElementById('modalFields');
    const modalTitle = document.getElementById('modalTitle');
    const modalError = document.getElementById('modalError');
    const modalSubmit = document.getElementById('modalSubmit');
    let modalHandler = null;

    function closeModal() {
        modal.hidden = true;
        modalFields.textContent = '';
        modalError.hidden = true;
        modalHandler = null;
    }

    function openModal(options) {
        modalTitle.textContent = options.title;
        modalSubmit.textContent = options.submitLabel || t('admin_save');
        modalFields.textContent = '';
        modalError.hidden = true;
        modalHandler = options.onSubmit;

        if (options.message) {
            modalFields.appendChild(el('p', { className: 'muted', text: options.message }));
        }

        (options.fields || []).forEach(function (field) {
            if (field.type === 'checkbox') {
                const input = el('input', { type: 'checkbox', name: field.name });
                input.checked = !!field.value;
                modalFields.appendChild(el('div', { className: 'field checkbox' },
                    input, el('label', { text: field.label })));
                return;
            }

            const input = el('input', {
                type: field.type || 'text',
                name: field.name,
                autocomplete: 'off',
                spellcheck: false,
                required: field.required !== false,
                value: field.value || ''
            });
            modalFields.appendChild(el('div', { className: 'field' },
                el('label', { text: field.label }), input));
        });

        modal.hidden = false;
        const first = modalFields.querySelector('input:not([type=checkbox])');
        if (first) first.focus();
    }

    document.getElementById('modalCancel').addEventListener('click', closeModal);
    modal.addEventListener('click', function (event) {
        if (event.target === modal) closeModal();
    });
    document.addEventListener('keydown', function (event) {
        if (event.key === 'Escape' && !modal.hidden) closeModal();
    });

    modalForm.addEventListener('submit', async function (event) {
        event.preventDefault();
        if (!modalHandler) return;

        modalError.hidden = true;
        modalSubmit.disabled = true;

        const values = {};
        Array.prototype.forEach.call(modalFields.querySelectorAll('input'), function (input) {
            values[input.name] = input.type === 'checkbox' ? input.checked : input.value;
        });

        try {
            await modalHandler(values);
            closeModal();
        } catch (error) {
            modalError.textContent = error.message;
            modalError.hidden = false;
        } finally {
            modalSubmit.disabled = false;
        }
    });

    // ---------------------------------------------------------------- state

    const state = { users: [], query: '', status: 'all' };

    const searchInput = document.getElementById('searchInput');
    const statusFilter = document.getElementById('statusFilter');
    const userRows = document.getElementById('userRows');
    const userTable = document.getElementById('userTable');
    const emptyState = document.getElementById('emptyState');
    const statusLine = document.getElementById('status');
    const currentUser = document.getElementById('currentUser');

    function visibleUsers() {
        const query = state.query.toLowerCase();
        return state.users.filter(function (user) {
            if (state.status !== 'all' && user.status !== state.status) return false;
            if (!query) return true;
            return (user.username || '').toLowerCase().indexOf(query) >= 0
                || (user.email || '').toLowerCase().indexOf(query) >= 0;
        });
    }

    function badge(label, kind) {
        return el('span', { className: 'badge ' + kind, text: label });
    }

    function actionButton(label, handler, danger) {
        const button = el('button', { type: 'button', text: label });
        if (danger) button.className = 'danger';
        button.addEventListener('click', handler);
        return button;
    }

    function userRequest(user, method, path) {
        return api(method, '/admin/api/users/' + encodeURIComponent(user.username) + path);
    }

    async function runAndRefresh(user, method, path) {
        try {
            const result = await userRequest(user, method, path);
            toast(result && result.message ? result.message : t('admin_save'), 'ok');
            await loadUsers();
        } catch (error) {
            if (error.status !== 401) toast(error.message, 'error');
        }
    }

    function confirmAction(label, question, perform) {
        openModal({
            title: label,
            submitLabel: label,
            message: question,
            onSubmit: async function () {
                const result = await perform();
                toast(result && result.message ? result.message : label, 'ok');
                await loadUsers();
            }
        });
    }

    // ---------------------------------------------------------------- render

    const statusLabel = function (status) {
        return {
            verified: t('admin_filter_verified'),
            pending: t('admin_filter_pending'),
            disabled: t('admin_filter_disabled')
        }[status] || status;
    };

    function render() {
        const users = visibleUsers();
        userRows.textContent = '';

        users.forEach(function (user) {
            const suspended = user.status === 'disabled';

            const roles = el('div', { className: 'role-list' });
            if (user.isDiatarUser) {
                (user.roles || []).forEach(function (role) { roles.appendChild(badge(role, 'other')); });
            } else {
                roles.appendChild(badge(t('admin_mqtt_client'), 'other'));
            }

            const actions = el('div', { className: 'actions' });

            if (user.status !== 'verified') {
                actions.appendChild(actionButton(t('admin_action_verify'),
                    function () { runAndRefresh(user, 'POST', '/verify'); }));
            }
            if (!suspended) {
                actions.appendChild(actionButton(t('admin_action_enable'),
                    function () { runAndRefresh(user, 'POST', '/enable'); }));
            }
            if (user.status === 'pending' && user.email) {
                actions.appendChild(actionButton(t('admin_action_resend'),
                    function () { runAndRefresh(user, 'POST', '/resend-verification'); }));
            }

            actions.appendChild(actionButton(t('admin_action_password'), function () { promptPassword(user); }));
            actions.appendChild(actionButton(t('admin_action_email'), function () { promptEmail(user); }));

            if (!suspended) {
                actions.appendChild(actionButton(t('admin_action_disable'), function () {
                    confirmAction(t('admin_action_disable'), t('admin_confirm_disable', user.username),
                        function () { return userRequest(user, 'POST', '/disable'); });
                }, true));
            }

            actions.appendChild(actionButton(t('admin_action_delete'), function () {
                confirmAction(t('admin_action_delete'), t('admin_confirm_delete', user.username),
                    function () { return userRequest(user, 'DELETE', ''); });
            }, true));

            userRows.appendChild(el('tr', null,
                el('td', { className: 'break', text: user.username }),
                el('td', { className: 'break', text: user.email || '-' }),
                el('td', null, badge(statusLabel(user.status), user.status)),
                el('td', null, roles),
                el('td', { className: 'right' }, actions)));
        });

        userTable.hidden = users.length === 0;
        emptyState.hidden = users.length > 0;
    }

    // ---------------------------------------------------------------- prompts

    function promptPassword(user) {
        openModal({
            title: t('admin_action_password') + ' - ' + user.username,
            fields: [{ name: 'newPassword', type: 'password', label: t('admin_new_password') }],
            onSubmit: async function (values) {
                const result = await api('POST', '/admin/api/users/' + encodeURIComponent(user.username) + '/password',
                    { newPassword: values.newPassword });
                toast(result && result.message ? result.message : t('admin_password_saved'), 'ok');
                await loadUsers();
            }
        });
    }

    function promptEmail(user) {
        openModal({
            title: t('admin_action_email') + ' - ' + user.username,
            fields: [{ name: 'newEmail', type: 'email', label: t('admin_new_email'), value: user.email || '' }],
            onSubmit: async function (values) {
                const result = await api('POST', '/admin/api/users/' + encodeURIComponent(user.username) + '/email',
                    { newEmail: values.newEmail });
                toast(result && result.message ? result.message : t('admin_email_saved'), 'ok');
                await loadUsers();
            }
        });
    }

    function promptNewUser() {
        openModal({
            title: t('admin_new_user'),
            fields: [
                { name: 'username', label: t('admin_column_user') },
                { name: 'email', type: 'email', label: t('admin_column_email') },
                { name: 'password', type: 'password', label: t('admin_password') },
                { name: 'skipVerification', type: 'checkbox', label: t('admin_skip_verification') }
            ],
            onSubmit: async function (values) {
                const result = await api('POST', '/admin/api/users', values);
                toast(result && result.message ? result.message : t('user_created'), 'ok');
                await loadUsers();
            }
        });
    }

    // ---------------------------------------------------------------- boot

    async function loadUsers() {
        statusLine.textContent = t('admin_loading');
        try {
            const result = await api('GET', '/admin/api/users');
            state.users = (result && result.data) || [];
            statusLine.textContent = '';
            render();
        } catch (error) {
            if (error.status === 401) return;
            statusLine.textContent = '';
            toast(error.message, 'error');
        }
    }

    document.getElementById('refreshButton').addEventListener('click', loadUsers);
    document.getElementById('newUserButton').addEventListener('click', promptNewUser);
    document.getElementById('logoutButton').addEventListener('click', async function () {
        try {
            await api('POST', '/admin/logout');
        } catch {
            // the cookie is gone either way, so go back to the login page
        }
        window.location.href = '/admin';
    });

    searchInput.addEventListener('input', function () {
        state.query = searchInput.value.trim();
        render();
    });

    statusFilter.addEventListener('change', function () {
        state.status = statusFilter.value;
        render();
    });

    applyTranslations();
    currentUser.textContent = I18N.__adminUser || '';
    loadUsers();
})();
