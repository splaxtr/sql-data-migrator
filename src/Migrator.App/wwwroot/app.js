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

// ── Servers ─────────────────────────────────────────────────────────────────

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
  // Port default follows the kind; hands off once the user changed it.
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

// ── Source database picker ──────────────────────────────────────────────────

let sourceDatabases = [];
let visibleCount = 0;
const selectedDatabases = new Set();
// Only holds databases whose target name was typed by hand; everything else follows the
// pattern, so editing the pattern keeps working for the rows nobody has touched.
const targetNames = new Map();

function targetFor(database) {
  if (targetNames.has(database)) return targetNames.get(database);
  return ($("tgtPattern").value.trim() || "{db}").replaceAll("{db}", database);
}

// Turkish casing on purpose: with the invariant rules "I" lower-cases to "i", so filtering
// "İSTANBUL" by typing "i" would miss it.
const fold = (value) => value.toLocaleLowerCase("tr");

function visibleDatabases() {
  const needle = fold($("srcFilter").value.trim());
  return sourceDatabases.filter(database => fold(database).includes(needle));
}

function renderDatabases() {
  const host = $("dbList");
  host.innerHTML = "";
  const visible = visibleDatabases();
  visibleCount = visible.length;

  for (const database of visible) {
    const row = document.createElement("div");
    row.className = "db-row";

    // The checkbox and the name share a label so the whole name is clickable; the target
    // input stays outside it, or typing in it would toggle the checkbox.
    const pick = document.createElement("label");
    pick.className = "db-pick";
    const check = document.createElement("input");
    check.type = "checkbox";
    check.checked = selectedDatabases.has(database);
    check.onchange = () => {
      if (check.checked) selectedDatabases.add(database); else selectedDatabases.delete(database);
      showSelection();
    };
    const name = document.createElement("span");
    name.textContent = database;
    pick.append(check, name);

    const target = document.createElement("input");
    target.className = "db-target";
    target.value = targetFor(database);
    // The column heading disappears on narrow screens, so the field carries its
    // own name rather than borrowing one.
    target.title = "Hedef veritabanı adı";
    target.setAttribute("aria-label", `${database} için hedef veritabanı adı`);
    target.oninput = () => targetNames.set(database, target.value);

    row.append(pick, target);
    host.appendChild(row);
  }
  showEmptyState(visible.length);
  showSelection();
}

// An empty area explains nothing. Whatever the reason there is no list, say it.
function showEmptyState(visibleCount) {
  const empty = $("dbEmpty");
  if (!$("srcServer").value)
    empty.textContent = "Önce bir SQL Server seç — veritabanları buraya gelir.";
  else if (!sourceDatabases.length)
    empty.textContent = "Bu sunucuda listelenecek veritabanı yok.";
  else
    empty.textContent = "Aramaya uyan veritabanı yok.";

  empty.hidden = visibleCount > 0;
  $("dbHead").hidden = visibleCount === 0;
  $("btnAll").disabled = visibleCount === 0;
  $("btnNone").disabled = selectedDatabases.size === 0;
}

function showSelection() {
  $("btnNone").disabled = selectedDatabases.size === 0;
  if (!sourceDatabases.length) return setMsg("srcMsg", "");
  const filtered = visibleCount === sourceDatabases.length ? "" : ` · ${visibleCount} listeleniyor`;
  setMsg("srcMsg",
    `${sourceDatabases.length} veritabanı · ${selectedDatabases.size} seçili${filtered}`,
    selectedDatabases.size ? "ok" : undefined);
}

async function loadSourceDatabases() {
  sourceDatabases = [];
  selectedDatabases.clear();
  targetNames.clear();
  const serverId = $("srcServer").value;
  if (!serverId) { renderDatabases(); return setMsg("srcMsg", ""); }
  setMsg("srcMsg", "Veritabanları okunuyor…");
  try {
    sourceDatabases = await api(`/api/servers/${serverId}/databases`);
    renderDatabases();
  } catch (e) {
    renderDatabases();
    setMsg("srcMsg", e.message, "err");
  }
}

async function loadTargetDatabases() {
  const serverId = $("tgtServer").value;
  if (!serverId) return setMsg("tgtMsg", "");
  setMsg("tgtMsg", "Bağlantı deneniyor…");
  try {
    const databases = await api(`/api/servers/${serverId}/databases`);
    setMsg("tgtMsg", `Bağlantı tamam — sunucuda ${databases.length} veritabanı var.`, "ok");
  } catch (e) {
    setMsg("tgtMsg", e.message, "err");
  }
}

$("srcServer").onchange = loadSourceDatabases;
$("tgtServer").onchange = loadTargetDatabases;
$("srcFilter").oninput = renderDatabases;
$("tgtPattern").oninput = renderDatabases;
$("btnAll").onclick = () => { visibleDatabases().forEach(d => selectedDatabases.add(d)); renderDatabases(); };
$("btnNone").onclick = () => { selectedDatabases.clear(); renderDatabases(); };

$("optUsers").onchange = () => { $("userOpts").hidden = !$("optUsers").checked; };

// Verifying must leave the target exactly as it was, and creating a role would not.
$("optVerifyOnly").onchange = () => {
  const verifying = $("optVerifyOnly").checked;
  $("optUsers").disabled = verifying;
  if (verifying) {
    $("optUsers").checked = false;
    $("userOpts").hidden = true;
  }
};

// ── Translation ─────────────────────────────────────────────────────────────
// The engine speaks English; the Turkish is built here from code + args. A message
// not in the dictionary (e.g. a driver error) is shown as-is, in English.

const TR = {
  "step.readingSchemas": "Şemalar okunuyor",
  "step.preflight": "Ön kontrol",
  "step.copying": "Veri taşınıyor",
  "step.verifyRowCounts": "Satır sayıları doğrulanıyor",
  "step.verifyForeignKeys": "Yabancı anahtar bütünlüğü doğrulanıyor",

  "info.tablesToMigrate": "{0} tablo taşınacak.",
  "error.noTablesToCopy": "Kopyalanacak tablo bulunamadı — kaynak/hedef yanlış olabilir ya da hedefte şema yok.",
  "error.columnNotSynthesizable": "{0}.{1}: kaynakta yok, NOT NULL ve güvenli varsayılan üretilemiyor ({2}).",
  "warn.sourceOnlyTable": "Kaynak tablosu '{0}' hedefte yok — verisi taşınmayacak.",
  "error.sourceOnlyTable": "Kaynak tablosu '{0}' hedefte yok — verisi taşınmayacak. Bilinçliyse 'hedefte olmayan tablolara izin ver', hedefte oluşturulsun istiyorsan 'aynala' seçeneğini işaretleyin.",

  "step.mirroring": "Eksik tablolar aynalanıyor",
  "info.mirrorPlan": "{0} tablo kaynak şemadan oluşturulacak. Default, index ve check constraint'ler kopyalanmaz.",
  "info.tableCreated": "  {0}: oluşturuldu",
  "info.mirrorForeignKeys": "{0} yabancı anahtar oluşturuldu.",
  "warn.mirrorFkSkipped": "Yabancı anahtar {0} atlandı — üst tablo '{1}' hedefte yok.",
  "warn.mirrorFkFailed": "Yabancı anahtar {0} oluşturulamadı: {1}",
  "error.mirrorUnsupportedType": "{0}.{1}: kaynak tipi '{2}' için PostgreSQL karşılığı yok — tablo aynalanamıyor.",
  "fail.mirrorFailed": "Şema aynalama başarısız — ayrıntılar yukarıda.",

  "info.preflightClean": "Ön kontrol temiz: NULL/uzunluk uyumsuzluğu yok.",
  "error.preflightNulls": "{0}.{1}: hedef NOT NULL ama kaynakta {2} NULL var.",
  "error.preflightLength": "{0}.{1}: hedef varchar({2}) ama kaynakta en uzun değer {3} karakter.",
  "warn.preflightAllowed": "İzin verildiği için devam ediliyor — kopyalama sırasında patlayabilir.",

  "info.tableCopied": "  {0}: {1} satır",
  "info.copyFinished": "Kopyalama bitti — {0} satır.",
  "info.sequencesAligned": "{0} identity sequence hizalandı.",
  "warn.truncateCascade": "TRUNCATE CASCADE {0} bağlı tabloyu da boşalttı: {1}. Kaynakta karşılığı olmayanlar boş kalır.",
  "warn.truncateCascadeMore": "TRUNCATE CASCADE {0} bağlı tabloyu da boşalttı: {1} (+{2} tablo daha). Kaynakta karşılığı olmayanlar boş kalır.",
  "error.zeroRows": "Hiç satır kopyalanmadı; kaynak boş ya da yanlış. Commit edilmedi.",

  "info.rowCountsMatch": "Tüm satır sayıları eşit.",
  "error.rowCountMismatch": "{0}: kaynak {1}, hedef {2} satır.",
  "info.foreignKeysClean": "{0} yabancı anahtar denetlendi, yetim satır yok.",
  "error.orphanRows": "Yetim satır: {0} ({1}) → {2}: {3} satır ({4}).",
  "error.verifyFailedRollback": "Doğrulama başarısız — geri alınıyor, hedefe yazılmadı.",

  "error.targetDbNameMissing": "Hedef bağlantı dizgisinde veritabanı adı yok.",
  "info.targetDbExists": "Hedef veritabanı '{0}' zaten var — dokunulmadı.",
  "success.targetDbCreated": "Hedef veritabanı '{0}' oluşturuldu.",
  "success.targetDbCreatedCollation": "Hedef veritabanı '{0}' oluşturuldu (collation: {1}).",
  "info.collationVerified": "Collation doğrulandı: {0}",
  "warn.collationMismatchAllowed": "Hedef collation '{0}' — beklenen ICU '{1}'. İzin verildiği için devam ediliyor.",
  "error.collationMismatch": "Hedef collation '{0}' — beklenen ICU '{1}'. Yanlış collation sessizdir: arama ve sıralama fark edilmeden yanlış davranır.",

  "step.creatingUser": "'{0}' veritabanı kullanıcısı oluşturuluyor",
  "success.userCreated": "'{0}' rolü oluşturuldu.",
  "warn.userExists": "'{0}' rolü zaten var — parolası değiştirilmedi.",
  "info.userPrivileges": "'{0}' veritabanının yetkileri '{1}' rolüne verildi.",
  "info.databaseIsolated": "'{1}' veritabanına artık yalnızca '{0}' bağlanabilir.",
  "warn.userOwnership": "Sahiplik '{0}' rolüne devredilemedi — tam yetkisi var ama nesneleri değiştiremez/silemez. Superuser ile bağlanırsan devredilir.",
  "error.userFailed": "'{0}' kullanıcısı oluşturulamadı: {1}",

  "step.batchDatabase": "[{0}/{1}] {2} → {3}",
  "info.batchSummary": "Toplu taşıma bitti: {0} başarılı, {1} başarısız, {2} satır.",
  "info.reportReady": "PDF raporu indirilmeye hazır.",
  "warn.reportFailed": "PDF raporu üretilemedi: {0}",
  "error.serverNotFound": "Kayıtlı sunucu bulunamadı.",
  "success.batchAll": "{0} veritabanı taşındı.",
  "fail.batchPartial": "{0} veritabanı taşındı, {1} tanesi başarısız.",

  "info.postgresNotice": "(pg) {0}",

  "success.migrated": "{0} satır taşındı ve doğrulandı.",
  "success.verifyPassed": "Doğrulama başarılı.",
  "fail.collationMismatch": "Collation uyuşmazlığı.",
  "fail.schemaMismatch": "Şema uyuşmazlığı — ayrıntılar yukarıda.",
  "fail.emptyIntersection": "Boş kesişim.",
  "fail.verifyFailed": "Doğrulama başarısız.",
  "fail.preflightUnresolved": "Ön kontrol uyumsuzlukları giderilmedi.",
  "fail.zeroRows": "Sıfır satır.",
  "fail.targetDbNotReady": "Hedef veritabanı hazırlanamadı.",
  "fail.exception": "Taşıma bir istisnayla durdu.",
};

// Sentinel values that arrive as arguments yet get translated themselves (see MessageCode.TokenUnknown).
const TR_TOKENS = {
  "@@unknown": "(bilinmiyor)",
  "@@unreadable": "(okunamadı)",
};

function translate(message) {
  const template = message.code && TR[message.code];
  if (!template) return message.text;
  const args = (message.args ?? []).map(a => TR_TOKENS[a] ?? a);
  return template.replace(/\{(\d+)\}/g, (placeholder, i) => args[i] ?? placeholder);
}

// ── Migration ───────────────────────────────────────────────────────────────

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
  const databases = sourceDatabases
    .filter(database => selectedDatabases.has(database))
    .map(database => ({ sourceDatabase: database, targetDatabase: targetFor(database).trim() }));

  const body = {
    sourceServerId: $("srcServer").value,
    targetServerId: $("tgtServer").value,
    databases,
    targetIcuLocale: $("tgtLocale").value.trim(),
    createUsers: $("optUsers").checked,
    userNamePattern: $("userPattern").value.trim(),
    allowSourceOnly: $("optSourceOnly").checked,
    mirrorMissingTables: $("optMirror").checked,
    allowSchemaRisk: $("optSchemaRisk").checked,
    allowCollationMismatch: $("optCollation").checked,
    verifyOnly: $("optVerifyOnly").checked,
  };
  if (!body.sourceServerId) return setMsg("runState", "Kaynak sunucu seç.", "err");
  if (!body.targetServerId) return setMsg("runState", "Hedef sunucu seç.", "err");
  if (!databases.length) return setMsg("runState", "En az bir veritabanı seç.", "err");
  const blank = databases.find(d => !d.targetDatabase);
  if (blank) return setMsg("runState", `'${blank.sourceDatabase}' için hedef ad boş.`, "err");

  $("log").textContent = "";
  $("btnReport").hidden = true;
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
    state.messages.forEach(m => appendLog(m.kind, translate(m)));
    cursor = state.next;
    if (state.done) {
      const summary = translate({ text: state.summary, code: state.summaryCode, args: state.summaryArgs });
      setMsg("runState", summary, state.succeeded ? "ok" : "err");
      appendLog(state.succeeded ? "Success" : "Error",
        (state.succeeded ? "✔ " : "✖ ") + summary);
      if (state.hasReport) {
        $("btnReport").hidden = false;
        // Content-Disposition makes this a download, so the page stays where it is.
        $("btnReport").onclick = () => { location.href = `/api/jobs/${jobId}/report.pdf`; };
      }
      return;
    }
    await new Promise(r => setTimeout(r, 500));
  }
}

// ── Theme ───────────────────────────────────────────────────────────────────
// Light is the default and the OS preference is deliberately not followed: this
// runs next to a terminal in daylight far more often than not, and the one
// choice that matters is remembered. index.html applies it before first paint.

$("btnTheme").onclick = () => {
  const goingDark = document.documentElement.dataset.theme !== "dark";
  if (goingDark) document.documentElement.dataset.theme = "dark";
  else delete document.documentElement.dataset.theme;
  try { localStorage.setItem("theme", goingDark ? "dark" : "light"); } catch { /* private mode */ }
};

renderDatabases();
loadServers().catch(e => setMsg("serverMsg", e.message, "err"));
