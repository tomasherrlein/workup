// WorkUp: small progressive enhancements. Every page must work without JavaScript.
(function () {
  "use strict";

  const live = document.getElementById("live-region");
  const announce = (message) => {
    if (!live) return;
    live.textContent = "";
    window.setTimeout(() => { live.textContent = message; }, 50);
  };

  // Owner navigation sheet ("Más" and the menu button).
  const sheet = document.getElementById("nav-sheet");
  const openers = document.querySelectorAll("[data-nav-open]");
  let lastOpener = null;

  // The sheet is a modal only while it is really on screen. The desktop media query hides the sheet (and its openers)
  // from 1024px up and shows the rail instead, so a viewport that grows with the sheet open keeps normal tabbing
  // instead of locking the keyboard inside a panel nobody can see.
  const sheetIsModal = () =>
    !!sheet && !sheet.hidden && window.getComputedStyle(sheet).display !== "none";

  // The controls a keyboard can actually reach, in document order: a disabled control or one that is not rendered is
  // skipped, exactly as the sequential focus navigation of the browser does.
  const sheetFocusables = () => {
    if (!sheet) return [];
    return Array.from(sheet.querySelectorAll("a, button")).filter((element) =>
      !element.hasAttribute("disabled")
      && !element.hidden
      && window.getComputedStyle(element).display !== "none");
  };

  const closeSheet = (restoreOpener = true) => {
    if (!sheet || sheet.hidden) return;
    sheet.hidden = true;
    openers.forEach((b) => b.setAttribute("aria-expanded", "false"));
    if (restoreOpener && lastOpener) lastOpener.focus();
  };

  // Where focus can land when there is no visible opener to return to: the page itself, which _OwnerLayout.cshtml
  // already made programmatically focusable with tabindex="-1".
  const focusPage = () => {
    const main = document.getElementById("main");
    if (!main || main.hidden || window.getComputedStyle(main).display === "none") return false;
    main.focus();
    return true;
  };

  openers.forEach((button) => {
    button.addEventListener("click", () => {
      if (!sheet) return;
      lastOpener = button;
      sheet.hidden = false;
      openers.forEach((b) => b.setAttribute("aria-expanded", "true"));
      const [first] = sheetFocusables();
      if (first) first.focus();
    });
  });

  if (sheet) {
    sheet.addEventListener("click", (event) => {
      if (event.target === sheet) closeSheet();
    });
  }

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      closeSheet();
      return;
    }
    // aria-modal="true" declares the rest of the page inert, but the browser still walks out of the panel with Tab, so
    // the wrap lives here: forward from the last reachable control, backward from the first one, and back into the
    // panel if the focus ever sits on the page behind it.
    if (event.key !== "Tab" || !sheetIsModal()) return;
    const focusables = sheetFocusables();
    if (focusables.length === 0) {
      // Nothing to focus inside: cancelling the Tab is what keeps the background from taking the focus at all.
      event.preventDefault();
      return;
    }
    const first = focusables[0];
    const last = focusables[focusables.length - 1];
    const inside = focusables.indexOf(event.target);
    if (event.shiftKey) {
      if (inside <= 0) {
        event.preventDefault();
        last.focus();
      }
      return;
    }
    if (inside === -1 || event.target === last) {
      event.preventDefault();
      first.focus();
    }
  });

  // Growing into the desktop rail layout hides the sheet with CSS (workup.css, `@media (min-width: 1024px)`), but the
  // DOM would stay logically open: `hidden` false, both openers still advertising aria-expanded="true" and the focus
  // parked in a panel nobody can see. Mirroring the stylesheet's own breakpoint, close the sheet on that transition and
  // hand the focus to the page, because the mobile opener that owned it is not on screen either -- focusing it would be
  // a no-op in the browser and would strand the keyboard. Escape and the backdrop keep restoring the real opener.
  const desktopQuery = typeof window.matchMedia === "function" ? window.matchMedia("(min-width: 1024px)") : null;
  if (desktopQuery) {
    const closeWhenDesktopTakesOver = (event) => {
      if (!event.matches || !sheet || sheet.hidden) return;
      closeSheet(false);
      if (focusPage()) return;
      const active = document.activeElement;
      if (active && typeof active.blur === "function") active.blur();
    };
    if (typeof desktopQuery.addEventListener === "function") desktopQuery.addEventListener("change", closeWhenDesktopTakesOver);
    else if (typeof desktopQuery.addListener === "function") desktopQuery.addListener(closeWhenDesktopTakesOver);
  }

  // <button data-dialog-open="id"> opens <dialog id="id" class="sheet">; [data-dialog-close] closes it.
  document.querySelectorAll("[data-dialog-open]").forEach((button) => {
    button.addEventListener("click", () => {
      const dialog = document.getElementById(button.dataset.dialogOpen);
      if (dialog && typeof dialog.showModal === "function") dialog.showModal();
    });
  });

  document.querySelectorAll("[data-dialog-close]").forEach((button) => {
    button.addEventListener("click", () => {
      const dialog = button.closest("dialog");
      if (dialog) dialog.close();
    });
  });

  // <button data-copy="text"> copies the text, for example a private link (RF-23).
  document.querySelectorAll("[data-copy]").forEach((button) => {
    button.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(button.dataset.copy);
        announce("Copiado.");
      } catch {
        announce("No se pudo copiar. Seleccioná el texto y copialo a mano.");
      }
    });
  });

  // <form data-confirm="message"> asks before submitting a destructive or irreversible action.
  document.querySelectorAll("form[data-confirm]").forEach((form) => {
    form.addEventListener("submit", (event) => {
      if (!window.confirm(form.dataset.confirm)) event.preventDefault();
    });
  });

  // <input data-slug-source> suggests a slug into <input data-slug-target> (RF-01), only until the person edits the
  // target by hand. The server is the source of truth for the format and for uniqueness either way.
  const slugSource = document.querySelector("[data-slug-source]");
  const slugTarget = document.querySelector("[data-slug-target]");
  if (slugSource && slugTarget) {
    let targetEdited = slugTarget.value.trim().length > 0;
    const slugify = (value) => value
      .normalize("NFD").replace(/[̀-ͯ]/g, "")
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-+|-+$/g, "");
    slugTarget.addEventListener("input", () => { targetEdited = true; });
    slugSource.addEventListener("input", () => {
      if (!targetEdited) slugTarget.value = slugify(slugSource.value);
    });
  }
})();
