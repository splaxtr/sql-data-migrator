// Server management. Loaded after app.js and reuses its $ and api helpers.
//
// Everything a server returns — a database name, a role name — is written with textContent
// and never with innerHTML. These strings come from someone else's server and the panel has
// no business deciding they are safe to parse as markup.

// The two products name the same ideas differently, and a panel that says "role" to a SQL
// Server operator is a panel they have to translate in their head. The words live here, in
// Turkish, next to the screen that shows them.
const ADMIN_WORDS = {
  PostgreSql: {
    roles: "Kullanıcılar ve roller",
    newOne: "Yeni rol",
    editOne: "Rolü düzenle",
    user: "kullanıcı",
    group: "grup",
    disabled: "kullanıcı (kapalı)",
    login: "Giriş yapabilir",
    createDb: "Veritabanı oluşturabilir (CREATEDB)",
    createRole: "Rol oluşturabilir (CREATEROLE)",
    superuser: "Superuser — her şeyi yapabilir",
    superuserShort: "SUPERUSER",
    createDbShort: "CREATEDB",
    createRoleShort: "CREATEROLE",
    flagNote: "PostgreSQL'de kullanıcı ile grup aynı şeydir: giriş yapabilen bir rol kullanıcıdır.",
    hint: "PostgreSQL'de kullanıcı ayrı bir nesne değildir: giriş yapabilen bir rol kullanıcıdır, giriş yapamayan bir rol gruptur. Her ikisi de bu listede.",
    collationNote: "ICU yerel adı — örneğin und ya da tr-TR. Bir veritabanının collation'ı sonradan değiştirilemez, düzeltmek onu yeniden oluşturmak demektir.",
    groupLabel: "Üyesi olduğu roller",
  },
  SqlServer: {
    roles: "Login'ler ve sunucu rolleri",
    newOne: "Yeni login",
    editOne: "Login'i düzenle",
    user: "login",
    group: "sunucu rolü",
    disabled: "login (kapalı)",
    login: "Etkin",
    createDb: "Veritabanı oluşturabilir (dbcreator)",
    createRole: "Login yönetebilir (securityadmin)",
    superuser: "Tam yetki (sysadmin)",
    superuserShort: "sysadmin",
    createDbShort: "dbcreator",
    createRoleShort: "securityadmin",
    flagNote: "SQL Server'da bu yetkiler sabit sunucu rolleridir; kutular o rollere üyeliği açar ve kapatır.",
    hint: "Kullanıcılar burada sunucu login'idir. Sabit sunucu rolleri de listelenir; onlar silinemez, yalnızca üyelik için vardır.",
    collationNote: "Sunucu collation adı — örneğin SQL_Latin1_General_CP1_CI_AS. Boş bırakırsan sunucunun varsayılanı kullanılır.",
    groupLabel: "Üyesi olduğu sunucu rolleri",
  },
};

// Fixed server roles SQL Server uses to express what PostgreSQL keeps as role attributes.
// They are already the three checkboxes above, so the membership list leaves them out
// rather than offering a second control for the same fact.
const MAPPED_SERVER_ROLES = ["dbcreator", "securityadmin", "sysadmin"];

const LEVELS = [
  ["None", "yetki yok"],
  ["Connect", "bağlanabilir"],
  ["ReadWrite", "okur ve yazar"],
  ["Owner", "sahibi"],
];

const admin = {
  kind: null,
  capabilities: null,
  databases: [],
  roles: [],
  // Set while a dialog is open, so its buttons know what they are acting on.
  target: null,
};

const words = () => ADMIN_WORDS[admin.kind] ?? ADMIN_WORDS.PostgreSql;
const adminApi = (path, body) => api(`/api/admin/${$("admServer").value}/${path}`, {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify(body),
});

function formatSize(bytes) {
  if (!bytes) return "—";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${value.toFixed(unit > 0 && value < 10 ? 1 : 0)} ${units[unit]}`;
}

// ── Tabs ────────────────────────────────────────────────────────────────────

function showView(showAdmin) {
  $("viewMigrate").hidden = showAdmin;
  $("viewAdmin").hidden = !showAdmin;
  $("tabMigrate").setAttribute("aria-selected", String(!showAdmin));
  $("tabAdmin").setAttribute("aria-selected", String(showAdmin));
  if (!showAdmin) return;
  // Filled here as well as on the hook below: app.js starts loading the server list before
  // this file has run, so a fast answer can land before the hook exists to catch it.
  fillAdminServers();
  if (!admin.capabilities) loadAdmin();
}

$("tabMigrate").onclick = () => showView(false);
$("tabAdmin").onclick = () => showView(true);

// ── Server picker ───────────────────────────────────────────────────────────

// app.js owns the server list; this runs whenever it has just refreshed it, so adding a
// server on the migration tab fills the panel's picker too.
window.onServersLoaded = fillAdminServers;

function fillAdminServers() {
  const select = $("admServer");
  const previous = select.value;
  select.innerHTML = `<option value="">— seç —</option>`;
  for (const s of servers) {
    const option = document.createElement("option");
    option.value = s.id;
    option.textContent = `${s.name} · ${s.kind === "SqlServer" ? "SQL Server" : "PostgreSQL"} · ${s.host}:${s.port}`;
    select.appendChild(option);
  }
  if ([...select.options].some(o => o.value === previous)) select.value = previous;
  else resetAdmin();
}

function resetAdmin() {
  admin.kind = null;
  admin.capabilities = null;
  admin.databases = [];
  admin.roles = [];
  $("admDbCard").hidden = true;
  $("admRoleCard").hidden = true;
  setMsg("admMsg", "");
}

async function loadAdmin() {
  const id = $("admServer").value;
  if (!id) { resetAdmin(); return; }
  admin.kind = servers.find(s => s.id === id)?.kind ?? null;
  setMsg("admMsg", "Sunucu okunuyor…");
  try {
    const data = await api(`/api/admin/${id}/overview`);
    admin.capabilities = data.capabilities;
    admin.databases = data.databases;
    admin.roles = data.roles;
    $("admDbCard").hidden = false;
    $("admRoleCard").hidden = false;
    $("admRoleTitle").textContent = words().roles;
    $("admRoleHint").textContent = words().hint;
    $("btnNewRole").textContent = words().newOne;
    renderAdminDatabases();
    renderAdminRoles();
    setMsg("admMsg", "Güncel.", "ok");
  } catch (e) {
    resetAdmin();
    setMsg("admMsg", e.message, "err");
  }
}

$("admServer").onchange = loadAdmin;
$("admRefresh").onclick = loadAdmin;
$("admDbFilter").oninput = renderAdminDatabases;
$("admRoleFilter").oninput = renderAdminRoles;
$("admDbSystem").onchange = renderAdminDatabases;
$("admRoleSystem").onchange = renderAdminRoles;

// A server's own roles outnumber the ones anybody created — fourteen pg_* entries would
// push the three that matter off the screen. They are hidden by default and counted out
// loud, because a list that quietly omits rows is worse than a long one.
function visible(items, needle, withSystem) {
  return items.filter(i => (withSystem || !i.isSystem) && fold(i.name).includes(needle));
}

function count(items, withSystem, noun) {
  const system = items.filter(i => i.isSystem).length;
  if (withSystem || system === 0) return `${items.length} ${noun}`;
  return `${items.length - system} ${noun} · ${system} sistem kaydı gizli`;
}

// ── Databases ───────────────────────────────────────────────────────────────

function renderAdminDatabases() {
  const host = $("admDbList");
  host.innerHTML = "";
  const needle = fold($("admDbFilter").value.trim());
  const rows = visible(admin.databases, needle, $("admDbSystem").checked);

  for (const database of rows) {
    const row = document.createElement("div");
    row.className = "adm-row adm-db-row";
    row.append(
      cell(database.name, "strong"),
      cell(database.owner),
      cell(database.collation),
      cell(formatSize(database.sizeBytes), "num"),
      cell(String(database.connections), "num"));

    const actions = document.createElement("div");
    actions.className = "adm-actions";
    if (database.isSystem) {
      actions.append(tag("sistem"));
    } else {
      actions.append(
        action("Yetkiler", `${database.name} veritabanının yetkileri`, () => openGrants(database)),
        action("Sahip", `${database.name} veritabanının sahibini değiştir`, () => openOwner(database)),
        action("Sil", `${database.name} veritabanını sil`, () => openDropDatabase(database), "danger-btn"));
    }
    row.append(actions);
    host.appendChild(row);
  }

  $("admDbCount").textContent = count(admin.databases, $("admDbSystem").checked, "veritabanı");
  const empty = $("admDbEmpty");
  empty.textContent = admin.databases.length === 0
    ? "Bu sunucuda veritabanı yok."
    : "Aramaya uyan veritabanı yok.";
  empty.hidden = rows.length > 0;
}

$("btnNewDb").onclick = () => {
  admin.target = null;
  $("dbName").value = "";
  $("dbCollation").value = admin.capabilities?.icuCollation ? "und" : "";
  $("dbCollationRow").hidden = !admin.capabilities?.collation;
  $("dbCollationNote").textContent = words().collationNote;
  $("dbCollationNote").hidden = !admin.capabilities?.collation;
  fillRoleSelect($("dbOwner"), true);
  setMsg("dbMsg", "");
  $("dlgDatabase").showModal();
};

$("dbCreate").onclick = async () => {
  try {
    await adminApi("database/create", {
      name: $("dbName").value.trim(),
      collation: $("dbCollation").value.trim(),
      owner: $("dbOwner").value,
    });
    $("dlgDatabase").close();
    await loadAdmin();
  } catch (e) { setMsg("dbMsg", e.message, "err"); }
};

function openOwner(database) {
  admin.target = database;
  $("ownerWhat").textContent = `'${database.name}' veritabanının şu anki sahibi: ${database.owner}`;
  fillRoleSelect($("ownerRole"), false);
  setMsg("ownerMsg", "");
  $("dlgOwner").showModal();
}

$("ownerApply").onclick = async () => {
  try {
    await adminApi("database/owner", { database: admin.target.name, owner: $("ownerRole").value });
    $("dlgOwner").close();
    await loadAdmin();
  } catch (e) { setMsg("ownerMsg", e.message, "err"); }
};

// ── Privileges ──────────────────────────────────────────────────────────────

async function openGrants(database) {
  admin.target = database;
  $("grantsWhat").textContent = `'${database.name}' üzerinde hangi rolün ne yapabildiği.`;
  $("publicConnectRow").hidden = !admin.capabilities?.publicConnect;
  setMsg("grantsMsg", "");
  $("grantsList").textContent = "";
  $("dlgGrants").showModal();
  await refreshGrants();
}

async function refreshGrants() {
  const host = $("grantsList");
  try {
    const grants = await adminApi("database/grants", { name: admin.target.name });
    const byRole = new Map(grants.map(g => [g.role, g.level]));
    host.textContent = "";

    // Every role that could be given something, not only the ones that already have it —
    // a screen that lists only current grants cannot be used to make a new one.
    for (const role of admin.roles.filter(r => !r.isGroup && !r.isSystem)) {
      const row = document.createElement("div");
      row.className = "grant-row";
      row.append(cell(role.name));

      const select = document.createElement("select");
      select.setAttribute("aria-label", `${role.name} için yetki düzeyi`);
      for (const [value, label] of LEVELS) {
        const option = document.createElement("option");
        option.value = value;
        option.textContent = label;
        select.appendChild(option);
      }
      select.value = byRole.get(role.name) ?? "None";
      select.onchange = async () => {
        setMsg("grantsMsg", "Uygulanıyor…");
        try {
          await adminApi("database/grant",
            { database: admin.target.name, role: role.name, level: select.value });
          setMsg("grantsMsg", `${role.name}: ${select.selectedOptions[0].textContent}`, "ok");
          await refreshGrants();
          await loadAdmin();
        } catch (e) {
          setMsg("grantsMsg", e.message, "err");
          await refreshGrants();
        }
      };
      row.append(select);
      host.appendChild(row);
    }
    if (!host.childElementCount) {
      const note = document.createElement("p");
      note.className = "empty";
      note.textContent = "Yetki verilebilecek bir hesap yok.";
      host.appendChild(note);
    }

    // PUBLIC is not a role, so it has no row above; the server reports it as its own entry
    // and the checkbox reads that rather than guessing from the roles that are listed.
    if (admin.capabilities?.publicConnect)
      $("publicConnect").checked = grants.some(g => g.role === "PUBLIC");
  } catch (e) {
    setMsg("grantsMsg", e.message, "err");
  }
}

$("publicConnect").onchange = async () => {
  setMsg("grantsMsg", "Uygulanıyor…");
  try {
    await adminApi("database/public-connect",
      { database: admin.target.name, allowed: $("publicConnect").checked });
    setMsg("grantsMsg", $("publicConnect").checked
      ? "PUBLIC yeniden bağlanabilir."
      : "PUBLIC artık bağlanamaz.", "ok");
    await refreshGrants();
  } catch (e) { setMsg("grantsMsg", e.message, "err"); }
};

// ── Roles ───────────────────────────────────────────────────────────────────

function renderAdminRoles() {
  const host = $("admRoleList");
  host.innerHTML = "";
  const needle = fold($("admRoleFilter").value.trim());
  const rows = visible(admin.roles, needle, $("admRoleSystem").checked)
    .sort((a, b) => (a.isGroup - b.isGroup) || a.name.localeCompare(b.name, "tr"));

  for (const role of rows) {
    const row = document.createElement("div");
    row.className = "adm-row adm-role-row";
    const flags = [
      role.attributes.superuser && words().superuserShort,
      role.attributes.createDb && words().createDbShort,
      role.attributes.createRole && words().createRoleShort,
    ].filter(Boolean).join(" · ");

    const kind = role.isGroup ? words().group
      : role.canLogin ? words().user
      : words().disabled;
    row.append(
      cell(role.name, "strong"),
      cell(kind, role.isGroup ? "muted" : undefined),
      cell(flags || "—"),
      cell(role.memberOf.length ? role.memberOf.join(", ") : "—"));

    const actions = document.createElement("div");
    actions.className = "adm-actions";
    if (role.isSystem) {
      actions.append(tag("sistem"));
    } else {
      actions.append(
        action("Düzenle", `${role.name} hesabını düzenle`, () => openRole(role)),
        action("Sil", `${role.name} hesabını sil`, () => openDropRole(role), "danger-btn"));
    }
    row.append(actions);
    host.appendChild(row);
  }

  $("admRoleCount").textContent = count(admin.roles, $("admRoleSystem").checked, "kayıt");
  const empty = $("admRoleEmpty");
  empty.textContent = admin.roles.length === 0 ? "Kayıt yok." : "Aramaya uyan kayıt yok.";
  empty.hidden = rows.length > 0;
}

function openRole(role) {
  admin.target = role;
  const editing = role !== null;
  $("roleTitle").textContent = editing ? `${words().editOne}: ${role.name}` : words().newOne;
  $("roleName").value = editing ? role.name : "";
  $("roleName").disabled = editing;
  $("rolePassword").value = "";
  $("rolePassword").placeholder = editing ? "boş bırak = değiştirme" : "boş bırak = üret";
  $("roleLogin").checked = editing ? role.attributes.canLogin : true;
  $("roleCreateDb").checked = editing ? role.attributes.createDb : false;
  $("roleCreateRole").checked = editing ? role.attributes.createRole : false;
  $("roleSuper").checked = editing ? role.attributes.superuser : false;
  $("lblLogin").textContent = words().login;
  $("lblCreateDb").textContent = words().createDb;
  $("lblCreateRole").textContent = words().createRole;
  $("lblSuper").textContent = words().superuser;
  $("roleFlagNote").textContent = words().flagNote;
  $("roleApply").textContent = editing ? "Kaydet" : "Oluştur";
  $("rolePasswordReset").hidden = !editing;
  $("membershipBox").hidden = !editing || !admin.capabilities?.membership;
  if (editing && admin.capabilities?.membership) renderMembership(role);
  setMsg("roleMsg", "");
  $("dlgRole").showModal();
}

$("btnNewRole").onclick = () => openRole(null);

function attributesFromForm() {
  return {
    canLogin: $("roleLogin").checked,
    createDb: $("roleCreateDb").checked,
    createRole: $("roleCreateRole").checked,
    superuser: $("roleSuper").checked,
  };
}

$("roleApply").onclick = async () => {
  const editing = admin.target !== null;
  try {
    if (editing) {
      await adminApi("role/attributes", { name: admin.target.name, attributes: attributesFromForm() });
      $("dlgRole").close();
    } else {
      const result = await adminApi("role/create", {
        name: $("roleName").value.trim(),
        password: $("rolePassword").value,
        attributes: attributesFromForm(),
      });
      $("dlgRole").close();
      if (result.generated) showSecret(result.role, result.password);
    }
    await loadAdmin();
  } catch (e) { setMsg("roleMsg", e.message, "err"); }
};

$("rolePasswordReset").onclick = async () => {
  try {
    const result = await adminApi("role/password",
      { name: admin.target.name, password: $("rolePassword").value });
    $("rolePassword").value = "";
    if (result.generated) showSecret(result.role, result.password);
    else setMsg("roleMsg", "Parola değiştirildi.", "ok");
  } catch (e) { setMsg("roleMsg", e.message, "err"); }
};

function renderMembership(role) {
  const host = $("membershipList");
  host.textContent = "";
  $("membershipLabel").textContent = words().groupLabel;

  // A group is a principal that cannot log in: a PostgreSQL group role, a SQL Server
  // server role. The three that back the checkboxes above are left out on purpose.
  const groups = admin.roles.filter(r =>
    r.isGroup && r.name !== role.name && !MAPPED_SERVER_ROLES.includes(r.name));

  for (const group of groups) {
    const label = document.createElement("label");
    label.className = "flag";
    const box = document.createElement("input");
    box.type = "checkbox";
    box.checked = role.memberOf.includes(group.name);
    box.onchange = async () => {
      setMsg("roleMsg", "Uygulanıyor…");
      try {
        await adminApi("role/membership",
          { name: role.name, group: group.name, member: box.checked });
        setMsg("roleMsg", `${group.name}: ${box.checked ? "eklendi" : "çıkarıldı"}`, "ok");
        await loadAdmin();
      } catch (e) {
        box.checked = !box.checked;
        setMsg("roleMsg", e.message, "err");
      }
    };
    const text = document.createElement("span");
    text.textContent = group.name;
    label.append(box, text);
    host.appendChild(label);
  }
  if (!host.childElementCount) {
    const note = document.createElement("p");
    note.className = "empty";
    note.textContent = "Üye olunabilecek bir grup yok.";
    host.appendChild(note);
  }
}

// ── Dropping ────────────────────────────────────────────────────────────────
// The confirmation shows what is about to be lost and then asks for the name to be typed.
// A dialog that only asks "are you sure?" is a dialog people learn to click through.

async function openDropDatabase(database) {
  admin.target = { kind: "database", name: database.name };
  $("dropTitle").textContent = `Veritabanını sil: ${database.name}`;
  $("dropForceRow").hidden = !admin.capabilities?.closeConnections;
  prepareDrop();
  try {
    const p = await adminApi("database/drop-preview", { name: database.name });
    $("dropWhat").textContent =
      `Sahibi ${p.owner}. ${formatSize(p.sizeBytes)}, ${p.tables} tablo, ` +
      `yaklaşık ${p.rows.toLocaleString("tr")} satır ve ${p.connections} açık bağlantı. ` +
      `Bunların hepsi kalıcı olarak silinecek.`;
  } catch (e) {
    $("dropWhat").textContent = `Silinecekler okunamadı: ${e.message}`;
  }
}

async function openDropRole(role) {
  admin.target = { kind: "role", name: role.name };
  $("dropTitle").textContent = `Hesabı sil: ${role.name}`;
  $("dropForceRow").hidden = true;
  $("dropReassignRow").hidden = true;
  prepareDrop();
  try {
    const p = await adminApi("role/drop-preview", { name: role.name });
    const parts = [];
    if (p.owns.length) parts.push(`Şu veritabanlarının sahibi: ${p.owns.join(", ")}.`);
    if (p.dependencies.length)
      parts.push("Sahip olduğu ya da yetkili olduğu nesneler: " +
        p.dependencies.map(d => `${d.database} içinde ${d.objects}`).join(", ") + ".");
    if (!parts.length) parts.push("Sahip olduğu bir nesne yok; doğrudan silinebilir.");
    if (p.isCurrentUser) parts.push("Bu hesap, uygulamanın şu anda bağlandığı hesaptır.");
    $("dropWhat").textContent = parts.join(" ");

    // A role that owns something cannot be dropped by either product. Offering the
    // hand-over here is what keeps the button from being a dead end on exactly the roles
    // this tool creates — a per-database login owns its database and all of its tables.
    const blocked = p.owns.length > 0 || p.dependencies.length > 0;
    $("dropReassignRow").hidden = !blocked;
    if (blocked) {
      const select = $("dropReassign");
      select.innerHTML = `<option value="">— devretme, yine de dene —</option>`;
      for (const other of admin.roles.filter(r => !r.isGroup && r.name !== role.name)) {
        const option = document.createElement("option");
        option.value = other.name;
        option.textContent = other.name;
        select.appendChild(option);
      }
    }
  } catch (e) {
    $("dropWhat").textContent = `Silinecekler okunamadı: ${e.message}`;
  }
}

function prepareDrop() {
  $("dropName").textContent = admin.target.name;
  $("dropConfirm").value = "";
  $("dropForce").checked = false;
  $("dropApply").disabled = true;
  $("dropWhat").textContent = "Okunuyor…";
  setMsg("dropMsg", "");
  $("dlgDrop").showModal();
}

// The button unlocks only on an exact match — same rule the server applies, so the page
// never offers an action the request would refuse.
$("dropConfirm").oninput = () => {
  $("dropApply").disabled = $("dropConfirm").value !== admin.target?.name;
};

$("dropApply").onclick = async () => {
  setMsg("dropMsg", "Siliniyor…");
  try {
    if (admin.target.kind === "database")
      await adminApi("database/drop", {
        database: admin.target.name,
        confirm: $("dropConfirm").value,
        closeConnections: $("dropForce").checked,
      });
    else {
      const heir = $("dropReassignRow").hidden ? "" : $("dropReassign").value;
      if (heir) {
        setMsg("dropMsg", `Sahiplik ${heir} rolüne devrediliyor…`);
        await adminApi("role/reassign", { name: admin.target.name, owner: heir });
      }
      await adminApi("role/drop", { name: admin.target.name, confirm: $("dropConfirm").value });
    }
    $("dlgDrop").close();
    await loadAdmin();
    setMsg("admMsg", `'${admin.target.name}' silindi.`, "ok");
  } catch (e) { setMsg("dropMsg", e.message, "err"); }
};

// Copying the name is allowed on purpose. What the gate is for is that the deletion was
// meant, and the panel has already said above what will be lost — making people transcribe
// a name by hand tests their typing, not their intent.
$("dropCopy").onclick = async () => {
  try {
    await navigator.clipboard.writeText($("dropName").textContent);
    setMsg("dropMsg", "Ad kopyalandı.", "ok");
  } catch {
    setMsg("dropMsg", "Kopyalanamadı — adı elle seçip kopyalayın.", "err");
  }
};

// ── A generated password, shown once ────────────────────────────────────────

function showSecret(role, password) {
  $("secretWhat").textContent = `'${role}' için üretilen parola:`;
  $("secretValue").textContent = password;
  setMsg("secretMsg", "");
  $("dlgSecret").showModal();
}

$("secretCopy").onclick = async () => {
  try {
    await navigator.clipboard.writeText($("secretValue").textContent);
    setMsg("secretMsg", "Kopyalandı.", "ok");
  } catch {
    // Clipboard access is refused on some setups; the value is on screen either way.
    setMsg("secretMsg", "Kopyalanamadı — elle seçip kopyalayın.", "err");
  }
};

// The value must not outlive the dialog: it exists nowhere else, and leaving it in the DOM
// is the one place this app would be keeping a password around.
$("dlgSecret").addEventListener("close", () => { $("secretValue").textContent = ""; });

// A dialog form's first submit button is the cancel one, which is what the browser picks
// for Enter. In a form that exists to perform an action, that is the wrong default.
for (const dialog of document.querySelectorAll("dialog.dlg")) {
  dialog.addEventListener("keydown", (event) => {
    if (event.key !== "Enter" || event.target.tagName !== "INPUT") return;
    const primary = dialog.querySelector(
      "button.primary:not([disabled]), button.danger-btn:not([disabled])");
    if (!primary) return;
    event.preventDefault();
    primary.click();
  });
}

// ── Small builders ──────────────────────────────────────────────────────────

function cell(text, className) {
  const span = document.createElement("span");
  span.textContent = text;
  // Cells clip rather than wrap, so the full value has to stay reachable — a truncated
  // owner or collation is a value the operator cannot check.
  span.title = text;
  if (className) span.className = className;
  return span;
}

function tag(text) {
  const span = document.createElement("span");
  span.className = "adm-tag";
  span.textContent = text;
  return span;
}

function action(text, label, handler, className) {
  const button = document.createElement("button");
  button.type = "button";
  button.textContent = text;
  button.setAttribute("aria-label", label);
  if (className) button.className = className;
  button.onclick = handler;
  return button;
}

function fillRoleSelect(select, allowEmpty) {
  select.innerHTML = "";
  if (allowEmpty) {
    const option = document.createElement("option");
    option.value = "";
    option.textContent = "— bağlanan hesap —";
    select.appendChild(option);
  }
  for (const role of admin.roles.filter(r => !r.isSystem || r.canLogin)) {
    const option = document.createElement("option");
    option.value = role.name;
    option.textContent = role.name;
    select.appendChild(option);
  }
}
