using System.Text.Json;
using KeyNavBrowser.Models;

namespace KeyNavBrowser.Services;

/// <summary>
/// Скрипт, внедряемый в каждую страницу WebView2. Он отвечает за всё поведение
/// клавиатурной навигации из п. 4 ТЗ: подсветку активной ссылки, обход ссылок,
/// плавную прокрутку, нумерацию ссылок поверх них, переход по номеру (1–9),
/// три режима отображения контента и определение того, находится ли фокус
/// в поле ввода.
///
/// Обработка самих нажатий приходит из C# через
/// CoreWebView2Controller.AcceleratorKeyPressed; скрипт только исполняет команды
/// и сообщает хост-приложению о состоянии фокуса и выбранной ссылке.
///
/// Скрипт идемпотентен: повторное внедрение (например, после смены настроек)
/// не дублирует обработчики клавиш и MutationObserver, а только обновляет конфигурацию.
/// </summary>
public static class KeyboardNavigationScript
{
    private const string ConfigPlaceholder = "\"__KEYNAV_CONFIG__\"";

    private sealed record Config(
        bool Enabled,
        int ScrollStep,
        bool HudEnabled,
        int HudHideDelayMs,
        bool NumberLinks,
        string ContentMode,
        bool ForceCurrentWindow);

    /// <summary>Готовая строка скрипта для вызова через ExecuteScriptAsync.</summary>
    public static string Build(BrowserSettings settings)
    {
        var config = new Config(
            settings.KeyboardNavigationEnabled,
            Math.Clamp(settings.ScrollStep, 50, 100),
            HudEnabled: true,
            HudHideDelayMs: 5000,
            NumberLinks: settings.ShowLinkNumbers,
            ContentMode: ContentModes.IsValid(settings.ContentMode)
                ? settings.ContentMode
                : ContentModes.TextAndImages,
            ForceCurrentWindow: !settings.TargetBlankInNewTab);

        string json = JsonSerializer.Serialize(config);

        return Raw.Replace(ConfigPlaceholder, json);
    }

    private const string Raw = """
(function () {
  'use strict';

  var CFG = "__KEYNAV_CONFIG__";

  var ACTIVE_ATTR = 'data-keynav-active';
  var IDX_ATTR    = 'data-keynav-idx';
  var HUD_ID      = '__keynav_hud_root__';
  var BADGE_ID    = '__keynav_badges_root__';

  var links   = [];
  var badges  = [];      // бейджи параллельны links; null — бейдж ещё не создан
  var current = -1;
  var activeEl = null;   // подсвеченный элемент (не зависит от индекса в links)
  var rafId   = 0;
  var hud     = null;
  var hudText = null;
  var hudKeys = null;
  var hudHideTimer = 0;
  var badgeLayer = null;
  var editable = false;
  var focusReported = false;

  /* ======================= Связь с C# ======================= */

  function post(msg) {
    try {
      if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(msg);
    } catch (e) { /* страница вне WebView2 — игнорируем */ }
  }

  /* Повторное внедрение того же скрипта (смена настроек): обновляем конфигурацию,
     но не регистрируем обработчики заново — иначе клавиши срабатывали бы дважды. */
  if (window.__keynav && window.__keynav.__installed) {
    try { window.__keynav.configure(CFG); } catch (e) { }
    return;
  }

  /* =============== Совместимость с полями ввода (п. 4.3) =============== */

  var NON_TEXT_INPUTS = {
    button: 1, submit: 1, reset: 1, checkbox: 1, radio: 1,
    image: 1, file: 1, range: 1, color: 1, hidden: 1
  };

  function isEditableElement(el) {
    if (!el || el.nodeType !== 1) return false;
    var tag = (el.tagName || '').toLowerCase();
    if (tag === 'textarea' || tag === 'select') return true;
    if (tag === 'input') {
      var type = (el.getAttribute('type') || 'text').toLowerCase();
      return !NON_TEXT_INPUTS[type];
    }
    return !!el.isContentEditable;
  }

  function reportFocus(force) {
    var now = isEditableElement(document.activeElement);
    if (!force && now === editable && focusReported) return;
    editable = now;
    focusReported = true;
    post({ type: 'focus', editable: now });
    updateHud();
    showHud();
  }

  /* ======================= Плавная прокрутка ======================= */

  function animateScroll(dx, dy) {
    if (rafId) { cancelAnimationFrame(rafId); rafId = 0; }
    clearScrollFallback();

    var doc = document.scrollingElement || document.documentElement;
    var startX = window.pageXOffset || doc.scrollLeft || 0;
    var startY = window.pageYOffset || doc.scrollTop || 0;
    var maxX = Math.max(0, doc.scrollWidth  - window.innerWidth);
    var maxY = Math.max(0, doc.scrollHeight - window.innerHeight);

    var endX = Math.min(maxX, Math.max(0, startX + dx));
    var endY = Math.min(maxY, Math.max(0, startY + dy));
    var distX = endX - startX;
    var distY = endY - startY;

    if (distX === 0 && distY === 0) return;

    /* Чем больше расстояние, тем дольше анимация, но не дольше 600 мс. */
    var duration = Math.min(600, 130 + Math.sqrt(Math.abs(distX) + Math.abs(distY)) * 2.2);
    var t0 = (window.performance && performance.now) ? performance.now() : new Date().getTime();

    function frame(now) {
      var k = Math.min(1, (now - t0) / duration);
      var e = k < 0.5 ? 2 * k * k : 1 - Math.pow(-2 * k + 2, 2) / 2;
      window.scrollTo(startX + distX * e, startY + distY * e);
      if (k < 1) rafId = window.requestAnimationFrame(frame);
      else { rafId = 0; clearScrollFallback(); }
    }

    /* Страховка: Chromium приостанавливает requestAnimationFrame в фоновых и
       перекрытых окнах. Без неё нажатие клавиши в таком окне не дало бы никакого
       эффекта. Если за полсекунды анимация не завершилась — просто прыгаем
       в конечную точку. */
    scrollFallbackTimer = window.setTimeout(function () {
      scrollFallbackTimer = 0;
      if (rafId) { cancelAnimationFrame(rafId); rafId = 0; }
      window.scrollTo(endX, endY);
    }, duration + 250);

    rafId = window.requestAnimationFrame(frame);
  }

  var scrollFallbackTimer = 0;

  function clearScrollFallback() {
    if (scrollFallbackTimer) { window.clearTimeout(scrollFallbackTimer); scrollFallbackTimer = 0; }
  }

  function scrollStep()      { animateScroll(0, -CFG.ScrollStep); }
  function scrollStepDown()  { animateScroll(0,  CFG.ScrollStep); }
  function scrollLeft()     { animateScroll(-CFG.ScrollStep, 0); }
  function scrollRight()    { animateScroll( CFG.ScrollStep, 0); }
  function pageDown()       { animateScroll(0,  window.innerHeight * 0.9); }
  function pageUp()         { animateScroll(0, -window.innerHeight * 0.9); }

  /* =================== Обход ссылок в порядке DOM =================== */

  function isVisible(el) {
    if (!el.getClientRects || el.getClientRects().length === 0) return false;
    var s = window.getComputedStyle(el);
    if (!s) return true;
    if (s.visibility === 'hidden' || s.display === 'none' || s.opacity === '0') return false;
    var r = el.getBoundingClientRect();
    return r.width > 0 && r.height > 0;
  }

  /* ТЗ: «обеспечить открытие всех ссылок в текущем окне». window.open и клик по
     ссылке с target="_blank" Chromium всё равно отдаёт в NewWindowRequested, но
     надёжнее заранее переписать target: часть скриптов страницы успевает
     отменить переход, полагаясь на target. Исходное значение сохраняем
     в data-keynav-target, чтобы ничего не потерять. */
  function neutralizeTargets() {
    if (!CFG.ForceCurrentWindow) return;

    var anchors = document.querySelectorAll('a[target]');
    for (var i = 0; i < anchors.length; i++) {
      var a = anchors[i];
      var t = a.getAttribute('target');
      if (!t || t === '_self') continue;
      a.setAttribute('data-keynav-target', t);
      a.setAttribute('target', '_self');
    }

    /* Формы с target="_blank" открывают результат в новом окне так же. */
    var forms = document.querySelectorAll('form[target]');
    for (var j = 0; j < forms.length; j++) {
      var f = forms[j];
      var ft = f.getAttribute('target');
      if (!ft || ft === '_self') continue;
      f.setAttribute('data-keynav-target', ft);
      f.setAttribute('target', '_self');
    }
  }

  function collect() {
    neutralizeTargets();

    var all = document.querySelectorAll('a[href]');
    var next = [];

    for (var i = 0; i < all.length; i++) {
      var a = all[i];
      /* Подсвеченная ссылка тоже входит в список: её нельзя исключать, иначе
         после каждой пересборки она выпадала бы из обхода. */
      if (a.closest && a.closest('#' + HUD_ID)) continue;
      if (a.closest && a.closest('#' + BADGE_ID)) continue;
      if (a.closest && a.closest('[aria-hidden="true"]')) continue;
      if (!isVisible(a)) continue;

      /* Порядковый номер с 1 — его показываем поверх ссылки (см. updateBadges). */
      var num = String(next.length + 1);
      if (a.getAttribute(IDX_ATTR) !== num) a.setAttribute(IDX_ATTR, num);

      next.push(a);
    }

    links = next;
    badges.length = links.length;

    /* Подсвеченная ссылка могла исчезнуть из DOM — тогда снимаем выделение. */
    if (activeEl && activeEl.isConnected) current = links.indexOf(activeEl);
    else { clearHighlightOnly(); current = -1; }

    updateBadges();
    updateHud();
  }

  var collectTimer = 0;
  /* Именно троттлинг, а не debounce: при частых изменениях DOM (бесконечная
     прокрутка, SPA) пересборка иначе никогда не запустится. */
  function scheduleCollect() {
    if (collectTimer) return;
    collectTimer = window.setTimeout(function () { collectTimer = 0; collect(); }, 250);
  }

  function setActive(i, doScroll) {
    if (links.length === 0) { clearActive(); return; }

    clearHighlightOnly();

    current = ((i % links.length) + links.length) % links.length;

    var el = links[current];
    if (!el) return;

    activeEl = el;
    el.setAttribute(ACTIVE_ATTR, '1');

    if (doScroll) {
      try {
        el.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'smooth' });
      } catch (e) {
        try { el.scrollIntoView(); } catch (e2) { /* нет поддержки — пропускаем */ }
      }
    }

    updateBadges();
    updateHud();
    showHud();
  }

  /* E — следующая (правая) ссылка; с конца списка возвращаемся к первой. */
  function nextLink() { setActive(current + 1, true); }

  /* Q — предыдущая (левая) ссылка; с первой переходим на последнюю. */
  function prevLink() { setActive(current <= 0 ? links.length - 1 : current - 1, true); }

  /* 1–9 — переход по ссылке с соответствующим номером (нумерация поверх ссылок). */
  function gotoNumber(n) {
    if (n < 1 || n > links.length) {
      var hudEl = ensureHud();
      if (hudEl && hudText) {
        hudText.textContent = links.length === 0
          ? 'На странице нет ссылок'
          : 'Ссылки с номером ' + n + ' нет — на странице ' + links.length;
      }
      showHud();
      return false;
    }

    /* Подсвечиваем и доводим до экрана, чтобы было видно, куда переходим,
       после чего открываем ссылку в текущей вкладке. */
    setActive(n - 1, true);
    activate();
    return true;
  }

  /* Снимает подсветку, не трогая current. */
  function clearHighlightOnly() {
    if (activeEl) activeEl.removeAttribute(ACTIVE_ATTR);
    activeEl = null;
  }

  function clearActive() {
    clearHighlightOnly();
    current = -1;
    updateBadges();
    updateHud();
  }

  /* ENTER — открыть выделенную ссылку в текущей вкладке (п. 4.2 ТЗ).
     Переход выполняется прямо здесь: это надёжнее, чем сообщение в C#,
     и не зависит от канала postMessage. */
  function activate() {
    if (current < 0 || !links[current]) return;
    var a = links[current];

    var hadTarget = a.hasAttribute('target');
    var oldTarget = a.getAttribute('target');

    /* По ТЗ ссылка открывается в текущей вкладке, поэтому принудительно
       снимаем target="_blank" (он иначе открыл бы отдельное окно Chromium). */
    if (hadTarget && oldTarget !== '_self') a.setAttribute('target', '_self');

    try {
      a.click();
    } catch (e) {
      if (a.href) window.location.href = a.href;
    } finally {
      if (hadTarget && oldTarget !== '_self') a.setAttribute('target', oldTarget);
    }
  }

  /* =================== Нумерация ссылок поверх них (ТЗ) =================== */

  function installStyle() {
    if (document.getElementById('__keynav_style__')) return;
    var style = document.createElement('style');
    style.id = '__keynav_style__';
    style.textContent =
      '[' + ACTIVE_ATTR + ']{' +
        'outline:3px solid #ff8a00 !important;' +
        'outline-offset:2px !important;' +
        'background-color:rgba(255,138,0,0.30) !important;' +
        'box-shadow:0 0 0 4px rgba(255,138,0,0.35) !important;' +
        'border-radius:3px !important;' +
      '}' +
      /* Слой бейджей: фиксированный и без событий мыши, чтобы не мешать странице. */
      '#' + BADGE_ID + '{' +
        'position:fixed !important;left:0 !important;top:0 !important;' +
        'width:0 !important;height:0 !important;overflow:visible !important;' +
        'pointer-events:none !important;z-index:2147483646 !important;' +
      '}' +
      '#' + BADGE_ID + ' > span{' +
        'position:absolute !important;display:block;' +
        'min-width:15px;height:15px;line-height:15px;padding:0 3px;' +
        'box-sizing:border-box !important;' +
        'transform:translate(-45%,-45%) !important;' +
        'border-radius:8px !important;' +
        'font:bold 10px/15px Consolas,monospace !important;' +
        'text-align:center !important;color:#fff !important;' +
        'background:#ff8a00 !important;border:1px solid #fff !important;' +
        'box-shadow:0 1px 3px rgba(0,0,0,.5) !important;' +
      '}' +
      /* Номера 10 и выше серые: подсказка, что клавиши для них нет. */
      '#' + BADGE_ID + ' > span.kbn-plain{' +
        'font-weight:normal !important;color:#ececef !important;' +
        'background:rgba(48,48,54,.88) !important;border-color:rgba(255,255,255,.55) !important;' +
      '}' +
      '#' + BADGE_ID + ' > span.kbn-active{' +
        'background:#fff !important;color:#ff8a00 !important;' +
        'transform:translate(-45%,-45%) scale(1.3) !important;' +
      '}';
    (document.head || document.documentElement).appendChild(style);
  }

  function ensureBadgeLayer() {
    if (!CFG.NumberLinks) return null;
    if (badgeLayer && badgeLayer.isConnected) return badgeLayer;

    var root = document.createElement('div');
    root.id = BADGE_ID;
    root.setAttribute('data-keynav-badges', '1');
    root.setAttribute('aria-hidden', 'true');
    (document.body || document.documentElement).appendChild(root);

    badgeLayer = root;
    return root;
  }

  function hideBadges() {
    for (var i = 0; i < badges.length; i++) {
      var b = badges[i];
      if (b) b.style.display = 'none';
    }
  }

  /* Раскладывает бейджи по экранным координатам ссылок. Ссылки вне видимой
     области пропускаются: на страницах с тысячами ссылок это заметно
     ускоряет и разметку, и прокрутку. */
  function updateBadges() {
    if (!CFG.NumberLinks) { hideBadges(); return; }

    var layer = ensureBadgeLayer();
    if (!layer) return;

    var vh = window.innerHeight;
    var vw = window.innerWidth;

    for (var i = 0; i < links.length; i++) {
      var el = links[i];
      var b = badges[i];

      if (!el || !el.isConnected) {
        if (b && b.parentNode) b.parentNode.removeChild(b);
        badges[i] = null;
        continue;
      }

      if (!b) { b = document.createElement('span'); badges[i] = b; layer.appendChild(b); }

      var r = el.getBoundingClientRect();
      var onScreen = r.width > 0 && r.height > 0 &&
                     r.bottom >= -40 && r.top <= vh + 40 &&
                     r.right >= -60 && r.left <= vw + 60;

      if (!onScreen) { b.style.display = 'none'; continue; }

      b.textContent = String(i + 1);
      b.className = (i < 9 ? '' : 'kbn-plain') + (el === activeEl ? ' kbn-active' : '');
      b.style.display = 'block';
      b.style.left = Math.round(r.left) + 'px';
      b.style.top = Math.round(r.top) + 'px';
    }
  }

  var badgeTimer = 0;
  var badgeLastRun = 0;

  /* Троттлинг по времени, а не по кадрам: пересчёт координат на странице с
     большим числом ссылок иначе заметно подтормаживает прокрутку. */
  function scheduleBadges() {
    if (badgeTimer) return;
    var wait = Math.max(0, 60 - (new Date().getTime() - badgeLastRun));
    badgeTimer = window.setTimeout(function () {
      badgeTimer = 0;
      badgeLastRun = new Date().getTime();
      updateBadges();
    }, wait);
  }

  /* ============ Три режима отображения контента (ТЗ) ============ */

  /* Скрытие сделано через display:none — элемент не занимает места, и страница
     не оставляет «дыр» на месте картинок. Фоновые изображения (background-image)
     не трогаются: это ломает тёмные темы и вёрстку, а в ТЗ перечислены
     только конкретные элементы. */
  var CONTENT_CSS = {
    TextOnly:      'img,picture,svg,canvas,video,iframe,object,embed,input[type="image"]{display:none !important;}',
    TextAndImages: 'video,iframe,object,embed{display:none !important;}',
    TextAndVideo:  'img,picture,svg{display:none !important;}'
  };

  function applyContentMode() {
    var style = document.getElementById('__keynav_content_style__');
    if (!style) {
      style = document.createElement('style');
      style.id = '__keynav_content_style__';
      (document.head || document.documentElement).appendChild(style);
    }

    var css = CONTENT_CSS[CFG.ContentMode] || CONTENT_CSS.TextAndImages;
    if (style.textContent !== css) style.textContent = css;
  }

  /* =================== Индикация активной ссылки =================== */

  function ensureHud() {
    if (!CFG.HudEnabled) return null;
    if (hud && hud.isConnected) return hud;

    var root = document.createElement('div');
    root.id = HUD_ID;
    root.setAttribute('data-keynav-hud', '1');
    root.setAttribute('aria-hidden', 'true');
    root.style.cssText = [
      'position:fixed', 'right:14px', 'bottom:14px', 'z-index:2147483647',
      'max-width:min(560px,72vw)', 'padding:9px 13px', 'border-radius:10px',
      'background:rgba(22,22,26,0.90)', 'color:#ffffff',
      'font:12px/1.45 "Segoe UI",system-ui,sans-serif',
      'box-shadow:0 8px 26px rgba(0,0,0,0.38)',
      'pointer-events:none', 'opacity:0', 'transition:opacity .25s ease'
    ].join(';');

    hudKeys = document.createElement('div');
    hudKeys.style.cssText = 'opacity:.82;margin-bottom:3px';

    hudText = document.createElement('div');
    hudText.style.cssText = 'font-family:Consolas,monospace;white-space:nowrap;overflow:hidden;text-overflow:ellipsis';

    root.appendChild(hudKeys);
    root.appendChild(hudText);
    (document.body || document.documentElement).appendChild(root);

    hud = root;
    return hud;
  }

  function showHud() {
    var el = ensureHud();
    if (!el) return;
    el.style.opacity = '1';
    if (hudHideTimer) window.clearTimeout(hudHideTimer);
    hudHideTimer = window.setTimeout(function () {
      if (el) el.style.opacity = '0';
    }, CFG.HudHideDelayMs);
  }

  function modeLabel() {
    if (CFG.ContentMode === 'TextOnly') return 'только текст';
    if (CFG.ContentMode === 'TextAndVideo') return 'текст + видео';
    return 'текст + изображения';
  }

  function updateHud() {
    if (!CFG.HudEnabled) return;
    var el = ensureHud();
    if (!el || !hudText) return;

    hudKeys.textContent =
      'W/S/A/D — прокрутка · ПРОБЕЛ — экран вниз · Q/E — ссылки · 1–9 — по номеру' +
      ' · ENTER — открыть · ESC — сброс · Режим: ' + modeLabel();

    if (editable) {
      hudText.textContent = 'Фокус в поле ввода — клавиатурная навигация приостановлена';
    } else if (current >= 0 && links[current]) {
      hudText.textContent = 'Ссылка ' + (current + 1) + '/' + links.length + ' · ' + links[current].href;
    } else {
      hudText.textContent = links.length + ' ссылок на странице';
    }
  }

  /* ================= Обработка нажатий внутри страницы ================= */

  /* Возвращает номер 1–9 по event.code; раскладка (русская/латинская) не важна. */
  function numberFromCode(code) {
    if (!code) return -1;
    if (code.length === 6 && code.indexOf('Digit') === 0) return code.charCodeAt(5) - 48;
    if (code.length === 7 && code.indexOf('Numpad') === 0) return code.charCodeAt(6) - 48;
    return -1;
  }

  /* П. 4.3 ТЗ: клавиатурная навигация активна, когда фокус находится на самой странице.
     Если фокус в поле ввода (input/textarea/select/contenteditable), нажатия
     передаём странице — они печатают символы, а не управляют браузером. */
  document.addEventListener('keydown', function (ev) {
    if (!CFG.Enabled) return;
    if (ev.ctrlKey || ev.altKey || ev.metaKey || ev.shiftKey) return;
    if (isEditableElement(document.activeElement)) return;

    var code = ev.code || '';
    var handled = true;

    switch (code) {
      case 'KeyW': scrollStep();     break;
      case 'KeyS': scrollStepDown(); break;
      case 'KeyA': scrollLeft();     break;
      case 'KeyD': scrollRight();    break;
      case 'Space': pageDown();      break;
      case 'KeyQ': prevLink();       break;
      case 'KeyE': nextLink();       break;
      case 'Escape': clearActive();  break;
      case 'Enter':
        /* Без выделенной ссылки Enter должен работать штатно (отправка форм). */
        if (current >= 0) { activate(); } else { handled = false; }
        break;
      default: handled = gotoNumber(numberFromCode(code));
    }

    if (handled) {
      /* Подавляем поведение браузера по умолчанию: например, ПРОБЕЛ не должен
         прокручивать страницу второй раз. */
      ev.preventDefault();
      ev.stopPropagation();
      showHud();
    }
  }, true);

  /* ======================= Инициализация ======================= */

  window.__keynav = {
    __installed: true,

    configure: function (cfg) {
      CFG = cfg;
      installStyle();
      applyContentMode();
      if (!CFG.NumberLinks) hideBadges();
      collect();
      updateHud();
    },

    collect: collect,
    next: nextLink,
    prev: prevLink,
    goto: gotoNumber,
    activate: activate,
    clear: clearActive,
    scrollStep: scrollStep,
    scrollStepDown: scrollStepDown,
    scrollLeft: scrollLeft,
    scrollRight: scrollRight,
    pageDown: pageDown,
    pageUp: pageUp,
    count: function () { return links.length; },
    activeIndex: function () { return current; }
  };

  installStyle();
  applyContentMode();
  collect();

  /* Бейджи следуют за прокруткой и изменением размеров окна. */
  window.addEventListener('scroll', scheduleBadges, true);
  window.addEventListener('resize', scheduleBadges);

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', function () {
      installStyle(); applyContentMode(); collect(); reportFocus(true);
      post({ type: 'ready', url: location.href });
    });
  } else {
    reportFocus(true);
    post({ type: 'ready', url: location.href });
  }

  document.addEventListener('focusin', function () { reportFocus(false); }, true);
  document.addEventListener('focusout', function () { window.setTimeout(function () { reportFocus(false); }, 0); }, true);
  window.addEventListener('blur', function () { reportFocus(false); });

  try {
    new MutationObserver(scheduleCollect).observe(document.documentElement, {
      childList: true,
      subtree: true,
      attributes: true,
      attributeFilter: ['href', 'class', 'style', 'target']
    });
  } catch (e) { /* нет поддержки MutationObserver */ }
})();
""";
}