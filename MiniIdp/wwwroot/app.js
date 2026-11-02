// Mini IDP dashboard. Plain JavaScript: poll the API every 2 seconds and update the page in place.
"use strict";

const POLL_MS = 2000;
const NAME_PATTERN = /^[a-z][a-z0-9-]{2,29}$/; // same rule the server enforces
const STEPS = ["Queued", "Generating", "Building", "Starting", "Running"];
const IN_PROGRESS = new Set(["Queued", "Generating", "Building", "Starting"]);

const $ = (id) => document.getElementById(id);
const els = {
  form: $("create-form"),
  nameInput: $("service-name"),
  createButton: $("create-button"),
  createError: $("create-error"),
  connection: $("connection-status"),
  loading: $("loading"),
  emptyState: $("empty-state"),
  list: $("service-list"),
  template: $("service-template"),
  logsPanel: $("logs-panel"),
  logsHeading: $("logs-heading"),
  logOutput: $("log-output"),
  closeLogs: $("close-logs"),
  tabs: document.querySelectorAll('[role="tab"]'),
};

const cards = new Map();     // service id -> <li> element (created once, then updated)
const services = new Map();  // service id -> latest data from the API
const deleting = new Set();  // ids with a DELETE request in flight
const logs = { serviceId: null, source: "provisioning", data: null };

// ---------------------------------------------------------------- API

async function api(method, url, body) {
  const response = await fetch(url, {
    method,
    headers: body ? { "Content-Type": "application/json" } : {},
    body: body ? JSON.stringify(body) : undefined,
  });
  if (response.status === 204) return null;
  const data = await response.json().catch(() => null);
  if (!response.ok) throw new Error(data?.error ?? `Request failed (HTTP ${response.status})`);
  return data;
}

// ---------------------------------------------------------------- create

els.form.addEventListener("submit", async (event) => {
  event.preventDefault();
  const name = els.nameInput.value.trim();

  if (!NAME_PATTERN.test(name)) {
    showCreateError(name ? `"${name}" is not a valid service name. Check the rules above.` : "Enter a service name.");
    return;
  }

  showCreateError("");
  setCreating(true);
  try {
    await api("POST", "/api/services", { name });
    els.nameInput.value = "";
    await refreshServices();
  } catch (error) {
    showCreateError(error.message);
  } finally {
    setCreating(false);
    els.nameInput.focus();
  }
});

els.nameInput.addEventListener("input", () => showCreateError(""));

function setCreating(isCreating) {
  // Disabling the button prevents double submissions while the request is in flight.
  els.createButton.disabled = isCreating;
  els.createButton.textContent = isCreating ? "Creating…" : "Create Service";
}

function showCreateError(message) {
  els.createError.textContent = message;
  els.createError.hidden = !message;
  els.nameInput.setAttribute("aria-invalid", message ? "true" : "false");
}

// ---------------------------------------------------------------- service list

async function refreshServices() {
  const list = await api("GET", "/api/services");
  els.connection.textContent = "";
  els.loading.hidden = true;
  els.emptyState.hidden = list.length > 0;

  const seen = new Set();
  list.forEach((service, index) => {
    seen.add(service.id);
    services.set(service.id, service);
    let card = cards.get(service.id);
    if (!card) {
      card = createCard(service.id);
      cards.set(service.id, card);
    }
    // Only move a card when it is out of place; moving a node would drop keyboard focus.
    if (els.list.children[index] !== card) els.list.insertBefore(card, els.list.children[index] ?? null);
    updateCard(card, service);
  });

  for (const [id, card] of cards) {
    if (!seen.has(id)) {
      card.remove();
      cards.delete(id);
      services.delete(id);
      if (logs.serviceId === id) closeLogs();
    }
  }
}

function createCard(id) {
  const card = els.template.content.firstElementChild.cloneNode(true);
  card.querySelector(".logs-button").addEventListener("click", () => openLogs(id));
  card.querySelector(".delete-button").addEventListener("click", () => deleteService(id));
  return card;
}

/** Updates a card in place. setText/setAttr skip unchanged values so nothing flickers. */
function updateCard(card, service) {
  const status = service.status;
  setText(card.querySelector(".service-name"), service.name);

  const badge = card.querySelector(".badge");
  setText(badge, status);
  badge.className = "badge " + badgeClass(status);

  const created = card.querySelector(".created");
  setAttr(created, "datetime", service.createdAt);
  setText(created, formatTime(service.createdAt));

  // Progress steps: everything before the current status is done.
  const steps = card.querySelector(".steps");
  const currentIndex = STEPS.indexOf(status);
  steps.hidden = currentIndex === -1; // Failed / Stopped show an error message instead
  steps.querySelectorAll("li").forEach((li, i) => {
    const done = i < currentIndex || status === "Running";
    li.className = done ? "done" : i === currentIndex ? "current" : "";
    if (i === currentIndex && status !== "Running") li.setAttribute("aria-current", "step");
    else li.removeAttribute("aria-current");
  });

  const urlRow = card.querySelector(".service-url");
  urlRow.hidden = !service.url;
  if (service.url) {
    const link = urlRow.querySelector("a");
    setAttr(link, "href", service.url);
    setText(link, service.url);
  }

  const errorRow = card.querySelector(".service-error");
  errorRow.hidden = !service.errorMessage;
  setText(errorRow, service.errorMessage ?? "");

  // Delete is only possible when the worker is not busy with this service.
  const deleteButton = card.querySelector(".delete-button");
  const isDeleting = deleting.has(service.id);
  deleteButton.disabled = IN_PROGRESS.has(status) || isDeleting;
  setText(deleteButton, isDeleting ? "Deleting…" : "Delete");
  setAttr(deleteButton, "title", IN_PROGRESS.has(status) ? "Available once provisioning has finished" : "");
  setAttr(deleteButton, "aria-label", `Delete ${service.name}`);
  setAttr(card.querySelector(".logs-button"), "aria-label", `View logs for ${service.name}`);

  card.classList.toggle("selected", logs.serviceId === service.id);
}

function badgeClass(status) {
  if (IN_PROGRESS.has(status)) return "in-progress";
  if (status === "Running") return "running";
  if (status === "Failed") return "failed";
  return "";
}

// ---------------------------------------------------------------- delete

async function deleteService(id) {
  const service = services.get(id);
  if (!service) return;
  const ok = confirm(`Delete "${service.name}"?\n\nThis stops and removes its container and image, ` +
    `and deletes its generated code and logs.`);
  if (!ok) return;

  const card = cards.get(id);
  const errorRow = card.querySelector(".action-error");
  errorRow.hidden = true;
  deleting.add(id);
  updateCard(card, service);

  try {
    await api("DELETE", `/api/services/${id}`);
    await refreshServices();
  } catch (error) {
    errorRow.textContent = error.message;
    errorRow.hidden = false;
  } finally {
    deleting.delete(id);
    if (cards.has(id)) updateCard(cards.get(id), services.get(id));
  }
}

// ---------------------------------------------------------------- logs

function openLogs(id) {
  logs.serviceId = id;
  logs.data = null;
  els.logsPanel.hidden = false;
  els.logOutput.textContent = "Loading logs…";
  setText(els.logsHeading, `Logs — ${services.get(id)?.name ?? ""}`);
  for (const [cardId, card] of cards) card.classList.toggle("selected", cardId === id);
  refreshLogs({ scrollToEnd: true });
  els.logsPanel.scrollIntoView({ behavior: "smooth", block: "nearest" });
}

function closeLogs() {
  logs.serviceId = null;
  els.logsPanel.hidden = true;
  for (const card of cards.values()) card.classList.remove("selected");
}

els.closeLogs.addEventListener("click", closeLogs);

els.tabs.forEach((tab) => {
  tab.addEventListener("click", () => selectTab(tab.dataset.source));
  // Left/right arrow keys move between tabs, as screen-reader users expect.
  tab.addEventListener("keydown", (event) => {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
    const other = [...els.tabs].find((t) => t !== tab);
    other.focus();
    selectTab(other.dataset.source);
  });
});

function selectTab(source) {
  logs.source = source;
  els.tabs.forEach((tab) => {
    const selected = tab.dataset.source === source;
    tab.setAttribute("aria-selected", String(selected));
    tab.tabIndex = selected ? 0 : -1;
    if (selected) els.logOutput.setAttribute("aria-labelledby", tab.id);
  });
  showLogs({ scrollToEnd: true });
}

async function refreshLogs({ scrollToEnd = false } = {}) {
  const id = logs.serviceId;
  if (!id) return;
  try {
    const data = await api("GET", `/api/services/${id}/logs`);
    if (logs.serviceId !== id) return; // user switched to another service meanwhile
    logs.data = data;
  } catch (error) {
    logs.data = { provisioning: `Could not load logs: ${error.message}`, runtime: null, runtimeMessage: error.message };
  }
  showLogs({ scrollToEnd });
}

function showLogs({ scrollToEnd = false } = {}) {
  const data = logs.data;
  if (!data) return;
  const text = logs.source === "provisioning"
    ? data.provisioning || "No provisioning output yet."
    : data.runtime ?? data.runtimeMessage ?? "No container output.";
  const shown = text || "The container has not written any output yet.";

  const pre = els.logOutput;
  if (pre.textContent === shown && !scrollToEnd) return;
  // Follow new output only if the reader is already at the bottom; otherwise keep their place.
  const wasAtBottom = pre.scrollHeight - pre.scrollTop - pre.clientHeight < 40;
  pre.textContent = shown;
  if (scrollToEnd || wasAtBottom) pre.scrollTop = pre.scrollHeight;
}

// ---------------------------------------------------------------- helpers & polling

function setText(el, value) {
  if (el.textContent !== value) el.textContent = value;
}

function setAttr(el, name, value) {
  if (el.getAttribute(name) !== value) el.setAttribute(name, value);
}

const timeFormat = new Intl.DateTimeFormat("en", { dateStyle: "medium", timeStyle: "short" });
function formatTime(iso) {
  return timeFormat.format(new Date(iso));
}

async function poll() {
  try {
    await refreshServices();
    await refreshLogs();
  } catch {
    els.connection.textContent = "Cannot reach the Mini IDP server. Retrying…";
  } finally {
    // setTimeout after each round (not setInterval) so slow requests never pile up.
    setTimeout(poll, POLL_MS);
  }
}

poll();
