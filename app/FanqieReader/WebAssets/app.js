/* global window, document */
(() => {
  'use strict';

  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  const clamp = (n, min, max) => Math.min(max, Math.max(min, n));
  const webview = window.chrome && window.chrome.webview ? window.chrome.webview : null;
  const previewMode = new URLSearchParams(location.search).has('preview');
  const isLive = !!webview;

  const state = {
    settings: {
      theme: 'day',
      fontSize: 19,
      opacity: 100,
      edgeHide: false,
      immersive: false
    },
    user: { loggedIn: false, name: '', id: '' },
    recent: null,
    chapter: null,
    catalog: null,
    catalogCache: new Map(),
    view: 'welcome',
    pendingAction: null,
    readerLoading: false,
    catalogLoading: false,
    catalogRequested: false,
    loginTab: 'cookie',
    drawerTimer: 0,
    progressTimer: 0,
    lastProgressSent: 0,
    glass: false
  };

  const els = {
    body: document.body,
    menuBtn: $('#menuBtn'),
    minBtn: $('#minBtn'),
    closeBtn: $('#closeBtn'),
    dragZone: $('#dragZone'),
    titlebarBook: $('#titlebarBook'),
    progressBar: $('#progressLine i'),
    welcomeView: $('#welcomeView'),
    shelfView: $('#shelfView'),
    readerView: $('#readerView'),
    readerScroll: $('#readerScroll'),
    readerLoading: $('#readerLoading'),
    readerLoadingText: $('#readerLoadingText'),
    bookTitle: $('#bookTitle'),
    chapterTitle: $('#chapterTitle'),
    chapterMeta: $('#chapterMeta'),
    chapterBody: $('#chapterBody'),
    previewNotice: $('#previewNotice'),
    prevBtn: $('#prevBtn'),
    nextBtn: $('#nextBtn'),
    tocBtn: $('#tocBtn'),
    menuPanel: $('#menuPanel'),
    accountStrip: $('#accountStrip'),
    accountAvatar: $('#accountAvatar'),
    accountName: $('#accountName'),
    accountHint: $('#accountHint'),
    accountBtn: $('#accountBtn'),
    menuShelfBtn: $('#menuShelfBtn'),
    fontSizeValue: $('#fontSizeValue'),
    opacityValue: $('#opacityValue'),
    themeSeg: $('#themeSeg'),
    edgeToggle: $('#edgeToggle'),
    immersiveToggle: $('#immersiveToggle'),
    loginStateText: $('#loginStateText'),
    logoutBtn: $('#logoutBtn'),
    welcomeShelfBtn: $('#welcomeShelfBtn'),
    welcomeInputBtn: $('#welcomeInputBtn'),
    shelfBackBtn: $('#shelfBackBtn'),
    shelfRefreshBtn: $('#shelfRefreshBtn'),
    shelfLoginBtn: $('#shelfLoginBtn'),
    shelfCount: $('#shelfCount'),
    shelfList: $('#shelfList'),
    shelfEmpty: $('#shelfEmpty'),
    shelfSkeleton: $('#shelfSkeleton'),
    continueCard: $('#continueCard'),
    continueBook: $('#continueBook'),
    continueChapter: $('#continueChapter'),
    continueBtn: $('#continueBtn'),
    loginOverlay: $('#loginOverlay'),
    loginCloseBtn: $('#loginCloseBtn'),
    cookiePane: $('#cookiePane'),
    qrPane: $('#qrPane'),
    cookieInput: $('#cookieInput'),
    cookieLoginBtn: $('#cookieLoginBtn'),
    qrLoginBtn: $('#qrLoginBtn'),
    openQrBtn: $('#openQrBtn'),
    pasteCookieBtn: $('#pasteCookieBtn'),
    loginMessage: $('#loginMessage'),
    qrVisual: $('#qrVisual'),
    qrImage: $('#qrImage'),
    qrPlaceholder: $('#qrPlaceholder'),
    qrStatus: $('#qrStatus'),
    qrTitle: $('#qrTitle'),
    tabInk: $('#tabInk'),
    inputOverlay: $('#inputOverlay'),
    inputCloseBtn: $('#inputCloseBtn'),
    openIdBtn: $('#openIdBtn'),
    idInput: $('#idInput'),
    catalogDrawer: $('#catalogDrawer'),
    drawerScrim: $('#drawerScrim'),
    catalogCloseBtn: $('#catalogCloseBtn'),
    catalogSearch: $('#catalogSearch'),
    catalogList: $('#catalogList'),
    catalogCount: $('#catalogCount'),
    catalogEmpty: $('#catalogEmpty'),
    toastStack: $('#toastStack'),
    immersiveExit: $('#immersiveExit')
  };

  // ------------------------------------------------------------
  // messaging
  // ------------------------------------------------------------

  function post(type, payload = {}) {
    if (webview) {
      webview.postMessage({ type, ...payload });
    } else if (previewMode) {
      mockPost(type, payload);
    }
  }

  function handleMessage(event) {
    const msg = event.data;
    if (!msg || !msg.type) return;
    const data = msg.data || {};
    switch (msg.type) {
      case 'init':
        applyInit(data);
        break;
      case 'settings':
        applySettings(data.settings || data);
        break;
      case 'user':
        updateUser(data.user || data);
        break;
      case 'shelf':
        renderShelf(data.books || [], data.error || '');
        break;
      case 'chapter':
        renderChapter(data.chapter, data.restoreRatio || 0);
        break;
      case 'catalog':
        onCatalog(data.catalog);
        break;
      case 'loginResult':
        onLoginResult(data);
        break;
      case 'toast':
        toast(data.message || '', data.kind || 'info', data.timeout);
        break;
      case 'clipboard':
        if (typeof data.text === 'string') {
          els.cookieInput.value = data.text;
          els.cookieInput.focus();
          els.loginMessage.textContent = '已粘贴剪贴板内容';
          els.loginMessage.className = 'modal-footnote is-success';
        }
        break;
      case 'opacity':
        applyOpacityMessage(data);
        break;
      case 'qrLoading':
        setQrLoading();
        break;
      case 'qr':
        showQr(data.dataUrl, data.expiresAt);
        break;
      case 'qrStatus':
        showQrStatus(data.status, data.message);
        break;
      case 'immersive':
        setImmersiveState(!!data.enabled, true);
        break;
      case 'edgeHide':
        setEdgeState(!!data.enabled, true);
        break;
      case 'error':
        handleError(data.message || '出现了一个问题');
        break;
      default:
        break;
    }
  }

  if (webview) {
    webview.addEventListener('message', handleMessage);
  }

  // ------------------------------------------------------------
  // state / settings
  // ------------------------------------------------------------

  function applyInit(data) {
    state.user = data.user || state.user;
    state.recent = data.recent || null;
    setGlassAvailable(!!data.glass);
    if (data.settings) applySettings(data.settings);
    updateUser(state.user);
    updateRecent();
    if (data.startChapterId) {
      openChapter(data.startChapterId);
    }
    if (previewMode && !data.settings) {
      // handled by preview bootstrap
    }
  }

  function applySettings(settings) {
    if (!settings) return;
    state.settings = { ...state.settings, ...settings };
    state.settings.fontSize = clamp(Number(state.settings.fontSize) || 19, 12, 32);
    state.settings.opacity = clamp(Number(state.settings.opacity) || 100, 30, 100);
    state.settings.theme = state.settings.theme === 'night' ? 'night' : 'day';
    setThemeState(state.settings.theme);
    setFontState(state.settings.fontSize);
    setOpacityState(state.settings.opacity);
    setEdgeState(!!state.settings.edgeHide, true);
    setImmersiveState(!!state.settings.immersive, true);
  }

  /**
   * The Windows 11 host paints a real acrylic backdrop behind a transparent
   * WebView2, so the page can hand the desktop through instead of faking a
   * screenshot. Without that backdrop the reader stays a solid sheet.
   */
  function setGlassAvailable(available) {
    state.glass = !!available;
    els.body.classList.toggle('is-glass', state.glass);
    document.documentElement.dataset.glass = state.glass ? 'on' : 'off';
    const row = $('[data-stepper="opacity"]');
    if (row) row.classList.toggle('is-disabled', !state.glass);
    if (!state.glass) {
      const note = $('[data-opacity-note]');
      if (note) note.textContent = '系统不支持真透明';
    }
  }

  function applyOpacityMessage(data) {
    if (typeof data.glass === 'boolean') setGlassAvailable(data.glass);
    if (typeof data.value === 'number') setOpacityState(data.value);
  }

  function setThemeState(theme) {
    document.documentElement.dataset.theme = theme;
    state.settings.theme = theme;
    $$('#themeSeg button').forEach(btn => btn.classList.toggle('is-active', btn.dataset.theme === theme));
  }

  function setFontState(value) {
    state.settings.fontSize = clamp(Math.round(value), 12, 32);
    document.documentElement.style.setProperty('--reader-size', `${state.settings.fontSize}px`);
    els.fontSizeValue.textContent = `${state.settings.fontSize} px`;
    $$('[data-stepper="font"] button').forEach(btn => {
      const dir = Number(btn.dataset.step);
      btn.disabled = (dir < 0 && state.settings.fontSize <= 12) || (dir > 0 && state.settings.fontSize >= 32);
    });
  }

  function setOpacityState(value) {
    state.settings.opacity = clamp(Math.round(value / 5) * 5, 30, 100);
    els.opacityValue.textContent = `${state.settings.opacity}%`;
    $$('[data-stepper="opacity"] button').forEach(btn => {
      const dir = Number(btn.dataset.step);
      btn.disabled = !state.glass
        || (dir < 0 && state.settings.opacity <= 30)
        || (dir > 0 && state.settings.opacity >= 100);
    });

    // The desktop shows through the window itself; only the reader's own surfaces
    // carry alpha, with a floor so the text never fights with the wallpaper.
    const alpha = state.glass ? state.settings.opacity / 100 : 1;
    // Floors only exist so text stays legible on a busy wallpaper; they are low so
    // the slider actually changes what you see.
    const chrome = Math.max(alpha, 0.06);
    const paper = Math.max(alpha, 0.34);
    const surface = Math.max(alpha, 0.58);
    const root = document.documentElement.style;
    root.setProperty('--window-alpha', String(alpha));
    root.setProperty('--chrome-alpha', String(chrome));
    root.setProperty('--paper-alpha', String(paper));
    root.setProperty('--surface-alpha', String(surface));
    document.documentElement.classList.toggle('is-opaque', alpha >= 1);
  }

  function setEdgeState(enabled, silent) {
    state.settings.edgeHide = !!enabled;
    els.edgeToggle.setAttribute('aria-checked', String(!!enabled));
    els.body.classList.toggle('edge-enabled', !!enabled);
    if (!silent) post('setEdgeHide', { enabled: !!enabled });
  }

  function setImmersiveState(enabled, silent) {
    state.settings.immersive = !!enabled;
    els.body.classList.toggle('immersive', !!enabled);
    els.immersiveToggle.setAttribute('aria-checked', String(!!enabled));
    if (enabled) {
      closeMenu();
      hideDrawer();
      hideLogin();
      hideInput();
      showImmersiveHint();
    } else {
      els.immersiveExit.classList.remove('show');
    }
    if (!silent) post('setImmersive', { enabled: !!enabled });
  }

  function showImmersiveHint() {
    els.immersiveExit.classList.add('show');
    clearTimeout(showImmersiveHint.timer);
    showImmersiveHint.timer = setTimeout(() => els.immersiveExit.classList.remove('show'), 2600);
  }

  function updateUser(user) {
    state.user = user || { loggedIn: false, name: '', id: '' };
    const logged = !!state.user.loggedIn;
    els.accountName.textContent = logged ? state.user.name || '番茄读者' : '未登录';
    els.accountHint.textContent = logged ? '已登录 · 书架可同步' : '登录后同步书架';
    els.accountAvatar.textContent = logged ? (state.user.name || '番').trim().slice(0, 1) : '番';
    els.accountBtn.textContent = logged ? '账号' : '登录';
    els.loginStateText.textContent = logged ? `已登录 · ${state.user.name || ''}`.trim() : '未登录';
    els.logoutBtn.hidden = !logged;
    els.accountStrip.classList.toggle('is-logged-in', logged);
  }

  function updateRecent() {
    if (state.recent && state.recent.chapterId) {
      els.continueCard.hidden = false;
      els.continueBook.textContent = state.recent.bookName || '继续阅读';
      els.continueChapter.textContent = state.recent.chapterTitle || '上次读到的章节';
      els.continueBtn.dataset.chapterId = state.recent.chapterId;
    } else {
      els.continueCard.hidden = true;
    }
  }

  // ------------------------------------------------------------
  // views / reader
  // ------------------------------------------------------------

  function showView(view) {
    state.view = view;
    els.welcomeView.hidden = view !== 'welcome';
    els.shelfView.hidden = view !== 'shelf';
    els.readerView.hidden = view !== 'reader';
    els.body.classList.toggle('has-book', view === 'reader' && !!state.chapter);
  }

  function showReaderLoading(text) {
    state.readerLoading = true;
    els.readerLoading.hidden = false;
    els.readerLoadingText.textContent = text || '正在取回正文…';
    showView('reader');
  }

  function hideReaderLoading() {
    state.readerLoading = false;
    els.readerLoading.hidden = true;
  }

  function openBook(bookId, chapterId, order) {
    if (!bookId && !chapterId) return;
    state.pendingAction = null;
    closeMenu();
    hideLogin();
    hideDrawer();
    showReaderLoading('正在取回正文…');
    post('openBook', {
      bookId: bookId || '',
      chapterId: chapterId || '',
      order: Number(order) > 0 ? Number(order) : 0
    });
  }

  function openChapter(chapterId) {
    if (!chapterId) return;
    closeMenu();
    hideDrawer();
    showReaderLoading('正在翻页…');
    post('openChapter', { chapterId: String(chapterId) });
  }

  function renderChapter(chapter, restoreRatio) {
    if (!chapter) return;
    hideReaderLoading();
    state.chapter = chapter;
    const isNewChapter = !state._lastChapterId || state._lastChapterId !== chapter.chapterId;
    state._lastChapterId = chapter.chapterId;

    els.bookTitle.textContent = chapter.bookName || '';
    els.chapterTitle.textContent = chapter.title || '';
    els.titlebarBook.textContent = [chapter.bookName, chapter.title].filter(Boolean).join(' · ');
    document.title = `${chapter.title || '阅读'} · ${chapter.bookName || '番茄阅读'}`;

    const meta = [];
    if (chapter.author) meta.push(chapter.author);
    if (chapter.wordCount) meta.push(`本章 ${chapter.wordCount} 字`);
    meta.push('番茄小说');
    els.chapterMeta.textContent = '';
    meta.forEach((item, index) => {
      if (index) {
        const sep = document.createElement('span');
        sep.className = 'sep';
        els.chapterMeta.appendChild(sep);
      }
      const span = document.createElement('span');
      span.textContent = item;
      els.chapterMeta.appendChild(span);
    });

    els.chapterBody.textContent = '';
    (chapter.paragraphs || []).forEach(text => {
      const p = document.createElement('p');
      p.textContent = text;
      els.chapterBody.appendChild(p);
    });

    applyChapterFont(chapter);
    els.previewNotice.hidden = !chapter.isPreview;

    els.prevBtn.disabled = !chapter.prevId;
    els.nextBtn.disabled = !chapter.nextId;
    els.prevBtn.dataset.chapterId = chapter.prevId || '';
    els.nextBtn.dataset.chapterId = chapter.nextId || '';

    showView('reader');
    setProgress(0);
    requestAnimationFrame(() => {
      const max = els.readerScroll.scrollHeight - els.readerScroll.clientHeight;
      const target = restoreRatio > 0 ? Math.round(max * clamp(restoreRatio, 0, 1)) : 0;
      els.readerScroll.scrollTop = target;
      updateProgress();
    });

    if (chapter.bookId && !state.catalogCache.has(chapter.bookId)) {
      // Fetch quietly so opening the drawer is instant.
      post('getCatalog', { bookId: chapter.bookId, background: true });
    }
  }

  const BODY_FONT_STACKS = {
    sans: '"Source Han Sans SC", "Noto Sans CJK SC", "Source Han Sans CN", "Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC", "Segoe UI", sans-serif',
    serif: '"Source Han Serif SC", "Noto Serif CJK SC", "Source Han Serif CN", "Songti SC", "STSong", "SimSun", Georgia, serif'
  };

  /**
   * The chapter font is a subset that only carries the masked Private-Use glyphs;
   * the remaining characters come from the fallback stack. Both have to be the same
   * design, otherwise every line mixes two typefaces (the "random bold" bug).
   */
  function applyChapterFont(chapter) {
    const root = document.documentElement;
    const kind = chapter.fontKind === 'serif' ? 'serif' : 'sans';
    root.style.setProperty('--reading-body-font', BODY_FONT_STACKS[kind]);
    root.dataset.fontKind = kind;

    const dataUrl = typeof chapter.fontDataUrl === 'string' ? chapter.fontDataUrl : '';
    const family = sanitizeFontFamily(chapter.fontFamily);
    if (!dataUrl.startsWith('data:font/') || !family) {
      removeDynamicFont();
      return;
    }

    const format = dataUrl.startsWith('data:font/woff2') ? 'woff2'
      : dataUrl.startsWith('data:font/woff') ? 'woff' : 'opentype';

    let style = $('#dynamicFontStyle');
    if (!style) {
      style = document.createElement('style');
      style.id = 'dynamicFontStyle';
      document.head.appendChild(style);
    }
    style.textContent = '@font-face{font-family:"' + family + '";src:url("' + dataUrl + '") format("' + format + '");'
      + 'font-weight:400;font-style:normal;font-display:block;}';
    root.style.setProperty('--chapter-font', '"' + family + '"');
    els.chapterBody.classList.add('has-custom-font');
  }

  function removeDynamicFont() {
    els.chapterBody.classList.remove('has-custom-font');
    document.documentElement.style.removeProperty('--chapter-font');
    const old = $('#dynamicFontStyle');
    if (old) old.remove();
  }

  function sanitizeFontFamily(value) {
    if (typeof value !== 'string') return '';
    const cleaned = value.trim().replace(/["'\\\s]/g, '');
    return /^[A-Za-z0-9_-]{1,64}$/.test(cleaned) ? cleaned : '';
  }

  function updateProgress() {
    const scroller = els.readerScroll;
    const max = Math.max(1, scroller.scrollHeight - scroller.clientHeight);
    const ratio = clamp(scroller.scrollTop / max, 0, 1);
    els.progressBar.style.width = `${Math.round(ratio * 1000) / 10}%`;
    return ratio;
  }

  function setProgress(ratio) {
    els.progressBar.style.width = `${Math.round(clamp(ratio, 0, 1) * 1000) / 10}%`;
  }

  // ------------------------------------------------------------
  // catalog
  // ------------------------------------------------------------

  function onCatalog(catalog) {
    state.catalogLoading = false;
    if (!catalog) return;
    state.catalog = catalog;
    if (catalog.bookId) state.catalogCache.set(catalog.bookId, catalog);
    renderCatalog(catalog);
    if (state.catalogRequested && state.chapter && catalog.bookId === state.chapter.bookId) {
      state.catalogRequested = false;
      openDrawer();
    }
  }

  function renderCatalog(catalog) {
    els.catalogList.textContent = '';
    const chapters = catalog.chapters || [];
    els.catalogCount.textContent = chapters.length ? `共 ${chapters.length} 章` : '';
    els.catalogEmpty.hidden = chapters.length > 0;
    const currentId = state.chapter ? state.chapter.chapterId : '';
    chapters.forEach((chapter, index) => {
      const btn = document.createElement('button');
      btn.className = 'catalog-item' + (chapter.id === currentId ? ' is-current' : '');
      btn.dataset.chapterId = chapter.id;
      btn.dataset.title = chapter.title || '';
      const idx = document.createElement('span');
      idx.className = 'idx';
      idx.textContent = String(index + 1).padStart(2, '0');
      const label = document.createElement('span');
      label.className = 'label';
      label.textContent = chapter.title || `第 ${index + 1} 章`;
      btn.append(idx, label);
      btn.addEventListener('click', () => {
        hideDrawer();
        openChapter(chapter.id);
      });
      els.catalogList.appendChild(btn);
    });
    filterCatalog();
  }

  function filterCatalog() {
    const q = els.catalogSearch.value.trim();
    const items = $$('.catalog-item', els.catalogList);
    items.forEach(item => {
      item.hidden = !!q && !(item.dataset.title || '').includes(q);
    });
  }

  function requestCatalog() {
    if (!state.chapter) {
      toast('先打开一章正文吧', 'info');
      return;
    }
    const cached = state.catalogCache.get(state.chapter.bookId);
    if (cached) {
      state.catalog = cached;
      renderCatalog(cached);
      openDrawer();
      return;
    }
    if (state.catalogLoading) return;
    state.catalogLoading = true;
    state.catalogRequested = true;
    toast('正在读取目录…');
    post('getCatalog', { bookId: state.chapter.bookId });
    setTimeout(() => {
      if (state.catalogLoading) {
        state.catalogLoading = false;
        toast('目录读取超时，可稍后重试', 'error');
      }
    }, 12000);
  }

  function openDrawer() {
    clearTimeout(state.drawerTimer);
    els.catalogDrawer.hidden = false;
    els.drawerScrim.hidden = false;
    els.catalogDrawer.classList.remove('is-open');
    requestAnimationFrame(() => {
      els.catalogDrawer.classList.add('is-open');
      const current = $('.catalog-item.is-current', els.catalogList);
      if (current) current.scrollIntoView({ block: 'center' });
    });
  }

  function hideDrawer() {
    if (els.catalogDrawer.hidden) return;
    els.catalogDrawer.classList.remove('is-open');
    els.drawerScrim.hidden = true;
    clearTimeout(state.drawerTimer);
    state.drawerTimer = setTimeout(() => { els.catalogDrawer.hidden = true; }, 280);
  }

  // ------------------------------------------------------------
  // shelf
  // ------------------------------------------------------------

  function loadShelf() {
    if (!state.user.loggedIn) {
      state.pendingAction = 'shelf';
      openLogin('cookie');
      toast('登录后就能读取你的书架', 'info');
      return;
    }
    state.pendingAction = null;
    closeMenu();
    showView('shelf');
    els.shelfList.textContent = '';
    els.shelfEmpty.hidden = true;
    els.shelfSkeleton.hidden = false;
    els.shelfCount.textContent = '';
    els.shelfRefreshBtn.disabled = true;
    post('getShelf');
    setTimeout(() => {
      if (!els.shelfSkeleton.hidden) {
        els.shelfSkeleton.hidden = true;
        els.shelfRefreshBtn.disabled = false;
        els.shelfEmpty.hidden = false;
        els.shelfEmpty.querySelector('h3').textContent = '书架读取超时';
        els.shelfEmpty.querySelector('p').textContent = '网络有点慢，或者番茄的书架接口正在调整。点击刷新再试一次。';
      }
    }, 15000);
  }

  function renderShelf(books, error) {
    els.shelfSkeleton.hidden = true;
    els.shelfRefreshBtn.disabled = false;
    els.shelfList.textContent = '';
    els.shelfCount.textContent = books.length ? `${books.length} 本` : '';

    if (!books.length) {
      els.shelfEmpty.hidden = false;
      if (error) {
        els.shelfEmpty.querySelector('h3').textContent = '书架读取失败';
        els.shelfEmpty.querySelector('p').textContent = String(error);
        els.shelfLoginBtn.textContent = '重新登录';
      } else {
        els.shelfEmpty.querySelector('h3').textContent = '书架还是空的';
        els.shelfEmpty.querySelector('p').textContent = '在番茄小说网页中加入书架，或登录后同步账号里的书。';
        els.shelfLoginBtn.textContent = '登录同步';
      }
      return;
    }

    els.shelfEmpty.hidden = true;
    books.forEach((book, index) => {
      const row = document.createElement('div');
      row.className = 'book-row';
      row.style.setProperty('--i', String(Math.min(index, 18)));
      row.tabIndex = 0;
      row.setAttribute('role', 'button');
      row.setAttribute('aria-label', `阅读 ${book.title}`);

      const num = document.createElement('span');
      num.className = 'book-index';
      num.textContent = String(index + 1).padStart(2, '0');

      const main = document.createElement('div');
      main.className = 'book-main';
      const title = document.createElement('div');
      title.className = 'book-name';
      title.textContent = book.title || '未命名书籍';
      const sub = document.createElement('div');
      sub.className = 'book-sub';
      const author = document.createElement('span');
      author.className = 'book-author';
      author.textContent = book.author || '作者未知';
      sub.appendChild(author);
      if (book.statusKnown !== false && book.statusText) {
        const sep = document.createElement('span');
        sep.className = 'sep';
        sub.appendChild(sep);
        const badge = document.createElement('span');
        badge.className = 'book-badge' + (book.isFinished ? ' book-badge--finish' : '');
        badge.textContent = book.isFinished ? '完结' : book.statusText;
        sub.appendChild(badge);
      }
      if (book.readText) {
        const sep2 = document.createElement('span');
        sep2.className = 'sep';
        sub.appendChild(sep2);
        const read = document.createElement('span');
        read.className = 'book-read';
        read.textContent = book.readText;
        sub.appendChild(read);
      }
      main.append(title, sub);

      const open = document.createElement('span');
      open.className = 'book-open';
      open.innerHTML = '<svg viewBox="0 0 20 20"><path d="M4 10h11m-4-4 4 4-4 4" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/></svg>';

      row.append(num, main, open);
      const go = () => openBook(book.bookId, book.chapterId || '', book.startOrder || 0);
      row.addEventListener('click', go);
      row.addEventListener('keydown', event => {
        if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); go(); }
      });
      els.shelfList.appendChild(row);
    });
  }

  // ------------------------------------------------------------
  // login
  // ------------------------------------------------------------

  function openLogin(tab = 'cookie') {
    closeMenu();
    els.loginOverlay.hidden = false;
    switchLoginTab(tab);
    if (tab === 'cookie') setTimeout(() => els.cookieInput.focus(), 60);
    els.loginMessage.className = 'modal-footnote';
    els.loginMessage.textContent = 'Cookie 只用于读取你自己的书架与正文。';
  }

  function hideLogin() {
    if (!els.loginOverlay.hidden && state.loginTab === 'qr') post('closeQr');
    els.loginOverlay.hidden = true;
    els.cookieLoginBtn.disabled = false;
    els.cookieLoginBtn.textContent = '登录';
  }

  function setQrLoading() {
    els.qrVisual.classList.remove('has-image');
    els.qrImage.hidden = true;
    els.qrImage.removeAttribute('src');
    els.qrPlaceholder.hidden = false;
    els.qrPlaceholder.textContent = '正在获取二维码…';
    els.qrTitle.textContent = '正在连接番茄…';
    els.qrStatus.className = 'qr-status';
    els.qrStatus.textContent = '正在向官方接口请求二维码';
    els.openQrBtn.disabled = true;
    els.openQrBtn.textContent = '请稍候…';
  }

  function showQr(dataUrl, expiresAt) {
    if (!dataUrl) {
      setQrLoading();
      return;
    }
    els.qrImage.src = dataUrl;
    els.qrImage.hidden = false;
    els.qrVisual.classList.add('has-image');
    els.qrTitle.textContent = '使用抖音 App 扫码登录';
    els.qrStatus.className = 'qr-status';
    els.qrStatus.textContent = '等待扫码 · 二维码约 3 分钟内有效';
    els.openQrBtn.disabled = false;
    els.openQrBtn.textContent = '刷新二维码';
  }

  function showQrStatus(status, message) {
    if (!message) return;
    els.qrStatus.className = 'qr-status';
    els.qrStatus.textContent = message;
    if (status === 'scanned') {
      els.qrTitle.textContent = '请在手机上确认';
      els.qrStatus.classList.add('is-success');
    } else if (status === 'confirming') {
      els.qrTitle.textContent = '正在同步登录';
    }
  }

  function switchLoginTab(tab) {
    if (state.loginTab === 'qr' && tab !== 'qr') post('closeQr');
    state.loginTab = tab;
    $$('.tab').forEach(btn => {
      const active = btn.dataset.loginTab === tab;
      btn.classList.toggle('is-active', active);
      btn.setAttribute('aria-selected', String(active));
    });
    els.cookiePane.hidden = tab !== 'cookie';
    els.qrPane.hidden = tab !== 'qr';
    const active = $(`.tab[data-login-tab="${tab}"]`);
    if (active) {
      els.tabInk.style.width = `${active.offsetWidth}px`;
      els.tabInk.style.transform = `translateX(${active.offsetLeft}px)`;
    }
  }

  function onLoginResult(data) {
    if (data.success) {
      updateUser(data.user || { loggedIn: true });
      els.loginMessage.className = 'modal-footnote is-success';
      els.loginMessage.textContent = '登录成功';
      hideLogin();
      toast('登录成功，书架已准备好');
      if (state.pendingAction === 'shelf') {
        state.pendingAction = null;
        loadShelf();
      }
    } else {
      els.cookieLoginBtn.disabled = false;
      els.cookieLoginBtn.textContent = '登录';
      if (state.loginTab === 'qr') {
        els.openQrBtn.disabled = false;
        els.openQrBtn.textContent = '刷新二维码';
        els.qrStatus.className = 'qr-status is-error';
        els.qrStatus.textContent = data.message || '扫码登录未完成，请重试';
        els.qrTitle.textContent = '使用抖音 App 扫码登录';
      } else {
        els.loginMessage.className = 'modal-footnote is-error';
        els.loginMessage.textContent = data.message || '登录失败，请检查 Cookie 是否过期';
      }
    }
  }

  function submitCookieLogin() {
    const value = els.cookieInput.value.trim();
    if (!value) {
      els.loginMessage.className = 'modal-footnote is-error';
      els.loginMessage.textContent = '请先粘贴 Cookie，或使用扫码登录';
      return;
    }
    els.cookieLoginBtn.disabled = true;
    els.cookieLoginBtn.textContent = '验证中…';
    els.loginMessage.className = 'modal-footnote';
    els.loginMessage.textContent = '正在验证登录状态…';
    post('cookieLogin', { text: value });
  }

  function doLogout() {
    post('logout');
    updateUser({ loggedIn: false });
    if (state.view === 'shelf') showView('welcome');
    toast('已退出登录');
  }

  // ------------------------------------------------------------
  // UI helpers
  // ------------------------------------------------------------

  function toast(text, kind = 'info', timeout) {
    if (!text) return;
    const node = document.createElement('div');
    node.className = 'toast' + (kind === 'error' ? ' is-error' : '');
    node.textContent = text;
    els.toastStack.appendChild(node);
    while (els.toastStack.children.length > 3) els.toastStack.firstElementChild.remove();
    const life = Number(timeout) > 0 ? Math.min(Number(timeout), 12000) : 3000;
    setTimeout(() => {
      node.classList.add('is-hiding');
      setTimeout(() => node.remove(), 190);
    }, life);
  }

  function handleError(message) {
    hideReaderLoading();
    hideShelfLoading();
    toast(message, 'error');
    if (!state.chapter && state.view === 'reader') showView('welcome');
  }

  function hideShelfLoading() {
    els.shelfSkeleton.hidden = true;
    els.shelfRefreshBtn.disabled = false;
  }

  function openMenu() {
    els.menuPanel.hidden = false;
    els.menuBtn.setAttribute('aria-expanded', 'true');
  }

  function closeMenu() {
    els.menuPanel.hidden = true;
    els.menuBtn.setAttribute('aria-expanded', 'false');
  }

  function toggleMenu() {
    if (els.menuPanel.hidden) openMenu(); else closeMenu();
  }

  function hideInput() { els.inputOverlay.hidden = true; }

  function openInput() {
    closeMenu();
    els.inputOverlay.hidden = false;
    setTimeout(() => els.idInput.focus(), 60);
  }

  // ------------------------------------------------------------
  // events
  // ------------------------------------------------------------

  els.menuBtn.addEventListener('click', event => { event.stopPropagation(); toggleMenu(); });
  els.minBtn.addEventListener('click', () => post('minimize'));
  els.closeBtn.addEventListener('click', () => post('close'));
  // Dragging is driven from JS on purpose: letting the window enter the OS
  // "move/snap" modal loop also brings up the system snap preview outline, which
  // reads as a stray frame around the window when edge hiding is on.
  let dragActive = false;
  let dragPointerId = null;
  let dragFrame = 0;

  els.dragZone.addEventListener('pointerdown', event => {
    if (event.button !== 0 || event.target.closest('button')) return;
    dragActive = true;
    dragPointerId = event.pointerId;
    try { els.dragZone.setPointerCapture(event.pointerId); } catch { /* ignore */ }
    post('dragStart');
    event.preventDefault();
  });

  els.dragZone.addEventListener('pointermove', event => {
    if (!dragActive || event.pointerId !== dragPointerId) return;
    if (dragFrame) return;
    dragFrame = requestAnimationFrame(() => {
      dragFrame = 0;
      post('dragMove');
    });
  });

  function finishDrag(event) {
    if (!dragActive) return;
    if (event && dragPointerId !== null && event.pointerId !== dragPointerId) return;
    dragActive = false;
    dragPointerId = null;
    if (dragFrame) {
      cancelAnimationFrame(dragFrame);
      dragFrame = 0;
    }
    if (event) {
      try { els.dragZone.releasePointerCapture(event.pointerId); } catch { /* ignore */ }
    }
    post('dragEnd');
  }

  els.dragZone.addEventListener('pointerup', finishDrag);
  els.dragZone.addEventListener('pointercancel', finishDrag);
  els.dragZone.addEventListener('lostpointercapture', finishDrag);

  els.dragZone.addEventListener('dblclick', event => {
    if (event.target.closest('button')) return;
    finishDrag(null);
    post('toggleMaximize');
  });

  // Frameless WebView2 covers the native resize border, so the HTML edge acts
  // as the resize handle and asks the host to enter a native resize loop.
  const RESIZE_ZONE = 6;
  function resizeHit(clientX, clientY) {
    const x = clientX;
    const y = clientY;
    const w = window.innerWidth;
    const h = window.innerHeight;
    const left = x <= RESIZE_ZONE;
    const right = x >= w - RESIZE_ZONE;
    const top = y <= RESIZE_ZONE;
    const bottom = y >= h - RESIZE_ZONE;
    if (top && left) return 13;
    if (top && right) return 14;
    if (bottom && left) return 16;
    if (bottom && right) return 17;
    if (left) return 10;
    if (right) return 11;
    if (top) return 12;
    if (bottom) return 15;
    return 0;
  }

  function resizeCursor(hit) {
    switch (hit) {
      case 10: case 11: return 'ew-resize';
      case 12: case 15: return 'ns-resize';
      case 13: case 17: return 'nwse-resize';
      case 14: case 16: return 'nesw-resize';
      default: return '';
    }
  }

  window.addEventListener('mousemove', event => {
    if (event.buttons === 0) {
      const hit = resizeHit(event.clientX, event.clientY);
      document.documentElement.style.cursor = resizeCursor(hit);
    }
  }, { passive: true });

  window.addEventListener('mousedown', event => {
    if (event.button !== 0) return;
    const hit = resizeHit(event.clientX, event.clientY);
    if (!hit) return;
    event.preventDefault();
    event.stopPropagation();
    post('resize', { value: hit });
  }, true);

  document.addEventListener('mousedown', event => {
    if (!els.menuPanel.hidden && !els.menuPanel.contains(event.target) && !els.menuBtn.contains(event.target)) {
      closeMenu();
    }
  });

  $$('[data-stepper="font"] button').forEach(btn => {
    btn.addEventListener('click', () => {
      const next = clamp(state.settings.fontSize + Number(btn.dataset.step), 12, 32);
      setFontState(next);
      post('setFontSize', { value: next });
    });
  });

  $$('[data-stepper="opacity"] button').forEach(btn => {
    btn.addEventListener('click', () => {
      if (!state.glass) {
        toast('当前系统不支持真透明，需要在 Windows 11 22H2 及以上运行', 'info');
        return;
      }
      const next = clamp(state.settings.opacity + Number(btn.dataset.step) * 5, 30, 100);
      setOpacityState(next);
      post('setOpacity', { value: next });
    });
  });

  els.themeSeg.addEventListener('click', event => {
    const btn = event.target.closest('button');
    if (!btn) return;
    setThemeState(btn.dataset.theme);
    post('setTheme', { value: btn.dataset.theme });
  });

  els.edgeToggle.addEventListener('click', () => {
    setEdgeState(!state.settings.edgeHide, false);
    if (state.settings.edgeHide) toast('贴边隐藏已开启 · 把窗口拖到左/右/上边缘');
  });

  els.immersiveToggle.addEventListener('click', () => {
    setImmersiveState(!state.settings.immersive, false);
  });

  els.immersiveExit.addEventListener('click', () => setImmersiveState(false, false));

  els.welcomeShelfBtn.addEventListener('click', loadShelf);
  els.menuShelfBtn.addEventListener('click', loadShelf);
  els.shelfLoginBtn.addEventListener('click', () => { state.pendingAction = 'shelf'; openLogin('cookie'); });
  els.shelfRefreshBtn.addEventListener('click', loadShelf);
  els.shelfBackBtn.addEventListener('click', () => showView(state.chapter ? 'reader' : 'welcome'));

  els.welcomeInputBtn.addEventListener('click', openInput);
  els.continueBtn.addEventListener('click', () => {
    const id = els.continueBtn.dataset.chapterId;
    if (id) openChapter(id);
  });

  els.accountBtn.addEventListener('click', () => {
    if (state.user.loggedIn) {
      toast(`已登录：${state.user.name || '番茄读者'}`);
    } else {
      openLogin('cookie');
    }
  });
  els.logoutBtn.addEventListener('click', doLogout);

  els.loginCloseBtn.addEventListener('click', hideLogin);
  els.loginOverlay.addEventListener('mousedown', event => { if (event.target === els.loginOverlay) hideLogin(); });
  $$('.tab').forEach(btn => btn.addEventListener('click', () => switchLoginTab(btn.dataset.loginTab)));
  els.qrLoginBtn.addEventListener('click', () => {
    switchLoginTab('qr');
    setTimeout(() => {
      if (!els.openQrBtn.disabled) els.openQrBtn.click();
    }, 120);
  });
  els.cookieLoginBtn.addEventListener('click', submitCookieLogin);
  els.openQrBtn.addEventListener('click', () => {
    setQrLoading();
    post('openQrLogin');
  });
  els.pasteCookieBtn.addEventListener('click', () => post('readClipboard'));
  els.cookieInput.addEventListener('keydown', event => {
    if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') submitCookieLogin();
  });

  els.inputCloseBtn.addEventListener('click', hideInput);
  els.inputOverlay.addEventListener('mousedown', event => { if (event.target === els.inputOverlay) hideInput(); });
  els.openIdBtn.addEventListener('click', () => {
    const text = els.idInput.value.trim();
    if (!text) { toast('请输入章节或书籍编号', 'error'); return; }
    hideInput();
    showReaderLoading('正在定位章节…');
    post('openInput', { text });
  });
  els.idInput.addEventListener('keydown', event => {
    if (event.key === 'Enter') els.openIdBtn.click();
  });

  els.prevBtn.addEventListener('click', () => openChapter(els.prevBtn.dataset.chapterId));
  els.nextBtn.addEventListener('click', () => openChapter(els.nextBtn.dataset.chapterId));
  els.tocBtn.addEventListener('click', requestCatalog);
  els.catalogCloseBtn.addEventListener('click', hideDrawer);
  els.drawerScrim.addEventListener('click', hideDrawer);
  els.catalogSearch.addEventListener('input', filterCatalog);

  els.readerScroll.addEventListener('scroll', () => {
    const ratio = updateProgress();
    if (!state.chapter) return;
    const now = Date.now();
    if (now - state.lastProgressSent < 500) return;
    state.lastProgressSent = now;
    post('progress', { chapterId: state.chapter.chapterId, ratio });
  }, { passive: true });

  window.addEventListener('resize', () => {
    if (!els.loginOverlay.hidden) switchLoginTab(state.loginTab);
  });

  document.addEventListener('keydown', event => {
    if (event.key === 'Escape') {
      if (!els.loginOverlay.hidden) { hideLogin(); return; }
      if (!els.inputOverlay.hidden) { hideInput(); return; }
      if (!els.catalogDrawer.hidden) { hideDrawer(); return; }
      if (!els.menuPanel.hidden) { closeMenu(); return; }
      if (state.settings.immersive) { setImmersiveState(false, false); return; }
    }
    if (state.view === 'reader' && !isTyping(event.target)) {
      if (event.key === '[' || (event.ctrlKey && event.key === '-')) {
        event.preventDefault();
        const next = clamp(state.settings.fontSize - 1, 12, 32);
        setFontState(next);
        post('setFontSize', { value: next });
      }
      if (event.key === ']' || (event.ctrlKey && event.key === '=')) {
        event.preventDefault();
        const next = clamp(state.settings.fontSize + 1, 12, 32);
        setFontState(next);
        post('setFontSize', { value: next });
      }
      if (event.key === 'ArrowLeft' && !event.ctrlKey && !event.metaKey && !event.altKey) {
        if (els.prevBtn.disabled) return;
        openChapter(els.prevBtn.dataset.chapterId);
      }
      if (event.key === 'ArrowRight' && !event.ctrlKey && !event.metaKey && !event.altKey) {
        if (els.nextBtn.disabled) return;
        openChapter(els.nextBtn.dataset.chapterId);
      }
    }
  });

  function isTyping(node) {
    return node && (node.tagName === 'INPUT' || node.tagName === 'TEXTAREA' || node.isContentEditable);
  }

  // ------------------------------------------------------------
  // preview mode (browser QA without native shell)
  // ------------------------------------------------------------

  const mockBooks = [
    { bookId: '700000000000001', title: '山茶文具店', author: '小川糸', chapterId: '700000000000011', serialCount: 46, statusKnown: true, statusText: '已完结', isFinished: true, readText: '已读完' },
    { bookId: '700000000000002', title: '长安的荔枝', author: '马伯庸', chapterId: '700000000000021', serialCount: 28, statusKnown: true, statusText: '已完结', isFinished: true, readText: '读到 12 章' },
    { bookId: '700000000000003', title: '我的阿勒泰', author: '李娟', chapterId: '700000000000031', serialCount: 12, statusKnown: true, statusText: '连载中', startOrder: 6, readText: '读到 6 章' },
    { bookId: '700000000000004', title: '一句顶一万句', author: '刘震云', chapterId: '', serialCount: 30, statusKnown: true, statusText: '连载中', startOrder: 3, readText: '读到 3 章' }
  ];

  const demoParagraphs = [
    '傍晚六点，城市正在缓慢地退潮。玻璃窗上还留着午后的温度，楼下的梧桐把影子铺得很长，像一封没有写完的信。',
    '她把书翻开，又合上。纸张发出很轻的声响，像有人在很远的地方叹了一口气。这个时刻不需要说话，连时间也放轻了脚步。',
    '窗外有小孩骑车经过，铃声一响，夏天就好像从树荫里跳了出来。她忽然想起小时候的午后，电风扇转得很慢，西瓜放在井水里，知了不知疲倦地叫着。',
    '很多年以后，她才明白，所谓安静，并不是没有声音。安静是心里那盏灯还亮着，而门外的风雨暂时与你无关。',
    '桌上的茶已经凉了。她端起杯子，看见杯底沉着两片舒展开的叶子，像两句刚刚想通的话。',
    '故事里的人也在这个时辰停下脚步。他们隔着一条街，一盏灯，一场没有下完的雨，各自想着各自的心事。',
    '书页翻过去的时候，夜色从窗缝里渗进来，一点点把房间染蓝。她伸手打开台灯，暖黄的光落在字上，字就活了。',
    '再读一页吧，她想。就一页。然后关灯，把今天轻轻放下。'
  ];

  const mockChapter = {
    bookId: '700000000000001',
    bookName: '山茶文具店',
    chapterId: '700000000000011',
    title: '第一章 · 一间安静的信笺店',
    author: '小川糸',
    wordCount: 1280,
    prevId: '',
    nextId: '700000000000012',
    isPreview: false,
    paragraphs: demoParagraphs,
    fontDataUrl: ''
  };

  function applyMockInit() {
    applySettings({ theme: 'day', fontSize: 19, opacity: 100, edgeHide: false, immersive: false });
    updateUser({ loggedIn: false, name: '', id: '' });
    state.recent = { bookId: '700000000000003', chapterId: '700000000000031', bookName: '我的阿勒泰', chapterTitle: '第六章 · 木耳' };
    updateRecent();
  }

  function mockPost(type, payload) {
    const reply = (kind, data) => {
      setTimeout(() => handleMessage({ data: { type: kind, data } }), 220);
    };
    switch (type) {
      case 'getShelf': reply('shelf', { books: mockBooks }); break;
      case 'openBook': reply('chapter', { chapter: { ...mockChapter, bookId: payload.bookId || mockChapter.bookId }, restoreRatio: 0 }); break;
      case 'openChapter': reply('chapter', { chapter: { ...mockChapter, chapterId: payload.chapterId, title: '第二章 · 一封没有寄出的信', prevId: mockChapter.chapterId, nextId: '700000000000013' }, restoreRatio: 0 }); break;
      case 'getCatalog': reply('catalog', { catalog: { bookId: mockChapter.bookId, bookName: mockChapter.bookName, chapters: Array.from({ length: 12 }, (_, i) => ({ id: String(700000000000011 + i), title: `第${i + 1}章 · ${['信笺', '夏日', '旧居', '晚风', '远行', '灯下', '山茶', '雨后', '归途', '长夜', '清晨', '尾声'][i]}` })) } }); break;
      case 'cookieLogin': reply('loginResult', { success: false, message: '预览模式下不会真正登录，请在桌面应用中操作。' }); break;
      case 'openQrLogin': reply('toast', { message: '预览模式：请在桌面应用中使用扫码登录', kind: 'info' }); break;
      case 'logout': reply('user', { user: { loggedIn: false } }); break;
      case 'readClipboard': reply('toast', { message: '预览模式无法读取剪贴板', kind: 'info' }); break;
      default: break;
    }
  }

  // ------------------------------------------------------------
  // boot
  // ------------------------------------------------------------

  if (previewMode) {
    applySettings({ theme: 'day', fontSize: 19, opacity: 100, edgeHide: false, immersive: false });
    updateUser({ loggedIn: false });
    updateRecent();
    setTimeout(applyMockInit, 200);
  } else if (isLive) {
    post('ready');
  }

  // Make the first paint feel intentional.
  els.body.classList.add('is-ready');
})();
