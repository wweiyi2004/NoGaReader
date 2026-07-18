namespace NoGaReader.Services;

internal static class ComicReaderAssets
{
    public const string StyleSheet = """
        :root {
          color-scheme: dark;
          --comic-background: #111315;
          --comic-surface: #1b1e22;
          --comic-muted: #a9b0ba;
          --comic-accent: #8b98ff;
        }
        * { box-sizing: border-box; }
        html, body { width: 100%; height: 100%; margin: 0; overflow: hidden; background: var(--comic-background); }
        body { color: #f5f6f8; font-family: "Microsoft YaHei UI", "Segoe UI", sans-serif; }
        #viewport {
          position: fixed; inset: 0; overflow: auto; overscroll-behavior: contain;
          background: radial-gradient(circle at 50% 35%, #20242a 0, var(--comic-background) 62%);
          scrollbar-color: #555d68 transparent; scrollbar-width: thin;
        }
        #viewport.drag-ready { cursor: grab; }
        #viewport.dragging { cursor: grabbing; user-select: none; }
        #stage {
          min-width: 100%; min-height: 100%; padding: 18px 58px 48px;
          display: flex; align-items: center; justify-content: center; gap: 12px;
          transform-origin: top center;
        }
        body.direction-rtl #stage { direction: rtl; }
        body.direction-ltr #stage { direction: ltr; }
        .comic-page {
          position: relative; flex: 0 0 auto; display: flex; align-items: center; justify-content: center;
          margin: 0; overflow: hidden; background: #08090a;
          border-radius: 3px; box-shadow: 0 16px 42px rgba(0,0,0,.42);
        }
        .comic-page img {
          display: block; max-width: 100%; max-height: 100%; width: auto; height: auto;
          object-fit: contain; user-select: none; -webkit-user-drag: none;
        }
        body.fit-height:not(.mode-continuous) .comic-page img { max-height: calc(100vh - 66px); }
        body.fit-width:not(.mode-continuous) .comic-page { width: min(100%, 1500px); }
        body.fit-width.mode-double .comic-page { width: min(calc((100% - 12px) / 2), 920px); }
        body.fit-width .comic-page img { width: 100%; height: auto; max-height: none; }
        body.fit-original .comic-page img { max-width: none; max-height: none; }
        body.mode-continuous #stage {
          min-height: 100%; padding: 14px 20px 80px; flex-direction: column; justify-content: flex-start;
          align-items: center; gap: 12px; direction: ltr;
        }
        body.mode-continuous .comic-page { width: min(100%, 1500px); flex: none; }
        body.mode-continuous.fit-height .comic-page img { max-height: none; max-width: 100%; }
        body.mode-continuous.fit-width .comic-page img { width: 100%; max-height: none; }
        body.mode-continuous.fit-original .comic-page { width: auto; max-width: none; }
        .page-label {
          position: absolute; right: 9px; bottom: 8px; padding: 3px 7px; border-radius: 7px;
          color: #e6e9ee; background: rgba(0,0,0,.62); font-size: 11px; line-height: 1.3;
          opacity: 0; transition: opacity .15s ease; pointer-events: none;
        }
        body.mode-continuous .comic-page:hover .page-label { opacity: 1; }
        #hud {
          position: fixed; z-index: 8; left: 50%; bottom: 12px; transform: translateX(-50%);
          min-width: 92px; padding: 6px 12px; border: 1px solid rgba(255,255,255,.09); border-radius: 999px;
          color: #e8eaf0; background: rgba(18,20,23,.82); backdrop-filter: blur(12px);
          text-align: center; font-size: 12px; line-height: 1.2; pointer-events: none;
        }
        #loading, #error {
          position: fixed; z-index: 12; inset: 0; display: grid; place-items: center;
          padding: 30px; color: var(--comic-muted); background: var(--comic-background); text-align: center;
        }
        #error { display: none; color: #f0b8b8; }
        body.ready #loading { display: none; }
        body.failed #loading { display: none; }
        body.failed #error { display: grid; }
        """;

    public const string Script = """
        (() => {
          'use strict';
          const body = document.body;
          const viewport = document.getElementById('viewport');
          const stage = document.getElementById('stage');
          const hud = document.getElementById('hud');
          const errorPanel = document.getElementById('error');
          const startPage = Math.max(0, Number.parseInt(body.dataset.startPage || '0', 10) || 0);
          const manifestUrl = body.dataset.manifest || '../manifest.json';
          const state = {
            pages: [], current: startPage, ready: false, scrollFrame: 0,
            settings: { display: 'single', direction: 'rtl', fit: 'height', coverSingle: true, scale: 1 }
          };
          const preloads = new Map();

          const post = value => {
            try { window.chrome?.webview?.postMessage(value); } catch { }
          };
          const clampPage = value => Math.max(0, Math.min(state.pages.length - 1, Number.parseInt(value, 10) || 0));
          const normalizeSettings = value => ({
            display: ['single', 'double', 'continuous'].includes(value?.display) ? value.display : state.settings.display,
            direction: value?.direction === 'ltr' ? 'ltr' : 'rtl',
            fit: ['width', 'height', 'original'].includes(value?.fit) ? value.fit : state.settings.fit,
            coverSingle: value?.coverSingle !== false,
            scale: Math.max(.5, Math.min(3, Number(value?.scale) || 1))
          });
          const pageProgress = () => state.pages.length <= 1 ? 0 : state.current / (state.pages.length - 1);
          const postLocation = () => post({
            type: 'nogareader.comic-location', pageIndex: state.current,
            pageCount: state.pages.length, progress: pageProgress()
          });
          const updateHud = () => {
            hud.textContent = `${state.current + 1} / ${state.pages.length} · ${Math.round(state.settings.scale * 100)}%`;
          };
          const pageElement = (index, eager) => {
            const figure = document.createElement('figure');
            figure.className = 'comic-page';
            figure.dataset.pageIndex = String(index);
            const image = document.createElement('img');
            image.src = state.pages[index];
            image.alt = `第 ${index + 1} 页`;
            image.loading = eager ? 'eager' : 'lazy';
            image.decoding = 'async';
            image.draggable = false;
            const label = document.createElement('span');
            label.className = 'page-label';
            label.textContent = `${index + 1}`;
            figure.append(image, label);
            return figure;
          };
          const groupStart = page => {
            const current = clampPage(page);
            if (state.settings.display !== 'double') return current;
            if (state.settings.coverSingle) {
              if (current === 0) return 0;
              return 1 + Math.floor((current - 1) / 2) * 2;
            }
            return Math.floor(current / 2) * 2;
          };
          const displayIndices = page => {
            const start = groupStart(page);
            if (state.settings.display !== 'double' || (state.settings.coverSingle && start === 0)) return [start];
            return [start, start + 1].filter(index => index < state.pages.length);
          };
          const preloadAround = page => {
            const wanted = new Set();
            for (let offset = -3; offset <= 3; offset++) {
              const index = clampPage(page + offset);
              if (index >= 0 && index < state.pages.length) wanted.add(index);
            }
            for (const index of wanted) {
              if (preloads.has(index)) continue;
              const image = new Image();
              image.decoding = 'async';
              image.src = state.pages[index];
              preloads.set(index, image);
            }
            for (const index of Array.from(preloads.keys())) {
              if (!wanted.has(index)) preloads.delete(index);
            }
          };
          const applyClasses = () => {
            body.classList.remove(
              'mode-single', 'mode-double', 'mode-continuous',
              'direction-ltr', 'direction-rtl', 'fit-width', 'fit-height', 'fit-original');
            body.classList.add(`mode-${state.settings.display}`, `direction-${state.settings.direction}`, `fit-${state.settings.fit}`);
            stage.style.zoom = String(state.settings.scale);
            viewport.classList.toggle('drag-ready', state.settings.scale > 1.001 || state.settings.fit === 'original');
          };
          const closestContinuousPage = () => {
            const center = viewport.getBoundingClientRect().top + viewport.clientHeight / 2;
            let bestIndex = state.current;
            let bestDistance = Number.POSITIVE_INFINITY;
            for (const element of stage.querySelectorAll('.comic-page')) {
              const rect = element.getBoundingClientRect();
              const distance = Math.abs((rect.top + rect.bottom) / 2 - center);
              if (distance < bestDistance) {
                bestDistance = distance;
                bestIndex = Number.parseInt(element.dataset.pageIndex || '0', 10) || 0;
              }
            }
            return clampPage(bestIndex);
          };
          const handleContinuousScroll = () => {
            if (state.settings.display !== 'continuous' || state.scrollFrame) return;
            state.scrollFrame = requestAnimationFrame(() => {
              state.scrollFrame = 0;
              const page = closestContinuousPage();
              if (page !== state.current) {
                state.current = page;
                updateHud();
                preloadAround(page);
                postLocation();
              }
            });
          };
          const render = (scrollToCurrent = false) => {
            if (!state.ready) return false;
            applyClasses();
            stage.replaceChildren();
            if (state.settings.display === 'continuous') {
              for (let index = 0; index < state.pages.length; index++) {
                stage.append(pageElement(index, Math.abs(index - state.current) <= 2));
              }
              requestAnimationFrame(() => {
                if (scrollToCurrent) {
                  stage.querySelector(`[data-page-index="${state.current}"]`)?.scrollIntoView({ block: 'center' });
                }
              });
            } else {
              state.current = groupStart(state.current);
              for (const index of displayIndices(state.current)) stage.append(pageElement(index, true));
              viewport.scrollTo({ left: 0, top: 0 });
            }
            updateHud();
            preloadAround(state.current);
            postLocation();
            return true;
          };
          const goToPage = (page, smooth = true) => {
            if (!state.ready) {
              state.current = Math.max(0, Number.parseInt(page, 10) || 0);
              return false;
            }
            state.current = clampPage(page);
            if (state.settings.display === 'continuous') {
              const target = stage.querySelector(`[data-page-index="${state.current}"]`);
              target?.scrollIntoView({ block: 'center', behavior: smooth ? 'smooth' : 'auto' });
              updateHud();
              preloadAround(state.current);
              postLocation();
              return Boolean(target);
            }
            render(false);
            return true;
          };
          const turn = direction => {
            const stepDirection = Math.sign(direction);
            if (!stepDirection || !state.ready) return false;
            let target = state.current;
            if (state.settings.display === 'double') {
              const start = groupStart(state.current);
              if (state.settings.coverSingle && start === 0 && stepDirection > 0) target = 1;
              else if (state.settings.coverSingle && start === 1 && stepDirection < 0) target = 0;
              else target = start + stepDirection * 2;
            } else {
              target = state.current + stepDirection;
            }
            target = clampPage(target);
            if (groupStart(target) === groupStart(state.current) && target === state.current) return false;
            return goToPage(target);
          };
          const applySettings = value => {
            const previousMode = state.settings.display;
            state.settings = normalizeSettings(value);
            render(state.settings.display === 'continuous' || previousMode !== state.settings.display);
            return state.settings;
          };
          const setScale = value => {
            state.settings.scale = Math.max(.5, Math.min(3, Number(value) || 1));
            applyClasses();
            updateHud();
            post({ type: 'nogareader.comic-scale', scale: state.settings.scale });
            return state.settings.scale;
          };
          window.__nogareaderComic = { applySettings, goToPage, turn, setScale, postLocation };

          viewport.addEventListener('scroll', handleContinuousScroll, { passive: true });
          viewport.addEventListener('wheel', event => {
            if (!event.ctrlKey) return;
            event.preventDefault();
            setScale(state.settings.scale + (event.deltaY < 0 ? .1 : -.1));
          }, { passive: false });
          stage.addEventListener('dblclick', event => {
            if (event.target instanceof HTMLImageElement) setScale(state.settings.scale > 1.05 ? 1 : 1.65);
          });
          let drag = null;
          viewport.addEventListener('pointerdown', event => {
            if (event.button !== 0 || !(state.settings.scale > 1.001 || state.settings.fit === 'original')) return;
            drag = { x: event.clientX, y: event.clientY, left: viewport.scrollLeft, top: viewport.scrollTop };
            viewport.classList.add('dragging');
            viewport.setPointerCapture(event.pointerId);
            event.preventDefault();
          });
          viewport.addEventListener('pointermove', event => {
            if (!drag) return;
            viewport.scrollLeft = drag.left - (event.clientX - drag.x);
            viewport.scrollTop = drag.top - (event.clientY - drag.y);
          });
          const stopDrag = () => { drag = null; viewport.classList.remove('dragging'); };
          viewport.addEventListener('pointerup', stopDrag);
          viewport.addEventListener('pointercancel', stopDrag);

          fetch(manifestUrl, { cache: 'no-store', credentials: 'same-origin' })
            .then(response => {
              if (!response.ok) throw new Error(`漫画清单读取失败 (${response.status})`);
              return response.json();
            })
            .then(manifest => {
              if (!Array.isArray(manifest?.pages) || manifest.pages.length === 0) throw new Error('漫画没有可显示页面');
              state.pages = manifest.pages.map(value => String(value));
              state.current = clampPage(state.current);
              state.ready = true;
              body.classList.add('ready');
              render(true);
              post({ type: 'nogareader.comic-ready', pageCount: state.pages.length, pageIndex: state.current });
            })
            .catch(error => {
              body.classList.add('failed');
              errorPanel.textContent = error instanceof Error ? error.message : String(error);
              post({ type: 'nogareader.comic-error', message: errorPanel.textContent });
            });
        })();
        """;
}
