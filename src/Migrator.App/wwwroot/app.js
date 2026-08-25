const $ = (id) => document.getElementById(id);
const api = async (url, options) => {
  const response = await fetch(url, options);
  if (!response.ok) {
    let detail = response.statusText;
    try { const body = await response.json(); detail = body.detail || body.error || detail; } catch {}
    throw new Error(detail);
  }
  return response.status === 204 ? null : response.json();
};

let servers = [];
let editingId = null;

// ── Sunucular ───────────────────────────────────────────────────────────────

async function loadServers() {
  servers = await api("/api/servers");
  renderServers();
  fillServerSelects();
}

function renderServers() {
  const host = $("serverList");
  if (!servers.length) {
    host.innerHTML = `<p class="hint">Henüz sunucu yok. Aşağıdan ekle.</p>`;
    return;
  }
  host.innerHTML = "";
  for (const s of servers) {
    const row = document.createElement("div");
    row.className = "server";
    row.innerHTML =
      `<span class="grow"><strong></strong> <span class="kind"></span></span>
       <button data-edit="${s.id}">Düzenle</button>
       <button data-del="${s.id}">Sil</button>`;
    row.querySelector("strong").textContent = s.name;
    row.querySelector(".kind").textContent =
      `${s.kind === "SqlServer" ? "SQL Server" : "PostgreSQL"} · ${s.host}:${s.port} · ${s.user || "—"}`;
    host.appendChild(row);
  }
  host.querySelectorAll("[data-edit]").forEach(b =>
    b.onclick = () => startEdit(b.dataset.edit));
  host.querySelectorAll("[data-del]").forEach(b =>
    b.onclick = async () => {
      if (!confirm("Bu sunucu kaydı silinsin mi?")) return;
      await api(`/api/servers/${b.dataset.del}`, { method: "DELETE" });
      await loadServers();
    });
}

function startEdit(id) {
  const s = servers.find(x => x.id === id);
  if (!s) return;
  editingId = s.id;
  $("fName").value = s.name; $("fKind").value = s.kind;
  $("fHost").value = s.host; $("fPort").value = s.port; $("fUser").value = s.user;
  $("fPass").value = "";
  $("serverForm").open = true;
  setMsg("serverMsg", "Düzenleniyor — şifreyi boş bırakırsan değişmez.");
}

function clearForm() {
  editingId = null;
  ["fName", "fHost", "fUser", "fPass"].forEach(id => $(id).value = "");
  $("fKind").value = "SqlServer"; $("fPort").value = 1433;
  setMsg("serverMsg", "");
}

$("fKind").onchange = () => {
  // Port varsayılanı türe göre; kullanıcı elle değiştirmişse dokunma.
  const port = $("fPort");
  if (port.value === "1433" || port.value === "5432" || !port.value)
    port.value = $("fKind").value === "SqlServer" ? 1433 : 5432;
};

$("btnSaveServer").onclick = async () => {
  try {
    await api("/api/servers", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        id: editingId,
        name: $("fName").value,
        kind: $("fKind").value,
        host: $("fHost").value,
        port: Number($("fPort").value) || 0,
        user: $("fUser").value,
        password: $("fPass").value,
      }),
    });
    clearForm();
    await loadServers();
    setMsg("serverMsg", "Kaydedildi.", "ok");
  } catch (e) { setMsg("serverMsg", e.message, "err"); }
};

$("btnClearForm").onclick = clearForm;

function fillServerSelects() {
  const fill = (select, kind) => {
    const previous = select.value;
    select.innerHTML = `<option value="">— seç —</option>`;
    servers.filter(s => s.kind === kind).forEach(s => {
      const option = document.createElement("option");
      option.value = s.id; option.textContent = `${s.name} (${s.host}:${s.port})`;
      select.appendChild(option);
    });
    if ([...select.options].some(o => o.value === previous)) select.value = previous;
  };
  fill($("srcServer"), "SqlServer");
  fill($("tgtServer"), "PostgreSql");
}

// ── Veritabanı listeleri ────────────────────────────────────────────────────

async function loadDatabases(serverId, datalistId, msgId) {
  const list = $(datalistId);
  list.innerHTML = "";
  if (!serverId) { setMsg(msgId, ""); return []; }
  setMsg(msgId, "Veritabanları okunuyor…");
  try {
    const databases = await api(`/api/servers/${serverId}/databases`);
    databases.forEach(name => {
      const option = document.createElement("option");
      option.value = name;
      list.appendChild(option);
    });
    setMsg(msgId, `${databases.length} veritabanı bulundu — yazarak filtreleyebilirsin.`, "ok");
    return databases;
  } catch (e) {
    setMsg(msgId, e.message, "err");
    return [];
  }
}

$("srcServer").onchange = () => loadDatabases($("srcServer").value, "srcDbList", "srcMsg");
$("tgtServer").onchange = () => loadDatabases($("tgtServer").value, "tgtDbList", "tgtMsg");

// Hedef adı varsayılan olarak kaynakla aynı gelir; kullanıcı değiştirdiyse ona dokunmayız.
let targetNameTouched = false;
$("tgtDb").oninput = () => { targetNameTouched = true; };
$("srcDbFilter").onchange = () => {
  if (!targetNameTouched) $("tgtDb").value = $("srcDbFilter").value;
};

// ── Taşıma ──────────────────────────────────────────────────────────────────

function setMsg(id, text, kind) {
  const element = $(id);
  element.textContent = text;
  element.className = "msg" + (kind ? " " + kind : "");
}

function appendLog(kind, text) {
  const line = document.createElement("span");
  line.className = kind;
  line.textContent = text + "\n";
  $("log").appendChild(line);
  $("log").scrollTop = $("log").scrollHeight;
}

$("btnRun").onclick = async () => {
  const body = {
    sourceServerId: $("srcServer").value,
    sourceDatabase: $("srcDbFilter").value.trim(),
    targetServerId: $("tgtServer").value,
    targetDatabase: $("tgtDb").value.trim(),
    targetIcuLocale: $("tgtLocale").value.trim(),
    allowSourceOnly: $("optSourceOnly").checked,
    allowSchemaRisk: $("optSchemaRisk").checked,
    allowCollationMismatch: $("optCollation").checked,
    verifyOnly: $("optVerifyOnly").checked,
  };
  if (!body.sourceServerId || !body.sourceDatabase) return setMsg("runState", "Kaynak seç.", "err");
  if (!body.targetServerId || !body.targetDatabase) return setMsg("runState", "Hedef seç.", "err");

  $("log").textContent = "";
  $("btnRun").disabled = true;
  setMsg("runState", "Çalışıyor…");

  try {
    const { jobId } = await api("/api/migrate", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    await followJob(jobId);
  } catch (e) {
    setMsg("runState", e.message, "err");
  } finally {
    $("btnRun").disabled = false;
  }
};

async function followJob(jobId) {
  let cursor = 0;
  for (;;) {
    const state = await api(`/api/jobs/${jobId}?from=${cursor}`);
    state.messages.forEach(m => appendLog(m.kind, m.text));
    cursor = state.next;
    if (state.done) {
      setMsg("runState", state.summary, state.succeeded ? "ok" : "err");
      appendLog(state.succeeded ? "Success" : "Error",
        (state.succeeded ? "✔ " : "✖ ") + state.summary);
      return;
    }
    await new Promise(r => setTimeout(r, 500));
  }
}

loadServers().catch(e => setMsg("serverMsg", e.message, "err"));
