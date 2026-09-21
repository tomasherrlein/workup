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

  const closeSheet = () => {
    if (!sheet || sheet.hidden) return;
    sheet.hidden = true;
    openers.forEach((b) => b.setAttribute("aria-expanded", "false"));
    if (lastOpener) lastOpener.focus();
  };

  openers.forEach((button) => {
    button.addEventListener("click", () => {
      if (!sheet) return;
      lastOpener = button;
      sheet.hidden = false;
      openers.forEach((b) => b.setAttribute("aria-expanded", "true"));
      const first = sheet.querySelector("a, button");
      if (first) first.focus();
    });
  });

  if (sheet) {
    sheet.addEventListener("click", (event) => {
      if (event.target === sheet) closeSheet();
    });
  }

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") closeSheet();
  });

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
})();
