import { InteractionRequiredAuthError, PublicClientApplication, type AccountInfo } from "@azure/msal-browser";

type Config = { tenantId: string; clientId: string; scope: string; apiBaseUrl: string };
type Component = { id: string; partNumber: string; name: string; description: string; physicalStock: number; reservedStock: number; availableStock: number };
type Recipe = { componentId: string; partNumber?: string; quantityPerBoard: number };
type Board = { id: string; partNumber: string; revision: number; name: string; description: string; lengthMm: number; widthMm: number; recipe: Recipe[] };
type OrderLine = { boardId: string; revision: number; buildQuantity: number };
type Order = { id: string; name: string; description: string; orderDate: string; status: "Reserved" | "Started"; boards: OrderLine[] };
type RecordItem = Component | Board | Order;
type Tab = "components" | "boards" | "orders";

const $ = <T extends HTMLElement>(id: string): T => {
  const node = document.getElementById(id);
  if (!node) throw new Error(`Missing element ${id}`);
  return node as T;
};
const node = <K extends keyof HTMLElementTagNameMap>(tag: K, className = "", value?: string): HTMLElementTagNameMap[K] => {
  const element = document.createElement(tag);
  element.className = className;
  if (value !== undefined) element.textContent = value;
  return element;
};
const add = (parent: HTMLElement, ...children: HTMLElement[]) => { children.forEach(child => parent.append(child)); return parent; };
const number = (value: number) => new Intl.NumberFormat().format(value);
const labels: Record<Tab, { title: string; description: string; singular: string }> = {
  components: { title: "Components", description: "Parts, physical stock, and reservations.", singular: "component" },
  boards: { title: "Boards", description: "Versioned recipes for production.", singular: "board" },
  orders: { title: "Orders", description: "Reserve stock and create production handoffs.", singular: "order" }
};

let config: Config;
let auth: PublicClientApplication;
let account: AccountInfo | null = null;
let tab: Tab = "components";
let items: RecordItem[] = [];
let selectedId: string | null = null;
let components: Component[] = [];
let boards: Board[] = [];
let boardRevisions: Board[] = [];
let requestNumber = 0;

function notice(message: string, error = false) {
  const target = $("notice");
  target.textContent = message;
  target.classList.toggle("error", error);
}

async function token(): Promise<string> {
  if (!account) throw new Error("Sign in to continue.");
  try {
    return (await auth.acquireTokenSilent({ account, scopes: [config.scope], redirectUri: `${location.origin}/auth.html` })).accessToken;
  } catch (error) {
    if (error instanceof InteractionRequiredAuthError) {
      return (await auth.acquireTokenPopup({ account, scopes: [config.scope], redirectUri: `${location.origin}/auth.html` })).accessToken;
    }
    throw error;
  }
}

async function apiResponse(method: string, path: string, body?: unknown): Promise<Response> {
  const response = await fetch(`${config.apiBaseUrl}/api${path}`, {
    method,
    headers: { Authorization: `Bearer ${await token()}`, ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
    body: body === undefined ? undefined : JSON.stringify(body),
    cache: "no-store"
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { message?: string } | null;
    throw new Error(problem?.message ?? `The API returned ${response.status}.`);
  }
  return response;
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await apiResponse(method, path, body);
  return response.status === 204 ? undefined as T : await response.json() as T;
}

async function refreshCatalog() {
  if (tab === "boards") components = await request<Component[]>("GET", "/components");
  if (tab === "orders") {
    boards = await request<Board[]>("GET", "/boards");
    boardRevisions = (await Promise.all(boards.map(board =>
      request<Board[]>("GET", `/boards/${board.id}/revisions`)))).flat();
  }
}

async function load(preferredId: string | null = selectedId) {
  const sequence = ++requestNumber;
  const currentTab = tab;
  notice("Loading…");
  try {
    await refreshCatalog();
    const query = $("search") as HTMLInputElement;
    const data = await request<RecordItem[]>("GET", `/${currentTab}?q=${encodeURIComponent(query.value.trim())}`);
    if (sequence !== requestNumber || currentTab !== tab) return;
    items = data;
    selectedId = preferredId && items.some(item => item.id === preferredId) ? preferredId : null;
    renderList();
    renderDetail();
    notice("");
  } catch (error) {
    if (sequence === requestNumber) notice(message(error), true);
  }
}

function message(error: unknown): string { return error instanceof Error ? error.message : "Something went wrong."; }

function renderList() {
  const list = $("list");
  list.replaceChildren();
  if (!items.length) { add(list, node("p", "empty", "No records found. Add one to get started.")); return; }
  for (const item of items) {
    const button = node("button", `record${item.id === selectedId ? " selected" : ""}`);
    button.type = "button";
    add(button, node("strong", "", item.name));
    const summary = tab === "components"
      ? `${(item as Component).partNumber} · ${number((item as Component).availableStock)} available`
      : tab === "boards" ? `${(item as Board).partNumber} · Revision ${(item as Board).revision}`
      : `${(item as Order).orderDate} · ${(item as Order).status}`;
    add(button, node("span", "meta", summary));
    button.onclick = () => { selectedId = item.id; renderList(); renderDetail(); };
    add(list, button);
  }
}

function field(form: HTMLFormElement, labelText: string, name: string, value: string | number, options: {
  type?: string; min?: string; step?: string; readonly?: boolean; full?: boolean; required?: boolean
} = {}) {
  const label = node("label", `field${options.full ? " full" : ""}`);
  add(label, node("span", "", labelText));
  const input = name === "description" ? node("textarea") : node("input");
  input.name = name;
  input.value = String(value);
  input.required = options.required ?? true;
  if (input instanceof HTMLInputElement) {
    input.type = options.type ?? "text";
    if (options.min) input.min = options.min;
    if (options.step) input.step = options.step;
    input.readOnly = !!options.readonly;
  }
  add(label, input);
  add(form, label);
  return input;
}

function formGrid() { return node("form", "form-grid") as HTMLFormElement; }
function formData(form: HTMLFormElement, key: string): string { return String(new FormData(form).get(key) ?? "").trim(); }

function actions(detail: HTMLElement, save: () => Promise<void>, item: RecordItem | null, canDelete = true) {
  const bar = node("div", "actions");
  const saveButton = node("button", "primary", item ? "Save changes" : "Create");
  saveButton.type = "button";
  saveButton.onclick = () => void save();
  add(bar, saveButton);
  if (item && canDelete) {
    const del = node("button", "danger", "Delete");
    del.type = "button";
    del.onclick = () => void remove(item);
    add(bar, del);
  }
  add(detail, bar);
}

async function saveItem(item: RecordItem | null, body: unknown, form: HTMLFormElement) {
  if (!form.reportValidity()) return;
  try {
    const saved = await request<RecordItem>(item ? "PUT" : "POST", item ? `/${tab}/${item.id}` : `/${tab}`, body);
    selectedId = saved.id;
    ($("search") as HTMLInputElement).value = "";
    await load(saved.id);
    notice(item ? "Changes saved." : `${labels[tab].singular} created.`);
  } catch (error) { notice(message(error), true); }
}

async function remove(item: RecordItem) {
  if (!window.confirm(`Delete ${item.name}? This cannot be undone.`)) return;
  try {
    await request<void>("DELETE", `/${tab}/${item.id}`);
    selectedId = null;
    await load(null);
    notice(`${labels[tab].singular} deleted.`);
  } catch (error) { notice(message(error), true); }
}

function renderComponent(detail: HTMLElement, item: Component | null) {
  const form = formGrid();
  field(form, "Part number", "partNumber", item?.partNumber ?? "");
  field(form, "Name", "name", item?.name ?? "");
  field(form, "Description", "description", item?.description ?? "", { full: true });
  field(form, "Physical stock", "physicalStock", item?.physicalStock ?? 0, { type: "number", min: "0", step: "1" });
  add(detail, form);
  if (item) add(detail, node("p", "summary", `${number(item.reservedStock)} reserved · ${number(item.availableStock)} available`));
  actions(detail, () => saveItem(item, {
    partNumber: formData(form, "partNumber"), name: formData(form, "name"),
    description: formData(form, "description"), physicalStock: Number(formData(form, "physicalStock"))
  }, form), item);
}

function lineEditor(container: HTMLElement, options: { value: string; label: string }[], selected: string, quantity: number,
  quantityLabel: string) {
  const line = node("div", "line");
  const selectLabel = node("label", "field");
  add(selectLabel, node("span", "", "Item"));
  const select = node("select");
  select.required = true;
  for (const option of options) {
    const entry = node("option", "", option.label);
    entry.value = option.value;
    select.add(entry);
  }
  select.value = selected || options[0]?.value || "";
  add(selectLabel, select);
  const qtyLabel = node("label", "field");
  add(qtyLabel, node("span", "", quantityLabel));
  const qty = node("input"); qty.type = "number"; qty.min = "1"; qty.step = "1"; qty.required = true; qty.value = String(quantity);
  add(qtyLabel, qty);
  const del = node("button", "quiet", "Remove"); del.type = "button"; del.onclick = () => line.remove();
  add(line, selectLabel, qtyLabel, del); add(container, line);
}

function collectLines(container: HTMLElement) {
  return [...container.querySelectorAll<HTMLElement>(".line")].map(line => ({
    value: line.querySelector("select")?.value ?? "", quantity: Number(line.querySelector("input")?.value)
  }));
}

function renderBoard(detail: HTMLElement, item: Board | null) {
  const form = formGrid();
  field(form, "Part number", "partNumber", item?.partNumber ?? "", { readonly: !!item });
  field(form, "Name", "name", item?.name ?? "");
  field(form, "Description", "description", item?.description ?? "", { full: true });
  field(form, "Length (mm)", "lengthMm", item?.lengthMm ?? "", { type: "number", min: "0.001", step: "any" });
  field(form, "Width (mm)", "widthMm", item?.widthMm ?? "", { type: "number", min: "0.001", step: "any" });
  add(detail, form);
  const heading = node("div", "section-title");
  add(heading, node("h3", "", "Recipe"));
  const addLine = node("button", "quiet", "Add component"); addLine.type = "button";
  add(heading, addLine); add(detail, heading);
  const lines = node("div"); add(detail, lines);
  const options = components.map(component => ({ value: component.id, label: `${component.partNumber} — ${component.name}` }));
  const append = (componentId = "", quantity = 1) => lineEditor(lines, options, componentId, quantity, "Per board");
  item?.recipe.forEach(line => append(line.componentId, line.quantityPerBoard));
  addLine.onclick = () => append();
  if (!components.length) add(detail, node("p", "callout", "Create a component before creating a board."));
  if (item) add(detail, node("p", "summary", `Current revision: ${item.revision}. Saving creates a new revision; existing orders keep their selected revision.`));
  actions(detail, async () => {
    if (!form.reportValidity()) return;
    const recipe = collectLines(lines);
    if (!recipe.length || recipe.some(line => !line.value || line.quantity < 1 || !Number.isInteger(line.quantity))) {
      notice("Add at least one component with a positive whole quantity.", true); return;
    }
    await saveItem(item, { ...(item ? {} : { partNumber: formData(form, "partNumber") }),
      name: formData(form, "name"), description: formData(form, "description"),
      lengthMm: Number(formData(form, "lengthMm")), widthMm: Number(formData(form, "widthMm")),
      recipe: recipe.map(line => ({ componentId: line.value, quantityPerBoard: line.quantity })) }, form);
  }, item);
  if (item) {
    const historyButton = node("button", "quiet", "Show revision history");
    const history = node("ul", "history"); history.hidden = true;
    historyButton.onclick = async () => {
      if (!history.hidden) { history.hidden = true; historyButton.textContent = "Show revision history"; return; }
      try {
        const revisions = await request<Board[]>("GET", `/boards/${item.id}/revisions`);
        history.replaceChildren(...revisions.map(revision => node("li", "", `Revision ${revision.revision} · ${revision.name} · ${revision.lengthMm} × ${revision.widthMm} mm · ${revision.recipe.length} components`)));
        history.hidden = false; historyButton.textContent = "Hide revision history";
      } catch (error) { notice(message(error), true); }
    };
    add(detail, historyButton, history);
  }
}

async function download(order: Order) {
  if (order.status === "Reserved" && !window.confirm("Start production and download the handoff? This consumes the reserved stock and prevents further edits or deletion.")) return;
  try {
    const response = await apiResponse("POST", `/orders/${order.id}/download`);
    const url = URL.createObjectURL(await response.blob());
    const link = node("a"); link.href = url; link.download = `smt-order-${order.id}.json`;
    document.body.append(link); link.click(); link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 60_000);
    await load(order.id);
    notice("Production handoff downloaded.");
  } catch (error) { notice(message(error), true); }
}

function renderOrder(detail: HTMLElement, item: Order | null) {
  if (item?.status === "Started") {
    add(detail, node("span", "badge started", "Started"));
    add(detail, node("p", "summary", `${item.orderDate} · ${item.description}`));
    const list = node("ul", "history");
    item.boards.forEach(line => add(list, node("li", "", `${boards.find(board => board.id === line.boardId)?.partNumber ?? line.boardId} · revision ${line.revision} · ${number(line.buildQuantity)} boards`)));
    add(detail, list);
    const button = node("button", "primary", "Download handoff again"); button.onclick = () => void download(item); add(detail, button);
    return;
  }
  const form = formGrid();
  field(form, "Name", "name", item?.name ?? "");
  field(form, "Order date", "orderDate", item?.orderDate ?? new Date().toISOString().slice(0, 10), { type: "date" });
  field(form, "Description", "description", item?.description ?? "", { full: true });
  add(detail, form);
  const heading = node("div", "section-title"); add(heading, node("h3", "", "Board lines"));
  const addLine = node("button", "quiet", "Add board"); addLine.type = "button"; add(heading, addLine); add(detail, heading);
  const lines = node("div"); add(detail, lines);
  const options = boardRevisions.map(board => ({ value: `${board.id}/${board.revision}`, label: `${board.partNumber} — revision ${board.revision}` }));
  const append = (boardId = "", revision = 0, quantity = 1) => lineEditor(lines, options, boardId ? `${boardId}/${revision}` : "", quantity, "Build quantity");
  item?.boards.forEach(line => append(line.boardId, line.revision, line.buildQuantity));
  addLine.onclick = () => append();
  if (!boards.length) add(detail, node("p", "callout", "Create a board before creating an order."));
  if (item) add(detail, node("p", "callout", "Saving updates the stock reservation. The first production download consumes stock and locks this order."));
  actions(detail, async () => {
    if (!form.reportValidity()) return;
    const selected = collectLines(lines);
    if (!selected.length || selected.some(line => !line.value || line.quantity < 1 || !Number.isInteger(line.quantity))) {
      notice("Add at least one board with a positive whole build quantity.", true); return;
    }
    await saveItem(item, { name: formData(form, "name"), description: formData(form, "description"),
      orderDate: formData(form, "orderDate"), boards: selected.map(line => {
        const [boardId, revision] = line.value.split("/");
        return { boardId, revision: Number(revision), buildQuantity: line.quantity };
      }) }, form);
  }, item);
  if (item) {
    const button = node("button", "quiet", "Start production & download handoff");
    button.onclick = () => void download(item); add(detail, button);
  }
}

function renderDetail(create = false) {
  const detail = $("detail"); detail.replaceChildren();
  const item = create ? null : items.find(record => record.id === selectedId) ?? null;
  if (!create && !item) { add(detail, node("h2", "", "Select a record"), node("p", "hint", `Choose a ${labels[tab].singular} from the list or add a new one.`)); return; }
  add(detail, node("h2", "", item ? item.name : `New ${labels[tab].singular}`));
  if (tab === "components") renderComponent(detail, item as Component | null);
  if (tab === "boards") renderBoard(detail, item as Board | null);
  if (tab === "orders") renderOrder(detail, item as Order | null);
}

function selectTab(next: Tab) {
  tab = next; selectedId = null; items = [];
  ($("search") as HTMLInputElement).value = "";
  $("page-title").textContent = labels[tab].title;
  $("page-subtitle").textContent = labels[tab].description;
  $("add").textContent = `Add ${labels[tab].singular}`;
  document.querySelectorAll<HTMLButtonElement>("[data-tab]").forEach(button => button.classList.toggle("active", button.dataset.tab === tab));
  $("list").replaceChildren(); $("detail").replaceChildren();
  void load(null);
}

async function start() {
  try {
    const response = await fetch("/config.json", { cache: "no-store" });
    if (!response.ok) throw new Error("Web configuration is unavailable.");
    config = await response.json() as Config;
    auth = new PublicClientApplication({ auth: { clientId: config.clientId,
      authority: `https://login.microsoftonline.com/${config.tenantId}`, redirectUri: `${location.origin}/auth.html` },
      cache: { cacheLocation: "sessionStorage" } });
    await auth.initialize();
    account = auth.getActiveAccount() ?? auth.getAllAccounts()[0] ?? null;
    if (account) auth.setActiveAccount(account);
    showAccount();
    ($("sign-in") as HTMLButtonElement).onclick = async () => {
      try {
        const result = await auth.loginPopup({ scopes: [config.scope], redirectUri: `${location.origin}/auth.html` });
        account = result.account; auth.setActiveAccount(account); showAccount();
      } catch (error) { $("sign-in-error").textContent = message(error); }
    };
    ($("sign-out") as HTMLButtonElement).onclick = async () => {
      try { await auth.logoutPopup({ account: account ?? undefined, postLogoutRedirectUri: `${location.origin}/auth.html` });
        account = null; showAccount(); } catch (error) { notice(message(error), true); }
    };
    ($("add") as HTMLButtonElement).onclick = () => { selectedId = null; renderList(); renderDetail(true); };
    ($("refresh") as HTMLButtonElement).onclick = () => void load();
    let timer: number | undefined;
    ($("search") as HTMLInputElement).oninput = () => { clearTimeout(timer); timer = window.setTimeout(() => void load(null), 250); };
    document.querySelectorAll<HTMLButtonElement>("[data-tab]").forEach(button => button.onclick = () => selectTab(button.dataset.tab as Tab));
  } catch (error) {
    $("sign-in-view").hidden = true; $("app-view").hidden = true; $("fatal").hidden = false;
    $("fatal-message").textContent = message(error);
  }
}
function showAccount() {
  $("sign-in-view").hidden = !!account;
  $("app-view").hidden = !account;
  $("account-name").textContent = account?.username ?? "";
  ($("sign-out") as HTMLButtonElement).hidden = !account;
  if (account) selectTab("components");
}
void start();
